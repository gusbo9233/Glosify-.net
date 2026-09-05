using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace ProjectVault;

/// <summary>Focused syntax lookup, not a project index. Identities include namespace, containing types and overload syntax.</summary>
public sealed class Declarations(Repository repo)
{
    static string Clean(SyntaxNode? node) => node?.WithoutTrivia().NormalizeWhitespace().ToFullString() ?? "";
    static string TypeName(TypeDeclarationSyntax type) => type.Identifier.ValueText + (type.TypeParameterList is null ? "" : "`" + type.TypeParameterList.Parameters.Count);
    static string Container(SyntaxNode node) => string.Join('.', node.Ancestors().Reverse().Select(a => a switch
    {
        BaseNamespaceDeclarationSyntax n => Clean(n.Name),
        TypeDeclarationSyntax t => TypeName(t),
        _ => ""
    }).Where(x => x.Length > 0));
    static string Parameters(ParameterListSyntax p) => string.Join(',', p.Parameters.Select(x => string.Join(' ', x.Modifiers.Select(m => m.ValueText).Where(m => m is "ref" or "out" or "in").Append(Clean(x.Type)))));
    static string? Identity(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => "method:" + Container(m) + "." + (m.ExplicitInterfaceSpecifier is null ? "" : Clean(m.ExplicitInterfaceSpecifier.Name) + ".") + m.Identifier.ValueText + (m.TypeParameterList is null ? "" : "`" + m.TypeParameterList.Parameters.Count) + "(" + Parameters(m.ParameterList) + ")",
        ConstructorDeclarationSyntax c => "constructor:" + Container(c) + "(" + Parameters(c.ParameterList) + ")",
        TypeDeclarationSyntax t => "type:" + (Container(t) is { Length: > 0 } scope ? scope + "." : "") + TypeName(t),
        EnumDeclarationSyntax e => "type:" + (Container(e) is { Length: > 0 } scope ? scope + "." : "") + e.Identifier.ValueText,
        _ => null
    };
    public async Task<List<SourceBinding>> Find(string path)
    {
        if (System.IO.Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(x => x is ".." or ".") || !Repository.Included(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Declaration lookup requires a repository-relative C# file.");
        var absolute = repo.Absolute(path); if (!File.Exists(absolute)) return [];
        var text = await repo.Read(path); var tree = CSharpSyntaxTree.ParseText(text); var root = await tree.GetRootAsync(); var hash = Format.Id(text);
        return root.DescendantNodes().Select(n => (Node: n, Id: Identity(n))).Where(n => n.Id is not null).Select(n =>
        {
            var span = tree.GetLineSpan(n.Node.Span);
            return new SourceBinding(path, n.Id!, hash, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, text.Substring(n.Node.SpanStart, n.Node.Span.Length));
        }).ToList();
    }
    public async Task<object> List(string path) => (await Find(path)).Select(s => new { s.Path, s.DeclarationId, s.Line, s.EndLine, s.Hash }).ToList();
    public async Task<SourceBinding> Resolve(string path, string declarationId)
    {
        var matches = (await Find(path)).Where(d => d.DeclarationId == declarationId).ToList();
        if (matches.Count != 1) throw new ArgumentException(matches.Count == 0 ? "Declaration is missing or renamed. Reconcile its identity; no replacement was selected." : "Declaration is ambiguous. Refine the source; no candidate was selected.");
        return matches[0];
    }
    public async Task Validate(SourceBinding source)
    {
        var current = await Resolve(source.Path, source.DeclarationId);
        if (current != source) throw new InvalidOperationException("Primary source differs from the current declaration. Use vault_declaration and review its full code before publishing.");
    }
}
