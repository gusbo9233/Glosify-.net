using Azure.AI.Projects;
using Azure.Core;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The authored-agent cache lives for the process lifetime, so what it stores matters more
/// than that it stores anything. A cached failure would silently drop an assistant profile
/// to in-code instructions until the app restarts.
/// </summary>
public sealed class FoundryAgentInvokerCacheTests
{
    [Fact]
    public async Task SuccessfulLookupIsCached()
    {
        var invoker = CreateInvoker();
        var calls = 0;
        var agent = new FoundryAuthoredAgent("Build quiz elements.", "gpt-5.6-luna", []);

        Task<FoundryAuthoredAgent?> Fetch(CancellationToken _)
        {
            calls++;
            return Task.FromResult<FoundryAuthoredAgent?>(agent);
        }

        var first = await invoker.GetOrAddAuthoredAgentAsync("agent@1", Fetch, CancellationToken.None);
        var second = await invoker.GetOrAddAuthoredAgentAsync("agent@1", Fetch, CancellationToken.None);

        Assert.Same(agent, first);
        Assert.Same(agent, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NonPromptAgentIsCached()
    {
        var invoker = CreateInvoker();
        var calls = 0;

        Task<FoundryAuthoredAgent?> Fetch(CancellationToken _)
        {
            calls++;
            return Task.FromResult<FoundryAuthoredAgent?>(null);
        }

        Assert.Null(await invoker.GetOrAddAuthoredAgentAsync("agent@1", Fetch, CancellationToken.None));
        Assert.Null(await invoker.GetOrAddAuthoredAgentAsync("agent@1", Fetch, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedLookupIsRetriedOnTheNextTurn()
    {
        var invoker = CreateInvoker();
        var calls = 0;
        var agent = new FoundryAuthoredAgent("Build quiz elements.", null, []);

        Task<FoundryAuthoredAgent?> Fetch(CancellationToken _)
        {
            calls++;
            return calls == 1
                ? throw new InvalidOperationException("Foundry was unreachable.")
                : Task.FromResult<FoundryAuthoredAgent?>(agent);
        }

        var degraded = await invoker.GetOrAddAuthoredAgentAsync("agent@1", Fetch, CancellationToken.None);
        var recovered = await invoker.GetOrAddAuthoredAgentAsync("agent@1", Fetch, CancellationToken.None);

        Assert.Null(degraded);
        Assert.Same(agent, recovered);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationPropagatesAndIsNotCached()
    {
        var invoker = CreateInvoker();
        var calls = 0;
        var agent = new FoundryAuthoredAgent("Build quiz elements.", null, []);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Task<FoundryAuthoredAgent?> Fetch(CancellationToken token)
        {
            calls++;
            token.ThrowIfCancellationRequested();
            return Task.FromResult<FoundryAuthoredAgent?>(agent);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.GetOrAddAuthoredAgentAsync("agent@1", Fetch, cancelled.Token));

        // A user cancelling their first assistant turn must not poison the profile.
        var recovered = await invoker.GetOrAddAuthoredAgentAsync("agent@1", Fetch, CancellationToken.None);

        Assert.Same(agent, recovered);
        Assert.Equal(2, calls);
    }

    private static FoundryAgentInvoker CreateInvoker() =>
        new(
            new AIProjectClient(
                new Uri("https://glosify-tests.services.ai.azure.com/api/projects/tests"),
                new StubCredential()),
            NullLoggerFactory.Instance);

    /// <summary>The project client is never called: every test supplies its own fetch.</summary>
    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("stub", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
