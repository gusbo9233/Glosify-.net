using Glosify.Services.Payments;
using Stripe;
using Xunit;

namespace Glosify.Tests;

public sealed class StripeOptionsTests
{
    [Fact]
    public void DocumentedWebhookVersion_MatchesThePinnedStripeSdkVersion()
    {
        Assert.Equal("2026-06-24.dahlia", StripeConfiguration.ApiVersion);
    }

    [Fact]
    public void DisabledStripe_DoesNotRequireSecretsOrPackages()
    {
        var result = new StripeOptionsValidator().Validate(null, new StripeOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EnabledStripe_RequiresValidServerConfiguration()
    {
        var options = new StripeOptions
        {
            Enabled = true,
            PublicBaseUrl = "https://glosify.se",
            SecretKey = "rk_live_example",
            WebhookSecret = "whsec_example",
            CreditPackages =
            [
                new StripeCreditPackageOptions
                {
                    Key = "starter",
                    DisplayName = "Starter",
                    Credits = 100,
                    PriceId = "price_starter",
                    UnitAmountMinor = 5_900,
                    Currency = "sek",
                },
            ],
        };

        Assert.True(new StripeOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void EnabledStripe_RejectsDuplicatePackageKeysAndPriceIds()
    {
        var options = new StripeOptions
        {
            Enabled = true,
            PublicBaseUrl = "https://glosify.se",
            SecretKey = "rk_live_example",
            WebhookSecret = "whsec_example",
            CreditPackages =
            [
                new StripeCreditPackageOptions
                {
                    Key = "starter",
                    DisplayName = "Starter",
                    Credits = 100,
                    PriceId = "price_same",
                    UnitAmountMinor = 5_900,
                    Currency = "sek",
                },
                new StripeCreditPackageOptions
                {
                    Key = "STARTER",
                    DisplayName = "Another",
                    Credits = 200,
                    PriceId = "price_same",
                    UnitAmountMinor = 9_900,
                    Currency = "sek",
                },
            ],
        };

        var result = new StripeOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(5900, "sek", "59.00 SEK")]
    [InlineData(5900, "jpy", "5900 JPY")]
    [InlineData(5900, "kwd", "5.900 KWD")]
    public void PriceFormatter_UsesStripeCurrencyMinorUnits(long amount, string currency, string expected)
    {
        Assert.Equal(expected, StripePriceFormatter.Format(amount, currency));
    }

    [Theory]
    [InlineData(100, 0, 5900, 0)]
    [InlineData(100, 1, 5900, 1)]
    [InlineData(100, 2950, 5900, 50)]
    [InlineData(100, 5900, 5900, 100)]
    [InlineData(100, 9999, 5900, 100)]
    public void RefundedCredits_RoundUpAndNeverExceedThePurchase(
        int credits,
        long refunded,
        long total,
        int expected)
    {
        Assert.Equal(expected, StripePaymentService.CalculateRefundedCredits(credits, refunded, total));
    }

    [Fact]
    public void CheckoutPrice_MustMatchConfiguredAmountCurrencyAndType()
    {
        var package = new StripeCreditPackageOptions
        {
            PriceId = "price_1",
            UnitAmountMinor = 5_900,
            Currency = "sek",
        };
        var price = new Price
        {
            Id = "price_1",
            Active = true,
            Type = "one_time",
            UnitAmount = 5_900,
            Currency = "sek",
        };

        Assert.True(StripePaymentService.PriceMatchesPackage(price, package));

        price.UnitAmount = 590;
        Assert.False(StripePaymentService.PriceMatchesPackage(price, package));
    }
}
