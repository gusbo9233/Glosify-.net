namespace Glosify.Services.Ai;

public sealed record AiUsageContext(
    string UserId,
    string Feature,
    string Operation,
    Guid OperationId,
    string? RelatedEntityType = null,
    string? RelatedEntityId = null,
    Guid? AssistantTurnId = null);

public sealed record AiTokenUsage(
    int PromptTokens,
    int CandidateTokens,
    int ThoughtTokens,
    int ToolPromptTokens,
    int TotalTokens);

public sealed record AiCreditReservation(
    Guid ReservationId,
    string UserId,
    decimal ReservedCredits,
    int EstimatedTokens);

public sealed record AiDurationCreditReservation(
    Guid ReservationId,
    string UserId,
    int ReservedCredits,
    int ReservedDurationSeconds);

public sealed record AiCreditAccountView(
    string UserId,
    decimal BalanceCredits,
    decimal ReservedCredits,
    decimal AvailableCredits,
    DateTimeOffset? TrialGrantedAt);

public sealed class InsufficientAiCreditsException : InvalidOperationException
{
    public InsufficientAiCreditsException(decimal availableCredits, decimal requiredCredits)
        : base("Your exact credit balance is insufficient for this request. Displayed balances are rounded up; add credits or reduce the request.")
    {
        AvailableCredits = availableCredits;
        RequiredCredits = requiredCredits;
    }

    public decimal AvailableCredits { get; }
    public decimal RequiredCredits { get; }
}

public class PaidServicesBudgetExhaustedException : InvalidOperationException
{
    public PaidServicesBudgetExhaustedException(string reason, DateTimeOffset resetsAtUtc)
        : base(reason)
    {
        Reason = reason;
        ResetsAtUtc = resetsAtUtc;
    }

    public string Reason { get; }
    public DateTimeOffset ResetsAtUtc { get; }
}

public sealed class MonthlyAiBudgetExceededException : PaidServicesBudgetExhaustedException
{
    public MonthlyAiBudgetExceededException(
        string periodKey,
        long limitMicros,
        long spentMicros,
        long reservedMicros,
        long requiredMicros,
        DateTimeOffset? resetsAtUtc = null,
        string? reason = null)
        : base(
            reason ?? PaidServiceGate.BudgetExhaustedReason,
            resetsAtUtc ?? DateTimeOffset.MaxValue)
    {
        PeriodKey = periodKey;
        LimitMicros = limitMicros;
        SpentMicros = spentMicros;
        ReservedMicros = reservedMicros;
        RequiredMicros = requiredMicros;
    }

    public string PeriodKey { get; }
    public long LimitMicros { get; }
    public long SpentMicros { get; }
    public long ReservedMicros { get; }
    public long RequiredMicros { get; }
}
