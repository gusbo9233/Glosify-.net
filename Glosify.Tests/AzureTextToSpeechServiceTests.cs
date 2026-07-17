using System.Net;
using System.Net.Http;
using Azure.Core;
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
            () => service.GetOrSynthesizeAsync("hej", "sv-SE"));
    }

    [Theory]
    [InlineData("et", "et-EE-AnuNeural")]
    [InlineData("de-DE", "de-DE-KatjaNeural")]
    [InlineData("pl", "pl-PL-AgnieszkaNeural")]
    [InlineData("uk-UA", "uk-UA-PolinaNeural")]
    public void Voice_map_resolves_supported_languages(string code, string expectedVoice)
    {
        Assert.True(VoiceMap.TryResolve(code, out _, out var voice));
        Assert.Equal(expectedVoice, voice);
    }

    [Fact]
    public void Voice_map_rejects_unknown_language()
    {
        Assert.False(VoiceMap.TryResolve("sv-SE", out _, out _));
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
            "https://glosify-speech.cognitiveservices.azure.com/cognitiveservices/v1",
            requestUri!.AbsoluteUri);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], credential.RequestedScopes);
    }

    private static AzureTextToSpeechService CreateService(
        SpeechOptions speech,
        StubHandler handler,
        TokenCredential? credential = null)
    {
        var factory = new StubHttpClientFactory(handler);
        return new AzureTextToSpeechService(
            Options.Create(speech),
            Options.Create(new BlobStorageOptions()),
            credential ?? new StubTokenCredential("unused"),
            factory,
            NullLogger<AzureTextToSpeechService>.Instance);
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
