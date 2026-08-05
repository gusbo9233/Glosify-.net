using Glosify.Services.Ai;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Auth;
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
