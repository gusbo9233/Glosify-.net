using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class PageTranslationOptionsTests
{
    [Fact]
    public void Foundry_requires_a_translation_deployment()
    {
        var options = ValidFoundryOptions();
        options.Foundry.PageTranslationDeployment = " ";

        var result = new GenerativeAiOptionsValidator(Options.Create(new GeminiOptions()), UnbudgetedUsage())
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("PageTranslationDeployment", StringComparison.Ordinal));
    }

    [Fact]
    public void Foundry_requires_a_translation_fallback_deployment()
    {
        var options = ValidFoundryOptions();
        options.Foundry.PageTranslationFallbackDeployment = " ";

        var result = new GenerativeAiOptionsValidator(Options.Create(new GeminiOptions()), UnbudgetedUsage())
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("PageTranslationFallbackDeployment", StringComparison.Ordinal));
    }

    [Fact]
    public void Gemini_rollback_requires_a_translation_model()
    {
        var options = ValidFoundryOptions();
        options.Provider = GenerativeAiOptions.GeminiProvider;
        var gemini = new GeminiOptions
        {
            ApiKey = "configured-secret",
            PageTranslationModel = " ",
        };

        var result = new GenerativeAiOptionsValidator(Options.Create(gemini), UnbudgetedUsage())
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("PageTranslationModel", StringComparison.Ordinal));
    }

    [Fact]
    public void Translation_output_reserve_must_be_positive_even_when_budget_is_disabled()
    {
        var options = new AiUsageOptions
        {
            PageTranslationOutputTokenReserve = 0,
            MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
        };

        var result = new AiUsageOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("PageTranslationOutputTokenReserve", StringComparison.Ordinal));
    }

    /// <summary>Budget disabled: these tests exercise deployment shape, not pricing.</summary>
    private static IOptions<AiUsageOptions> UnbudgetedUsage() =>
        Options.Create(new AiUsageOptions
        {
            MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
        });

    private static GenerativeAiOptions ValidFoundryOptions() => new()
    {
        Provider = GenerativeAiOptions.FoundryProvider,
        Foundry = new FoundryGenerativeAiOptions
        {
            ProjectEndpoint = "https://example.services.ai.azure.com/api/projects/glosify",
            AssistantDeployment = "gpt-5.4-mini",
            StructuredDeployment = "gpt-5.4-mini",
            VisionDeployment = "gpt-5.4-mini",
            PageTranslationDeployment = "gpt-5.4-mini",
            PageTranslationFallbackDeployment = "grok-4-1-fast-non-reasoning",
            AllowedAssistantDeployments = ["gpt-5.4-mini"],
            AssistantModels =
            [
                new AssistantModelOptions
                {
                    Deployment = "gpt-5.4-mini",
                    DisplayName = "GPT-5.4 Mini",
                    Provider = "OpenAI",
                    SpeedTier = "Balanced",
                    CostTier = "Standard",
                    CreditMultiplier = 1m,
                },
            ],
            TimeoutSeconds = 30,
        },
    };
}
