using Glosify.Services.Ai.Generation;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai;

public sealed class CreditPricingOptions
{
    public const string SectionName = "CreditPricing";

    public decimal? DefaultModelMultiplier { get; set; }
    public Dictionary<string, decimal> TokenFeatures { get; set; } = [];
    public SubtitleCreditPricingOptions Subtitles { get; set; } = new();
}

public sealed class SubtitleCreditPricingOptions
{
    public int? EnhancedCreditsPerStartedMinute { get; set; }
    public int? ScribeCreditsPerStartedMinute { get; set; }
    public int? CloudflareScribeCreditsPerStartedMinute { get; set; }
    public int? EnhancedWithTranscriptCreditsPerStartedMinute { get; set; }
}

public sealed class CreditPricingOptionsValidator : IValidateOptions<CreditPricingOptions>
{
    private static readonly HashSet<string> KnownFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        AiUsageFeatures.Assistant,
        AiUsageFeatures.ImageExtraction,
        AiUsageFeatures.PageTranslation,
    };

    public ValidateOptionsResult Validate(string? name, CreditPricingOptions options)
    {
        var failures = new List<string>();
        if (options.DefaultModelMultiplier is <= 0)
        {
            failures.Add("CreditPricing:DefaultModelMultiplier must be greater than zero when specified.");
        }
        foreach (var (feature, rate) in options.TokenFeatures)
        {
            if (!KnownFeatures.Contains(feature))
            {
                failures.Add($"CreditPricing:TokenFeatures contains unknown feature '{feature}'.");
            }
            if (rate <= 0)
            {
                failures.Add($"CreditPricing:TokenFeatures:{feature} must be greater than zero.");
            }
        }

        ValidatePositive(
            options.Subtitles.EnhancedCreditsPerStartedMinute,
            "CreditPricing:Subtitles:EnhancedCreditsPerStartedMinute",
            failures);
        ValidatePositive(
            options.Subtitles.ScribeCreditsPerStartedMinute,
            "CreditPricing:Subtitles:ScribeCreditsPerStartedMinute",
            failures);
        ValidatePositive(
            options.Subtitles.CloudflareScribeCreditsPerStartedMinute,
            "CreditPricing:Subtitles:CloudflareScribeCreditsPerStartedMinute",
            failures);
        ValidatePositive(
            options.Subtitles.EnhancedWithTranscriptCreditsPerStartedMinute,
            "CreditPricing:Subtitles:EnhancedWithTranscriptCreditsPerStartedMinute",
            failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePositive(int? value, string path, ICollection<string> failures)
    {
        if (value is <= 0)
        {
            failures.Add($"{path} must be greater than zero when specified.");
        }
    }
}

public interface ICreditPricingResolver
{
    int CalculateTokenCredits(int totalTokens, string feature, string model);
    decimal GetTokenFeatureRate(string feature);
    decimal GetModelMultiplier(string model);
    int EnhancedSubtitleCreditsPerStartedMinute { get; }
    int ScribeSubtitleCreditsPerStartedMinute { get; }
    int CloudflareScribeSubtitleCreditsPerStartedMinute { get; }
    int EnhancedWithTranscriptCreditsPerStartedMinute { get; }
    EffectiveCreditPricingCatalog GetCatalog();
}

public sealed class CreditPricingResolver : ICreditPricingResolver
{
    private const decimal DefaultLunaMultiplier = 0.08m;

    private static readonly (string Code, string Name)[] TokenFeatureNames =
    [
        (AiUsageFeatures.Assistant, "Assistant"),
        (AiUsageFeatures.ImageExtraction, "Image text extraction"),
        (AiUsageFeatures.PageTranslation, "Book page translation"),
    ];

    private readonly CreditPricingOptions _pricing;
    private readonly AiUsageOptions _usage;
    private readonly RealtimeTranslationOptions _realtime;

    public CreditPricingResolver(
        IOptions<CreditPricingOptions> pricing,
        IOptions<AiUsageOptions> usage,
        IOptions<RealtimeTranslationOptions> realtime)
    {
        _pricing = pricing.Value;
        _usage = usage.Value;
        _realtime = realtime.Value;
    }

    public int EnhancedSubtitleCreditsPerStartedMinute =>
        _pricing.Subtitles.EnhancedCreditsPerStartedMinute
        ?? _realtime.CreditsPerStartedMinute;

    public int ScribeSubtitleCreditsPerStartedMinute =>
        _pricing.Subtitles.ScribeCreditsPerStartedMinute
        ?? _realtime.ElevenLabs.CreditsPerStartedMinute;

    public int CloudflareScribeSubtitleCreditsPerStartedMinute =>
        _pricing.Subtitles.CloudflareScribeCreditsPerStartedMinute
        ?? _realtime.Cloudflare.CreditsPerStartedMinute;

    public int EnhancedWithTranscriptCreditsPerStartedMinute =>
        _pricing.Subtitles.EnhancedWithTranscriptCreditsPerStartedMinute
        ?? _realtime.SavedTranscriptCreditsPerStartedMinute;

    public int CalculateTokenCredits(int totalTokens, string feature, string model)
    {
        if (totalTokens <= 0)
        {
            return 0;
        }

        var thousands = decimal.Ceiling(totalTokens / 1000m);
        var credits = decimal.Ceiling(
            thousands
            * GetTokenFeatureRate(feature)
            * GetModelMultiplier(model));
        return Math.Max(1, checked((int)credits));
    }

    public decimal GetTokenFeatureRate(string feature)
    {
        var known = TokenFeatureNames.FirstOrDefault(candidate =>
            string.Equals(candidate.Code, feature?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(known.Code))
        {
            throw new InvalidOperationException(
                $"No customer credit price is defined for AI feature '{feature}'.");
        }

        return TryGetValue(_pricing.TokenFeatures, known.Code, out var configured)
            ? configured
            : _usage.CreditsPerThousandTokens;
    }

    public decimal GetModelMultiplier(string model) =>
        _pricing.DefaultModelMultiplier ?? DefaultLunaMultiplier;

    public EffectiveCreditPricingCatalog GetCatalog()
    {
        var featureRates = TokenFeatureNames.Select(feature =>
        {
            var configured = TryGetValue(_pricing.TokenFeatures, feature.Code, out var value);
            return new EffectiveCreditPrice(
                feature.Code,
                feature.Name,
                configured ? value : _usage.CreditsPerThousandTokens,
                "credits / 1K tokens",
                configured ? CreditPriceSources.CreditPricing : CreditPriceSources.LegacyFallback);
        }).ToArray();

        EffectiveCreditPrice[] deployments =
        [
            new(
                OpenAiModels.Luna,
                OpenAiModels.Luna,
                GetModelMultiplier(OpenAiModels.Luna),
                "multiplier",
                _pricing.DefaultModelMultiplier.HasValue
                    ? CreditPriceSources.CreditPricing
                    : CreditPriceSources.BuiltInDefault),
        ];

        return new EffectiveCreditPricingCatalog(
            featureRates,
            deployments,
            [
                SubtitlePrice(
                    "enhanced",
                    _realtime.Modes.Enhanced.DisplayName.Trim(),
                    _pricing.Subtitles.EnhancedCreditsPerStartedMinute,
                    _realtime.CreditsPerStartedMinute),
                SubtitlePrice(
                    "scribe_cloudflare",
                    _realtime.Modes.ScribeCloudflare.DisplayName.Trim(),
                    _pricing.Subtitles.CloudflareScribeCreditsPerStartedMinute,
                    _realtime.Cloudflare.CreditsPerStartedMinute),
                SubtitlePrice(
                    "enhanced_transcript",
                    "Enhanced subtitles with saved transcript",
                    _pricing.Subtitles.EnhancedWithTranscriptCreditsPerStartedMinute,
                    _realtime.SavedTranscriptCreditsPerStartedMinute),
            ]);
    }

    private static EffectiveCreditPrice SubtitlePrice(
        string code,
        string name,
        int? configured,
        int fallback) =>
        new(
            code,
            name,
            configured ?? fallback,
            "credits / started minute",
            configured.HasValue ? CreditPriceSources.CreditPricing : CreditPriceSources.LegacyFallback);

    private static bool TryGetValue(
        IReadOnlyDictionary<string, decimal> values,
        string? key,
        out decimal value)
    {
        foreach (var candidate in values)
        {
            if (string.Equals(candidate.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                value = candidate.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

public sealed record EffectiveCreditPricingCatalog(
    IReadOnlyList<EffectiveCreditPrice> TokenFeatures,
    IReadOnlyList<EffectiveCreditPrice> ModelMultipliers,
    IReadOnlyList<EffectiveCreditPrice> Subtitles);

public sealed record EffectiveCreditPrice(
    string Code,
    string Name,
    decimal Value,
    string Unit,
    string Source);

public static class CreditPriceSources
{
    public const string CreditPricing = "CreditPricing";
    public const string CreditPricingDefault = "CreditPricing default";
    public const string LegacyFallback = "Legacy fallback";
    public const string BuiltInDefault = "Built-in default";
}
