using System.Threading.Channels;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Glosify.Services.Speech;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarLiveSmokeTests
{
    [LiveAvatarFact]
    [Trait("Category", "LiveAvatar")]
    public async Task RecordedSpeechProducesTranscriptBilledReplyAndPcmAudio()
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var openAiKey = secrets["OPENAI_SECRET_KEY"];
        var elevenKey = Glosify.Extensions.ApplicationServiceExtensions.ResolveElevenLabsApiKey(secrets);
        Assert.False(string.IsNullOrWhiteSpace(openAiKey), "Configure the local OpenAI key.");
        Assert.False(string.IsNullOrWhiteSpace(elevenKey), "Configure the local ElevenLabs key.");
        var pcm = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("AVATAR_SMOKE_PCM_PATH")!);
        Assert.InRange(pcm.Length, 3200, 32000 * 15);
        using var app = new AvatarFixture { Overrides = services =>
        {
            services.AddHttpClient<IAvatarVoices, AvatarVoices>();
            services.PostConfigure<SpeechOptions>(o => o.ApiKey = elevenKey!);
            services.PostConfigure<GenerativeAiOptions>(o => o.ApiKey = openAiKey!);
        } };
        using var client = await app.Client("admin");
        await using var scope = app.Services.CreateAsyncScope();
        var speech = scope.ServiceProvider.GetRequiredService<IAvatarSpeech>();
        var ai = scope.ServiceProvider.GetRequiredService<IGenerativeAiClient>();
        var language = QuizLanguageCatalog.Find("en")!;
        var voice = await scope.ServiceProvider.GetRequiredService<IAvatarVoices>().ResolveAsync(language, CancellationToken.None);
        var session = new AvatarSession("admin", language, "free", DateTimeOffset.UtcNow, voice);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = Channel.CreateBounded<byte[]>(8);
        var listen = speech.ListenAsync(session, "push-to-talk", channel.Reader, _ => Task.CompletedTask,
            () => { ready.TrySetResult(); return Task.CompletedTask; }, timeout.Token);
        await Task.WhenAny(ready.Task, listen);
        if (!ready.Task.IsCompleted) await listen; // Surface a provider handshake failure without hanging.
        for (var offset = 0; offset < pcm.Length; offset += 6400)
        {
            await channel.Writer.WriteAsync(pcm[offset..Math.Min(pcm.Length, offset + 6400)], timeout.Token);
            await Task.Delay(200, timeout.Token);
        }
        await channel.Writer.WriteAsync([], timeout.Token); channel.Writer.Complete();
        var text = await listen;
        Assert.False(string.IsNullOrWhiteSpace(text));
        var response = await ai.RunAgentTurnAsync(new AgentRequest("Reply warmly in English using at most twelve words. Plain spoken text only.",
            [AvatarConversation.TextTurn("user", text)], [], MaxOutputTokens: 128),
            new AiUsageContext("admin", AiUsageFeatures.Assistant, "avatar.reply", Guid.NewGuid(), "AvatarSession", session.Id.ToString()), cancellationToken: timeout.Token);
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        var audioBytes = 0;
        await speech.SpeakAsync(session, response.Text, bytes => { audioBytes += bytes.Length; return Task.CompletedTask; }, timeout.Token);
        Assert.True(audioBytes > 2400, "Expected at least 50 ms of generated 24 kHz PCM speech.");
        var account = await scope.ServiceProvider.GetRequiredService<IAiCreditService>().GetOrCreateAccountAsync("admin", timeout.Token);
        Assert.Equal(0, account.ReservedCredits);
        Assert.InRange(account.BalanceCredits, 90m, 99.999999m);
    }
}

internal sealed class LiveAvatarFactAttribute : FactAttribute
{
    public LiveAvatarFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_AVATAR_LIVE_SMOKE") != "true")
            Skip = "Set RUN_AVATAR_LIVE_SMOKE=true and AVATAR_SMOKE_PCM_PATH to a short mono 16 kHz PCM16 recording. Uses real provider credit.";
    }
}
