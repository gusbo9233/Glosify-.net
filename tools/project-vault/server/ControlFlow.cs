using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;
namespace ProjectVault;

// A syntax-backed control-flow view. Interprocedural calls link to their own pages;
// catch dispatch and runtime conditions remain explicitly unverified.
public sealed class ControlFlow(string entry, MethodDeclarationSyntax method, SemanticModel model, string path, string hash, Func<ISymbol, string> symbolId)
{
    readonly Workflow flow = new() { EntryId = entry };
    int sequence;
    Evidence Evidence(SyntaxNode node) => new(path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, hash);
    string Add(SyntaxNode node, string label, string kind, string? element = null)
    {
        var id = Format.Id(entry + ":" + sequence++);
        flow.Steps.Add(new(id, label, kind, element, Evidence(node))); return id;
    }
    void Connect(IEnumerable<string> from, string to, string label = "") { foreach (var f in from) flow.Edges.Add(new(f, to, label)); }
    string Label(SyntaxNode node) => Regex.Replace(node.ToString(), "\"(?:\\\\.|[^\"])*\"", "\"…\"");
    public Workflow Build()
    {
        var start = Add(method, "Start · " + method.Identifier.Text, "entry", entry);
        var tails = method.Body is not null ? Statements(method.Body.Statements, [start]) : Expression(method.ExpressionBody?.Expression, [start]);
        if (tails.Count > 0) { var end = Add(method, "Return / complete", "return"); Connect(tails, end); }
        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var left = assignment.Left.ToString();
            if (!Regex.IsMatch(left, @"(?:^|\.)(Status|State)$")) continue;
            var value = assignment.Right.ToString();
            if (!Regex.IsMatch(value, @"^(?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*$")) { flow.Gaps.Add("Computed state assignment cannot be resolved statically."); continue; }
            var from = Format.Id(entry + left + ":previous"); var to = Format.Id(entry + left + value);
            if (flow.States.All(s => s.Id != from)) flow.States.Add(new(from, left + " · prior value unknown", "unresolved", entry, Evidence(assignment)));
            if (flow.States.All(s => s.Id != to)) flow.States.Add(new(to, value, "state", entry, Evidence(assignment)));
            var condition = assignment.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
            flow.Transitions.Add(new(from, to, condition is null ? "assignment" : Label(condition.Condition) + " (branch context)"));
        }
        if (flow.States.Count > 0) flow.Gaps.Add("State view records assignments; previous values and transition guards require semantic review.");
        return flow;
    }
    List<string> Statements(IEnumerable<StatementSyntax> statements, List<string> incoming)
    {
        var tails = incoming;
        foreach (var s in statements) { if (tails.Count == 0) break; tails = Statement(s, tails); }
        return tails;
    }
    List<string> Statement(StatementSyntax node, List<string> incoming)
    {
        switch (node)
        {
            case BlockSyntax b: return Statements(b.Statements, incoming);
            case IfStatementSyntax condition:
                var conditionTails = Expression(condition.Condition, incoming);
                var decision = Add(condition.Condition, Label(condition.Condition), "decision"); Connect(conditionTails, decision);
                var yes = Add(condition, "True", "branch"); flow.Edges.Add(new(decision, yes, "true"));
                var no = Add(condition, "False", "branch"); flow.Edges.Add(new(decision, no, "false"));
                return Statement(condition.Statement, [yes]).Concat(condition.Else is null ? [no] : Statement(condition.Else.Statement, [no])).ToList();
            case ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                var header = Add(node, node.GetType().Name.Replace("StatementSyntax", "") + " · loop", "loop"); Connect(incoming, header);
                var body = node switch { ForStatementSyntax f => f.Statement, ForEachStatementSyntax f => f.Statement, WhileStatementSyntax w => w.Statement, DoStatementSyntax d => d.Statement, _ => throw new InvalidOperationException() };
                var enter = Add(node, "Iteration", "branch"); flow.Edges.Add(new(header, enter, "iterate")); Connect(Statement(body, [enter]), header, "repeat");
                flow.Gaps.Add("Loop header side effects, break/continue targets, and do/while first iteration require review."); return [header];
            case TryStatementSyntax attempt:
                var boundary = Add(node, "Try / exception boundary", "boundary"); Connect(incoming, boundary);
                var normal = Statement(attempt.Block, [boundary]);
                foreach (var handler in attempt.Catches) { var caught = Add(handler, "Catch " + (handler.Declaration?.Type.ToString() ?? "exception"), "exception"); flow.Edges.Add(new(boundary, caught, "exception (dispatch unverified)")); normal.AddRange(Statement(handler.Block, [caught])); }
                flow.Gaps.Add("Exception edges summarize a try region; thrown types and finally execution across terminal paths require review.");
                return attempt.Finally is null ? normal : Statement(attempt.Finally.Block, normal);
            case SwitchStatementSyntax sw:
                var select = Add(sw.Expression, "Switch · " + Label(sw.Expression), "decision"); Connect(Expression(sw.Expression, incoming), select);
                var exits = new List<string>(); foreach (var section in sw.Sections) { var branch = Add(section, string.Join(" / ", section.Labels.Select(Label)), "branch"); flow.Edges.Add(new(select, branch, "case")); exits.AddRange(Statements(section.Statements, [branch])); }
                return exits.Count > 0 ? exits : [select];
            case ReturnStatementSyntax ret:
                var returnTails = Expression(ret.Expression, incoming); var done = Add(ret, ret.Expression is null ? "Return" : "Return · " + (model.GetTypeInfo(ret.Expression).Type?.ToDisplayString() ?? "result"), "return"); Connect(returnTails, done); return [];
            case ThrowStatementSyntax thr:
                var thrown = Add(thr, "Throw · " + (thr.Expression is ObjectCreationExpressionSyntax obj ? obj.Type.ToString() : "exception"), "exception"); Connect(incoming, thrown); return [];
            case LockStatementSyntax locked:
                var gate = Add(locked, "lock · " + Label(locked.Expression), "boundary"); Connect(incoming, gate); return Statement(locked.Statement, [gate]);
            default: return Expression(node, incoming);
        }
    }
    List<string> Expression(SyntaxNode? node, List<string> incoming)
    {
        if (node is null) return incoming;
        var tails = incoming;
        // Innermost calls precede their containing call; sibling source order is retained.
        foreach (var call in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().OrderBy(c => c.Span.End).ThenBy(c => c.Span.Length))
        {
            var symbol = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
            var step = Add(call, symbol is null ? "Unresolved call" : symbol.ContainingType.Name + "." + symbol.Name, symbol is null ? "unresolved" : "call", symbol is null ? null : symbolId(symbol.ReducedFrom ?? symbol)); Connect(tails, step); tails = [step];
        }
        if (node.DescendantNodesAndSelf().Any(n => n is ConditionalExpressionSyntax or LambdaExpressionSyntax)) flow.Gaps.Add("Conditional expressions and deferred lambdas need review; invocation listing alone does not prove runtime order.");
        return tails;
    }
}
