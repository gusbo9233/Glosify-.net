namespace Glosify.Services.Ai;

/// <summary>Exact ledger arithmetic and the integer presentation boundary.</summary>
public static class CreditAmounts
{
    public static decimal RoundCharge(decimal credits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(credits);
        return decimal.Ceiling(credits * 1_000_000m) / 1_000_000m;
    }

    public static void ValidatePrecision(decimal credits)
    {
        if (credits != decimal.Round(credits, 6) || Math.Abs(credits) >= 10_000_000_000_000m)
            throw new ArgumentOutOfRangeException(nameof(credits), "Credits must fit decimal(19,6).");
    }

    // Preserve the sign for debits, including a negative balance after a refund.
    public static int Display(decimal credits) => checked((int)(Math.Sign(credits) * decimal.Ceiling(Math.Abs(credits))));
}
