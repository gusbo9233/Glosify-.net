using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai;

public sealed class AiCreditService : IAiCreditService
{
    private const decimal MicrosPerSek = 1_000_000m;

    private readonly GlosifyContext _context;
    private readonly IDbContextFactory<GlosifyContext> _contextFactory;
    private readonly AiUsageOptions _options;
    private readonly IGenerativeAiModelResolver _modelResolver;
    private readonly ITrialEligibilityService _trialEligibility;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _budgetTimeZone;

    public AiCreditService(
        GlosifyContext context,
        IDbContextFactory<GlosifyContext> contextFactory,
        IOptions<AiUsageOptions> options,
        IGenerativeAiModelResolver modelResolver,
        ITrialEligibilityService trialEligibility,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _contextFactory = contextFactory;
        _options = options.Value;
        _modelResolver = modelResolver;
        _trialEligibility = trialEligibility;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _budgetTimeZone = _options.MonthlyBudget.Enabled
            ? TimeZoneInfo.FindSystemTimeZoneById(_options.MonthlyBudget.TimeZoneId)
            : TimeZoneInfo.Utc;
    }

    public Task<AiCreditAccountView> GetOrCreateAccountAsync(
        string userId,
        CancellationToken cancellationToken = default)
        => WithConcurrencyRetryAsync(async () =>
        {
            var account = await GetOrCreateAccountEntityAsync(userId, cancellationToken);
            await ApplyTrialGrantIfNeededAsync(account, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return Map(account);
        });

    public async Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(
        string userId,
        int count = 25,
        CancellationToken cancellationToken = default)
    {
        await GetOrCreateAccountAsync(userId, cancellationToken);
        return await _context.AiCreditTransactions
            .AsNoTracking()
            .Where(transaction => transaction.UserId == userId)
            .OrderByDescending(transaction => transaction.CreatedAt)
            .Take(Math.Clamp(count, 1, 100))
            .ToListAsync(cancellationToken);
    }

    public Task<AiCreditReservation> ReserveAsync(
        AiUsageContext usageContext,
        string provider,
        string model,
        int estimatedTokens,
        CancellationToken cancellationToken = default)
        => WithConcurrencyRetryAsync(() => ReserveCoreAsync(usageContext, provider, model, estimatedTokens, cancellationToken));

    private async Task<AiCreditReservation> ReserveCoreAsync(
        AiUsageContext usageContext,
        string provider,
        string model,
        int estimatedTokens,
        CancellationToken cancellationToken)
    {
        var account = await GetOrCreateAccountEntityAsync(usageContext.UserId, cancellationToken);
        await ApplyTrialGrantIfNeededAsync(account, cancellationToken);

        var requiredCredits = CalculateCredits(estimatedTokens, model);
        if (account.AvailableCredits < requiredCredits)
        {
            throw new InsufficientAiCreditsException(account.AvailableCredits, requiredCredits);
        }

        var budgetReservation = await ReserveMonthlyBudgetAsync(
            provider,
            model,
            estimatedTokens,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var reservationId = Guid.NewGuid();
        account.ReservedCredits += requiredCredits;
        account.UpdatedAt = now;
        _context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = usageContext.UserId,
            ReservationId = reservationId,
            OperationId = usageContext.OperationId,
            AssistantTurnId = usageContext.AssistantTurnId,
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
            BudgetPeriodKey = budgetReservation?.PeriodKey,
            BudgetAmountMicros = budgetReservation?.AmountMicros,
            CreatedAt = now,
        });

        await _context.SaveChangesAsync(cancellationToken);
        return new AiCreditReservation(reservationId, usageContext.UserId, requiredCredits, estimatedTokens);
    }

    public Task CommitUsageAsync(
        Guid reservationId,
        AiTokenUsage usage,
        CancellationToken cancellationToken = default)
        => WithConcurrencyRetryAsync(() => CommitUsageCoreAsync(reservationId, usage, cancellationToken));

