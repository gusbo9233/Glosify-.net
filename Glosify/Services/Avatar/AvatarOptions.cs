using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Speech;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Avatar;

public sealed class AvatarOptions
{
    public bool Enabled { get; set; }
    public string VoiceId { get; set; } = "";
    // Conservative cost ceiling for the provider budget, independent of customer rates.
    public decimal SynthesisSekPerMillionCharacters { get; set; } = 1928.29m;
    public const string SpeechModel = "eleven_v4_turbo";
    public const string RecognitionModel = "scribe_v2_realtime";
}

public sealed record AvatarRates(decimal RecognitionPerMinute, decimal ReplyPerThousandTokens, decimal SpeechPerThousandCharacters);

public sealed class AvatarPricing(ICreditPricingResolver pricing, IOptions<SpeechOptions> speech, IOptions<AvatarOptions> options,
    IOptions<AiUsageOptions> usage, IOptions<GenerativeAiOptions> ai)
{
    public AvatarRates Rates => new(pricing.ScribeSubtitleCreditsPerStartedMinute,
        pricing.GetTokenFeatureRate(AiUsageFeatures.Assistant) * pricing.GetModelMultiplier(OpenAiModels.Luna),
        speech.Value.CalculateCredits(1000));
    public bool Available => options.Value.Enabled && !string.IsNullOrWhiteSpace(speech.Value.ApiKey)
        && !string.IsNullOrWhiteSpace(ai.Value.ApiKey);
    public string VoiceId => string.IsNullOrWhiteSpace(options.Value.VoiceId) ? speech.Value.DefaultVoiceId : options.Value.VoiceId;
    public decimal CreditRate(string kind) => kind == "recognition" ? Rates.RecognitionPerMinute / 60m
        : speech.Value.TextSekPerMillionCharacters / 1_000_000m / speech.Value.SekPerCredit;
    public decimal ProviderSekRate(string kind) => kind == "recognition"
        ? (usage.Value.MonthlyBudget.FindModelPrice("elevenlabs-scribe-v2-realtime")?.AudioSekPerMinute
            ?? throw new InvalidOperationException("Scribe provider pricing is not configured.")) / 60m
        : options.Value.SynthesisSekPerMillionCharacters / 1_000_000m;
}
