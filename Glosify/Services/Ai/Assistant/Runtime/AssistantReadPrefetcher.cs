using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>Only independent, declared read tools enter this path; each gets a fresh DI scope.</summary>
internal sealed class AssistantReadPrefetcher(IServiceScopeFactory scopes)
{
    internal async Task<IReadOnlyList<string>> ReadAsync(IReadOnlyList<AgentFunctionCall> calls,
        AgentToolContext context, CancellationToken ct)
    {
        if (calls.Count > 3) throw new ArgumentOutOfRangeException(nameof(calls));
        return await Task.WhenAll(calls.Select(async call =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var tools = scope.ServiceProvider.GetRequiredService<IAssistantTools>();
            var name = tools.ResolveCanonicalName(call.Name);
            if (name is null || ToolExecutionPolicy.For(name).Operation != "read")
                throw new InvalidOperationException("Only registered read tools can be prefetched.");
            // No shared DbContext or mutable pending-change list between reads.
            var copy = new AgentToolContext
            {
                UserId = context.UserId, QuizId = context.QuizId, CurrentLanguage = context.CurrentLanguage,
                CurrentLanguageCode = context.CurrentLanguageCode, SourceLanguage = context.SourceLanguage,
                IsFreestyle = context.IsFreestyle, FocusedWordId = context.FocusedWordId,
                FocusedWordLabel = context.FocusedWordLabel, TranscriptId = context.TranscriptId,
                BookDocumentId = context.BookDocumentId, RequestedContentKind = context.RequestedContentKind,
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(ToolExecutionPolicy.For(name).TimeoutSeconds));
            return RuntimeJson.Write(await tools.ExecuteAsync(call.Name, call.ArgsJson, copy, timeout.Token));
        }));
    }
}
