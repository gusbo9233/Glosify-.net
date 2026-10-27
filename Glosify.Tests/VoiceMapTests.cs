using Glosify.Services.Language;
using Glosify.Services.Speech;
using Xunit;

namespace Glosify.Tests;

public sealed class VoiceMapTests
{
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
            Assert.Equal(browserLocale, VoiceMap.ResolveLocale(browserLocale));
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
}
