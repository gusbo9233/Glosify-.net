using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai;

public sealed class AiUsageOptions
{
    public int TrialGrantCredits { get; set; } = 25;
    public int CreditsPerThousandTokens { get; set; } = 1;
    public int AssistantOutputTokenReserve { get; set; } = 2048;
    public int RepairOutputTokenReserve { get; set; } = 1024;
    public int ImageExtractionOutputTokenReserve { get; set; } = 1024;
    public int SpeakingOutputTokenReserve { get; set; } = 768;
    public AiMonthlyBudgetOptions MonthlyBudget { get; set; } = new();

    public int GetOutputReserve(string feature)
    {
        return feature switch
        {
            AiUsageFeatures.Assistant => AssistantOutputTokenReserve,
            AiUsageFeatures.Repair => RepairOutputTokenReserve,
            AiUsageFeatures.ImageExtraction => ImageExtractionOutputTokenReserve,
            AiUsageFeatures.Speaking => SpeakingOutputTokenReserve,
            _ => AssistantOutputTokenReserve,
        };
    }
}

public sealed class AiMonthlyBudgetOptions
{
    public bool Enabled { get; set; } = true;
    public decimal LimitSek { get; set; } = 200m;
    public string TimeZoneId { get; set; } = "Europe/Stockholm";
    public decimal ReservationSafetyMultiplier { get; set; } = 1.25m;
    public List<string> Providers { get; set; } = ["foundry", "azure_ai_foundry"];
    public List<AiModelPriceOptions> Models { get; set; } = [];
}

public sealed class AiModelPriceOptions
{
    public string Deployment { get; set; } = string.Empty;
    public decimal InputSekPerMillionTokens { get; set; }
    public decimal OutputSekPerMillionTokens { get; set; }
}

public sealed class AiUsageOptionsValidator : IValidateOptions<AiUsageOptions>
{
    public ValidateOptionsResult Validate(string? name, AiUsageOptions options)
    {
        var failures = new List<string>();
        var budget = options.MonthlyBudget;

        if (!budget.Enabled)
        {
            return ValidateOptionsResult.Success;
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

            if (model.InputSekPerMillionTokens <= 0
                || model.OutputSekPerMillionTokens <= 0)
            {
                failures.Add(
                    $"AiUsage:MonthlyBudget:Models deployment '{model.Deployment.Trim()}' requires positive input and output prices.");
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
}
