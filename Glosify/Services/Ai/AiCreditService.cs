using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services;

public sealed class AiCreditService : IAiCreditService
{
    private readonly GlosifyContext _context;
    private readonly AiUsageOptions _options;

    public AiCreditService(GlosifyContext context, IOptions<AiUsageOptions> options)
    {
        _context = context;
        _options = options.Value;
    }

    public async Task<AiCreditAccountView> GetOrCreateAccountAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var account = await GetOrCreateAccountEntityAsync(userId, cancellationToken);
        await ApplyTrialGrantIfNeededAsync(account, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return Map(account);
    }

    public async Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(
        string userId,
        int count = 25,
        CancellationToken cancellationToken = default)
    {
        await GetOrCreateAccountAsync(userId, cancellationToken);
        return await _context.AiCreditTransactions
            .Where(transaction => transaction.UserId == userId)
            .OrderByDescending(transaction => transaction.CreatedAt)
            .Take(Math.Clamp(count, 1, 100))
            .ToListAsync(cancellationToken);
    }

    public async Task<AiCreditReservation> ReserveAsync(
        AiUsageContext usageContext,
        string provider,
        string model,
        int estimatedTokens,
        CancellationToken cancellationToken = default)
    {
        var account = await GetOrCreateAccountEntityAsync(usageContext.UserId, cancellationToken);
        await ApplyTrialGrantIfNeededAsync(account, cancellationToken);

        var requiredCredits = CalculateCredits(estimatedTokens);
        if (account.AvailableCredits < requiredCredits)
        {
            throw new InsufficientAiCreditsException(account.AvailableCredits, requiredCredits);
        }

        var reservationId = Guid.NewGuid();
        account.ReservedCredits += requiredCredits;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        _context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = usageContext.UserId,
            ReservationId = reservationId,
            Kind = AiCreditTransactionKinds.Reservation,
            CreditAmount = requiredCredits,
            BalanceAfterCredits = account.BalanceCredits,
            ReservedAfterCredits = account.ReservedCredits,
            Provider = provider,
            Model = model,
            Feature = usageContext.Feature,
            Operation = usageContext.Operation,
            TotalTokens = estimatedTokens,
            RelatedEntityType = usageContext.RelatedEntityType,
            RelatedEntityId = usageContext.RelatedEntityId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync(cancellationToken);
        return new AiCreditReservation(reservationId, usageContext.UserId, requiredCredits, estimatedTokens);
    }

