using Microsoft.Extensions.Options;

namespace Glosify.Services.Payments;

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public bool Enabled { get; set; }
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public List<StripeCreditPackageOptions> CreditPackages { get; set; } = [];
}

public sealed class StripeCreditPackageOptions
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Credits { get; set; }
    public string PriceId { get; set; } = string.Empty;
    public long UnitAmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
}

public sealed class StripeOptionsValidator : IValidateOptions<StripeOptions>
{
    public ValidateOptionsResult Validate(string? name, StripeOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("https" or "http")
            || string.IsNullOrWhiteSpace(baseUri.Host))
        {
            errors.Add("Stripe:PublicBaseUrl must be an absolute HTTP(S) URL.");
        }

        if (string.IsNullOrWhiteSpace(options.SecretKey)
            || !(options.SecretKey.StartsWith("rk_", StringComparison.Ordinal)
                || options.SecretKey.StartsWith("sk_", StringComparison.Ordinal)))
        {
            errors.Add("Stripe:SecretKey must be a Stripe secret or restricted key.");
        }

        if (string.IsNullOrWhiteSpace(options.WebhookSecret)
            || !options.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal))
        {
            errors.Add("Stripe:WebhookSecret must be a Stripe webhook signing secret.");
        }

        if (options.CreditPackages.Count == 0)
        {
            errors.Add("Stripe:CreditPackages must contain at least one package when Stripe is enabled.");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in options.CreditPackages)
        {
            if (string.IsNullOrWhiteSpace(package.Key) || !keys.Add(package.Key))
            {
                errors.Add("Stripe credit package keys must be non-empty and unique.");
            }

            if (string.IsNullOrWhiteSpace(package.DisplayName))
            {
                errors.Add($"Stripe credit package '{package.Key}' needs a display name.");
            }

            if (package.Credits <= 0)
            {
                errors.Add($"Stripe credit package '{package.Key}' must grant a positive number of credits.");
            }

            if (string.IsNullOrWhiteSpace(package.PriceId)
                || !package.PriceId.StartsWith("price_", StringComparison.Ordinal)
                || !priceIds.Add(package.PriceId))
            {
                errors.Add($"Stripe credit package '{package.Key}' must use a unique Stripe Price ID.");
            }

            if (package.UnitAmountMinor <= 0)
            {
                errors.Add($"Stripe credit package '{package.Key}' must have a positive minor-unit amount.");
            }

            if (string.IsNullOrWhiteSpace(package.Currency)
                || package.Currency.Length != 3
                || !package.Currency.All(char.IsAsciiLetter))
            {
                errors.Add($"Stripe credit package '{package.Key}' must use a three-letter ISO currency code.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
