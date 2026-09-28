using System.Collections.Concurrent;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantReadPrefetcherTests
{
    [Fact]
    public async Task Independent_reads_use_three_distinct_scopes_and_no_shared_mutable_context()
    {
        var tracker = new Tracker();
        await using var provider = new ServiceCollection().AddSingleton(tracker)
            .AddScoped<IAssistantTools, ReadTools>().BuildServiceProvider();
        var prefetcher = new AssistantReadPrefetcher(provider.GetRequiredService<IServiceScopeFactory>());
        var context = new AgentToolContext { UserId = "owner", QuizId = Guid.NewGuid() };
        var results = await prefetcher.ReadAsync([
            new("get_word", "{}"), new("list_words", "{}"), new("list_sentences", "{}"),
        ], context, default);
        Assert.Equal(3, results.Count);
        Assert.Equal(3, tracker.ScopeIds.Distinct().Count());
        Assert.Equal(3, tracker.Contexts.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(tracker.Contexts, copy => { Assert.Equal(context.UserId, copy.UserId); Assert.NotSame(context, copy); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => prefetcher.ReadAsync([new("delete_word", "{}")], context, default));
    }
    private sealed class Tracker
    {
        internal ConcurrentBag<Guid> ScopeIds { get; } = [];
        internal ConcurrentBag<AgentToolContext> Contexts { get; } = [];
        internal TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ReadTools(Tracker tracker) : IAssistantTools
    {
        private readonly Guid id = Guid.NewGuid();
        public IReadOnlyList<AgentToolDeclaration> Declarations => [];
        public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations => [];
        public IReadOnlyList<AgentToolDeclaration> QuizAssistantDeclarations => [];
        public IReadOnlyList<AgentToolDeclaration> LibrarianDeclarations => [];
        public string? ResolveCanonicalName(string name) => name;
        public async Task<object> ExecuteAsync(string name, string argsJson, AgentToolContext context, CancellationToken ct)
        {
            tracker.ScopeIds.Add(id); tracker.Contexts.Add(context);
            if (tracker.ScopeIds.Count == 3) tracker.AllEntered.TrySetResult();
            await tracker.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return new { name, scope = id };
        }
    }
}