    public async Task CommitUsageAsync(
        Guid reservationId,
        AiTokenUsage usage,
        CancellationToken cancellationToken = default)
    {
        var reservation = await LoadReservationAsync(reservationId, cancellationToken);
        if (reservation == null)
        {
            return;
        }

        var account = await GetOrCreateAccountEntityAsync(reservation.UserId, cancellationToken);
        var debitCredits = CalculateCredits(usage.TotalTokens);
        var releaseCredits = Math.Max(0, reservation.CreditAmount - debitCredits);
        account.ReservedCredits = Math.Max(0, account.ReservedCredits - reservation.CreditAmount);
        account.BalanceCredits -= debitCredits;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        _context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = reservation.UserId,
            ReservationId = reservationId,
            Kind = AiCreditTransactionKinds.UsageDebit,
            CreditAmount = -debitCredits,
            BalanceAfterCredits = account.BalanceCredits,
            ReservedAfterCredits = account.ReservedCredits,
            Provider = reservation.Provider,
            Model = reservation.Model,
            Feature = reservation.Feature,
            Operation = reservation.Operation,
            PromptTokens = usage.PromptTokens,
            CandidateTokens = usage.CandidateTokens,
            ThoughtTokens = usage.ThoughtTokens,
            ToolPromptTokens = usage.ToolPromptTokens,
            TotalTokens = usage.TotalTokens,
            RelatedEntityType = reservation.RelatedEntityType,
            RelatedEntityId = reservation.RelatedEntityId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        if (releaseCredits > 0)
        {
            _context.AiCreditTransactions.Add(BuildReleaseTransaction(
                reservation,
                account,
                releaseCredits,
                "Released unused reserved credits."));
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        var reservation = await LoadReservationAsync(reservationId, cancellationToken);
        if (reservation == null)
        {
            return;
        }

        var account = await GetOrCreateAccountEntityAsync(reservation.UserId, cancellationToken);
        account.ReservedCredits = Math.Max(0, account.ReservedCredits - reservation.CreditAmount);
        account.UpdatedAt = DateTimeOffset.UtcNow;
        _context.AiCreditTransactions.Add(BuildReleaseTransaction(
            reservation,
            account,
            reservation.CreditAmount,
            "Released reserved credits."));
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task GrantAsync(
        string adminUserId,
        string targetUserId,
        int credits,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (credits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(credits), "Grant credits must be greater than zero.");
        }
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("A grant note is required.", nameof(note));
        }

        var account = await GetOrCreateAccountEntityAsync(targetUserId, cancellationToken);
        await ApplyTrialGrantIfNeededAsync(account, cancellationToken);
        account.BalanceCredits += credits;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        _context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = targetUserId,
            Kind = AiCreditTransactionKinds.AdminGrant,
            CreditAmount = credits,
            BalanceAfterCredits = account.BalanceCredits,
            ReservedAfterCredits = account.ReservedCredits,
            ActorUserId = adminUserId,
            Note = note.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<AiCreditAccount> GetOrCreateAccountEntityAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var account = await _context.AiCreditAccounts
            .FirstOrDefaultAsync(existing => existing.UserId == userId, cancellationToken);
        if (account != null)
        {
            return account;
        }

        account = new AiCreditAccount
        {
            UserId = userId,
            BalanceCredits = 0,
            ReservedCredits = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.AiCreditAccounts.Add(account);
        return account;
    }

    private Task ApplyTrialGrantIfNeededAsync(AiCreditAccount account, CancellationToken cancellationToken)
    {
        if (account.TrialGrantedAt.HasValue || _options.TrialGrantCredits <= 0)
        {
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        account.TrialGrantedAt = now;
        account.BalanceCredits += _options.TrialGrantCredits;
        account.UpdatedAt = now;
        _context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            Kind = AiCreditTransactionKinds.TrialGrant,
            CreditAmount = _options.TrialGrantCredits,
            BalanceAfterCredits = account.BalanceCredits,
            ReservedAfterCredits = account.ReservedCredits,
            Note = "One-time trial grant.",
            CreatedAt = now,
        });
        return Task.CompletedTask;
    }

    private async Task<AiCreditTransaction?> LoadReservationAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var reservation = await _context.AiCreditTransactions
            .FirstOrDefaultAsync(transaction =>
                transaction.ReservationId == reservationId
                && transaction.Kind == AiCreditTransactionKinds.Reservation,
                cancellationToken);
        if (reservation == null)
        {
            return null;
        }

        var hasTerminalTransaction = await _context.AiCreditTransactions
            .AnyAsync(transaction =>
                transaction.ReservationId == reservationId
                && (transaction.Kind == AiCreditTransactionKinds.UsageDebit
                    || transaction.Kind == AiCreditTransactionKinds.Release),
                cancellationToken);
        return hasTerminalTransaction ? null : reservation;
    }

    private AiCreditTransaction BuildReleaseTransaction(
        AiCreditTransaction reservation,
        AiCreditAccount account,
        int credits,
        string note)
    {
        return new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = reservation.UserId,
            ReservationId = reservation.ReservationId,
            Kind = AiCreditTransactionKinds.Release,
            CreditAmount = credits,
            BalanceAfterCredits = account.BalanceCredits,
            ReservedAfterCredits = account.ReservedCredits,
            Provider = reservation.Provider,
            Model = reservation.Model,
            Feature = reservation.Feature,
            Operation = reservation.Operation,
            Note = note,
            RelatedEntityType = reservation.RelatedEntityType,
            RelatedEntityId = reservation.RelatedEntityId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private int CalculateCredits(int totalTokens)
    {
        if (totalTokens <= 0)
        {
            return 0;
        }

        return (int)Math.Ceiling(totalTokens / 1000.0) * Math.Max(1, _options.CreditsPerThousandTokens);
    }

    private static AiCreditAccountView Map(AiCreditAccount account)
    {
        return new AiCreditAccountView(
            account.UserId,
            account.BalanceCredits,
            account.ReservedCredits,
            account.AvailableCredits,
            account.TrialGrantedAt);
    }
}
