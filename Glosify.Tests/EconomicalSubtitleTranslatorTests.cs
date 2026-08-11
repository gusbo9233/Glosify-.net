using System.Net;
using System.Text;
using Azure.Core;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class EconomicalSubtitleTranslatorTests
{
    [Fact]
    public async Task FinalizedSegments_AreTranslatedInCallOrder()
    {
        var client = new FakeTranslator();
        var sut = new EconomicalSubtitleTranslator(client);
        var now = DateTimeOffset.UtcNow;

        var first = await sut.TranslateAsync(
            new RecognizedSpeechSegment(1, "Dzień dobry", "pl", "pl-PL", now),
            "en",
            CancellationToken.None);
        var second = await sut.TranslateAsync(
            new RecognizedSpeechSegment(2, "Do widzenia", "pl", "pl-PL", now.AddSeconds(1)),
            "en",
            CancellationToken.None);

        Assert.Equal(["Dzień dobry", "Do widzenia"], client.Requests);
        Assert.Equal(1, first.Sequence);
        Assert.Equal("translated:Dzień dobry", first.TranslatedText);
        Assert.Equal(2, second.Sequence);
    }

    [Fact]
    public async Task MatchingSourceAndTarget_BypassesTranslator()
    {
        var client = new FakeTranslator();
        var sut = new EconomicalSubtitleTranslator(client);

        var result = await sut.TranslateAsync(
            new RecognizedSpeechSegment(1, "Hej", "sv", "sv-SE", DateTimeOffset.UtcNow),
            "sv",
            CancellationToken.None);

        Assert.Empty(client.Requests);
        Assert.Equal("Hej", result.TranslatedText);
    }

    private sealed class FakeTranslator : IRealtimeTextTranslator
    {
        public List<string> Requests { get; } = [];

        public Task<string> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken)
        {
            Requests.Add(text);
            return Task.FromResult("translated:" + text);
        }
    }
}

public sealed class AzureRealtimeTextTranslatorTests
{
    private const string TranslatorResourceId =
        "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/glosify/providers/Microsoft.CognitiveServices/accounts/glosify-translator";

    [Fact]
    public async Task GlobalEndpoint_UsesManagedIdentityResourceHeadersAndProviderLanguageCodes()
    {
        var handler = new CapturingHandler();
        var sut = CreateTranslator(handler, options =>
        {
            options.TranslatorEndpoint = "https://api.cognitive.microsofttranslator.com/";
            options.TranslatorResourceId = TranslatorResourceId;
            options.TranslatorRegion = "swedencentral";
        });

        var result = await sut.TranslateAsync("God morgen", "no", "zh", CancellationToken.None);

        Assert.Equal("translated", result);
        Assert.Equal(
            "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=nb&to=zh-Hans",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer entra-token", handler.Authorization);
        Assert.Equal(TranslatorResourceId, handler.ResourceId);
        Assert.Equal("swedencentral", handler.Region);
    }

    [Fact]
    public async Task CustomEndpoint_UsesTranslatorApiPathWithoutGlobalResourceHeaders()
    {
        var handler = new CapturingHandler();
        var sut = CreateTranslator(handler, options =>
        {
            options.TranslatorEndpoint =
                "https://glosify-translator.cognitiveservices.azure.com/";
        });

        await sut.TranslateAsync("Dzień dobry", "pl", "en", CancellationToken.None);

        Assert.Equal(
            "https://glosify-translator.cognitiveservices.azure.com/translator/text/v3.0/translate?api-version=3.0&from=pl&to=en",
            handler.RequestUri?.AbsoluteUri);
        Assert.Null(handler.ResourceId);
        Assert.Null(handler.Region);
    }

    private static AzureRealtimeTextTranslator CreateTranslator(
        CapturingHandler handler,
        Action<RealtimeTranslationOptions> configure)
    {
        var options = new RealtimeTranslationOptions
        {
            SourceLanguages =
            [
                new RealtimeTranslationSourceLanguageOptions
                {
                    Code = "no", Name = "Norwegian", Locale = "nb-NO", TranslatorCode = "nb",
                },
                new RealtimeTranslationSourceLanguageOptions
                {
                    Code = "pl", Name = "Polish", Locale = "pl-PL", TranslatorCode = "pl",
                },
            ],
            Languages =
            [
                new RealtimeTranslationLanguageOptions
                {
                    Code = "zh", Name = "Chinese", TranslatorCode = "zh-Hans",
                },
                new RealtimeTranslationLanguageOptions
                {
                    Code = "en", Name = "English", TranslatorCode = "en",
                },
            ],
        };
        configure(options);
        return new AzureRealtimeTextTranslator(
            new StubHttpClientFactory(handler),
            new StubTokenCredential(),
            Options.Create(options));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(AzureRealtimeTextTranslator.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? ResourceId { get; private set; }
        public string? Region { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ResourceId = ReadHeader(request, "Ocp-Apim-ResourceId");
            Region = ReadHeader(request, "Ocp-Apim-Subscription-Region");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"translations\":[{\"text\":\"translated\"}]}]",
                    Encoding.UTF8,
                    "application/json"),
            });
        }

        private static string? ReadHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private static readonly AccessToken Token =
            new("entra-token", DateTimeOffset.UtcNow.AddHours(1));

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }
}
