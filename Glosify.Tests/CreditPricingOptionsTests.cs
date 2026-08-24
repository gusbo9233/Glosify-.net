using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class CreditPricingOptionsTests
{
    [Fact]
    public void AppServiceStyleConfiguration_BindsNamedFeaturesAndSubtitleRates()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CreditPricing:TokenFeatures:assistant"] = "1.5",
                ["CreditPricing:Subtitles:EnhancedCreditsPerStartedMinute"] = "8",
                ["CreditPricing:Subtitles:ScribeCreditsPerStartedMinute"] = "4",
                ["CreditPricing:Subtitles:EnhancedWithTranscriptCreditsPerStartedMinute"] = "16",
            })
            .Build();

        var options = configuration
            .GetSection(CreditPricingOptions.SectionName)
            .Get<CreditPricingOptions>();

        Assert.NotNull(options);
        Assert.Equal(1.5m, options.TokenFeatures["assistant"]);
        Assert.Equal(4, options.Subtitles.ScribeCreditsPerStartedMinute);
    }

    [Fact]
    public void Validator_RejectsUnknownFreeAndNegativePrices()
    {
        var options = new CreditPricingOptions
        {
            TokenFeatures =
            {
                ["unknown"] = 1,
                [AiUsageFeatures.Assistant] = 0,
            },
            Subtitles = new SubtitleCreditPricingOptions
            {
                ScribeCreditsPerStartedMinute = 0,
            },
        };

        var result = new CreditPricingOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("unknown feature", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("assistant", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("ScribeCredits", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolver_AppliesFeatureRateWithTheSingleLunaMultiplier()
    {
        var resolver = CreateResolver(new CreditPricingOptions
        {
            TokenFeatures = { [AiUsageFeatures.Speaking] = 1.5m },
        });

        Assert.Equal(8, resolver.CalculateTokenCredits(
            5_000,
            AiUsageFeatures.Speaking,
            OpenAiModels.Luna));
        Assert.Equal(0, resolver.CalculateTokenCredits(
            0,
            AiUsageFeatures.Speaking,
            "test-model"));
    }

    [Fact]
    public void Resolver_UsesSingleModelMultiplierWhenNoOverrideIsConfigured()
    {
        var resolver = CreateResolver(new CreditPricingOptions());

        Assert.Equal(2m, resolver.GetTokenFeatureRate(AiUsageFeatures.Assistant));
        Assert.Equal(1m, resolver.GetModelMultiplier("test-model"));
        Assert.Equal(8, resolver.EnhancedSubtitleCreditsPerStartedMinute);
        Assert.Equal(6, resolver.ScribeSubtitleCreditsPerStartedMinute);
        Assert.Equal(16, resolver.EnhancedWithTranscriptCreditsPerStartedMinute);
        Assert.All(
            resolver.GetCatalog().TokenFeatures,
            price => Assert.Equal(CreditPriceSources.LegacyFallback, price.Source));
        var model = Assert.Single(resolver.GetCatalog().ModelMultipliers);
        Assert.Equal(OpenAiModels.Luna, model.Code);
        Assert.Equal(1m, model.Value);
    }

    [Fact]
    public void Resolver_RejectsUnknownFeatureBeforeCalculatingCharge()
    {
        var resolver = CreateResolver(new CreditPricingOptions());

        Assert.Throws<InvalidOperationException>(() =>
            resolver.CalculateTokenCredits(1_000, "new_paid_feature", "test-model"));
    }

    private static CreditPricingResolver CreateResolver(CreditPricingOptions pricing) =>
        new(
            Options.Create(pricing),
            Options.Create(new AiUsageOptions
            {
                CreditsPerThousandTokens = 2,
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }),
            Options.Create(new RealtimeTranslationOptions
            {
                CreditsPerStartedMinute = 8,
                SavedTranscriptCreditsPerStartedMinute = 16,
                ElevenLabs = new ElevenLabsRealtimeSpeechOptions
                {
                    CreditsPerStartedMinute = 6,
                },
            }));
}
