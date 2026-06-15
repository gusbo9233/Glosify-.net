namespace Glosify.Models.Entities;

public sealed class AiCreditTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public Guid? ReservationId { get; set; }
    public string Kind { get; set; } = AiCreditTransactionKinds.UsageDebit;
    public int CreditAmount { get; set; }
    public int BalanceAfterCredits { get; set; }
    public int ReservedAfterCredits { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Feature { get; set; }
    public string? Operation { get; set; }
    public int? PromptTokens { get; set; }
    public int? CandidateTokens { get; set; }
    public int? ThoughtTokens { get; set; }
    public int? ToolPromptTokens { get; set; }
    public int? TotalTokens { get; set; }
    public string? ActorUserId { get; set; }
    public string? Note { get; set; }
    public string? RelatedEntityType { get; set; }
    public string? RelatedEntityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class AiCreditTransactionKinds
{
    public const string TrialGrant = "trial_grant";
    public const string AdminGrant = "admin_grant";
    public const string Reservation = "reservation";
    public const string UsageDebit = "usage_debit";
    public const string Release = "release";
}
