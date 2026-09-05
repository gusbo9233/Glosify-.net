using Microsoft.Build.Locator;
using ProjectVault;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;

MSBuildLocator.RegisterDefaults();
string? Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var command = args.FirstOrDefault() ?? "serve";
var root = Option("--repo") ?? Directory.GetCurrentDirectory();
var toolRoot = Option("--tool-root") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
Format.AnalyzerPath = Path.Combine(toolRoot, "analyzers/frontend.mjs");
var repository = new Repository(root); var vault = new Vault(repository, toolRoot); var documents = new Documents(repository, toolRoot); var declarations = new Declarations(repository);
if (command is "refresh" or "status" or "check" or "mcp" or "document-status" or "document-check")
{
    if (command == "mcp") { await Mcp.Run(vault, documents, declarations); return; }
    try
    {
        if (command is "document-status" or "document-check") { var status = JsonSerializer.SerializeToElement(await documents.Status(), Format.Json); Console.WriteLine(status); if (command == "document-check" && !status.GetProperty("fresh").GetBoolean()) Environment.ExitCode = 2; }
        else if (command == "refresh") { var snapshot = await vault.Refresh(args.Contains("--force")); Console.WriteLine(JsonSerializer.Serialize(new { snapshot.Id, elements = snapshot.Elements.Count, workflows = snapshot.Workflows.Count, diagnostics = snapshot.Diagnostics }, Format.Json)); }
        else { var status = await vault.Status(); Console.WriteLine(JsonSerializer.Serialize(status, Format.Json)); if (command == "check" && !status.Fresh) Environment.ExitCode = 2; }
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    return;
}
await documents.Initialize();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], WebRootPath = Path.Combine(toolRoot, "server/wwwroot") });
builder.WebHost.UseUrls("http://127.0.0.1:" + (Option("--port") ?? "5188"));
builder.Services.AddProblemDetails(); builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
var app = builder.Build();
app.Use(async (context, next) =>
{
    // Loopback binding plus host/origin checks protect repository writes from browser cross-origin requests.
    var host = context.Request.Host.Host;
    if (host is not ("localhost" or "127.0.0.1")) { context.Response.StatusCode = 403; return; }
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != "http://127.0.0.1:5178" && origin != "http://localhost:5178" && origin != $"http://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
        if (context.Request.Headers["X-Project-Vault"] != "local") { context.Response.StatusCode = 403; return; }
    }
    try { await next(context); } catch (ArgumentException ex) { await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(context); } catch (InvalidOperationException ex) { await Results.Problem(ex.Message, statusCode: 409).ExecuteAsync(context); } catch (Exception) { await Results.Problem("Operation failed. The last valid map was retained; inspect synchronization status.", statusCode: 500).ExecuteAsync(context); }
});
app.MapGet("/api/documents", documents.Library);
app.MapGet("/api/document-status", documents.Status);
app.MapGet("/api/documents/{id}", documents.Get);
app.MapGet("/api/documents/{id}/backlinks", documents.Backlinks);
app.MapGet("/api/documents/{id}/source", (string id, int? version) => documents.BoundSource(id, version));
app.MapPost("/api/documents", documents.Save);
app.MapPost("/api/document-reviews", documents.Review);
app.MapGet("/api/document-requests", documents.Requests);
app.MapPost("/api/document-requests", documents.SaveRequest);
app.MapGet("/api/documents/{id}/notes", (string id) => documents.Notes(id));
app.MapPost("/api/document-notes", documents.SaveNote);
app.MapGet("/api/documents/{id}/presentation", documents.Presentation);
app.MapPut("/api/documents/{id}/presentation", documents.SavePresentation);
app.MapGet("/api/source-file", async (string path) => { await declarations.Find(path); return Results.Text(await repository.Read(path), "text/plain"); });
app.MapGet("/api/document-source", (string path, int? line, int? count) => documents.Source(path, line ?? 1, count ?? 80));
app.MapGet("/api/snapshot", async () => Results.Ok(await vault.Current()));
app.MapGet("/api/status", vault.Status);
app.MapPost("/api/refresh", async () => { var s = await vault.Refresh(); return Results.Ok(new { s.Id, elements = s.Elements.Count, workflows = s.Workflows.Count }); });
app.MapGet("/api/elements/{id}", vault.Element);
app.MapGet("/api/notes", vault.Notes);
app.MapPut("/api/notes/{id}", async (string id, Annotation value) => { if (value.ElementId != id) return Results.BadRequest(); await vault.SaveNote(value); return Results.NoContent(); });
app.MapPut("/api/interpretations/{id}", async (string id, Annotation value) => { if (value.ElementId != id) return Results.BadRequest(); await vault.SaveInterpretation(value); return Results.NoContent(); });
app.MapGet("/api/layout", vault.GetLayout);
app.MapPut("/api/layout", async (Layout value) => { await vault.SaveLayout(value); return Results.NoContent(); });
app.MapGet("/api/proposals", vault.Proposals);
app.MapPost("/api/proposals", vault.SaveProposal);
app.MapGet("/api/compare/{id}", (string id) => vault.Compare(id));
app.UseDefaultFiles(); app.UseStaticFiles(); app.MapFallbackToFile("index.html");
await app.RunAsync();
