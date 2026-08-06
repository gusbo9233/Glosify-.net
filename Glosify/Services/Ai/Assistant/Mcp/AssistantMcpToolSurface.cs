using System.Text.Json;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Mcp;

/// <summary>
/// Which assistant tools an agent may call over MCP. Tool handlers still run inside
/// Glosify against the calling user's rows; only the decision to call them moves to the
/// agent in Foundry.
/// </summary>
public interface IAssistantMcpToolSurface
{
    IReadOnlyList<AgentToolDeclaration> Declarations { get; }
    bool Allows(string toolName);

    Task<object> ExecuteAsync(
        string toolName,
        string argumentsJson,
        AssistantMcpSession session,
        CancellationToken cancellationToken);
}

public sealed class AssistantMcpToolSurface : IAssistantMcpToolSurface
{
    private readonly IAssistantTools _tools;
    private readonly IAssistantPendingChangeStore _pendingChanges;
    private readonly HashSet<string> _allowed;

    public AssistantMcpToolSurface(
        IAssistantTools tools,
        IAssistantPendingChangeStore pendingChanges)
    {
        _tools = tools;
        _pendingChanges = pendingChanges;
        // The quiz-builder profile's tools, minus nothing: its set already excludes
        // everything that could create a quiz and strand the open editor.
        Declarations = tools.CustomQuizBuilderDeclarations;
        _allowed = Declarations.Select(declaration => declaration.Name).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<AgentToolDeclaration> Declarations { get; }

    public bool Allows(string toolName) => _allowed.Contains(toolName);

    public async Task<object> ExecuteAsync(
        string toolName,
        string argumentsJson,
        AssistantMcpSession session,
        CancellationToken cancellationToken)
    {
        if (!Allows(toolName))
        {
            return new { error = $"Tool {toolName} is not available over MCP." };
        }

        var context = new AgentToolContext
        {
            UserId = session.UserId,
            QuizId = session.QuizId,
            CustomQuizId = session.CustomQuizId,
            TranscriptId = session.TranscriptId,
            FocusedWordId = session.FocusedWordId,
            CurrentLanguage = session.CurrentLanguage,
            CurrentLanguageCode = session.CurrentLanguageCode,
        };

        // Each MCP call gets a fresh context, but the tools check what a turn has already
        // queued — duplicate answer labels, for one. Priming the context with the
        // conversation's stored changes keeps those checks working across calls.
        var alreadyQueued = await _pendingChanges.ListPendingAsync(
            session.ConversationId,
            session.UserId,
            cancellationToken);
        context.PendingChanges.AddRange(alreadyQueued);
        var queuedBefore = context.PendingChanges.Count;

        var result = await _tools.ExecuteAsync(
            toolName,
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
            context,
            cancellationToken);

        foreach (var change in context.PendingChanges.Skip(queuedBefore))
        {
            await _pendingChanges.AppendAsync(
                session.ConversationId,
                session.UserId,
                session.QuizId,
                change,
                cancellationToken);
        }

        return result;
    }

    public static string SerializeResult(object result) =>
        JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
