using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Speaking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Binds the configuration the application actually ships with, rather than options built
/// in a test. Every other options test hand-builds its input, which is why repointing
/// GenerativeAi:Foundry at a new project with new deployments left the budget price table
/// stale without anything failing until the first request reached credit reservation.
/// </summary>
public sealed class ShippedConfigurationTests
{
    [Fact]
    public void EveryMeteredDeploymentIsPriced()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var budget = services.GetRequiredService<IOptions<AiUsageOptions>>().Value.MonthlyBudget;
        var foundry = services.GetRequiredService<IOptions<GenerativeAiOptions>>().Value.Foundry;
        var speaking = services.GetRequiredService<IOptions<SpeakingOptions>>().Value;

        var unpriced = new List<string>();

        if (budget.MetersProvider(AiUsageProviders.Foundry))
        {
            string[] routable =
            [
                foundry.AssistantDeployment,
                foundry.StructuredDeployment,
                foundry.VisionDeployment,
                foundry.PageTranslationDeployment,
                foundry.PageTranslationFallbackDeployment,
                .. foundry.AllowedAssistantDeployments,
            ];
            unpriced.AddRange(routable
                .Where(deployment => !string.IsNullOrWhiteSpace(deployment))
                .Select(deployment => deployment.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(deployment => !budget.HasTokenPrice(deployment))
                .Select(deployment => $"GenerativeAi:Foundry -> {deployment}"));
        }

        if (budget.MetersProvider(AiUsageProviders.AzureAiFoundry)
            && !string.IsNullOrWhiteSpace(speaking.ModelDeployment)
            && !budget.HasTokenPrice(speaking.ModelDeployment))
        {
            unpriced.Add($"Speaking:ModelDeployment -> {speaking.ModelDeployment.Trim()}");
        }

        Assert.True(
            unpriced.Count == 0,
            "Deployments reachable from the shipped configuration have no AiUsage:MonthlyBudget:Models "
            + $"price and would fail at credit reservation: {string.Join(", ", unpriced)}");
    }

    [Fact]
    public void RealtimeTranslationDeploymentsArePricedWhenEnabled()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var options = services.GetRequiredService<IOptions<RealtimeTranslationOptions>>().Value;
        var budget = services.GetRequiredService<IOptions<AiUsageOptions>>().Value.MonthlyBudget;
        if (!options.Enabled || !budget.MetersProvider(AiUsageProviders.Foundry))
        {
            return;
        }

        // Realtime translation bills by audio minute rather than tokens.
        Assert.True(
            budget.FindModelPrice(options.Deployment)?.AudioSekPerMinute is > 0,
            $"RealtimeTranslation:Deployment '{options.Deployment}' has no AudioSekPerMinute price.");

        if (options.SavedSourceTranscriptsEnabled)
        {
            Assert.True(
                budget.FindModelPrice(options.SavedTranscriptBillingModel)?.AudioSekPerMinute is > 0,
                $"RealtimeTranslation:SavedTranscriptBillingModel '{options.SavedTranscriptBillingModel}' "
                + "has no AudioSekPerMinute price.");
        }
        if (options.EconomicalEnabled)
        {
            Assert.True(
                budget.FindModelPrice(options.EconomicalBillingModel)?.AudioSekPerMinute is >= 0.35m,
                $"RealtimeTranslation:EconomicalBillingModel '{options.EconomicalBillingModel}' "
                + "must retain the conservative 0.35 SEK/minute safety price until invoice data justifies a reviewed change.");
        }
    }

    [Fact]
    public void EconomicalSubtitleConfigurationRetainsTheReviewedSafetyLimits()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptions<RealtimeTranslationOptions>>().Value;
        var budget = services.GetRequiredService<IOptions<AiUsageOptions>>().Value.MonthlyBudget;

        Assert.Equal(
            ["de", "et", "pl", "uk"],
            options.SourceLanguages
                .Where(language => language.Enabled && language.AutoDetect)
                .Select(language => language.Code)
                .Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(
            options.SourceLanguages.Where(language => language.Enabled),
            language => Assert.False(string.IsNullOrWhiteSpace(language.TranslatorCode)));
        Assert.All(
            options.Languages.Where(language => language.Enabled),
            language => Assert.False(string.IsNullOrWhiteSpace(language.TranslatorCode)));
        Assert.True(
            budget.FindModelPrice(options.EconomicalBillingModel)?.AudioSekPerMinute is >= 0.35m,
            "Economical subtitles must retain the reviewed 0.35 SEK/minute safety price until invoice data justifies a reviewed change.");
    }

    [Fact]
    public void AssistantModelMetadataCoversTheAllowlist()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IGenerativeAiModelResolver>();

        // The model menu is built from this, so an allowlisted deployment with no metadata
        // would be selectable by API but invisible in the UI.
        Assert.NotEmpty(resolver.AssistantModels);
        Assert.Contains(
            resolver.AssistantModels,
            model => string.Equals(
                model.Deployment,
                resolver.DefaultAssistantModel,
                StringComparison.OrdinalIgnoreCase));
    }
}
