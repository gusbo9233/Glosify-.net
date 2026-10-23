using System.Net;
using System.Text;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace Glosify.Tests;

public sealed class CloudflareSubtitleTranslatorTests
{
    [Fact]
    public async Task TranslateAsync_SendsCodesAndBearerTokenToConfiguredWorker()
    {
        var handler = new RecordingHandler("""{"translated":"Bonjour."}""");
        var translator = CreateTranslator(handler);
        var capturedAt = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

        var result = await translator.TranslateAsync(
            new RecognizedSpeechSegment(3, "Hello.", "en-US", "en-US", capturedAt),
            "fr",
            CancellationToken.None);

        Assert.Equal("Bonjour.", result.TranslatedText);
        Assert.True(result.ProviderRequest);
        Assert.Equal("https://glosify-test.workers.dev/translate", handler.Uri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-secret", handler.AuthorizationParameter);
        Assert.Contains("\"source_lang\":\"en\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"target_lang\":\"fr\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_SkipsWorkerForTheSameNormalizedLanguage()
    {
        var handler = new RecordingHandler("""{"translated":"unused"}""");
        var translator = CreateTranslator(handler);

        var result = await translator.TranslateAsync(
            new RecognizedSpeechSegment(1, "Hei", "nb-NO", "nb-NO", DateTimeOffset.UtcNow),
            "no",
            CancellationToken.None);

        Assert.Equal("Hei", result.TranslatedText);
        Assert.False(result.ProviderRequest);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TranslateAsync_WaitsForDetectedLanguageBeforeCallingWorker()
    {
        var handler = new RecordingHandler("""{"translated":"Bonjour."}""");
        var translator = CreateTranslator(handler);

        await Assert.ThrowsAsync<RealtimeTranslationUpstreamException>(() =>
            translator.TranslateAsync(
                new RecognizedSpeechSegment(
                    1,
                    "Hello.",
                    "auto",
                    "auto",
                    DateTimeOffset.UtcNow,
                    IsAutoDetected: true,
                    IsFinal: false),
                "fr",
                CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void SplitIntoBoundedChunks_PrefersSentenceBoundariesAndPreservesText()
    {
        var chunks = CloudflareSubtitleTranslator.SplitIntoBoundedChunks(
            "First sentence. Second sentence is longer.",
            20);

        Assert.Equal(["First sentence.", "Second sentence is", "longer."], chunks);
        Assert.Equal(
            "First sentence. Second sentence is longer.",
            string.Join(' ', chunks));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 20));
    }

    [Fact]
    public void SplitIntoSentenceBoundedChunks_KeepsSentencesWholeNearPreferredSize()
    {
        var chunks = CloudflareSubtitleTranslator.SplitIntoSentenceBoundedChunks(
            "First sentence is short. Second sentence is also short. "
                + "One deliberately longer sentence remains whole even when it exceeds the preference.",
            preferredCharacters: 50,
            maximumCharacters: 100);

        Assert.Equal(
            [
                "First sentence is short.",
                "Second sentence is also short.",
                "One deliberately longer sentence remains whole even when it exceeds the preference.",
            ],
            chunks);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 100));
    }

    [Fact]
    public async Task TranslateAsync_TranslatesSentenceChunksInParallelAndPreservesOrder()
    {
        var handler = new ConcurrentRecordingHandler();
        var translator = CreateTranslator(
            handler,
            preferredChunkCharacters: 20,
            maxParallelRequests: 2);

        var result = await translator.TranslateAsync(
            new RecognizedSpeechSegment(
                1,
                "First sentence. Second sentence. Third sentence.",
                "en",
                "en-US",
                DateTimeOffset.UtcNow),
            "sv",
            CancellationToken.None);

        Assert.Equal("translated:First sentence. translated:Second sentence. translated:Third sentence.", result.TranslatedText);
        Assert.Equal(2, handler.MaximumConcurrency);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("circuit")]
    public async Task TranslateAsync_ConvertsResilienceRejectionsToUpstreamFailures(
        string rejection)
    {
        var exception = rejection == "timeout"
            ? (Exception)new TimeoutRejectedException()
            : new BrokenCircuitException();
        var translator = CreateTranslator(new ThrowingHandler(exception));

        var result = await Assert.ThrowsAsync<RealtimeTranslationUpstreamException>(() =>
            translator.TranslateAsync(
                new RecognizedSpeechSegment(
                    1,
                    "Hello.",
                    "en",
                    "en-US",
                    DateTimeOffset.UtcNow),
                "fr",
                CancellationToken.None));

        Assert.Contains("temporarily unavailable", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranslateAsync_BoundsBodyReadAndPreservesCallerCancellation(bool cancelCaller)
    {
        using var body = new BlockedStream();
        using var handler = new HeaderOnlyHandler(body);
        var translator = CreateTranslator(handler, timeoutSeconds: 1);
        using var caller = new CancellationTokenSource();
        var translation = translator.TranslateAsync(
            new RecognizedSpeechSegment(1, "Hello.", "en", "en-US", DateTimeOffset.UtcNow),
            "sv", caller.Token);
        try
        {
            await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelCaller)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => translation.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                var error = await Assert.ThrowsAsync<RealtimeTranslationUpstreamException>(
                    () => translation.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
                Assert.False(caller.IsCancellationRequested);
            }
        }
        finally
        {
            await caller.CancelAsync();
            try { await translation; }
            catch (OperationCanceledException) { }
            catch (RealtimeTranslationUpstreamException) { }
        }
    }

    private sealed class HeaderOnlyHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }
    private sealed class BlockedStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static CloudflareSubtitleTranslator CreateTranslator(
        HttpMessageHandler handler,
        int preferredChunkCharacters = 240,
        int maxParallelRequests = 4,
        int timeoutSeconds = 20) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(new RealtimeTranslationOptions
            {
                Cloudflare = new CloudflareRealtimeTranslationOptions
                {
                    Enabled = true,
                    Endpoint = "https://glosify-test.workers.dev/translate",
                    ApiToken = "test-secret",
                    MaxInputCharacters = 2_000,
                    PreferredChunkCharacters = preferredChunkCharacters,
                    MaxParallelRequests = maxParallelRequests,
                    TimeoutSeconds = timeoutSeconds,
                },
            }));

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class ConcurrentRecordingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstBatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _concurrency;
        private int _maximumConcurrency;

        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var concurrency = Interlocked.Increment(ref _concurrency);
            var maximum = Volatile.Read(ref _maximumConcurrency);
            while (concurrency > maximum)
            {
                maximum = Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    concurrency,
                    maximum);
            }
            try
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(body);
                var text = document.RootElement.GetProperty("text").GetString();
                // Hold the first request until its batch partner arrives instead of
                // assuming the runner schedules both within a fixed delay.
                if (concurrency >= 2) _firstBatchStarted.TrySetResult();
                await _firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"translated":"translated:{{text}}"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }
}
