using System.Net;
using System.Text.Json;
using Glosify.Services.Ai;
using Glosify.Services.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class ElevenLabsTextToSpeechServiceTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void CharacterPricesAreValidated(decimal price, bool valid)
    {
        var options = new AiUsageOptions { MonthlyBudget = new() { Models = [new() { Deployment = "eleven_v3", TextSekPerMillionCharacters = price }] } };
        Assert.Equal(valid, new AiUsageOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public async Task V3UsesServerKeyAllowlistedVoiceAndLanguage_AndCachesAudio()
    {
        using var h = new Harness();
        await using var first = await h.Service.GetOrSynthesizeAsync("Tere", "et-EE");
        await using var second = await h.Service.GetOrSynthesizeAsync("Tere", "et-EE");
        Assert.Equal(new byte[] { 73, 68, 51, 1, 2, 3 }, await Read(second));
        var request = Assert.Single(h.Handler.Requests);
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/JBFqnCBsd6RMkjVDRZzb?output_format=mp3_44100_128", request.Url);
        Assert.Equal("test-key", request.Key);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("eleven_v3", body.RootElement.GetProperty("model_id").GetString());
        Assert.Equal("et", body.RootElement.GetProperty("language_code").GetString());
        Assert.Equal("Tere", body.RootElement.GetProperty("text").GetString());
        Assert.Equal(4, Assert.Single(h.Budget.Characters));
        Assert.True(Assert.Single(h.Budget.Charges));
    }

    [Fact]
    public async Task IdenticalConcurrentRequestsShareProviderWork_AndEvictionRegenerates()
    {
        using var h = new Harness();
        h.Handler.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = h.Service.GetOrSynthesizeAsync("Hej", "sv-SE");
        var second = h.Service.GetOrSynthesizeAsync("Hej", "sv-SE");
        h.Handler.Gate.SetResult();
        var streams = await Task.WhenAll(first, second);
        foreach (var stream in streams) await stream.DisposeAsync();
        Assert.Single(h.Handler.Requests);
        h.Cache.Audio.Compact(1);
        await using var third = await h.Service.GetOrSynthesizeAsync("Hej", "sv-SE");
        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.Equal(2, h.Budget.Characters.Count);
    }

    [Theory]
    [InlineData(429, false)]
    [InlineData(500, true)]
    public async Task FailedRequestsAreNotRetried_AndUnknownOutcomesRetainCost(int status, bool charged)
    {
        using var h = new Harness(); h.Handler.Status = (HttpStatusCode)status;
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Service.GetOrSynthesizeAsync("Hej", "sv"));
        Assert.Single(h.Handler.Requests);
        Assert.Equal(charged, Assert.Single(h.Budget.Charges));
    }

    [Fact]
    public async Task InvalidVoiceLanguageAndOversizedTextNeverReachProvider()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.GetOrSynthesizeAsync("Hej", "sv", voicePreference: "unapproved"));
        await Assert.ThrowsAsync<NotSupportedException>(() => h.Service.GetOrSynthesizeAsync("Hej", "yue-HK"));
        await Assert.ThrowsAsync<NotSupportedException>(() => h.Service.GetOrSynthesizeAsync("Hej", "zh-HK"));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.GetOrSynthesizeAsync(new string('x', 201), "sv"));
        Assert.Empty(h.Handler.Requests); Assert.Empty(h.Budget.Characters);
    }

    private static async Task<byte[]> Read(Stream stream) { using var result = new MemoryStream(); await stream.CopyToAsync(result); return result.ToArray(); }
    private sealed class Harness : IDisposable
    {
        public Handler Handler { get; } = new();
        public Budget Budget { get; } = new();
        public SpeechAudioCache Cache { get; }
        public ElevenLabsTextToSpeechService Service { get; }
        private readonly ServiceProvider _services;
        public Harness()
        {
            var settings = Options.Create(new SpeechOptions { Enabled = true, ApiKey = "test-key" });
            Cache = new(settings);
            _services = new ServiceCollection().AddSingleton<ISpeechProviderBudget>(Budget).BuildServiceProvider();
            Service = new(new Clients(Handler), settings,
                Options.Create(new AiUsageOptions { MonthlyBudget = new() { Models = [new() { Deployment = "eleven_v3", TextSekPerMillionCharacters = 1 }] } }),
                Cache, _services.GetRequiredService<IServiceScopeFactory>());
        }
        public void Dispose() { Cache.Dispose(); _services.Dispose(); Handler.Dispose(); }
    }
    private sealed class Budget : ISpeechProviderBudget
    {
        public List<int> Characters { get; } = [];
        public List<bool> Charges { get; } = [];
        public Task<Guid> ReserveAsync(int characters, CancellationToken ct) { Characters.Add(characters); return Task.FromResult(Guid.NewGuid()); }
        public Task SettleAsync(Guid id, bool charge, CancellationToken ct) { Charges.Add(charge); return Task.CompletedTask; }
    }
    private sealed class Clients(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Url, string Key, string Body)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public TaskCompletionSource? Gate { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.GetValues("xi-api-key").Single(), await request.Content!.ReadAsStringAsync(ct)));
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            return new(Status) { Content = new ByteArrayContent([73, 68, 51, 1, 2, 3]) };
        }
    }
}
