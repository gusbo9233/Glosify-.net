namespace Glosify.Models.Entities;

public sealed class AiCreditAccount
{
    public string UserId { get; set; } = string.Empty;
    public decimal BalanceCredits { get; set; }
    public decimal ReservedCredits { get; set; }
    public DateTimeOffset? TrialGrantedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public byte[] RowVersion { get; set; } = [];

    public decimal AvailableCredits => BalanceCredits - ReservedCredits;
}
