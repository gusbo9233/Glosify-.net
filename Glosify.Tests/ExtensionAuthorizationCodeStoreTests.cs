using Glosify.Services.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class ExtensionAuthorizationCodeStoreTests
{
    private const string RedirectUri = "https://abcdefghijklmnop.chromiumapp.org/glosify";

    [Fact]
    public void Code_IsSingleUseAndBoundToPkceAndRedirectUri()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateStore(cache, clock);
        var verifier = new string('a', 43);
        var code = store.Create(
            "user-1",
            RedirectUri,
            ExtensionAuthorizationCodeStore.CreateCodeChallenge(verifier));

        Assert.True(store.TryRedeem(code, RedirectUri, verifier, out var userId));
        Assert.Equal("user-1", userId);
        Assert.False(store.TryRedeem(code, RedirectUri, verifier, out _));
    }

    [Fact]
    public void WrongVerifier_ConsumesTheCode()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateStore(cache, clock);
        var verifier = new string('a', 43);
        var code = store.Create(
            "user-1",
            RedirectUri,
            ExtensionAuthorizationCodeStore.CreateCodeChallenge(verifier));

        Assert.False(store.TryRedeem(code, RedirectUri, new string('b', 43), out _));
        Assert.False(store.TryRedeem(code, RedirectUri, verifier, out _));
    }

    [Fact]
    public void ExpiredCode_CannotBeRedeemed()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateStore(cache, clock);
        var verifier = new string('a', 43);
        var code = store.Create(
            "user-1",
            RedirectUri,
            ExtensionAuthorizationCodeStore.CreateCodeChallenge(verifier));
        clock.Advance(TimeSpan.FromMinutes(3));

        Assert.False(store.TryRedeem(code, RedirectUri, verifier, out _));
    }

    private static ExtensionAuthorizationCodeStore CreateStore(IMemoryCache cache, TimeProvider clock) =>
        new(
            cache,
            Options.Create(new ExtensionAuthOptions
            {
                AuthorizationCodeLifetimeSeconds = 120,
                AllowedRedirectUris = [RedirectUri],
            }),
            clock);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
