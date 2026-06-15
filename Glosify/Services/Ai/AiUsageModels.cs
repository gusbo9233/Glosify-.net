namespace Glosify.Services;

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
