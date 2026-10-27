using Glosify.Services.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AuthorizationCodeConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverlappingRedemptionsHaveExactlyOneWinner(bool extension)
    {
        const string redirect = "https://abcdefghijklmnop.chromiumapp.org/glosify";
        var verifier = new string('a', 43);
        using var cache = new OverlappingReadCache();
        var extensions = new ExtensionAuthorizationCodeStore(cache,
            Options.Create(new ExtensionAuthOptions { AllowedRedirectUris = [redirect] }), TimeProvider.System);
        var mobile = new MobileAuthorizationCodeStore(cache, TimeProvider.System);
        var challenge = Pkce.CreateChallenge(verifier);
        var code = extension ? extensions.Create("user", redirect, challenge) : mobile.Create("user", challenge);
        (bool Success, string UserId) Redeem()
        {
            var success = extension
                ? extensions.TryRedeem(code, redirect, verifier, out var userId)
                : mobile.TryRedeem(code, verifier, out userId);
            return (success, userId);
        }

        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Factory.StartNew(Redeem,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("user", Assert.Single(results, result => result.Success).UserId);
        Assert.Equal(string.Empty, Assert.Single(results, result => !result.Success).UserId);
        Assert.False(Redeem().Success);
    }

    private sealed class OverlappingReadCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(new MemoryCacheOptions());
        private readonly Barrier _reads = new(2);

        public bool TryGetValue(object key, out object? value)
        {
            var found = _inner.TryGetValue(key, out value);
            // Both callers hold the entry before either can consume or remove it.
            if (found && !_reads.SignalAndWait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Concurrent redemption did not read the entry.");
            return found;
        }

        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);
        public void Remove(object key) => _inner.Remove(key);
        public void Dispose() { _inner.Dispose(); _reads.Dispose(); }
    }
}