    private async Task<bool> CommitUsageCoreAsync(
        Guid reservationId,
        AiTokenUsage usage,
        CancellationToken cancellationToken)
    {
        var reservation = await LoadReservationAsync(reservationId, cancellationToken);
        if (reservation == null)
        {
            return false;
        }

        var account = await GetOrCreateAccountEntityAsync(reservation.UserId, cancellationToken);
        var debitCredits = CalculateCredits(usage.TotalTokens, reservation.Model ?? string.Empty);
        var releaseCredits = Math.Max(0, reservation.CreditAmount - debitCredits);
        var budgetCharge = await CommitMonthlyBudgetAsync(
            reservation,
            usage,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        account.ReservedCredits = Math.Max(0, account.ReservedCredits - reservation.CreditAmount);
        account.BalanceCredits -= debitCredits;
        account.UpdatedAt = now;

        _context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = reservation.UserId,
            ReservationId = reservationId,
            OperationId = reservation.OperationId,
            AssistantTurnId = reservation.AssistantTurnId,
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
            BudgetPeriodKey = reservation.BudgetPeriodKey,
            BudgetAmountMicros = budgetCharge?.ActualMicros,
            CreatedAt = now,
        });

        var releasedBudgetMicros = budgetCharge is null
            ? 0
            : Math.Max(0, budgetCharge.ReservedMicros - budgetCharge.ActualMicros);
        if (releaseCredits > 0 || releasedBudgetMicros > 0)
        {
            _context.AiCreditTransactions.Add(BuildReleaseTransaction(
                reservation,
                account,
                releaseCredits,
                releasedBudgetMicros,
                "Released unused reservation."));
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<AiDurationCreditReservation> ReserveDurationAsync(
        AiUsageContext usageContext,
        string provider,
        string model,
        int durationSeconds,
        int requiredCredits,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }
        if (requiredCredits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredCredits));
        }

        return WithConcurrencyRetryAsync(() => ReserveDurationCoreAsync(
            usageContext,
            provider,
            model,
            durationSeconds,
            requiredCredits,
            cancellationToken));
    }

    private async Task<AiDurationCreditReservation> ReserveDurationCoreAsync(
        AiUsageContext usageContext,
        string provider,
        string model,
        int durationSeconds,
        int requiredCredits,
        CancellationToken cancellationToken)
    {
        var account = await GetOrCreateAccountEntityAsync(usageContext.UserId, cancellationToken);
        await ApplyTrialGrantIfNeededAsync(account, cancellationToken);
        if (account.AvailableCredits < requiredCredits)
        {
            throw new InsufficientAiCreditsException(account.AvailableCredits, requiredCredits);
        }

        var budgetReservation = await ReserveDurationMonthlyBudgetAsync(
            provider,
            model,
            durationSeconds,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var reservationId = Guid.NewGuid();
        account.ReservedCredits += requiredCredits;
        account.UpdatedAt = now;
        _context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = usageContext.UserId,
            ReservationId = reservationId,
            OperationId = usageContext.OperationId,
            AssistantTurnId = usageContext.AssistantTurnId,
            Kind = AiCreditTransactionKinds.Reservation,
            CreditAmount = requiredCredits,
            BalanceAfterCredits = account.BalanceCredits,
            ReservedAfterCredits = account.ReservedCredits,
            Provider = provider,
            Model = model,
            Feature = usageContext.Feature,
            Operation = usageContext.Operation,
            AudioDurationSeconds = durationSeconds,
            RelatedEntityType = usageContext.RelatedEntityType,
            RelatedEntityId = usageContext.RelatedEntityId,
            BudgetPeriodKey = budgetReservation?.PeriodKey,
            BudgetAmountMicros = budgetReservation?.AmountMicros,
            CreatedAt = now,
        });

        await _context.SaveChangesAsync(cancellationToken);
        return new AiDurationCreditReservation(
            reservationId,
            usageContext.UserId,
            requiredCredits,
            durationSeconds);
    }

    public Task CommitDurationUsageAsync(
        Guid reservationId,
        int actualDurationSeconds,
        CancellationToken cancellationToken = default)
    {
        if (actualDurationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actualDurationSeconds));
        }

        return WithConcurrencyRetryAsync(() => CommitDurationUsageCoreAsync(
            reservationId,
            actualDurationSeconds,
            cancellationToken));
    }

    private async Task<bool> CommitDurationUsageCoreAsync(
        Guid reservationId,
        int actualDurationSeconds,
        CancellationToken cancellationToken)
    {
        var reservation = await LoadReservationAsync(reservationId, cancellationToken);
        if (reservation is null)
        {
            return false;
        }

        var account = await GetOrCreateAccountEntityAsync(reservation.UserId, cancellationToken);
        var budgetCharge = await CommitDurationMonthlyBudgetAsync(
            reservation,
            actualDurationSeconds,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        account.ReservedCredits = Math.Max(0, account.ReservedCredits - reservation.CreditAmount);
        account.BalanceCredits -= reservation.CreditAmount;
        account.UpdatedAt = now;

        _context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = reservation.UserId,
            ReservationId = reservationId,
            OperationId = reservation.OperationId,
            AssistantTurnId = reservation.AssistantTurnId,
            Kind = AiCreditTransactionKinds.UsageDebit,
            CreditAmount = -reservation.CreditAmount,
            BalanceAfterCredits = account.BalanceCredits,
            ReservedAfterCredits = account.ReservedCredits,
            Provider = reservation.Provider,
            Model = reservation.Model,
            Feature = reservation.Feature,
            Operation = reservation.Operation,
            AudioDurationSeconds = actualDurationSeconds,
            RelatedEntityType = reservation.RelatedEntityType,
            RelatedEntityId = reservation.RelatedEntityId,
            BudgetPeriodKey = reservation.BudgetPeriodKey,
            BudgetAmountMicros = budgetCharge?.ActualMicros,
            CreatedAt = now,
        });

        var releasedBudgetMicros = budgetCharge is null
            ? 0
            : Math.Max(0, budgetCharge.ReservedMicros - budgetCharge.ActualMicros);
        if (releasedBudgetMicros > 0)
        {
            _context.AiCreditTransactions.Add(BuildReleaseTransaction(
                reservation,
                account,
                0,
                releasedBudgetMicros,
                "Released unused duration budget reservation."));
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
        => WithConcurrencyRetryAsync(() => ReleaseCoreAsync(reservationId, cancellationToken));

    private async Task<bool> ReleaseCoreAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await LoadReservationAsync(reservationId, cancellationToken);
        if (reservation == null)
        {
            return false;
        }

        var account = await GetOrCreateAccountEntityAsync(reservation.UserId, cancellationToken);
        await ReleaseMonthlyBudgetAsync(reservation, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        account.ReservedCredits = Math.Max(0, account.ReservedCredits - reservation.CreditAmount);
        account.UpdatedAt = now;
        _context.AiCreditTransactions.Add(BuildReleaseTransaction(
            reservation,
            account,
            reservation.CreditAmount,
            reservation.BudgetAmountMicros ?? 0,
            "Released reservation."));
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task GrantAsync(
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

        return WithConcurrencyRetryAsync(() => GrantCoreAsync(adminUserId, targetUserId, credits, note, cancellationToken));
    }

    private async Task<bool> GrantCoreAsync(
        string adminUserId,
        string targetUserId,
        int credits,
        string note,
        CancellationToken cancellationToken)
    {
        var account = await GetOrCreateAccountEntityAsync(targetUserId, cancellationToken);
        await ApplyTrialGrantIfNeededAsync(account, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        account.BalanceCredits += credits;
        account.UpdatedAt = now;
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
            CreatedAt = now,
        });
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Each mutating flow reads the account, applies a delta, and saves once. A
    // concurrent request can invalidate the read (RowVersion conflict) or win the
    // race to insert the account row (key conflict); both are resolved by dropping
    // the tracked credit state and re-running the whole read-modify-write.
    private async Task<T> WithConcurrencyRetryAsync<T>(Func<Task<T>> operation)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (DbUpdateException ex) when (IsRetryableCreditConflict(ex))
            {
                DetachCreditEntities();
                if (attempt >= maxAttempts)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Detaches only the entities this service owns, so the read-modify-write can start
    /// from a clean read on the next attempt.
    /// </summary>
    /// <remarks>
    /// This deliberately does not call <c>ChangeTracker.Clear()</c>. The service is
    /// registered scoped and shares the request's <see cref="GlosifyContext"/>, so
    /// clearing detaches everything the caller is tracking too. That caused silent data
    /// loss: <c>RealtimeTranslationService.BeginMinuteAsync</c> holds a tracked session
    /// and minute across <c>CommitDurationUsageAsync</c>, and once they were detached its
    /// later mutations — including <c>ChargedMinutes += 1</c> — were written by a
    /// <c>SaveChangesAsync</c> that silently affected no rows, leaving the user charged
    /// for a minute the session never recorded. <c>CreateSessionAsync</c> lost an Added
    /// transcript the same way and then wrote a session referencing a row that was never
    /// inserted.
    /// </remarks>
    internal void DetachCreditEntities()
    {
        var owned = _context.ChangeTracker
            .Entries()
            .Where(entry => entry.Entity is AiCreditAccount or AiMonthlyBudget or AiCreditTransaction)
            .ToList();

        foreach (var entry in owned)
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsRetryableCreditConflict(DbUpdateException ex)
    {
        return ex is DbUpdateConcurrencyException
            || ex.Entries.Any(entry =>
                entry.Entity is AiCreditAccount or AiMonthlyBudget);
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
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        _context.AiCreditAccounts.Add(account);
        return account;
    }

    private async Task ApplyTrialGrantIfNeededAsync(AiCreditAccount account, CancellationToken cancellationToken)
    {
        if (account.TrialGrantedAt.HasValue || _options.TrialGrantCredits <= 0)
        {
            return;
        }

        if (!await _trialEligibility.IsEligibleAsync(account.UserId, cancellationToken))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
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
        long budgetMicros,
        string note)
    {
        return new AiCreditTransaction
        {
            Id = Guid.NewGuid(),
            UserId = reservation.UserId,
            ReservationId = reservation.ReservationId,
            OperationId = reservation.OperationId,
            AssistantTurnId = reservation.AssistantTurnId,
            Kind = AiCreditTransactionKinds.Release,
            CreditAmount = credits,
            BalanceAfterCredits = account.BalanceCredits,
            ReservedAfterCredits = account.ReservedCredits,
            Provider = reservation.Provider,
            Model = reservation.Model,
            Feature = reservation.Feature,
            Operation = reservation.Operation,
            AudioDurationSeconds = reservation.AudioDurationSeconds,
            Note = note,
            RelatedEntityType = reservation.RelatedEntityType,
            RelatedEntityId = reservation.RelatedEntityId,
            BudgetPeriodKey = reservation.BudgetPeriodKey,
            BudgetAmountMicros = budgetMicros,
            CreatedAt = _timeProvider.GetUtcNow(),
        };
    }

    private async Task<BudgetReservation?> ReserveMonthlyBudgetAsync(
        string provider,
        string model,
        int estimatedTokens,
        CancellationToken cancellationToken)
    {
        if (!IsBudgetedProvider(provider))
        {
            return null;
        }

        var price = GetModelPrice(model);
        var amountMicros = CalculateEstimatedBudgetMicros(estimatedTokens, price);
        var periodKey = GetBudgetPeriodKey();
        var budget = await GetOrCreateMonthlyBudgetAsync(periodKey, cancellationToken);
        if (budget.ExhaustedAt.HasValue || budget.AvailableMicros < amountMicros)
        {
            await MarkExhaustedAndThrowAsync(budget, amountMicros, cancellationToken);
        }

        budget.ReservedMicros += amountMicros;
        budget.UpdatedAt = _timeProvider.GetUtcNow();
        return new BudgetReservation(periodKey, amountMicros);
    }

    private async Task<BudgetReservation?> ReserveDurationMonthlyBudgetAsync(
        string provider,
        string model,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        if (!IsBudgetedProvider(provider))
        {
            return null;
        }

        var price = GetDurationModelPrice(model);
        var amountMicros = ToMicros(
            price.AudioSekPerMinute!.Value
            * Math.Max(0, durationSeconds)
            / 60m
            * MicrosPerSek
            * _options.MonthlyBudget.ReservationSafetyMultiplier);
        var periodKey = GetBudgetPeriodKey();
        var budget = await GetOrCreateMonthlyBudgetAsync(periodKey, cancellationToken);
        if (budget.ExhaustedAt.HasValue || budget.AvailableMicros < amountMicros)
        {
            await MarkExhaustedAndThrowAsync(budget, amountMicros, cancellationToken);
        }

        budget.ReservedMicros += amountMicros;
        budget.UpdatedAt = _timeProvider.GetUtcNow();
        return new BudgetReservation(periodKey, amountMicros);
    }

    private async Task<BudgetCharge?> CommitMonthlyBudgetAsync(
        AiCreditTransaction reservation,
        AiTokenUsage usage,
        CancellationToken cancellationToken)
    {
        if (reservation.BudgetPeriodKey is null
            || reservation.BudgetAmountMicros is not { } reservedMicros)
        {
            return null;
        }

        var budget = await _context.AiMonthlyBudgets
            .SingleAsync(
                item => item.PeriodKey == reservation.BudgetPeriodKey,
                cancellationToken);
        var price = GetModelPrice(reservation.Model ?? string.Empty);
        var calculatedMicros = CalculateActualBudgetMicros(usage, price);
        var configuredLimit = GetConfiguredLimitMicros();
        var chargeCapacity = Math.Max(
            0,
            configuredLimit - budget.SpentMicros - Math.Max(0, budget.ReservedMicros - reservedMicros));
        var actualMicros = Math.Min(calculatedMicros, chargeCapacity);
        var overrunMicros = Math.Max(0, calculatedMicros - chargeCapacity);
        budget.ReservedMicros = Math.Max(0, budget.ReservedMicros - reservedMicros);
        budget.SpentMicros += actualMicros;
        budget.OverrunMicros += overrunMicros;
        budget.LimitMicros = configuredLimit;
        budget.UpdatedAt = _timeProvider.GetUtcNow();
        if (calculatedMicros > chargeCapacity)
        {
            MarkExhausted(budget);
        }
        return new BudgetCharge(reservedMicros, actualMicros);
    }

    private async Task<BudgetCharge?> CommitDurationMonthlyBudgetAsync(
        AiCreditTransaction reservation,
        int actualDurationSeconds,
        CancellationToken cancellationToken)
    {
        if (reservation.BudgetPeriodKey is null
            || reservation.BudgetAmountMicros is not { } reservedMicros)
        {
            return null;
        }

        var budget = await _context.AiMonthlyBudgets.SingleAsync(
            item => item.PeriodKey == reservation.BudgetPeriodKey,
            cancellationToken);
        var price = GetDurationModelPrice(reservation.Model ?? string.Empty);
        var calculatedMicros = ToMicros(
            price.AudioSekPerMinute!.Value
            * Math.Max(0, actualDurationSeconds)
            / 60m
            * MicrosPerSek);
        var configuredLimit = GetConfiguredLimitMicros();
        var chargeCapacity = Math.Max(
            0,
            configuredLimit - budget.SpentMicros - Math.Max(0, budget.ReservedMicros - reservedMicros));
        var actualMicros = Math.Min(calculatedMicros, chargeCapacity);
        var overrunMicros = Math.Max(0, calculatedMicros - chargeCapacity);
        budget.ReservedMicros = Math.Max(0, budget.ReservedMicros - reservedMicros);
        budget.SpentMicros += actualMicros;
        budget.OverrunMicros += overrunMicros;
        budget.LimitMicros = configuredLimit;
        budget.UpdatedAt = _timeProvider.GetUtcNow();
        if (calculatedMicros > chargeCapacity)
        {
            MarkExhausted(budget);
        }
        return new BudgetCharge(reservedMicros, actualMicros);
    }

    private async Task ReleaseMonthlyBudgetAsync(
        AiCreditTransaction reservation,
        CancellationToken cancellationToken)
    {
        if (reservation.BudgetPeriodKey is null
            || reservation.BudgetAmountMicros is not { } reservedMicros)
        {
            return;
        }

        var budget = await _context.AiMonthlyBudgets
            .SingleAsync(
                item => item.PeriodKey == reservation.BudgetPeriodKey,
                cancellationToken);
        budget.ReservedMicros = Math.Max(0, budget.ReservedMicros - reservedMicros);
        budget.LimitMicros = GetConfiguredLimitMicros();
        budget.UpdatedAt = _timeProvider.GetUtcNow();
    }

    private async Task<AiMonthlyBudget> GetOrCreateMonthlyBudgetAsync(
        string periodKey,
        CancellationToken cancellationToken)
    {
        var budget = await _context.AiMonthlyBudgets
            .FirstOrDefaultAsync(item => item.PeriodKey == periodKey, cancellationToken);
        var configuredLimit = GetConfiguredLimitMicros();
        if (budget is not null)
        {
            budget.LimitMicros = configuredLimit;
            return budget;
        }

        var now = _timeProvider.GetUtcNow();
        budget = new AiMonthlyBudget
        {
            PeriodKey = periodKey,
            LimitMicros = configuredLimit,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.AiMonthlyBudgets.Add(budget);
        return budget;
    }

    private async Task MarkExhaustedAndThrowAsync(
        AiMonthlyBudget budget,
        long requiredMicros,
        CancellationToken cancellationToken)
    {
        if (!budget.ExhaustedAt.HasValue)
        {
            MarkExhausted(budget);
            await PersistBudgetExhaustionAsync(budget, cancellationToken);
        }

        var exception = new MonthlyAiBudgetExceededException(
            budget.PeriodKey,
            budget.LimitMicros,
            budget.SpentMicros,
            budget.ReservedMicros,
            requiredMicros,
            GetBudgetResetAtUtc(),
            budget.ExhaustedReason);

        // The isolated context owns the durable exhaustion row. Drop every pending entity
        // owned by this service so a caller that catches the exception cannot accidentally
        // persist an account or trial grant prepared before the budget check. This must also
        // run when the budget was already exhausted. Unrelated caller changes stay tracked.
        DetachCreditEntities();
        throw exception;
    }

    private async Task PersistBudgetExhaustionAsync(
        AiMonthlyBudget source,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var budget = await context.AiMonthlyBudgets
                .SingleOrDefaultAsync(item => item.PeriodKey == source.PeriodKey, cancellationToken);
            if (budget is null)
            {
                budget = new AiMonthlyBudget
                {
                    PeriodKey = source.PeriodKey,
                    LimitMicros = source.LimitMicros,
                    SpentMicros = source.SpentMicros,
                    ReservedMicros = source.ReservedMicros,
                    OverrunMicros = source.OverrunMicros,
                    ExhaustedAt = source.ExhaustedAt,
                    ExhaustedReason = source.ExhaustedReason,
                    CreatedAt = source.CreatedAt,
                    UpdatedAt = source.UpdatedAt,
                };
                context.AiMonthlyBudgets.Add(budget);
            }
            else if (!budget.ExhaustedAt.HasValue)
            {
                budget.LimitMicros = source.LimitMicros;
                budget.ExhaustedAt = source.ExhaustedAt;
                budget.ExhaustedReason = source.ExhaustedReason;
                budget.UpdatedAt = source.UpdatedAt;
            }
            else
            {
                return;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maximumAttempts)
            {
                // Another reservation changed or closed the row; reload on retry.
            }
            catch (DbUpdateException exception)
                when (attempt < maximumAttempts && IsUniquePeriodRace(exception))
            {
                // Two first requests attempted to create the same monthly row.
            }
        }

        throw new DbUpdateConcurrencyException(
            $"Could not persist closure of monthly budget '{source.PeriodKey}'.");
    }

    private static bool IsUniquePeriodRace(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private void MarkExhausted(AiMonthlyBudget budget)
    {
        var now = _timeProvider.GetUtcNow();
        budget.ExhaustedAt ??= now;
        budget.ExhaustedReason ??= PaidServiceGate.BudgetExhaustedReason;
        budget.UpdatedAt = now;
    }

    private bool IsBudgetedProvider(string provider) =>
        _options.MonthlyBudget.MetersProvider(provider);

    // Startup validation covers every deployment configuration can route to, so reaching
    // this throw means something bypassed it — fail closed rather than charge nothing.
    private AiModelPriceOptions GetModelPrice(string model) =>
        _options.MonthlyBudget.FindModelPrice(model)
        ?? throw new InvalidOperationException(
            $"No monthly AI budget price is configured for deployment '{model}'.");

    private AiModelPriceOptions GetDurationModelPrice(string model)
    {
        var price = GetModelPrice(model);
        return price.AudioSekPerMinute is > 0
            ? price
            : throw new InvalidOperationException(
                $"No duration budget price is configured for deployment '{model}'.");
    }

    private long CalculateEstimatedBudgetMicros(
        int estimatedTokens,
        AiModelPriceOptions price)
    {
        var highestPrice = Math.Max(
            price.InputSekPerMillionTokens,
            price.OutputSekPerMillionTokens);
        return ToMicros(
            Math.Max(0, estimatedTokens)
            * highestPrice
            * _options.MonthlyBudget.ReservationSafetyMultiplier);
    }

    private static long CalculateActualBudgetMicros(
        AiTokenUsage usage,
        AiModelPriceOptions price)
    {
        var inputTokens = (long)Math.Max(0, usage.PromptTokens)
            + Math.Max(0, usage.ToolPromptTokens);
        var outputTokens = (long)Math.Max(0, usage.CandidateTokens);
        var classifiedTokens = inputTokens + outputTokens;
        var unclassifiedTokens = Math.Max(0L, (long)usage.TotalTokens - classifiedTokens);
        var highestPrice = Math.Max(
            price.InputSekPerMillionTokens,
            price.OutputSekPerMillionTokens);
        return ToMicros(
            inputTokens * price.InputSekPerMillionTokens
            + outputTokens * price.OutputSekPerMillionTokens
            + unclassifiedTokens * highestPrice);
    }

    private long GetConfiguredLimitMicros() =>
        ToMicros(_options.MonthlyBudget.LimitSek * MicrosPerSek);

    private string GetBudgetPeriodKey()
    {
        var localNow = TimeZoneInfo.ConvertTime(
            _timeProvider.GetUtcNow(),
            _budgetTimeZone);
        return localNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    }

    private DateTimeOffset GetBudgetResetAtUtc()
    {
        var localNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _budgetTimeZone);
        var nextMonth = new DateTime(
            localNow.Year,
            localNow.Month,
            1,
            0,
            0,
            0,
            DateTimeKind.Unspecified).AddMonths(1);
        return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(nextMonth, _budgetTimeZone),
            TimeSpan.Zero);
    }

    private static long ToMicros(decimal micros) =>
        checked((long)decimal.Ceiling(micros));

    private int CalculateCredits(int totalTokens, string model)
    {
        if (totalTokens <= 0)
        {
            return 0;
        }

        var baseCredits =
            (decimal)Math.Ceiling(totalTokens / 1000.0)
            * Math.Max(1, _options.CreditsPerThousandTokens);
        var configuredMultiplier = _modelResolver.GetCreditMultiplier(model);
        var multiplier = configuredMultiplier > 0 ? configuredMultiplier : 1m;
        return Math.Max(1, (int)Math.Ceiling(baseCredits * multiplier));
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

    private sealed record BudgetReservation(string PeriodKey, long AmountMicros);
    private sealed record BudgetCharge(long ReservedMicros, long ActualMicros);
}
