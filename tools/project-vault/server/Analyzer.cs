using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;

namespace ProjectVault;

public sealed class Analyzer(Repository repo, string toolRoot)
{
    static readonly ConcurrentDictionary<string, (string Shape, MSBuildWorkspace Workspace)> Workspaces = new();
    readonly Dictionary<string, string> assemblyProjects = [];
    readonly Dictionary<string, Element> elements = [];
    readonly Dictionary<string, Relation> relations = [];
    readonly Dictionary<string, (MethodDeclarationSyntax Node, SemanticModel Model, string Path)> methods = [];
    readonly Dictionary<string, List<string>> implementations = [];
    readonly Dictionary<string, List<string>> unresolved = [];
    readonly Dictionary<string, Workflow> flows = [];
    Snapshot snapshot = new();
    string projectKey = "";
    Evidence Ev(SyntaxNode node, string path) => new(path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, snapshot.Files.GetValueOrDefault(path, ""));
    string SymbolId(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly?.Name ?? "";
        var owner = assemblyProjects.GetValueOrDefault(assembly, symbol.Locations.Any(l => l.IsInSource) ? projectKey : "external:" + assembly);
        return Format.Id(owner + ":" + (symbol.OriginalDefinition.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
    }
    void Link(string source, string target, string kind, string status = "extracted", int? order = null)
    {
        var id = Format.Id(source + kind + target + order);
        relations[id] = new(id, source, target, kind, status, order);
    }
    static string Words(string value) => Regex.Replace(value, "(?<=[a-z])([A-Z])", " $1");
    static string Safe(string text) => Regex.Replace(text, "\"(?:\\\\.|[^\"])*\"", "\"…\"");
    static bool IsTest(string path) => Regex.IsMatch(path, @"(?:^|[/.])(?:Tests?|BrowserTests|ClientTests)(?:[/.]|$)", RegexOptions.IgnoreCase);
    public async Task<Snapshot> Analyze(SortedDictionary<string, string> files)
    {
        var identity = await repo.Identity();
        snapshot = new Snapshot { Project = Path.GetFileName(repo.Root), Files = files, Branch = identity.Branch, Revision = identity.Revision, Worktree = identity.Worktree, CreatedAt = DateTimeOffset.UtcNow };
        snapshot.Id = Repository.Fingerprint(files, identity.Revision, identity.Branch, identity.Worktree);
        var projects = files.Keys.Where(x => x.EndsWith(".csproj") && !IsTest(x)).ToList();
        foreach (var path in projects) { var definition = XDocument.Parse(await repo.Read(path)); assemblyProjects[definition.Descendants("AssemblyName").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(path)] = path; }
        var shape = Format.Id(string.Join("|", files.Where(f => f.Key.EndsWith(".cs") || f.Key.EndsWith(".csproj") || f.Key.EndsWith(".props") || f.Key.EndsWith(".targets")).Select(f => f.Key + (f.Key.EndsWith(".cs") ? "" : f.Value))));
        if (!Workspaces.TryGetValue(repo.Root, out var cached) || cached.Shape != shape)
        {
            cached.Workspace?.Dispose(); cached = (shape, MSBuildWorkspace.Create(new Dictionary<string, string> { { "Configuration", "Debug" } })); Workspaces[repo.Root] = cached;
        }
        var workspace = cached.Workspace;
        var solution = workspace.CurrentSolution;
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is null || !files.ContainsKey(repo.Relative(document.FilePath))) continue;
            var text = SourceText.From(await repo.Read(repo.Relative(document.FilePath)));
            if (!(await document.GetTextAsync()).ContentEquals(text)) solution = solution.WithDocumentText(document.Id, text);
        }

        using var workspaceRegistration = workspace.RegisterWorkspaceFailedHandler(e => { if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) snapshot.Diagnostics.Add("Project loading: " + Regex.Replace(e.Diagnostic.Message, Regex.Escape(repo.Root), ".")); });
        foreach (var path in projects)
        {
            projectKey = path;
            var pid = Format.Id("project:" + path);
            elements[pid] = new Element { Id = pid, Name = Path.GetFileNameWithoutExtension(path), Kind = "application", Layer = "architecture", Group = "Applications", Summary = "Repository-defined .NET application or library.", Evidence = [new(path, 1, files[path])] };
            try
            {
                var project = solution.Projects.FirstOrDefault(p => p.FilePath == repo.Absolute(path)) ?? await workspace.OpenProjectAsync(repo.Absolute(path));
                assemblyProjects[project.AssemblyName ?? Path.GetFileNameWithoutExtension(path)] = path;
                var compilation = await project.GetCompilationAsync();
                if (compilation is null) throw new InvalidOperationException("No compilation available.");
                var errorDiagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.DefaultSeverity == DiagnosticSeverity.Error).ToList();
                var errors = errorDiagnostics.Count;
                if (errors > 0) snapshot.Diagnostics.Add($"{path}: {errors} design-time compilation errors ({string.Join(", ", errorDiagnostics.GroupBy(d => d.Id).Select(g => g.Key + ":" + g.Count()))}); affected semantic bindings may be unresolved.");
                foreach (var tree in compilation.SyntaxTrees)
                {
                    var file = repo.Relative(tree.FilePath);
                    if (!files.ContainsKey(file) || file.Contains("/Migrations/") || file.EndsWith(".Designer.cs")) continue;
                    ScanTree(await tree.GetRootAsync(), compilation.GetSemanticModel(tree), file, pid);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                snapshot.Diagnostics.Add($"{path}: semantic project loading failed ({ex.GetType().Name}); source syntax is still inventoried.");
                var directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                var trees = new List<SyntaxTree>();
                foreach (var file in files.Keys.Where(x => x.EndsWith(".cs") && x.StartsWith(directory) && !x.Contains("/Migrations/"))) trees.Add(CSharpSyntaxTree.ParseText(await repo.Read(file), path: repo.Absolute(file)));
                var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path), trees);
                foreach (var tree in trees) ScanTree(await tree.GetRootAsync(), compilation.GetSemanticModel(tree), repo.Relative(tree.FilePath), pid);
            }
            await Packages(path, pid);
        }
        foreach (var (id, method) in methods)
        {
            projectKey = elements[id].Group;
            AnalyzeBody(id, method.Node, method.Model, method.Path);
        }
        await Frontend();
        await StackManifests();
        await Infrastructure();
        AddConcepts();
        snapshot.Elements = elements.Values.OrderBy(x => x.Id).ToList();
        snapshot.Relations = relations.Values.Where(r => elements.ContainsKey(r.Source) && elements.ContainsKey(r.Target)).OrderBy(x => x.Id).ToList();
        BuildWorkflows();
        snapshot.Diagnostics.Add("Scope: first-party sources; tests, EF generated migrations, dependencies, minified/vendor scripts, secrets, and this tool are excluded from behavioral analysis. Their supported source files still participate in freshness checks.");
        if (files.Keys.Any(x => x.EndsWith(".razor"))) snapshot.Diagnostics.Add("Blazor .razor components are inventoried but their UI bindings require an adapter; coverage is incomplete.");
        return snapshot;
    }
    void ScanTree(SyntaxNode root, SemanticModel model, string path, string pid)
    {
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(type) is not INamedTypeSymbol symbol) continue;
            var id = SymbolId(symbol);
            var layer = path.Contains("/Models/") || type is RecordDeclarationSyntax || symbol.Name.EndsWith("Dto") || symbol.Name.EndsWith("Request") || symbol.Name.EndsWith("Response") ? "models" : "architecture";
            elements[id] = new Element { Id = id, Name = symbol.Name, Kind = type is InterfaceDeclarationSyntax ? "interface" : layer == "models" ? "model" : "component", Layer = layer, Group = Path.GetFileNameWithoutExtension(projectKey), Summary = $"{Words(symbol.Name)} · {symbol.TypeKind.ToString().ToLowerInvariant()} declared in this project.", Evidence = [Ev(type, path)], Inputs = symbol.GetMembers().OfType<IPropertySymbol>().Select(p => new Field(p.Name, p.Type.ToDisplayString())).ToList() };
            Link(pid, id, "contains");
            foreach (var property in symbol.GetMembers().OfType<IPropertySymbol>()) foreach (var target in ReferencedTypes(property.Type)) Link(id, SymbolId(target), "relates to");
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(method) is not IMethodSymbol ms) continue;
                var mid = SymbolId(ms);
                var attrs = method.AttributeLists.SelectMany(a => a.Attributes).Concat(type.AttributeLists.SelectMany(a => a.Attributes)).ToList();
                var controller = symbol.Name.EndsWith("Controller") || symbol.BaseType?.Name is "Controller" or "ControllerBase";
                var entry = controller && ms.DeclaredAccessibility == Accessibility.Public && !attrs.Any(a => a.Name.ToString().Contains("NonAction")) || method.Identifier.Text == "ExecuteAsync" && symbol.BaseType?.Name == "BackgroundService";
                var http = method.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString().StartsWith("Http"));
                string? route = null;
                if (controller)
                {
                    var prefix = type.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString() == "Route")?.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
                    var suffix = http?.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax ?? method.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString() == "Route")?.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
                    route = suffix?.Token.ValueText.StartsWith('/') == true || suffix?.Token.ValueText.StartsWith("~/") == true ? suffix.Token.ValueText.TrimStart('~') : prefix is not null ? "/" + prefix.Token.ValueText.Trim('/') + (suffix is not null ? "/" + suffix.Token.ValueText.Trim('/') : "") : "/" + symbol.Name.Replace("Controller", "") + "/" + method.Identifier.Text;
                    route = route.Replace("[controller]", symbol.Name.Replace("Controller", "")).Replace("[action]", method.Identifier.Text);
                }
                var e = new Element { Id = mid, Name = method.Identifier.Text, Kind = entry ? controller ? "endpoint" : "background" : "function", Layer = "functions", Group = projectKey, Summary = $"{Words(method.Identifier.Text)} in {symbol.Name}.", Signature = ms.ToDisplayString(), Output = ms.ReturnType.ToDisplayString(), Inputs = ms.Parameters.Select(p => new Field(p.Name, p.Type.ToDisplayString())).ToList(), Async = ms.IsAsync, EntryPoint = entry, Route = route, Verb = http?.Name.ToString().Replace("Http", "").ToUpperInvariant() ?? (controller ? "ANY" : null), Evidence = [Ev(method, path)] };
                foreach (var attr in attrs.Where(a => Regex.IsMatch(a.Name.ToString(), "Authorize|Anonymous|Antiforgery|Validate|Limit"))) e.Checks.Add(attr.Name.ToString() + (attr.ArgumentList is null ? "" : " (configured)"));
                if (attrs.Any(a => a.Name.ToString().Contains("AllowAnonymous"))) e.Checks.Add("AllowAnonymous overrides authorization at this endpoint.");
                if (ms.IsAsync) e.Concepts.Add("async");
                if (ms.Parameters.Any(p => p.Type.Name == "CancellationToken")) e.Checks.Add("Accepts cancellation token; propagation requires inspection.");
                if (e.Checks.Count > 0) e.Concerns.Add(new("security", "extracted", "Boundary attributes are present; their presence does not prove all access paths are protected."));
                elements[mid] = e;
                methods[mid] = (method, model, path);
                Link(id, mid, "contains");
                foreach (var t in ms.Parameters.Select(p => p.Type).Append(ms.ReturnType).SelectMany(ReferencedTypes)) Link(mid, SymbolId(t), "uses model");
                foreach (var iface in symbol.AllInterfaces)
                    foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                        if (SymbolEqualityComparer.Default.Equals(symbol.FindImplementationForInterfaceMember(member), ms))
                        {
                            var key = SymbolId(member);
                            if (!implementations.ContainsKey(key)) implementations[key] = [];
                            implementations[key].Add(mid);
                        }
            }
        }
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => c.Ancestors().OfType<MethodDeclarationSyntax>().Any() == false))
        {
            var name = (call.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;
            if (name is not ("MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch" or "MapMethods")) continue;
            var route = call.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            var id = Format.Id(projectKey + "minimal:" + name + ":" + (route?.Token.ValueText ?? call.SpanStart.ToString()));
            elements[id] = new Element { Id = id, Name = $"{name[3..].ToUpperInvariant()} {route?.Token.ValueText ?? "dynamic route"}", Route = route?.Token.ValueText, Verb = name[3..].ToUpperInvariant(), Layer = "functions", Kind = "endpoint", Group = projectKey, EntryPoint = true, Summary = "Minimal API route registration. Route groups and runtime conventions require review.", Evidence = [Ev(call, path)] };
            Link(pid, id, "contains");
            BindCalls(id, call, model, path);
        }
    }
    static IEnumerable<ITypeSymbol> ReferencedTypes(ITypeSymbol type)
    {
        yield return type;
        if (type is IArrayTypeSymbol array) foreach (var child in ReferencedTypes(array.ElementType)) yield return child;
        if (type is INamedTypeSymbol named) foreach (var arg in named.TypeArguments) foreach (var child in ReferencedTypes(arg)) yield return child;
    }
    void Gap(string id, string text) { if (!unresolved.ContainsKey(id)) unresolved[id] = []; if (!unresolved[id].Contains(text)) unresolved[id].Add(text); }
    void BindCalls(string id, SyntaxNode node, SemanticModel model, string path)
    {
        int order = 0;
        foreach (var call in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var info = model.GetSymbolInfo(call);
            var symbol = info.Symbol as IMethodSymbol;
            if (symbol is null) { Gap(id, $"Unresolved call at {path}:{Ev(call, path).Line}."); continue; }
            var target = SymbolId(symbol.ReducedFrom ?? symbol);
            if (implementations.TryGetValue(target, out var impls))
            {
                foreach (var impl in impls) Link(id, impl, "may call", "interpreted", order++);
                Gap(id, "Interface dispatch candidates are shown; runtime dependency-injection selection is unverified.");
            }
            if (!elements.ContainsKey(target))
            {
                if (symbol.Locations.Any(l => l.IsInSource)) { Gap(id, $"Referenced source symbol {symbol.Name} is outside analyzed declarations."); continue; }
                elements[target] = new Element { Id = target, Name = symbol.ContainingType.Name + "." + symbol.Name, Kind = "external", Layer = "architecture", Group = symbol.ContainingNamespace.ToDisplayString().Split('.')[0], Summary = "External library boundary. Internal implementation and runtime outcome are not traced.", Signature = symbol.ToDisplayString(), Output = symbol.ReturnType.ToDisplayString(), Status = "unresolved" };
            }
            Link(id, target, "calls", "extracted", order++);
            var name = symbol.Name;
            if (Regex.IsMatch(name, "SaveChanges|ExecuteUpdate|ExecuteDelete|AddAsync|Remove")) Link(id, target, "writes");
            if (Regex.IsMatch(name, "ToListAsync|FirstOrDefaultAsync|SingleOrDefaultAsync|FindAsync")) Link(id, target, "reads");
            if (Regex.IsMatch(name, "Transaction|Commit|Rollback")) { elements[id].Concepts.Add("transactions"); elements[id].Concerns.Add(new("financial", "extracted", "Transaction-related operation: " + name + ". Inspect boundaries and rollback behavior.")); }
            if (Regex.IsMatch(name, "Reserve|Debit|Refund|Checkout|Payment|Settle")) elements[id].Concerns.Add(new("financial", "interpreted", "Operation name suggests accounting or payment behavior: " + name + "; confirm business semantics."));
            if (Regex.IsMatch(name, "WaitAsync|Release|GetOrAdd|CompareExchange")) { elements[id].Concepts.Add("concurrency"); elements[id].Concerns.Add(new("concurrency", "interpreted", "Synchronization/shared-state operation: " + name + ". Review lock scope and concurrent callers; no race is proven.")); }
        }
    }
    void AnalyzeBody(string id, MethodDeclarationSyntax method, SemanticModel model, string path)
    {
        BindCalls(id, method, model, path);
        var e = elements[id];
        foreach (var condition in method.DescendantNodes().OfType<IfStatementSyntax>()) e.Checks.Add(Safe(condition.Condition.ToString()));
        if (method.DescendantNodes().OfType<LockStatementSyntax>().Any()) { e.Concepts.Add("concurrency"); e.Checks.Add("lock statement protects a synchronous critical section."); }
        if (method.DescendantNodes().OfType<TryStatementSyntax>().Any()) e.Checks.Add("Local exception handling is present; expand control flow for branches.");
        if (method.DescendantNodes().OfType<ThrowStatementSyntax>().Any()) e.Checks.Add("Explicit exception path.");
        var doc = Regex.Match(method.GetLeadingTrivia().ToFullString(), @"<summary>(.*?)</summary>", RegexOptions.Singleline);
        if (doc.Success) { e.Summary = Regex.Replace(doc.Groups[1].Value, @"///|<[^>]+>", "").Trim(); e.Status = "interpreted"; }
        var flow = new ControlFlow(id, method, model, path, snapshot.Files[path], target => SymbolId(target)).Build();
        flows[id] = flow;
    }
    async Task Packages(string path, string pid)
    {
        var xml = XDocument.Parse(await repo.Read(path));
        var versions = new Dictionary<string, string>();
        foreach (var central in snapshot.Files.Keys.Where(x => x.EndsWith("Directory.Packages.props")))
            foreach (var p in XDocument.Parse(await repo.Read(central)).Descendants("PackageVersion")) versions[p.Attribute("Include")?.Value ?? ""] = p.Attribute("Version")?.Value ?? "";
        foreach (var p in xml.Descendants("PackageReference"))
        {
            var name = p.Attribute("Include")?.Value;
            if (name is null) continue;
            var id = Format.Id("package:" + name);
            var version = p.Attribute("Version")?.Value ?? versions.GetValueOrDefault(name, "version unresolved");
            elements[id] = new Element { Id = id, Name = name, Layer = "stack", Kind = "package", Group = ".NET packages", Summary = "Declared dependency · " + version, Evidence = [new(path, 1, snapshot.Files[path])] };
            Link(pid, id, "depends on");
        }
    }
    async Task Frontend()
    {
        var script = Path.Combine(toolRoot, "analyzers", "frontend.mjs");
        var info = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = toolRoot };
        info.ArgumentList.Add(script);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { root = repo.Root, files = snapshot.Files }, Format.Json)); process.StandardInput.Close();
        await process.WaitForExitAsync();
        var err = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Frontend analyzer failed. Run npm ci in the tool directory. " + err[..Math.Min(err.Length, 300)]);
        var result = JsonSerializer.Deserialize<Snapshot>(await output, Format.Json)!;
        foreach (var e in result.Elements) elements[e.Id] = e;
        foreach (var r in result.Relations) relations[r.Id] = r;
        foreach (var w in result.Workflows) flows[w.EntryId] = w;
        snapshot.Diagnostics.AddRange(result.Diagnostics);
        foreach (var e in result.Elements.Where(x => x.Route is not null))
        {
            var matches = elements.Values.Where(x => x.Kind == "endpoint" && x.Route is not null && RouteMatches(x.Route, e.Route!) && (e.Verb is null || x.Verb == "ANY" || e.Verb == x.Verb)).ToList();
            foreach (var target in matches) Link(e.Id, target.Id, "requests", matches.Count == 1 ? "extracted" : "interpreted");
            if (matches.Count == 0) Gap(e.Id, "Request target is external, dynamic, or not matched to an extracted endpoint.");
            if (matches.Count > 1) Gap(e.Id, "Multiple route candidates; runtime routing requires verification.");
        }
    }
    static bool RouteMatches(string template, string route)
    {
        var clean = route.Split('?')[0].Trim('/');
        var pattern = "^" + Regex.Replace(Regex.Escape(template.Trim('/')), @"\\\{[^}]+}", "[^/]+") + "$";
        return Regex.IsMatch(clean, pattern, RegexOptions.IgnoreCase);
    }
    async Task StackManifests()
    {
        foreach (var path in snapshot.Files.Keys.Where(x => x.EndsWith("package.json") && !IsTest(x)))
        {
            using var document = JsonDocument.Parse(await repo.Read(path));
            foreach (var section in new[] { "dependencies", "devDependencies" })
            {
                if (!document.RootElement.TryGetProperty(section, out var packages) || packages.ValueKind != JsonValueKind.Object) continue;
                foreach (var package in packages.EnumerateObject())
                {
                    var id = Format.Id("npm:" + package.Name + ":" + path);
                    elements[id] = new Element { Id = id, Name = package.Name, Kind = "package", Layer = "stack", Group = section == "dependencies" ? "Frontend dependencies" : "Frontend tooling", Summary = "Declared npm dependency · " + package.Value.GetString(), Evidence = [new(path, 1, snapshot.Files[path])] };
                    var directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                    foreach (var module in elements.Values.Where(e => e.Kind == "module" && e.Evidence.Any(v => v.Path.StartsWith(directory + "/")))) Link(module.Id, id, "declares dependency");
                }
            }
        }
        foreach (var path in snapshot.Files.Keys.Where(x => x.EndsWith(".csproj") || x.EndsWith(".props")))
        {
            XDocument definition; try { definition = XDocument.Parse(await repo.Read(path)); } catch (System.Xml.XmlException) { continue; }
            foreach (var target in definition.Descendants().Where(x => x.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
            {
                if (string.IsNullOrWhiteSpace(target.Value)) continue;
                var id = Format.Id("framework:" + target.Value);
                elements[id] = new Element { Id = id, Name = target.Value, Kind = "framework", Layer = "stack", Group = ".NET platform", Summary = "Declared target framework.", Evidence = [new(path, 1, snapshot.Files[path])] };
            }
        }
    }
    async Task Infrastructure()
    {
        var services = new Dictionary<string, string> { { "Azure.Storage.Blobs", "Azure Blob Storage" }, { "EntityFrameworkCore.SqlServer", "SQL Server / Azure SQL candidate" }, { "CognitiveServices.Speech", "Azure Speech" }, { "Azure.Monitor", "Azure Monitor" }, { "AddAzureKeyVault", "Azure Key Vault" }, { "Microsoft.Web/sites", "Azure App Service" }, { "azure/webapps-deploy", "Azure App Service" }, { "ServiceBusClient", "Azure Service Bus" }, { "CosmosClient", "Azure Cosmos DB" } };
        foreach (var path in snapshot.Files.Keys.Where(x => !IsTest(x) && !x.Contains("/Migrations/")))
        {
            var source = await repo.Read(path);
            foreach (var (token, name) in services)
            {
                var index = source.IndexOf(token, StringComparison.Ordinal);
                if (index < 0) continue;
                var id = Format.Id("azure:" + name);
                if (!elements.TryGetValue(id, out var item)) elements[id] = item = new Element { Id = id, Name = name, Layer = "azure", Kind = "service", Group = "Repository infrastructure", Status = "interpreted", Summary = "Repository evidence indicates this dependency. Deployed resources and configuration are unverified." };
                item.Evidence.Add(new(path, source[..index].Count(c => c == '\n') + 1, snapshot.Files[path]));
                foreach (var owner in elements.Values.Where(e => e.Kind == "application" && path.StartsWith(Path.GetDirectoryName(e.Evidence[0].Path)?.Replace('\\', '/') ?? ""))) Link(owner.Id, id, "depends on", "interpreted");
            }
        }
    }
    void AddConcepts()
    {
        var concepts = new Dictionary<string, (string, string)> { { "async", ("Asynchronous operations", "async/await allows a method to suspend while an operation completes. It does not by itself establish parallelism or thread safety.") }, { "transactions", ("Transactions", "A transaction groups operations under a commit/rollback boundary. External service calls may require separate compensation.") }, { "concurrency", ("Concurrency", "Shared state can be accessed by overlapping operations. Correctness depends on synchronization scope, atomicity, and deployment topology.") }, { "idempotency", ("Idempotency", "Repeating an operation should not repeat its intended side effects. Look for durable keys and uniqueness constraints.") }, { "dependency-injection", ("Dependency injection", "Implementations are supplied through registrations and lifetimes. Static interface calls may have several runtime targets.") } };
        foreach (var (key, value) in concepts)
        {
            var id = Format.Id("concept:" + key);
            elements[id] = new Element { Id = id, Name = value.Item1, Kind = "concept", Layer = "stack", Group = "Concept library", Summary = value.Item2, Status = "interpreted" };
            foreach (var e in elements.Values.Where(e => e.Concepts.Contains(key)).ToList()) Link(e.Id, id, "explained by", "interpreted");
        }
    }
    void BuildWorkflows()
    {
        var adjacency = relations.Values.Where(r => r.Kind is "calls" or "may call" or "requests" or "handles").GroupBy(x => x.Source).ToDictionary(g => g.Key, g => g.Select(x => x.Target).Distinct().ToList());
        foreach (var entry in elements.Values.Where(x => x.EntryPoint).ToList())
        {
            var visited = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue(entry.Id);
            while (queue.TryDequeue(out var id)) { if (!visited.Add(id)) continue; if (adjacency.TryGetValue(id, out var targets)) foreach (var t in targets) queue.Enqueue(t); }
            var w = flows.GetValueOrDefault(entry.Id) ?? new Workflow { EntryId = entry.Id };
            w.Id = Format.Id("workflow:" + entry.Id); w.Name = entry.Name; w.Members = visited.ToList();
            foreach (var member in visited) if (unresolved.TryGetValue(member, out var gaps)) w.Gaps.AddRange(gaps);
            if (visited.Any(x => elements.TryGetValue(x, out var e) && e.Kind == "external")) w.Gaps.Add("External library/service internals terminate at an explicit boundary.");
            if (snapshot.Diagnostics.Any(x => x.Contains("compilation errors") || x.Contains("loading failed"))) w.Gaps.Add("Project diagnostics may affect semantic coverage.");
            if (entry.Kind == "endpoint") w.Gaps.Add("Global middleware, filters, and route conventions require agent review.");
            if (entry.Kind == "ui") w.Gaps.Add("DOM delegation, generated markup, and runtime event binding require review.");
            w.Gaps = w.Gaps.Distinct().ToList(); w.Coverage = w.Gaps.Count > 0 ? "partial" : "extracted";
            if (w.Steps.Count == 0) { w.Steps.Add(new(entry.Id, entry.Name, "entry", entry.Id, entry.Evidence.FirstOrDefault())); foreach (var member in visited.Where(x => x != entry.Id)) { w.Steps.Add(new(member, elements[member].Name, "call", member, elements[member].Evidence.FirstOrDefault())); } w.Edges.AddRange(relations.Values.Where(r => visited.Contains(r.Source) && visited.Contains(r.Target) && r.Kind is "calls" or "requests" or "handles" or "may call").Select(r => new FlowEdge(r.Source, r.Target, r.Kind))); }
            snapshot.Workflows.Add(w);
            foreach (var member in visited) Link(member, entry.Id, "appears in");
        }
        snapshot.Relations = relations.Values.Where(r => elements.ContainsKey(r.Source) && elements.ContainsKey(r.Target)).OrderBy(r => r.Id).ToList();
    }
}
