using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Glosify.Services.Speech;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarCastingSmokeTests
{
    [AvatarCatalogueFact]
    [Trait("Category", "LiveAvatarCatalogue")]
    public async Task EveryShippedVoiceResolvesInTheProviderAccount()
    {
        using var app = App();
        using var client = await app.Client("admin");
        await using var scope = app.Services.CreateAsyncScope();
        var voices = scope.ServiceProvider.GetRequiredService<IAvatarVoices>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AvatarOptions>>().Value;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        // Languages without a matching native voice use a multilingual voice of the partner's gender.
        string[] fallbacks = ["rain/ka", "rowan/ka", "kwame/ka", "kwame/lv", "mei/ka", "mei/my"];
        foreach (var character in AvatarCharacter.All)
        {
            var casting = options.VoiceIdsFor(character);
            foreach (var language in QuizLanguageCatalog.LanguageLearning)
            {
                var voice = await voices.ResolveAsync(language, timeout.Token, character);
                Assert.Equal(casting[language.Code], voice.Id);
                Assert.Equal(!fallbacks.Contains($"{character.Id}/{language.Code}"), voice.Native);
            }
        }
    }

    [AvatarCastingTheory]
    [Trait("Category", "LiveAvatarCasting")]
    [InlineData("rain", "en", "Hello. Let’s learn together.")]
    [InlineData("rowan", "en", "Hello. Let’s learn together.")]
    [InlineData("rain", "sv", "Hej! Vad vill du prata om?")]
    [InlineData("rain", "ja", "こんにちは。一緒に学びましょう。")]
    [InlineData("rain", "ka", "გამარჯობა.")]
    [InlineData("rowan", "ka", "გამარჯობა.")]
    [InlineData("kwame", "en", "Hello. Let’s learn together.")]
    [InlineData("mei", "en", "Hello. Let’s learn together.")]
    [InlineData("mei", "zh-Hans", "你好。我们一起学习吧。")]
    [InlineData("kwame", "lv", "Sveiki! Mācīsimies kopā.")]
    [InlineData("mei", "my", "မင်္ဂလာပါ။")]
    public async Task SelectedVoiceProducesPcmThroughAvatarSpeech(string characterId, string code, string text)
    {
        using var app = App();
        using var client = await app.Client("admin");
        await using var scope = app.Services.CreateAsyncScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var character = AvatarCharacter.Find(characterId)!;
        var language = QuizLanguageCatalog.Find(code)!;
        var voice = await scope.ServiceProvider.GetRequiredService<IAvatarVoices>().ResolveAsync(language, timeout.Token, character);
        var session = new AvatarSession("admin", language, "free", DateTimeOffset.UtcNow, voice, character);
        var bytes = 0;
        await scope.ServiceProvider.GetRequiredService<IAvatarSpeech>().SpeakAsync(session, text,
            audio => { bytes += audio.Length; return Task.CompletedTask; }, timeout.Token);
        Assert.True(bytes > 2400, $"Expected PCM speech for {characterId}/{code}.");
    }

    private static AvatarFixture App()
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var key = secrets["Speech:ApiKey"] ?? Glosify.Extensions.ApplicationServiceExtensions.ResolveElevenLabsApiKey(secrets);
        Assert.False(string.IsNullOrWhiteSpace(key), "Configure the deployment account’s ElevenLabs key.");
        return new AvatarFixture { Overrides = services =>
        {
            services.AddHttpClient<IAvatarVoices, AvatarVoices>();
            services.PostConfigure<SpeechOptions>(o => o.ApiKey = key!);
        } };
    }
}

internal sealed class AvatarCatalogueFactAttribute : FactAttribute
{
    public AvatarCatalogueFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_AVATAR_CATALOGUE_SMOKE") != "true")
            Skip = "Set RUN_AVATAR_CATALOGUE_SMOKE=true to check all shipped selections against the provider account. No synthesis.";
    }
}

internal sealed class AvatarCastingTheoryAttribute : TheoryAttribute
{
    public AvatarCastingTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_AVATAR_CASTING_SMOKE") != "true")
            Skip = "Set RUN_AVATAR_CASTING_SMOKE=true to synthesize eleven short samples using real provider credit.";
    }
}
