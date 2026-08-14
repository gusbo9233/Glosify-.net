using System.Net;
using System.Text;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AzureTranslatorLanguageCatalogTests
{
    [Fact]
    public async Task Catalog_LoadsAllTranslatorLanguagesAndCachesTheResult()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"translation":{"sv":{"name":"Swedish"},"en":{"name":"English"},"zh-Hans":{"name":"Chinese (Simplified)"}}}
            """);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = CreateCatalog(handler, cache);

        var first = await catalog.GetLanguagesAsync();
        var second = await catalog.GetLanguagesAsync();

        Assert.Equal(["zh-Hans", "en", "sv"], first.Select(language => language.Code).ToArray());
        Assert.Same(first, second);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("en", handler.LastRequest?.Headers.AcceptLanguage.Single().Value);
    }

    [Fact]
    public async Task Catalog_UsesConfiguredFallbackWhenTranslatorCatalogIsUnavailable()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "{}");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = CreateCatalog(handler, cache, new RealtimeTranslationOptions
        {
            Languages =
            [
                new RealtimeTranslationLanguageOptions { Code = "pl", Name = "Polish" },
                new RealtimeTranslationLanguageOptions { Code = "de", Name = "German" },
            ],
        });

        var languages = await catalog.GetLanguagesAsync();

        Assert.Equal(["de", "pl"], languages.Select(language => language.Code).ToArray());
    }

    private static AzureTranslatorLanguageCatalog CreateCatalog(
        HttpMessageHandler handler,
        IMemoryCache cache,
        RealtimeTranslationOptions? options = null) => new(
            new StubHttpClientFactory(handler),
            cache,
            Options.Create(options ?? new RealtimeTranslationOptions()),
            NullLogger<AzureTranslatorLanguageCatalog>.Instance);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.cognitive.microsofttranslator.com/"),
        };
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
