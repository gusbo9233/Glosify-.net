using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai;

public sealed class AiUsageOptions
{
    public int TrialGrantCredits { get; set; } = 25;
    public int CreditsPerThousandTokens { get; set; } = 1;
    public int AssistantOutputTokenReserve { get; set; } = 16384;
    public int RepairOutputTokenReserve { get; set; } = 1024;
    public int ImageExtractionOutputTokenReserve { get; set; } = 1024;
    public int SpeakingOutputTokenReserve { get; set; } = 768;
    public int PageTranslationOutputTokenReserve { get; set; } = 4096;
    public AiMonthlyBudgetOptions MonthlyBudget { get; set; } = new();

    public int GetOutputReserve(string feature)
    {
        return feature switch
        {
            AiUsageFeatures.Assistant => AssistantOutputTokenReserve,
            AiUsageFeatures.Repair => RepairOutputTokenReserve,
            AiUsageFeatures.ImageExtraction => ImageExtractionOutputTokenReserve,
            AiUsageFeatures.Speaking => SpeakingOutputTokenReserve,
            AiUsageFeatures.PageTranslation => PageTranslationOutputTokenReserve,
            _ => AssistantOutputTokenReserve,
        };
    }
}

public sealed class AiMonthlyBudgetOptions
{
    public bool Enabled { get; set; } = true;
    public decimal LimitSek { get; set; } = 300m;
    public string TimeZoneId { get; set; } = "Europe/Stockholm";
    public decimal ReservationSafetyMultiplier { get; set; } = 1.25m;
    public List<string> Providers { get; set; } = [AiUsageProviders.Foundry, AiUsageProviders.AzureAiFoundry];
    public List<AiModelPriceOptions> Models { get; set; } = [];

    /// <summary>Whether this budget meters the given credit provider.</summary>
    public bool MetersProvider(string? provider) =>
        Enabled
        && Providers.Any(candidate => string.Equals(
            candidate?.Trim(),
            provider?.Trim(),
            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The configured price for a deployment, or null when none is configured. A metered
    /// provider treats null as fatal: an unpriced deployment cannot be charged, so the
    /// request fails at credit reservation rather than going unaccounted.
    /// </summary>
    public AiModelPriceOptions? FindModelPrice(string? deployment) =>
        Models.FirstOrDefault(candidate => string.Equals(
            candidate.Deployment?.Trim(),
            deployment?.Trim(),
            StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a deployment can be charged per token. Used by startup validation.</summary>
    public bool HasTokenPrice(string? deployment) =>
        FindModelPrice(deployment) is
        {
            InputSekPerMillionTokens: > 0,
            OutputSekPerMillionTokens: > 0,
        };
}

/// <summary>
/// Credit provider names. Shared so that startup validation and the runtime reservation
/// path cannot disagree about which calls the monthly budget meters.
/// </summary>
public static class AiUsageProviders
{
    /// <summary>Generative AI through Microsoft Foundry (assistant, repair, vision, translation).</summary>
    public const string Foundry = "foundry";

    /// <summary>Speaking practice through Microsoft Foundry.</summary>
    public const string AzureAiFoundry = "azure_ai_foundry";

    /// <summary>Explicit rollback through the Google Gemini API.</summary>
    public const string Gemini = "gemini";
}

public sealed class AiModelPriceOptions
{
    public string Deployment { get; set; } = string.Empty;
    public decimal InputSekPerMillionTokens { get; set; }
    public decimal OutputSekPerMillionTokens { get; set; }
    public decimal? AudioSekPerMinute { get; set; }
}

public sealed class AiUsageOptionsValidator : IValidateOptions<AiUsageOptions>
{
    public ValidateOptionsResult Validate(string? name, AiUsageOptions options)
    {
        var failures = new List<string>();
        var budget = options.MonthlyBudget;

        if (options.PageTranslationOutputTokenReserve <= 0)
        {
            failures.Add("AiUsage:PageTranslationOutputTokenReserve must be greater than zero.");
        }

        if (!budget.Enabled)
        {
            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        if (budget.LimitSek <= 0)
        {
            failures.Add("AiUsage:MonthlyBudget:LimitSek must be greater than zero.");
        }

        if (budget.ReservationSafetyMultiplier < 1m)
        {
            failures.Add("AiUsage:MonthlyBudget:ReservationSafetyMultiplier must be at least 1.");
        }

        if (budget.Providers.Count == 0
            || budget.Providers.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("AiUsage:MonthlyBudget:Providers must contain at least one provider.");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(budget.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            failures.Add("AiUsage:MonthlyBudget:TimeZoneId is not a recognized time zone.");
        }
        catch (InvalidTimeZoneException)
        {
            failures.Add("AiUsage:MonthlyBudget:TimeZoneId is invalid.");
        }

        var deployments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in budget.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Deployment))
            {
                failures.Add("AiUsage:MonthlyBudget:Models:Deployment must not be empty.");
                continue;
            }

            if (!deployments.Add(model.Deployment.Trim()))
            {
                failures.Add(
                    $"AiUsage:MonthlyBudget:Models contains duplicate deployment '{model.Deployment.Trim()}'.");
            }

            var hasTokenPrice = model.InputSekPerMillionTokens > 0
                && model.OutputSekPerMillionTokens > 0;
            var hasPartialTokenPrice = model.InputSekPerMillionTokens != 0
                || model.OutputSekPerMillionTokens != 0;
            var hasAudioPrice = model.AudioSekPerMinute is > 0;
            if (!hasTokenPrice && !hasAudioPrice)
            {
                failures.Add(
                    $"AiUsage:MonthlyBudget:Models deployment '{model.Deployment.Trim()}' requires positive token prices or AudioSekPerMinute.");
            }
            else if (hasPartialTokenPrice && !hasTokenPrice)
            {
                failures.Add(
                    $"AiUsage:MonthlyBudget:Models deployment '{model.Deployment.Trim()}' must configure both token prices together.");
            }
        }

        if (budget.Models.Count == 0)
        {
            failures.Add("AiUsage:MonthlyBudget:Models must contain at least one deployment price.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public static class AiUsageFeatures
{
    public const string Assistant = "assistant";
    public const string Repair = "repair";
    public const string ImageExtraction = "image_extraction";
    public const string Speaking = "speaking";
    public const string PageTranslation = "page_translation";
    public const string RealtimeTranslation = "realtime_translation";
}
