using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Speaking;

public sealed class SpeakingOptions
{
    public const string SectionName = "Speaking";

    public int SessionTtlMinutes { get; set; } = 60;
    public int MaxSessionsPerUser { get; set; } = 3;
    public bool InteractiveBartenderEnabled { get; set; }
    public bool GenericTutorEnabled { get; set; }
}

/// <summary>Speaking text turns use the fixed direct OpenAI Luna model.</summary>
public sealed class SpeakingOptionsValidator(IOptions<AiUsageOptions> aiUsageOptions)
    : IValidateOptions<SpeakingOptions>
{
    public ValidateOptionsResult Validate(string? name, SpeakingOptions options)
    {
        var budget = aiUsageOptions.Value.MonthlyBudget;
        if (!budget.MetersProvider(AiUsageProviders.OpenAi))
        {
            return ValidateOptionsResult.Success;
        }

        return budget.HasTokenPrice(OpenAiModels.Luna)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"AiUsage:MonthlyBudget:Models must price model '{OpenAiModels.Luna}', "
                + "which speaking turns use. Without a price every speaking turn fails at "
                + "credit reservation.");
    }
}
