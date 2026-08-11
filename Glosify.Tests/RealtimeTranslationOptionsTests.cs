using Glosify.Services.Ai;
using Glosify.Services.Auth;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationOptionsTests
{
    [Fact]
    public void DisabledFeature_DoesNotRequireSecrets()
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions()),
            ExtensionAuth());
        var result = validator.Validate(null, new RealtimeTranslationOptions { Enabled = false });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EnabledFeature_RequiresFoundryDurationBudgetConfiguration()
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions
                {
                    Enabled = true,
                    Providers = ["foundry"],
                    Models =
                    [
                        new AiModelPriceOptions
                        {
                            Deployment = "gpt-realtime-translate",
                            InputSekPerMillionTokens = 1,
                            OutputSekPerMillionTokens = 1,
                        },
                    ],
                },
            }), ExtensionAuth());
        var result = validator.Validate(null, ValidOptions());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("AudioSekPerMinute", StringComparison.Ordinal));
    }

    [Fact]
    public void EnabledFeature_AcceptsCompletePilotConfiguration()
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions
                {
                    Enabled = true,
                    Providers = ["foundry"],
                    Models =
                    [
                        new AiModelPriceOptions
                        {
                            Deployment = "gpt-realtime-translate",
                            AudioSekPerMinute = 0.5m,
                        },
                        new AiModelPriceOptions
                        {
                            Deployment = "gpt-realtime-translate+gpt-realtime-whisper",
                            AudioSekPerMinute = 1m,
                        },
                    ],
                },
            }), ExtensionAuth());
        Assert.True(validator.Validate(null, ValidOptions()).Succeeded);
    }

    [Fact]
    public void EnabledFeature_RejectsThePublicOpenAiEndpoint()
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }), ExtensionAuth());
        var options = ValidOptions();
        options.FoundryEndpoint = "https://api.openai.com/";

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("FoundryEndpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void EconomicalFeature_RequiresManagedIdentityEndpointsAndAutoDetectCandidates()
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }), ExtensionAuth());
        var options = ValidOptions();
        options.EconomicalEnabled = true;

        var invalid = validator.Validate(null, options);

        Assert.False(invalid.Succeeded);
        Assert.Contains(invalid.Failures!, failure => failure.Contains("SpeechEndpoint", StringComparison.Ordinal));
        Assert.Contains(invalid.Failures!, failure => failure.Contains("SourceLanguages", StringComparison.Ordinal));

        options.SpeechEndpoint = "https://glosify-speech.cognitiveservices.azure.com/";
        options.TranslatorResourceId =
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/glosify/providers/Microsoft.CognitiveServices/accounts/glosify-translator";
        options.TranslatorRegion = "swedencentral";
        options.SourceLanguages =
        [
            new RealtimeTranslationSourceLanguageOptions
            {
                Code = "pl",
                Name = "Polish",
                Locale = "pl-PL",
                TranslatorCode = "pl",
                AutoDetect = true,
            },
        ];
        options.Languages[0].TranslatorCode = "es";

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void EconomicalFeature_RejectsMoreThanFourAtStartCandidatesAndNonAzureTranslatorHosts()
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }), ExtensionAuth());
        var options = ValidOptions();
        options.EconomicalEnabled = true;
        options.SpeechEndpoint = "https://glosify-speech.cognitiveservices.azure.com/";
        options.TranslatorEndpoint = "https://example.test/";
        options.Languages[0].TranslatorCode = "es";
        options.SourceLanguages = Enumerable.Range(1, 5)
            .Select(index => new RealtimeTranslationSourceLanguageOptions
            {
                Code = $"l{index}",
                Name = $"Language {index}",
                Locale = $"l{index}-XX",
                TranslatorCode = $"l{index}",
                AutoDetect = true,
            })
            .ToList();

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("between 1 and 4", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("TranslatorEndpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void EconomicalFeature_CustomTranslatorDomainDoesNotRequireGlobalResourceHeaders()
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }), ExtensionAuth());
        var options = ValidOptions();
        options.EconomicalEnabled = true;
        options.SpeechEndpoint = "https://glosify-speech.cognitiveservices.azure.com/";
        options.TranslatorEndpoint = "https://glosify-translator.cognitiveservices.azure.com/";
        options.Languages[0].TranslatorCode = "es";
        options.SourceLanguages =
        [
            new RealtimeTranslationSourceLanguageOptions
            {
                Code = "pl",
                Name = "Polish",
                Locale = "pl-PL",
                TranslatorCode = "pl",
                AutoDetect = true,
            },
        ];

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    private static RealtimeTranslationOptions ValidOptions() => new()
    {
        Enabled = true,
        FoundryEndpoint = "https://glosify-foundry.openai.azure.com/",
        Deployment = "gpt-realtime-translate",
        SavedSourceTranscriptsEnabled = true,
        SourceTranscriptionDeployment = "gpt-realtime-whisper",
        SavedTranscriptBillingModel = "gpt-realtime-translate+gpt-realtime-whisper",
        Languages =
        [
            new RealtimeTranslationLanguageOptions { Code = "es", Name = "Spanish" },
        ],
    };

    private static IOptions<ExtensionAuthOptions> ExtensionAuth() =>
        Options.Create(new ExtensionAuthOptions
        {
            AllowedRedirectUris = ["https://abcdefghijklmnop.chromiumapp.org/glosify"],
        });
}
