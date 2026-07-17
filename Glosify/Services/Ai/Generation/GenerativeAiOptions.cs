using Glosify.Services.Ai.Llm;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Generation;

public sealed class GenerativeAiOptions
{
    public const string SectionName = "GenerativeAi";
    public const string FoundryProvider = "Foundry";
    public const string GeminiProvider = "Gemini";

    public string Provider { get; set; } = FoundryProvider;
    public FoundryGenerativeAiOptions Foundry { get; set; } = new();
}

public sealed class FoundryGenerativeAiOptions
{
    public string ProjectEndpoint { get; set; } = string.Empty;
    public string AssistantDeployment { get; set; } = "gpt-5.4-mini";
    public string StructuredDeployment { get; set; } = "gpt-5.4-mini";
    public string VisionDeployment { get; set; } = "gpt-5.4-mini";
    public List<string> AllowedAssistantDeployments { get; set; } = [];
    public List<AssistantModelOptions> AssistantModels { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 180;
}

public sealed class AssistantModelOptions
{
    public string Deployment { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string SpeedTier { get; set; } = string.Empty;
    public string CostTier { get; set; } = string.Empty;
    public decimal CreditMultiplier { get; set; } = 1m;
}

public sealed record AssistantModelChoice(
    string Deployment,
    string DisplayName,
    string Provider,
    string SpeedTier,
    string CostTier,
    decimal CreditMultiplier);

public sealed class GenerativeAiOptionsValidator(IOptions<GeminiOptions> geminiOptions)
    : IValidateOptions<GenerativeAiOptions>
{
    public ValidateOptionsResult Validate(string? name, GenerativeAiOptions options)
    {
        if (!IsProvider(options.Provider, GenerativeAiOptions.FoundryProvider)
            && !IsProvider(options.Provider, GenerativeAiOptions.GeminiProvider))
        {
            return ValidateOptionsResult.Fail(
                "GenerativeAi:Provider must be either 'Foundry' or the explicit rollback value 'Gemini'.");
        }

        if (IsProvider(options.Provider, GenerativeAiOptions.GeminiProvider))
        {
            return string.IsNullOrWhiteSpace(geminiOptions.Value.ApiKey)
                ? ValidateOptionsResult.Fail(
                    "Gemini:ApiKey must be configured when GenerativeAi:Provider is 'Gemini'.")
                : ValidateOptionsResult.Success;
        }

        var foundry = options.Foundry;
        var failures = new List<string>();
        if (!Uri.TryCreate(foundry.ProjectEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("GenerativeAi:Foundry:ProjectEndpoint must be an absolute HTTPS URI.");
        }

        RequireValue(foundry.AssistantDeployment, "AssistantDeployment", failures);
        RequireValue(foundry.StructuredDeployment, "StructuredDeployment", failures);
        RequireValue(foundry.VisionDeployment, "VisionDeployment", failures);

        if (foundry.TimeoutSeconds <= 0)
        {
            failures.Add("GenerativeAi:Foundry:TimeoutSeconds must be positive.");
        }

        var allowlist = foundry.AllowedAssistantDeployments ?? [];
        if (allowlist.Count == 0)
        {
            failures.Add("GenerativeAi:Foundry:AllowedAssistantDeployments must not be empty.");
        }
        else
        {
            if (!allowlist.Any(candidate =>
                    string.Equals(candidate?.Trim(), foundry.AssistantDeployment?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(
                    "GenerativeAi:Foundry:AssistantDeployment must be present in AllowedAssistantDeployments.");
            }

            var duplicateAllowlistDeployment = allowlist
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Select(candidate => candidate.Trim())
                .GroupBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateAllowlistDeployment is not null)
            {
                failures.Add(
                    $"GenerativeAi:Foundry:AllowedAssistantDeployments contains duplicate deployment '{duplicateAllowlistDeployment.Key}'.");
            }
        }

        var assistantModels = foundry.AssistantModels ?? [];
        if (assistantModels.Count == 0)
        {
            failures.Add("GenerativeAi:Foundry:AssistantModels must not be empty.");
        }
        else
        {
            var configuredDeployments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in assistantModels)
            {
                ValidateAssistantModel(model, allowlist, configuredDeployments, failures);
            }

            foreach (var deployment in allowlist.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!configuredDeployments.Contains(deployment.Trim()))
                {
                    failures.Add(
                        $"GenerativeAi:Foundry:AssistantModels must describe allowed deployment '{deployment.Trim()}'.");
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsProvider(string? configured, string expected) =>
        string.Equals(configured?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static void RequireValue(string? value, string property, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"GenerativeAi:Foundry:{property} must not be empty.");
        }
    }

    private static void ValidateAssistantModel(
        AssistantModelOptions model,
        IReadOnlyCollection<string> allowlist,
        ISet<string> configuredDeployments,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(model.Deployment))
        {
            failures.Add("GenerativeAi:Foundry:AssistantModels:Deployment must not be empty.");
            return;
        }

        var deployment = model.Deployment.Trim();
        if (!configuredDeployments.Add(deployment))
        {
            failures.Add(
                $"GenerativeAi:Foundry:AssistantModels contains duplicate deployment '{deployment}'.");
        }

        if (!allowlist.Any(candidate =>
                string.Equals(candidate?.Trim(), deployment, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add(
                $"GenerativeAi:Foundry:AssistantModels deployment '{deployment}' must be present in AllowedAssistantDeployments.");
        }

        if (string.IsNullOrWhiteSpace(model.DisplayName)
            || string.IsNullOrWhiteSpace(model.Provider)
            || string.IsNullOrWhiteSpace(model.SpeedTier)
            || string.IsNullOrWhiteSpace(model.CostTier))
        {
            failures.Add(
                $"GenerativeAi:Foundry:AssistantModels deployment '{deployment}' requires display name, provider, speed tier, and cost tier.");
        }

        if (model.CreditMultiplier <= 0)
        {
            failures.Add(
                $"GenerativeAi:Foundry:AssistantModels deployment '{deployment}' must have a positive CreditMultiplier.");
        }
    }
}

public interface IGenerativeAiModelResolver
{
    string DefaultAssistantModel { get; }
    IReadOnlyList<string> AllowedAssistantModels { get; }
    IReadOnlyList<AssistantModelChoice> AssistantModels { get; }
    string ResolveAssistantModel(string? requestedModel);
    decimal GetCreditMultiplier(string model);
}

public sealed class GenerativeAiModelResolver(
    IOptions<GenerativeAiOptions> options,
    IOptions<GeminiOptions> geminiOptions) : IGenerativeAiModelResolver
{
    private readonly GenerativeAiOptions _options = options.Value;
    private readonly GeminiOptions _geminiOptions = geminiOptions.Value;

    public string DefaultAssistantModel =>
        IsGemini
            ? string.IsNullOrWhiteSpace(_geminiOptions.AssistantModel)
                ? _geminiOptions.StructuredModel
                : _geminiOptions.AssistantModel
            : _options.Foundry.AssistantDeployment;

    public IReadOnlyList<string> AllowedAssistantModels =>
        AssistantModels
            .Select(model => model.Deployment)
            .ToArray();

    public IReadOnlyList<AssistantModelChoice> AssistantModels =>
        IsGemini
            ? new[] { DefaultAssistantModel, _geminiOptions.Model }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => new AssistantModelChoice(
                    value,
                    value,
                    "Gemini",
                    "Rollback",
                    "Legacy",
                    1m))
                .ToArray()
            : (_options.Foundry.AssistantModels ?? [])
                .Where(model => !string.IsNullOrWhiteSpace(model.Deployment))
                .GroupBy(model => model.Deployment.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var model = group.First();
                    return new AssistantModelChoice(
                        model.Deployment.Trim(),
                        model.DisplayName.Trim(),
                        model.Provider.Trim(),
                        model.SpeedTier.Trim(),
                        model.CostTier.Trim(),
                        model.CreditMultiplier);
                })
                .ToArray();

    public string ResolveAssistantModel(string? requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return DefaultAssistantModel;
        }

        var requested = requestedModel.Trim();
        if (!AllowedAssistantModels.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            throw new GenerativeAiValidationException("The requested assistant model is not available.");
        }

        return AllowedAssistantModels.First(value =>
            string.Equals(value, requested, StringComparison.OrdinalIgnoreCase));
    }

    public decimal GetCreditMultiplier(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return 1m;
        }

        return AssistantModels.FirstOrDefault(candidate =>
                   string.Equals(
                       candidate.Deployment,
                       model.Trim(),
                       StringComparison.OrdinalIgnoreCase))
               ?.CreditMultiplier
            ?? 1m;
    }

    private bool IsGemini =>
        string.Equals(
            _options.Provider?.Trim(),
            GenerativeAiOptions.GeminiProvider,
            StringComparison.OrdinalIgnoreCase);
}
