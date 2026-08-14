using System.Globalization;

namespace Glosify.Services.Payments;

public static class StripePriceFormatter
{
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg",
        "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf",
    };

    private static readonly HashSet<string> ThreeDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "bhd", "jod", "kwd", "omr", "tnd",
    };

    public static string Format(long unitAmountMinor, string currency)
    {
        var exponent = ZeroDecimalCurrencies.Contains(currency)
            ? 0
            : ThreeDecimalCurrencies.Contains(currency)
                ? 3
                : 2;
        var divisor = exponent switch
        {
            0 => 1m,
            3 => 1_000m,
            _ => 100m,
        };
        var amount = unitAmountMinor / divisor;
        return $"{amount.ToString($"F{exponent}", CultureInfo.InvariantCulture)} {currency.ToUpperInvariant()}";
    }
}
