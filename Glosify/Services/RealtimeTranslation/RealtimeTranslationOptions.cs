using System.Diagnostics.CodeAnalysis;
using Glosify.Services.Ai;
using Glosify.Services.Auth;
using Microsoft.Extensions.Options;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationOptions
{
    public const string SectionName = "RealtimeTranslation";

    public bool Enabled { get; set; }
    public string Model { get; set; } = "gpt-realtime-translate";
    public int CreditsPerStartedMinute { get; set; } = 8;
    public int MaxSessionMinutes { get; set; } = 30;
    public int RenewalLeadSeconds { get; set; } = 5;
    public int HeartbeatSeconds { get; set; } = 15;
    public int StaleSessionSeconds { get; set; } = 60;
    public int ReservationExpirySeconds { get; set; } = 120;
    public int RelayTokenLifetimeSeconds { get; set; } = 120;
    public int RelayStartupTimeoutSeconds { get; set; } = 15;
    public int RelayBillingGraceSeconds { get; set; } = 3;
    public string FoundryEndpoint { get; set; } = string.Empty;
    public string Deployment { get; set; } = "gpt-realtime-translate";
    public bool EconomicalEnabled { get; set; }
    public int EconomicalCreditsPerStartedMinute { get; set; } = 4;
    public string EconomicalBillingModel { get; set; } = "azure-speech-standard+azure-translator-nmt";
    public string SpeechEndpoint { get; set; } = string.Empty;
    public string TranslatorEndpoint { get; set; } = "https://api.cognitive.microsofttranslator.com/";
    public string TranslatorResourceId { get; set; } = string.Empty;
    public string TranslatorRegion { get; set; } = string.Empty;
    public int TranslatorTimeoutSeconds { get; set; } = 5;
    public List<RealtimeTranslationSourceLanguageOptions> SourceLanguages { get; set; } = [];
    public bool SavedSourceTranscriptsEnabled { get; set; }
    public string SourceTranscriptionDeployment { get; set; } = "gpt-realtime-whisper";
    public string SavedTranscriptBillingModel { get; set; } = "gpt-realtime-translate+gpt-realtime-whisper";
    public string SourceTranscriptionDelay { get; set; } = "medium";
    public int SavedTranscriptCreditsPerStartedMinute { get; set; } = 16;
    public List<RealtimeTranslationLanguageOptions> Languages { get; set; } = [];

    public RealtimeTranslationLanguageOptions? FindLanguage(string? code) =>
        Languages.FirstOrDefault(language => language.Enabled
            && string.Equals(language.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    public RealtimeTranslationSourceLanguageOptions? FindSourceLanguage(string? code) =>
        SourceLanguages.FirstOrDefault(language => language.Enabled
            && string.Equals(language.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class RealtimeTranslationLanguageOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TranslatorCode { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class RealtimeTranslationSourceLanguageOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;
    public string TranslatorCode { get; set; } = string.Empty;
    public bool AutoDetect { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RealtimeTranslationOptionsValidator : IValidateOptions<RealtimeTranslationOptions>
{
    private readonly AiUsageOptions _aiUsageOptions;
    private readonly ExtensionAuthOptions _extensionAuthOptions;

    public RealtimeTranslationOptionsValidator(
        IOptions<AiUsageOptions> aiUsageOptions,
        IOptions<ExtensionAuthOptions> extensionAuthOptions)
    {
        _aiUsageOptions = aiUsageOptions.Value;
        _extensionAuthOptions = extensionAuthOptions.Value;
    }

    public ValidateOptionsResult Validate(string? name, RealtimeTranslationOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (!TryValidateFoundryEndpoint(options.FoundryEndpoint, out _))
        {
            failures.Add(
                "RealtimeTranslation:FoundryEndpoint must be an Azure OpenAI HTTPS resource root.");
        }
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add("RealtimeTranslation:Model is required.");
        }
        if (string.IsNullOrWhiteSpace(options.Deployment))
        {
            failures.Add("RealtimeTranslation:Deployment is required.");
        }
        if (options.EconomicalEnabled)
        {
            if (!TryValidateCognitiveEndpoint(options.SpeechEndpoint, out _))
            {
                failures.Add("RealtimeTranslation:SpeechEndpoint must be an Azure AI HTTPS custom endpoint.");
            }
            if (!TryValidateTranslatorEndpoint(
                    options.TranslatorEndpoint,
                    out _,
                    out var usesGlobalTranslatorEndpoint))
            {
                failures.Add(
                    "RealtimeTranslation:TranslatorEndpoint must be the Azure Translator global endpoint or an Azure AI custom-domain root.");
            }
            if (usesGlobalTranslatorEndpoint
                && !TryValidateCognitiveResourceId(options.TranslatorResourceId))
            {
                failures.Add(
                    "RealtimeTranslation:TranslatorResourceId must identify the Azure AI resource used with the global Translator endpoint.");
            }
            if (string.IsNullOrWhiteSpace(options.EconomicalBillingModel))
            {
                failures.Add("RealtimeTranslation:EconomicalBillingModel is required when economical subtitles are enabled.");
            }
            if (options.EconomicalCreditsPerStartedMinute <= 0)
            {
                failures.Add("RealtimeTranslation:EconomicalCreditsPerStartedMinute must be greater than zero.");
            }
            if (options.TranslatorTimeoutSeconds is < 1 or > 30)
            {
                failures.Add("RealtimeTranslation:TranslatorTimeoutSeconds must be between 1 and 30.");
            }

            var enabledSources = options.SourceLanguages.Where(language => language.Enabled).ToArray();
            if (enabledSources.Length == 0
                || enabledSources.Any(language => string.IsNullOrWhiteSpace(language.Code)
                    || string.IsNullOrWhiteSpace(language.Name)
                    || string.IsNullOrWhiteSpace(language.Locale)
                    || string.IsNullOrWhiteSpace(language.TranslatorCode))
                || enabledSources.Select(language => language.Code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    != enabledSources.Length)
            {
                failures.Add(
                    "RealtimeTranslation:SourceLanguages must contain unique enabled codes with names, Speech locales, and Translator codes.");
            }
            var autoDetectCount = enabledSources.Count(language => language.AutoDetect);
            if (autoDetectCount is < 1 or > 4)
            {
                failures.Add(
                    "RealtimeTranslation:SourceLanguages must mark between 1 and 4 enabled languages for at-start auto detection.");
            }
        }
        if (options.SavedSourceTranscriptsEnabled
            && string.IsNullOrWhiteSpace(options.SourceTranscriptionDeployment))
        {
            failures.Add("RealtimeTranslation:SourceTranscriptionDeployment is required.");
        }
        if (options.SavedSourceTranscriptsEnabled
            && string.IsNullOrWhiteSpace(options.SavedTranscriptBillingModel))
        {
            failures.Add("RealtimeTranslation:SavedTranscriptBillingModel is required.");
        }
        if (options.SavedSourceTranscriptsEnabled
            && options.SourceTranscriptionDelay is not ("minimal" or "low" or "medium" or "high" or "xhigh"))
        {
            failures.Add("RealtimeTranslation:SourceTranscriptionDelay must be minimal, low, medium, high, or xhigh.");
        }
        if (options.CreditsPerStartedMinute <= 0)
        {
            failures.Add("RealtimeTranslation:CreditsPerStartedMinute must be greater than zero.");
        }
        if (options.SavedSourceTranscriptsEnabled
            && options.SavedTranscriptCreditsPerStartedMinute < options.CreditsPerStartedMinute)
        {
            failures.Add("RealtimeTranslation:SavedTranscriptCreditsPerStartedMinute cannot be lower than CreditsPerStartedMinute.");
        }
        if (options.MaxSessionMinutes is < 1 or > 60)
        {
            failures.Add("RealtimeTranslation:MaxSessionMinutes must be between 1 and 60.");
        }
        if (options.RenewalLeadSeconds is < 1 or > 30)
        {
            failures.Add("RealtimeTranslation:RenewalLeadSeconds must be between 1 and 30.");
        }
        if (options.HeartbeatSeconds is < 5 or > 60)
        {
            failures.Add("RealtimeTranslation:HeartbeatSeconds must be between 5 and 60.");
        }
        if (options.RelayTokenLifetimeSeconds is < 30 or > 300)
        {
            failures.Add("RealtimeTranslation:RelayTokenLifetimeSeconds must be between 30 and 300.");
        }
        if (options.RelayStartupTimeoutSeconds is < 5 or > 30)
        {
            failures.Add("RealtimeTranslation:RelayStartupTimeoutSeconds must be between 5 and 30.");
        }
        if (options.RelayBillingGraceSeconds is < 0 or > 10)
        {
            failures.Add("RealtimeTranslation:RelayBillingGraceSeconds must be between 0 and 10.");
        }

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in options.Languages.Where(language => language.Enabled))
        {
            if (string.IsNullOrWhiteSpace(language.Code)
                || string.IsNullOrWhiteSpace(language.Name)
                || (options.EconomicalEnabled && string.IsNullOrWhiteSpace(language.TranslatorCode))
                || !codes.Add(language.Code.Trim()))
            {
                failures.Add(options.EconomicalEnabled
                    ? "RealtimeTranslation:Languages must contain unique non-empty codes, names, and Translator codes."
                    : "RealtimeTranslation:Languages must contain unique non-empty codes and names.");
                break;
            }
        }
        if (codes.Count == 0)
        {
            failures.Add("RealtimeTranslation:Languages must enable at least one target language.");
        }

        if (_extensionAuthOptions.AllowedRedirectUris.Count == 0
            || _extensionAuthOptions.AllowedRedirectUris.Any(uri =>
                !_extensionAuthOptions.IsAllowedRedirectUri(uri)))
        {
            failures.Add(
                "ExtensionAuth:AllowedRedirectUris must contain at least one exact chromiumapp.org callback URI.");
        }

        if (_aiUsageOptions.MonthlyBudget.Enabled)
        {
            var foundryIsBudgeted = _aiUsageOptions.MonthlyBudget.Providers.Any(provider =>
                string.Equals(provider?.Trim(), RealtimeTranslationConstants.Provider, StringComparison.OrdinalIgnoreCase));
            var durationPrice = _aiUsageOptions.MonthlyBudget.Models.FirstOrDefault(model =>
                string.Equals(model.Deployment?.Trim(), options.Deployment.Trim(), StringComparison.OrdinalIgnoreCase));
            var savedDurationPrice = options.SavedSourceTranscriptsEnabled
                ? _aiUsageOptions.MonthlyBudget.Models.FirstOrDefault(model =>
                    string.Equals(model.Deployment?.Trim(), options.SavedTranscriptBillingModel.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            var economicalDurationPrice = options.EconomicalEnabled
                ? _aiUsageOptions.MonthlyBudget.Models.FirstOrDefault(model =>
                    string.Equals(model.Deployment?.Trim(), options.EconomicalBillingModel.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            if (!foundryIsBudgeted || durationPrice?.AudioSekPerMinute is not > 0
                || (options.SavedSourceTranscriptsEnabled && savedDurationPrice?.AudioSekPerMinute is not > 0)
                || (options.EconomicalEnabled && economicalDurationPrice?.AudioSekPerMinute is not > 0))
            {
                failures.Add("AiUsage:MonthlyBudget must include provider 'foundry' and positive AudioSekPerMinute prices for every enabled realtime subtitle billing model.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryValidateCognitiveEndpoint(string? value, [NotNullWhen(true)] out Uri? endpoint) =>
        TryValidateHttpsEndpoint(value, out endpoint)
        && (endpoint.AbsolutePath == "/" || endpoint.AbsolutePath.Length == 0)
        && endpoint.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);

    internal static bool TryValidateTranslatorEndpoint(
        string? value,
        [NotNullWhen(true)] out Uri? endpoint,
        out bool usesGlobalEndpoint)
    {
        usesGlobalEndpoint = false;
        if (!TryValidateHttpsEndpoint(value, out endpoint)
            || (endpoint.AbsolutePath != "/" && endpoint.AbsolutePath.Length != 0))
        {
            endpoint = null;
            return false;
        }

        if (string.Equals(
                endpoint.Host,
                "api.cognitive.microsofttranslator.com",
                StringComparison.OrdinalIgnoreCase))
        {
            usesGlobalEndpoint = true;
            return true;
        }

        if (endpoint.Host.EndsWith(
                ".cognitiveservices.azure.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        endpoint = null;
        return false;
    }

    internal static bool TryValidateCognitiveResourceId(string? value)
    {
        var resourceId = value?.Trim();
        return !string.IsNullOrWhiteSpace(resourceId)
            && resourceId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            && resourceId.Contains("/resourceGroups/", StringComparison.OrdinalIgnoreCase)
            && resourceId.Contains(
                "/providers/Microsoft.CognitiveServices/accounts/",
                StringComparison.OrdinalIgnoreCase)
            && !resourceId.Any(char.IsWhiteSpace);
    }

    internal static bool TryValidateHttpsEndpoint(string? value, [NotNullWhen(true)] out Uri? endpoint)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            endpoint = null;
            return false;
        }
        return true;
    }

    internal static bool TryValidateFoundryEndpoint(
        string? value,
        [NotNullWhen(true)] out Uri? endpoint)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || (endpoint.AbsolutePath != "/" && endpoint.AbsolutePath.Length != 0))
        {
            endpoint = null;
            return false;
        }

        var host = endpoint.Host;
        if (!host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = null;
            return false;
        }

        return true;
    }
}
