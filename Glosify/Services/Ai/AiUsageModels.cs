namespace Glosify.Services.Ai;

public sealed record AiUsageContext(
    string UserId,
    string Feature,
    string Operation,
    Guid OperationId,
    string? RelatedEntityType = null,
    string? RelatedEntityId = null);

public sealed record AiTokenUsage(
    int PromptTokens,
    int CandidateTokens,
    int ThoughtTokens,
    int ToolPromptTokens,
    int TotalTokens);

public sealed record AiCreditReservation(
    Guid ReservationId,
    string UserId,
    int ReservedCredits,
    int EstimatedTokens);

public sealed record AiCreditAccountView(
    string UserId,
    int BalanceCredits,
    int ReservedCredits,
    int AvailableCredits,
    DateTimeOffset? TrialGrantedAt);

public sealed class InsufficientAiCreditsException : InvalidOperationException
{
    public InsufficientAiCreditsException(int availableCredits, int requiredCredits)
        : base($"You need {requiredCredits} AI credits for this request, but you have {availableCredits} available.")
    {
        AvailableCredits = availableCredits;
        RequiredCredits = requiredCredits;
    }

    public int AvailableCredits { get; }
    public int RequiredCredits { get; }
}

public sealed class MonthlyAiBudgetExceededException : InvalidOperationException
{
    public MonthlyAiBudgetExceededException(
        string periodKey,
        long limitMicros,
        long spentMicros,
        long reservedMicros,
        long requiredMicros)
        : base("AI is temporarily unavailable because this request would exceed the application's monthly budget.")
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
