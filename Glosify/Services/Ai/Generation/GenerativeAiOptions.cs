using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Generation;

public static class OpenAiModels
{
    public const string Luna = "gpt-5.6-luna";
    public const string RealtimeTranslation = "gpt-realtime-translate";
}

public sealed class GenerativeAiOptions
{
    public const string SectionName = "GenerativeAi";

    /// <summary>
    /// The direct OpenAI API key. Production supplies this through the
    /// OPENAI_SECRET_KEY App Service setting; it is never serialized to a client.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 180;
}

public sealed class GenerativeAiOptionsValidator(
    IHostEnvironment environment,
    IOptions<AiUsageOptions> aiUsageOptions)
    : IValidateOptions<GenerativeAiOptions>
{
    public ValidateOptionsResult Validate(string? name, GenerativeAiOptions options)
    {
        var failures = new List<string>();
        if (options.TimeoutSeconds <= 0)
        {
            failures.Add("GenerativeAi:TimeoutSeconds must be positive.");
        }

        if (!environment.IsDevelopment()
            && !environment.IsEnvironment("Testing")
            && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add(
                "OPENAI_SECRET_KEY must be configured outside development and testing.");
        }

        var budget = aiUsageOptions.Value.MonthlyBudget;
        if (budget.MetersProvider(AiUsageProviders.OpenAi)
            && !budget.HasTokenPrice(OpenAiModels.Luna))
        {
            failures.Add(
                $"AiUsage:MonthlyBudget:Models must price model '{OpenAiModels.Luna}'.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
