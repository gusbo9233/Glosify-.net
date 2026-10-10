using System.Net;
using System.Net.Http.Json;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Glosify.Services.Speech;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarVoicesTests
{
    private static object Voice(string id, string language, string gender = "female", string[]? verified = null) => new
    {
        voice_id = id, name = "NPC Estonian", // Names are deliberately misleading.
        labels = new { language, gender, age = "young" },
        verified_languages = (verified ?? []).Select(x => new { language = x })
    };

    [Fact]
    public async Task NativeFemaleOnLaterPageBeatsMaleAndVerifiedFallbackAndIsCached()
    {
        var handler = new CatalogueHandler(
            new { voices = new[] { Voice("male", "et", "male"), Voice("finnish", "fi", verified: ["et"]) }, has_more = true, next_page_token = "second+page" },
            new { voices = new[] { Voice("estonian", "et") }, has_more = false });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(handler, cache);
        var selected = await service.ResolveAsync(QuizLanguageCatalog.Find("Estonian")!, default);
        Assert.Equal("estonian", selected.Id);
        Assert.True(selected.Native);
        Assert.Equal(selected, await service.ResolveAsync(QuizLanguageCatalog.Find("et")!, default));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("next_page_token=second%2Bpage", handler.Requests[1]);
        Assert.All(handler.Requests, url => Assert.Contains("include_custom_rates=false", url));
        var finnish = await service.ResolveAsync(QuizLanguageCatalog.Find("fi")!, default);
        Assert.Equal("finnish", finnish.Id);
        Assert.True(finnish.Native);
    }

    [Fact]
    public async Task VerifiedFemaleWinsWhenNativeVoiceIsMissing()
    {
        var handler = new CatalogueHandler(new { voices = new[] { Voice("EXAVITQu4vr4xnSDxMaL", "en"), Voice("verified", "de", verified: ["sv"]) }, has_more = false });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var voice = await Service(handler, cache).ResolveAsync(QuizLanguageCatalog.Find("sv")!, default);
        Assert.Equal("verified", voice.Id);
        Assert.False(voice.Native);
    }

    [Fact]
    public async Task DefaultFemaleIsHonestMultilingualFallbackAndOverrideMustBeAvailable()
    {
        var handler = new CatalogueHandler(new { voices = new[] { Voice("EXAVITQu4vr4xnSDxMaL", "en"), Voice("other", "de") }, has_more = false });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new AvatarOptions();
        var service = Service(handler, cache, options);
        var language = QuizLanguageCatalog.Find("ka")!;
        var voice = await service.ResolveAsync(language, default);
        Assert.Equal("EXAVITQu4vr4xnSDxMaL", voice.Id);
        Assert.False(voice.Native);
        options.VoiceIds["ka"] = "other";
        Assert.Equal("other", (await service.ResolveAsync(language, default)).Id);
        options.VoiceIds["ka"] = "missing";
        Assert.Equal(503, (await Assert.ThrowsAsync<AvatarException>(() => service.ResolveAsync(language, default))).Status);
    }

    [Fact]
    public async Task MissingFemaleVoiceFailsWithoutSelectingMaleDefault()
    {
        var handler = new CatalogueHandler(new { voices = new[] { Voice("George", "en", "male") }, has_more = false });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(503, (await Assert.ThrowsAsync<AvatarException>(() => Service(handler, cache).ResolveAsync(QuizLanguageCatalog.Find("en")!, default))).Status);
    }

    [Fact]
    public async Task IncompleteCatalogueFailsInsteadOfCachingPartialSelection()
    {
        var handler = new CatalogueHandler(
            new { voices = new[] { Voice("wrong", "en") }, has_more = true, next_page_token = (string?)null },
            new { voices = new[] { Voice("correct", "sv") }, has_more = false });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(handler, cache);
        var language = QuizLanguageCatalog.Find("sv")!;
        Assert.Equal(503, (await Assert.ThrowsAsync<AvatarException>(() => service.ResolveAsync(language, default))).Status);
        Assert.Equal("correct", (await service.ResolveAsync(language, default)).Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Swedish", "sv")]
    [InlineData("Chinese", "zh")]
    [InlineData("Norwegian", "no")]
    [InlineData("Italian", "it")]
    public void SynthesisEnforcesCurrentLearningLanguage(string language, string code)
    {
        var uri = AvatarSpeech.SpeechUri(QuizLanguageCatalog.Find(language)!);
        Assert.Equal("wss", uri.Scheme);
        Assert.Contains("language_code=" + code, uri.Query);
        Assert.Contains("model_id=" + AvatarOptions.SpeechModel, uri.Query);
    }

    private static AvatarVoices Service(CatalogueHandler handler, IMemoryCache cache, AvatarOptions? options = null) =>
        new(new HttpClient(handler), cache, Options.Create(new SpeechOptions { ApiKey = "test-key" }), Options.Create(options ?? new()));
    private sealed class CatalogueHandler(params object[] pages) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("test-key", request.Headers.GetValues("xi-api-key").Single());
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(pages[Requests.Count - 1]) });
        }
    }
}
