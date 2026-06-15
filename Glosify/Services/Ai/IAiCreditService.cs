namespace Glosify.Services;

public interface IAiCreditService
{
    Task<AiCreditAccountView> GetOrCreateAccountAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(
        string userId,
        int count = 25,
        CancellationToken cancellationToken = default);

    Task<AiCreditReservation> ReserveAsync(
        AiUsageContext context,
        string provider,
        string model,
        int estimatedTokens,
        CancellationToken cancellationToken = default);

    Task CommitUsageAsync(
        Guid reservationId,
        AiTokenUsage usage,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default);

    Task GrantAsync(
        string adminUserId,
        string targetUserId,
        int credits,
        string note,
        CancellationToken cancellationToken = default);
}
