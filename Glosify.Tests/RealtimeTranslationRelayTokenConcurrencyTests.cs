using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationRelayTokenConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRedemption_ConsumesGrantEvenWhenFirstRequestHasWrongSession(bool wrongSessionFirst)
    {
        using var cache = new OverlappingReadCache();
        var store = new RealtimeTranslationRelayTokenStore(
            cache, Options.Create(new RealtimeTranslationOptions()), TimeProvider.System);
        var sessionId = Guid.NewGuid();
        var grant = store.Create(sessionId, "user-1", "sv", RealtimeTranslationModes.Enhanced,
            RealtimeSpeechProviders.OpenAi, "en", false, null);

        // Hold both cache reads before either redemption can remove the entry.
        // Then let the first attempt finish before the second returns its stale reference.
        var first = Task.Factory.StartNew(() =>
        {
            try
            {
                var success = store.TryRedeem(wrongSessionFirst ? Guid.NewGuid() : sessionId,
                    grant.Token, out var authorization);
                return (success, authorization);
            }
            finally
            {
                cache.ReleaseSecondRead();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await cache.FirstRead.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Task.Factory.StartNew(() =>
        {
            var success = store.TryRedeem(sessionId, grant.Token, out var authorization);
            return (success, authorization);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, cache.SuccessfulReads);
        Assert.Equal(!wrongSessionFirst, results[0].success);
        if (wrongSessionFirst)
            Assert.Null(results[0].authorization);
        else
        {
            Assert.Equal(sessionId, results[0].authorization.SessionId);
            Assert.Equal("user-1", results[0].authorization.UserId);
            Assert.Equal("sv", results[0].authorization.TargetLanguage);
            Assert.Equal("en", results[0].authorization.SourceLanguage);
        }
        Assert.False(results[1].success);
        Assert.Null(results[1].authorization);
        Assert.False(store.TryRedeem(sessionId, grant.Token, out _));

        var independentSession = Guid.NewGuid();
        var independentGrant = store.Create(independentSession, "user-2", "pl", RealtimeTranslationModes.Enhanced,
            RealtimeSpeechProviders.OpenAi, null, false, null);
        Assert.True(store.TryRedeem(independentSession, independentGrant.Token, out var independentAuthorization));
        Assert.Equal("user-2", independentAuthorization.UserId);
        Assert.Equal("pl", independentAuthorization.TargetLanguage);
    }

    private sealed class OverlappingReadCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(new MemoryCacheOptions());
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _secondRead = new();
        private readonly ManualResetEventSlim _releaseSecond = new();
        private int _successfulReads;

        public Task FirstRead => _firstRead.Task;
        public int SuccessfulReads => Volatile.Read(ref _successfulReads);

        public bool TryGetValue(object key, out object? value)
        {
            var found = _inner.TryGetValue(key, out value);
            if (!found) return false;
            switch (Interlocked.Increment(ref _successfulReads))
            {
                case 1:
                    _firstRead.SetResult();
                    if (!_secondRead.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Second redemption did not read the cached grant.");
                    break;
                case 2:
                    _secondRead.Set();
                    if (!_releaseSecond.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("First redemption did not finish.");
                    break;
            }
            return found;
        }

        public void ReleaseSecondRead() => _releaseSecond.Set();
        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);
        public void Remove(object key) => _inner.Remove(key);
        public void Dispose()
        {
            _inner.Dispose();
            _secondRead.Dispose();
            _releaseSecond.Dispose();
        }
    }
}
