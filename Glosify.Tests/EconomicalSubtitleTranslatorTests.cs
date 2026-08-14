using System.Net;
using System.Text;
using Azure.Core;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class EconomicalSubtitleTranslatorTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FinalizedSegments_AreTranslatedInCallOrder()
    {
        var client = new FakeTranslator();
        var sut = new EconomicalSubtitleTranslator(client);
        var now = TestNow;

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
            new RecognizedSpeechSegment(1, "Hej", "sv", "sv-SE", TestNow),
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
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task AutoDetectedSourceAndDynamicTarget_DoNotRequireStaticMappings()
    {
        var handler = new CapturingHandler();
        var sut = CreateTranslator(handler, options =>
        {
            options.TranslatorEndpoint = "https://api.cognitive.microsofttranslator.com/";
            options.TranslatorResourceId = TranslatorResourceId;
        });

        await sut.TranslateAsync("Bonjour", "auto", "fr", CancellationToken.None);

        Assert.Equal(
            "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=fr",
            handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task UpstreamFailure_IncludesStatusWithoutLeakingResponseContent()
    {
        var handler = new CapturingHandler
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            ResponseBody = "submitted subtitle text must not escape",
        };
        var sut = CreateTranslator(handler, options =>
            options.TranslatorEndpoint =
                "https://glosify-translator.cognitiveservices.azure.com/");

        var exception = await Assert.ThrowsAsync<RealtimeTranslationUpstreamException>(() =>
            sut.TranslateAsync("private subtitle", "pl", "en", CancellationToken.None));

        Assert.Contains("HTTP 429", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("submitted subtitle", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"translations\":[] }]")]
    [InlineData("not-json")]
    public async Task MalformedSuccessResponse_MapsToUpstreamException(string responseBody)
    {
        var handler = new CapturingHandler { ResponseBody = responseBody };
        var sut = CreateTranslator(handler, options =>
            options.TranslatorEndpoint =
                "https://glosify-translator.cognitiveservices.azure.com/");

        var exception = await Assert.ThrowsAsync<RealtimeTranslationUpstreamException>(() =>
            sut.TranslateAsync("Dzień dobry", "pl", "en", CancellationToken.None));

        Assert.Contains("invalid response", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public string ResponseBody { get; init; } =
            "[{\"translations\":[{\"text\":\"translated\"}]}]";
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
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(
                    ResponseBody,
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
            new("entra-token", TestNow.AddHours(1));

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }
}
