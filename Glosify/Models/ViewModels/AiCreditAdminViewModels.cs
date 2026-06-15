namespace Glosify.Models.ViewModels;

public sealed class AiCreditAdminViewModel
{
    public string? Search { get; set; }
    public IReadOnlyList<AiCreditUserRow> Users { get; set; } = [];
    public AiCreditUserRow? SelectedUser { get; set; }
    public IReadOnlyList<AiCreditTransaction> RecentTransactions { get; set; } = [];
    public AiCreditGrantInput Grant { get; set; } = new();
}

public sealed class AiCreditUserRow
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int BalanceCredits { get; set; }
    public int ReservedCredits { get; set; }
    public int AvailableCredits { get; set; }
    public DateTimeOffset? TrialGrantedAt { get; set; }
}

public sealed class AiCreditGrantInput
{
    public string UserId { get; set; } = string.Empty;
    public string? Search { get; set; }
    public int Credits { get; set; }
    public string Note { get; set; } = string.Empty;
}
