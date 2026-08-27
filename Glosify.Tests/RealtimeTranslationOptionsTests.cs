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
    public void EnabledFeature_RequiresOpenAiDurationBudgetConfiguration()
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions
                {
                    Enabled = true,
                    Providers = ["openai", "elevenlabs"],
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
                    Providers = ["openai", "elevenlabs"],
                    Models =
                    [
                        new AiModelPriceOptions
                        {
                            Deployment = "gpt-realtime-translate",
                            AudioSekPerMinute = 0.5m,
                        },
                        new AiModelPriceOptions
                        {
                            Deployment = "gpt-realtime-translate+elevenlabs-scribe-v2-realtime",
                            AudioSekPerMinute = 1m,
                        },
                        new AiModelPriceOptions
                        {
                            Deployment = "elevenlabs-scribe-v2-realtime+azure-translator-nmt",
                            AudioSekPerMinute = 0.4m,
                        },
                    ],
                },
            }), ExtensionAuth());
        Assert.True(validator.Validate(null, ValidOptions()).Succeeded);
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
        options.SpeechEndpoint = "https://glosify-speech.cognitiveservices.azure.com/sts/v1.0/";
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
        Assert.Contains(result.Failures!, failure => failure.Contains("SpeechEndpoint", StringComparison.Ordinal));
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

    [Fact]
    public void ElevenLabsFeature_RequiresSecureConfigurationAndBudgetCoverage()
    {
        var aiUsage = new AiUsageOptions
        {
            MonthlyBudget = new AiMonthlyBudgetOptions
            {
                Enabled = true,
                Providers = ["openai"],
                Models =
                [
                    new AiModelPriceOptions
                    {
                        Deployment = "gpt-realtime-translate",
                        AudioSekPerMinute = 0.5m,
                    },
                    new AiModelPriceOptions
                    {
                        Deployment = "gpt-realtime-translate+elevenlabs-scribe-v2-realtime",
                        AudioSekPerMinute = 1m,
                    },
                ],
            },
        };
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(aiUsage),
            ExtensionAuth());
        var options = ValidOptions();
        ConfigureScribe(options);
        options.ElevenLabs.Enabled = true;
        options.ElevenLabs.ApiKey = string.Empty;
        options.ElevenLabs.Endpoint = "https://example.test/";
        options.ElevenLabs.Model = "scribe_v2";
        options.ElevenLabs.CreditsPerStartedMinute = 0;
        options.ElevenLabs.VadSilenceThresholdSeconds = 0.1;

        var invalid = validator.Validate(null, options);

        Assert.False(invalid.Succeeded);
        Assert.Contains(invalid.Failures!, failure => failure.Contains("ElevenLabs:Endpoint", StringComparison.Ordinal));
        Assert.Contains(invalid.Failures!, failure => failure.Contains("ElevenLabs:ApiKey", StringComparison.Ordinal));
        Assert.Contains(invalid.Failures!, failure => failure.Contains("ElevenLabs:Model", StringComparison.Ordinal));
        Assert.Contains(invalid.Failures!, failure => failure.Contains("ElevenLabs:CreditsPerStartedMinute", StringComparison.Ordinal));
        Assert.Contains(invalid.Failures!, failure => failure.Contains("VadSilenceThresholdSeconds", StringComparison.Ordinal));
        Assert.Contains(invalid.Failures!, failure => failure.Contains("every enabled", StringComparison.Ordinal));

        options.ElevenLabs.Endpoint = "wss://api.elevenlabs.io/v1/speech-to-text/realtime";
        options.ElevenLabs.ApiKey = "test-key";
        options.ElevenLabs.Model = "scribe_v2_realtime";
        options.ElevenLabs.CreditsPerStartedMinute = 7;
        options.ElevenLabs.VadSilenceThresholdSeconds = 1.5;
        aiUsage.MonthlyBudget.Providers.Add("elevenlabs");
        aiUsage.MonthlyBudget.Models.Add(new AiModelPriceOptions
        {
            Deployment = options.ElevenLabs.BillingModel,
            AudioSekPerMinute = 0.4m,
        });

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("ws://api.elevenlabs.io/v1/speech-to-text/realtime")]
    [InlineData("wss://user:password@api.elevenlabs.io/v1/speech-to-text/realtime")]
    [InlineData("wss://api.elevenlabs.io/v1/speech-to-text/realtime?debug=true")]
    [InlineData("wss://api.elevenlabs.io/v1/speech-to-text/realtime#fragment")]
    [InlineData("wss://elevenlabs.io.attacker.test/v1/speech-to-text/realtime")]
    [InlineData("wss://api.elevenlabs.io/v1/text-to-speech/realtime")]
    public void ElevenLabsFeature_RejectsUnsafeEndpointVariants(string endpoint)
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }), ExtensionAuth());
        var options = ValidOptions();
        ConfigureScribe(options);
        options.ElevenLabs.Endpoint = endpoint;

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("ElevenLabs:Endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void ElevenLabsPartialScheduler_DefaultsAndFinalOnlyModeAreValid()
    {
        var defaults = new ElevenLabsRealtimeSpeechOptions();
        Assert.True(defaults.TranslatePartials);
        Assert.Equal(1, defaults.PartialInitialDelaySeconds);
        Assert.Equal(2, defaults.PartialIntervalSeconds);
        Assert.Equal(8, defaults.PartialMinimumGrowthCharacters);
        Assert.Equal(10, defaults.AutoDetectedLanguageRefreshSeconds);

        var options = ValidOptions();
        ConfigureScribe(options);
        options.ElevenLabs.TranslatePartials = false;

        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions { MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false } }),
            ExtensionAuth());

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void ElevenLabsPartialScheduler_AcceptsLegacyRollbackValues()
    {
        var options = ValidOptions();
        ConfigureScribe(options);
        options.ElevenLabs.PartialInitialDelaySeconds = 0;
        options.ElevenLabs.PartialIntervalSeconds = 0.75;
        options.ElevenLabs.PartialMinimumGrowthCharacters = 1;
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions { MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false } }),
            ExtensionAuth());

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(-0.01, 2, 8, "PartialInitialDelaySeconds")]
    [InlineData(10.01, 2, 8, "PartialInitialDelaySeconds")]
    [InlineData(double.NaN, 2, 8, "PartialInitialDelaySeconds")]
    [InlineData(1, 0.74, 8, "PartialIntervalSeconds")]
    [InlineData(1, 10.01, 8, "PartialIntervalSeconds")]
    [InlineData(1, double.PositiveInfinity, 8, "PartialIntervalSeconds")]
    [InlineData(1, 2, 0, "PartialMinimumGrowthCharacters")]
    [InlineData(1, 2, 101, "PartialMinimumGrowthCharacters")]
    public void ElevenLabsPartialScheduler_RejectsInvalidValues(
        double initialDelay,
        double interval,
        int minimumGrowth,
        string expectedFailure)
    {
        var options = ValidOptions();
        ConfigureScribe(options);
        options.ElevenLabs.PartialInitialDelaySeconds = initialDelay;
        options.ElevenLabs.PartialIntervalSeconds = interval;
        options.ElevenLabs.PartialMinimumGrowthCharacters = minimumGrowth;
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions { MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false } }),
            ExtensionAuth());

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30.01)]
    [InlineData(double.NaN)]
    public void ElevenLabsPartialScheduler_RejectsInvalidLanguageRefresh(double refreshSeconds)
    {
        var options = ValidOptions();
        ConfigureScribe(options);
        options.ElevenLabs.AutoDetectedLanguageRefreshSeconds = refreshSeconds;
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions { MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false } }),
            ExtensionAuth());

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("AutoDetectedLanguageRefreshSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ElevenLabsFeature_RejectsOutOfRangeVadThreshold(double threshold)
    {
        var validator = new RealtimeTranslationOptionsValidator(
            Options.Create(new AiUsageOptions
            {
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }), ExtensionAuth());
        var options = ValidOptions();
        ConfigureScribe(options);
        options.ElevenLabs.VadThreshold = threshold;

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("VadThreshold", StringComparison.Ordinal));
    }

    private static void ConfigureScribe(RealtimeTranslationOptions options)
    {
        ConfigureSpeechRecognitionTranslation(options);
    }

    private static void ConfigureSpeechRecognitionTranslation(RealtimeTranslationOptions options)
    {
        options.TranslatorResourceId =
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/glosify/providers/Microsoft.CognitiveServices/accounts/glosify-translator";
        options.TranslatorRegion = "swedencentral";
        options.Languages[0].TranslatorCode = "es";
        options.SourceLanguages =
        [
            new RealtimeTranslationSourceLanguageOptions
            {
                Code = "pl",
                Name = "Polish",
                Locale = "pl-PL",
                TranslatorCode = "pl",
                ScribeCode = "pl",
                AutoDetect = true,
            },
        ];
    }

    private static RealtimeTranslationOptions ValidOptions() => new()
    {
        Enabled = true,
        SavedSourceTranscriptsEnabled = true,
        SavedTranscriptBillingModel = "gpt-realtime-translate+elevenlabs-scribe-v2-realtime",
        TranslatorResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/glosify/providers/Microsoft.CognitiveServices/accounts/glosify-translator",
        TranslatorRegion = "swedencentral",
        ElevenLabs = new ElevenLabsRealtimeSpeechOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            CreditsPerStartedMinute = 6,
        },
        Languages =
        [
            new RealtimeTranslationLanguageOptions { Code = "es", Name = "Spanish", TranslatorCode = "es" },
        ],
    };

    private static IOptions<ExtensionAuthOptions> ExtensionAuth() =>
        Options.Create(new ExtensionAuthOptions
        {
            AllowedRedirectUris = ["https://abcdefghijklmnop.chromiumapp.org/glosify"],
        });
}
