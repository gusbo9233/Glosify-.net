using Glosify.Services.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The mobile code travels through the custom scheme glosify://auth, which any application on
/// the device can register. These cover the PKCE binding that makes an intercepted code useless.
/// </summary>
public sealed class MobileAuthorizationCodeStoreTests
{
    private static readonly string Verifier = new('a', 43);

    [Fact]
    public void Code_is_single_use_and_bound_to_the_verifier()
    {
        var store = CreateStore(out _);
        var code = store.Create("user-1", Pkce.CreateChallenge(Verifier));

        Assert.True(store.TryRedeem(code, Verifier, out var userId));
        Assert.Equal("user-1", userId);
        Assert.False(store.TryRedeem(code, Verifier, out _));
    }

    [Fact]
    public void An_intercepted_code_without_the_verifier_is_useless()
    {
        var store = CreateStore(out _);
        var code = store.Create("user-1", Pkce.CreateChallenge(Verifier));

        // What a malicious app that grabbed the redirect would have: the code, not the verifier.
        Assert.False(store.TryRedeem(code, new string('b', 43), out var userId));
        Assert.Equal(string.Empty, userId);
    }

    [Fact]
    public void A_wrong_verifier_consumes_the_code_so_it_cannot_be_retried()
    {
        var store = CreateStore(out _);
        var code = store.Create("user-1", Pkce.CreateChallenge(Verifier));

        Assert.False(store.TryRedeem(code, new string('b', 43), out _));
        // Even the correct verifier fails now: guessing gets exactly one attempt.
        Assert.False(store.TryRedeem(code, Verifier, out _));
    }

    [Fact]
    public void Codes_expire()
    {
        var store = CreateStore(out var clock);
        var code = store.Create("user-1", Pkce.CreateChallenge(Verifier));

        clock.Advance(TimeSpan.FromMinutes(3));

        Assert.False(store.TryRedeem(code, Verifier, out _));
    }

    [Fact]
    public void A_missing_or_malformed_verifier_is_rejected()
    {
        var store = CreateStore(out _);
        var code = store.Create("user-1", Pkce.CreateChallenge(Verifier));

        Assert.False(store.TryRedeem(code, null, out _));
        Assert.False(store.TryRedeem(code, "too-short", out _));
    }

    [Fact]
    public void A_challenge_that_is_not_S256_shaped_is_refused_up_front()
    {
        var store = CreateStore(out _);

        Assert.Throws<ArgumentException>(() => store.Create("user-1", "not-a-challenge"));
    }

    [Fact]
    public void Issued_codes_are_unguessable_and_distinct()
    {
        var store = CreateStore(out _);
        var challenge = Pkce.CreateChallenge(Verifier);

        var codes = Enumerable.Range(0, 50).Select(_ => store.Create("user-1", challenge)).ToList();

        Assert.Equal(50, codes.Distinct(StringComparer.Ordinal).Count());
        // 256 bits base64url-encoded, so 43 characters with padding trimmed.
        Assert.All(codes, code => Assert.Equal(43, code.Length));
    }

    private static MobileAuthorizationCodeStore CreateStore(out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return new MobileAuthorizationCodeStore(new MemoryCache(new MemoryCacheOptions()), clock);
    }
}
