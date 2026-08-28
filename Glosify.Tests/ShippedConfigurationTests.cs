using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Payments;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Binds the configuration the application actually ships with, rather than options built
/// in a test. This catches drift between the code-fixed OpenAI models and budget prices.
/// </summary>
public sealed class ShippedConfigurationTests
{
    [Fact]
    public void StripeCatalogShipsWithTheLivePackageMappings()
    {
        using var factory = new WebApplicationFactory<Program>();
        var stripe = factory.Services.GetRequiredService<IOptions<StripeOptions>>().Value;

        Assert.False(stripe.Enabled);
        Assert.Equal("https://www.glosify.se", stripe.PublicBaseUrl);
        Assert.Collection(
            stripe.CreditPackages,
            package => AssertPackage(
                package, "starter", "500 credits", 500, "price_1U4J8dISaVlY8AHns1cfq0mI", 5900),
            package => AssertPackage(
                package, "standard", "1,000 credits", 1000, "price_1U4J8rISaVlY8AHnIK7euJCd", 10900),
            package => AssertPackage(
                package, "value", "5,000 credits", 5000, "price_1U4J8uISaVlY8AHnN0SZZauF", 52900));
    }

    [Fact]
    public void EveryMeteredDeploymentIsPriced()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var budget = services.GetRequiredService<IOptions<AiUsageOptions>>().Value.MonthlyBudget;
        var openAi = services.GetRequiredService<IOptions<GenerativeAiOptions>>().Value;

        Assert.Equal(180, openAi.TimeoutSeconds);
        Assert.True(budget.MetersProvider(AiUsageProviders.OpenAi));
        Assert.True(budget.HasTokenPrice(OpenAiModels.Luna));
        var price = budget.FindModelPrice(OpenAiModels.Luna);
        Assert.Equal(2.2373m, price?.InputSekPerMillionTokens);
        Assert.Equal(13.4233m, price?.OutputSekPerMillionTokens);
    }

    [Fact]
    public void RealtimeTranslationDeploymentsArePricedWhenEnabled()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var options = services.GetRequiredService<IOptions<RealtimeTranslationOptions>>().Value;
        var budget = services.GetRequiredService<IOptions<AiUsageOptions>>().Value.MonthlyBudget;
        if (!options.Enabled)
        {
            return;
        }

        if (budget.MetersProvider(AiUsageProviders.OpenAi))
        {
            // Realtime translation bills by audio minute rather than tokens.
            Assert.True(
                budget.FindModelPrice(OpenAiModels.RealtimeTranslation)?.AudioSekPerMinute is > 0,
                $"'{OpenAiModels.RealtimeTranslation}' has no AudioSekPerMinute price.");

            if (options.SavedSourceTranscriptsEnabled)
            {
                Assert.True(
                    budget.FindModelPrice(options.SavedTranscriptBillingModel)?.AudioSekPerMinute is > 0,
                    $"RealtimeTranslation:SavedTranscriptBillingModel '{options.SavedTranscriptBillingModel}' "
                    + "has no AudioSekPerMinute price.");
            }
        }
    }

    [Fact]
    public void ScribeSubtitleConfigurationRetainsTheReviewedSafetyLimits()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptions<RealtimeTranslationOptions>>().Value;
        var budget = services.GetRequiredService<IOptions<AiUsageOptions>>().Value.MonthlyBudget;
        var environment = services.GetRequiredService<IHostEnvironment>();
        var shippedConfiguration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(environment.ContentRootPath, "appsettings.json"))
            .Build();
        var shippedRealtime = shippedConfiguration
            .GetSection(RealtimeTranslationOptions.SectionName)
            .Get<RealtimeTranslationOptions>();

        Assert.False(options.EconomicalEnabled);
        Assert.NotNull(shippedRealtime);
        Assert.False(shippedRealtime.Cloudflare.Enabled);
        Assert.Equal(5, options.ElevenLabs.CreditsPerStartedMinute);
        Assert.Contains("cloudflare", budget.Providers, StringComparer.OrdinalIgnoreCase);
        Assert.True(
            budget.FindModelPrice(options.Cloudflare.BillingModel)?.AudioSekPerMinute is >= 0.35m,
            "Cloudflare Scribe subtitles must retain the reviewed 0.35 SEK/minute safety price until invoice data justifies a reviewed change.");
    }

    [Fact]
    public void CustomerCreditPricingResolvesEveryActiveService()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var pricing = scope.ServiceProvider.GetRequiredService<ICreditPricingResolver>();

        Assert.All(pricing.GetCatalog().TokenFeatures, price => Assert.True(price.Value > 0));
        var multiplier = Assert.Single(pricing.GetCatalog().ModelMultipliers);
        Assert.Equal(0.08m, multiplier.Value);
        Assert.Collection(
            pricing.GetCatalog().Subtitles,
            enhanced => Assert.Equal(7m, enhanced.Value),
            cloudflareScribe => Assert.Equal(4m, cloudflareScribe.Value),
            enhancedTranscript => Assert.Equal(8m, enhancedTranscript.Value));
    }

    [Fact]
    public void AssistantHasNoModelCatalogOrPickerService()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<OpenAiGenerativeAiClient>(
            scope.ServiceProvider.GetRequiredService<IGenerativeAiClient>());
    }

    private static void AssertPackage(
        StripeCreditPackageOptions package,
        string key,
        string displayName,
        int credits,
        string priceId,
        long unitAmountMinor)
    {
        Assert.Equal(key, package.Key);
        Assert.Equal(displayName, package.DisplayName);
        Assert.Equal(credits, package.Credits);
        Assert.Equal(priceId, package.PriceId);
        Assert.Equal(unitAmountMinor, package.UnitAmountMinor);
        Assert.Equal("sek", package.Currency);
    }
}
