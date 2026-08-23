using System.Net;
using Azure.Core;
using Glosify.Services.Language;
using Glosify.Services.Speech;
using Glosify.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public class AzureTextToSpeechServiceTests
{
    [Fact]
    public void IsConfigured_false_when_key_or_region_missing()
    {
        var service = CreateService(new SpeechOptions(), StubHandler.NeverCalled());
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void IsConfigured_true_when_Entra_endpoint_and_resource_id_are_present()
    {
        var service = CreateService(new SpeechOptions
        {
            Endpoint = "https://glosify-speech.cognitiveservices.azure.com",
            ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/speech",
        }, StubHandler.NeverCalled());

        Assert.True(service.IsConfigured);
    }

    [Fact]
    public async Task Throws_when_language_has_no_voice()
    {
        var service = CreateService(
            new SpeechOptions { Key = "k", Region = "swedencentral", BlobContainer = string.Empty },
            StubHandler.NeverCalled());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.GetOrSynthesizeAsync("sannu", "ha-NG"));
    }

    [Theory]
    [InlineData("et", "et-EE-AnuNeural")]
    [InlineData("Danish", "da-DK-ChristelNeural")]
    [InlineData("da-DK", "da-DK-ChristelNeural")]
    [InlineData("de-DE", "de-DE-KatjaNeural")]
    [InlineData("pl", "pl-PL-ZofiaNeural")]
    [InlineData("uk-UA", "uk-UA-PolinaNeural")]
    public void Voice_map_resolves_supported_languages(string code, string expectedVoice)
    {
        Assert.True(VoiceMap.TryResolve(code, out var voice));
        Assert.Equal(expectedVoice, voice);
    }

    [Fact]
    public void Voice_map_rejects_unknown_language()
    {
        Assert.False(VoiceMap.TryResolve("not-a-language", out _));
    }

    [Fact]
    public void Every_quiz_language_has_an_explicit_Azure_or_browser_fallback_classification()
    {
        var browserFallbackOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ha", // Hausa
            "ky", // Kyrgyz
            "mi", // Māori
        };

        foreach (var language in QuizLanguageCatalog.LanguageLearning.DistinctBy(item => item.Code))
        {
            var expectedAzureSupport = !browserFallbackOnly.Contains(language.Code);
            Assert.Equal(expectedAzureSupport, VoiceMap.TryResolve(language.Code, out _));
            Assert.Equal(expectedAzureSupport, VoiceMap.TryResolve(language.Name, out _));
            Assert.Equal(expectedAzureSupport, VoiceMap.TryResolve(language.Locale, out _));

            Assert.True(VoiceMap.ClientLocaleAliases.TryGetValue(language.Code, out var browserLocale));
            Assert.False(string.IsNullOrWhiteSpace(browserLocale));
        }
    }

    [Fact]
    public void Cantonese_uses_Azures_Hong_Kong_Cantonese_voice_and_locale()
    {
        Assert.True(VoiceMap.TryResolve("yue-HK", null, out var locale, out var voice));
        Assert.Equal("zh-HK", locale);
        Assert.Equal("zh-HK-HiuMaanNeural", voice);
        Assert.Equal("zh-HK", VoiceMap.ClientLocaleAliases["Cantonese"]);
    }

    [Theory]
    [InlineData("zofia", "pl-PL-ZofiaNeural")]
    [InlineData("agnieszka", "pl-PL-AgnieszkaNeural")]
    [InlineData("marek", "pl-PL-MarekNeural")]
    [InlineData("not-an-azure-voice", "pl-PL-ZofiaNeural")]
    public void Voice_map_allowlists_Polish_voice_preferences(string preference, string expectedVoice)
    {
        Assert.True(VoiceMap.TryResolve("pl", preference, out var locale, out var voice));
        Assert.Equal("pl-PL", locale);
        Assert.Equal(expectedVoice, voice);
    }

    [Theory]
    [InlineData("zofia", "pl-PL-ZofiaNeural")]
    [InlineData("agnieszka", "pl-PL-AgnieszkaNeural")]
    [InlineData("marek", "pl-PL-MarekNeural")]
    public async Task Polish_voice_uses_selected_voice_and_48_kHz_output(
        string preference,
        string expectedVoice)
    {
        string? outputFormat = null;
        string? ssml = null;
        var handler = new StubHandler(request =>
        {
            outputFormat = request.Headers.GetValues("X-Microsoft-OutputFormat").Single();
            ssml = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            };
        });
        var service = CreateService(
            new SpeechOptions { Key = "k", Region = "swedencentral", BlobContainer = string.Empty },
            handler);

        using var stream = await service.GetOrSynthesizeAsync(
            "Dzień dobry",
            "pl",
            voicePreference: preference);

        Assert.Equal("audio-48khz-192kbitrate-mono-mp3", outputFormat);
        Assert.Contains($"name='{expectedVoice}'", ssml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("de", "de-DE-Seraphina:DragonHDLatestNeural")]
    [InlineData("de-DE", "de-DE-Seraphina:DragonHDLatestNeural")]
    public void Voice_map_resolves_only_explicitly_supported_HD_voices(string language, string expected)
    {
        Assert.True(VoiceMap.TryResolveHighDefinition(language, out var voice));
        Assert.Equal(expected, voice);
        Assert.False(VoiceMap.TryResolveHighDefinition("et", out _));
        Assert.False(VoiceMap.TryResolveHighDefinition("pl", out _));
        Assert.False(VoiceMap.TryResolveHighDefinition("uk", out _));
    }

    [Fact]
    public async Task Synthesize_returns_audio_bytes_from_upstream()
    {
        var expected = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expected),
        });

        var service = CreateService(
            new SpeechOptions { Key = "k", Region = "swedencentral", BlobContainer = string.Empty },
            handler);

        using var stream = await service.GetOrSynthesizeAsync("Tere", "et");
        using var mem = new MemoryStream();
        await stream.CopyToAsync(mem);
        Assert.Equal(expected, mem.ToArray());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Synthesize_prefers_Entra_authentication_when_configured()
    {
        const string resourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/speech";
        string? authorization = null;
        string? subscriptionKey = null;
        Uri? requestUri = null;
        var handler = new StubHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            subscriptionKey = request.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var values)
                ? values.Single()
                : null;
            requestUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            };
        });
        var credential = new StubTokenCredential("entra-token");
        var service = CreateService(new SpeechOptions
        {
            Endpoint = "https://glosify-speech.cognitiveservices.azure.com/",
            ResourceId = resourceId,
            Key = "fallback-key",
            Region = "swedencentral",
            BlobContainer = string.Empty,
        }, handler, credential);

        using var stream = await service.GetOrSynthesizeAsync("Tere", "et");

        Assert.Equal($"Bearer aad#{resourceId}#entra-token", authorization);
        Assert.Null(subscriptionKey);
        Assert.Equal(
            "https://swedencentral.tts.speech.microsoft.com/cognitiveservices/v1",
            requestUri!.AbsoluteUri);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], credential.RequestedScopes);
    }

    [Fact]
    public async Task High_definition_request_uses_Dragon_HD_Omni_and_dedicated_resource()
    {
        const string hdResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry";
        Uri? requestUri = null;
        string? authorization = null;
        string? ssml = null;
        var handler = new StubHandler(request =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            ssml = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            };
        });
        var service = CreateService(new SpeechOptions
        {
            Endpoint = "https://standard.cognitiveservices.azure.com",
            ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/standard",
            Region = "swedencentral",
            BlobContainer = string.Empty,
            HighDefinition = new SpeechHighDefinitionOptions
            {
                Enabled = true,
                ResourceId = hdResourceId,
                Region = "eastus",
            },
        }, handler, new StubTokenCredential("entra-token"));

        using var stream = await service.GetOrSynthesizeAsync("Guten Tag", "de", preferHighDefinition: true);

        Assert.Equal("https://eastus.tts.speech.microsoft.com/cognitiveservices/v1", requestUri!.AbsoluteUri);
        Assert.Equal($"Bearer aad#{hdResourceId}#entra-token", authorization);
        Assert.Contains("name='de-DE-Seraphina:DragonHDLatestNeural'", ssml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task High_definition_failure_falls_back_to_standard_voice_and_resource()
    {
        var requests = new List<(Uri Uri, string Ssml)>();
        var handler = new StubHandler(request =>
        {
            requests.Add((
                request.RequestUri!,
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([4, 5, 6]),
                };
        });
        var service = CreateService(new SpeechOptions
        {
            Endpoint = "https://standard.cognitiveservices.azure.com",
            ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/standard",
            Region = "swedencentral",
            BlobContainer = string.Empty,
            HighDefinition = new SpeechHighDefinitionOptions
            {
                Enabled = true,
                ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry",
                Region = "eastus",
            },
        }, handler, new StubTokenCredential("entra-token"));

        using var stream = await service.GetOrSynthesizeAsync("Guten Tag", "de", preferHighDefinition: true);

        Assert.Equal(2, requests.Count);
        Assert.Equal("eastus.tts.speech.microsoft.com", requests[0].Uri.Host);
        Assert.Contains("DragonHDLatestNeural", requests[0].Ssml, StringComparison.Ordinal);
        Assert.Equal("swedencentral.tts.speech.microsoft.com", requests[1].Uri.Host);
        Assert.Contains("name='de-DE-KatjaNeural'", requests[1].Ssml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Language_without_native_HD_voice_uses_standard_voice_directly()
    {
        Uri? requestUri = null;
        string? ssml = null;
        var handler = new StubHandler(request =>
        {
            requestUri = request.RequestUri;
            ssml = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            };
        });
        var service = CreateService(new SpeechOptions
        {
            Endpoint = "https://standard.cognitiveservices.azure.com",
            ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/standard",
            Region = "swedencentral",
            BlobContainer = string.Empty,
            HighDefinition = new SpeechHighDefinitionOptions
            {
                Enabled = true,
                ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry",
                Region = "eastus",
            },
        }, handler, new StubTokenCredential("entra-token"));

        using var stream = await service.GetOrSynthesizeAsync("Tere", "et", preferHighDefinition: true);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("swedencentral.tts.speech.microsoft.com", requestUri!.Host);
        Assert.Contains("name='et-EE-AnuNeural'", ssml, StringComparison.Ordinal);
    }

    private static AzureTextToSpeechService CreateService(
        SpeechOptions speech,
        StubHandler handler,
        TokenCredential? credential = null)
    {
        var factory = new StubHttpClientFactory(handler);
        var sharedCredential = credential ?? new StubTokenCredential("unused");
        var blobs = new GlosifyBlobServiceClient(
            Options.Create(new BlobStorageOptions()),
            sharedCredential,
            new StubHostEnvironment(Microsoft.Extensions.Hosting.Environments.Production));
        return new AzureTextToSpeechService(
            Options.Create(speech),
            blobs,
            sharedCredential,
            factory,
            NullLogger<AzureTextToSpeechService>.Instance,
            new AlwaysAvailablePaidServiceGate());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public static StubHandler NeverCalled() => new(_ =>
            throw new InvalidOperationException("HTTP call not expected."));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubTokenCredential(string token) : TokenCredential
    {
        public IReadOnlyList<string> RequestedScopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestedScopes = requestContext.Scopes;
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            RequestedScopes = requestContext.Scopes;
            return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
