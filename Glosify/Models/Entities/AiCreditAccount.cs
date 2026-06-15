namespace Glosify.Models.Entities;

public sealed class AiCreditAccount
{
    public string UserId { get; set; } = string.Empty;
    public int BalanceCredits { get; set; }
    public int ReservedCredits { get; set; }
    public DateTimeOffset? TrialGrantedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public byte[] RowVersion { get; set; } = [];

    public int AvailableCredits => BalanceCredits - ReservedCredits;
}
