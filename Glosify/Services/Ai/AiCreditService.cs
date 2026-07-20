using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai;

public sealed class AiCreditService : IAiCreditService
{
    private const decimal MicrosPerSek = 1_000_000m;

    private readonly GlosifyContext _context;
    private readonly AiUsageOptions _options;
    private readonly IGenerativeAiModelResolver _modelResolver;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _budgetTimeZone;

    public AiCreditService(
        GlosifyContext context,
        IOptions<AiUsageOptions> options,
        IGenerativeAiModelResolver modelResolver,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _options = options.Value;
        _modelResolver = modelResolver;
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
    // the tracked state and re-running the whole read-modify-write.
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
                _context.ChangeTracker.Clear();
                if (attempt >= maxAttempts)
                {
                    throw;
                }
            }
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

    private Task ApplyTrialGrantIfNeededAsync(AiCreditAccount account, CancellationToken cancellationToken)
    {
        if (account.TrialGrantedAt.HasValue || _options.TrialGrantCredits <= 0)
        {
            return Task.CompletedTask;
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
        long budgetMicros,
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
        if (budget.AvailableMicros < amountMicros)
        {
            throw new MonthlyAiBudgetExceededException(
                periodKey,
                budget.LimitMicros,
                budget.SpentMicros,
                budget.ReservedMicros,
                amountMicros);
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
        var actualMicros = CalculateActualBudgetMicros(usage, price);
        budget.ReservedMicros = Math.Max(0, budget.ReservedMicros - reservedMicros);
        budget.SpentMicros += actualMicros;
        budget.LimitMicros = GetConfiguredLimitMicros();
        budget.UpdatedAt = _timeProvider.GetUtcNow();
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

    private bool IsBudgetedProvider(string provider) =>
        _options.MonthlyBudget.Enabled
        && _options.MonthlyBudget.Providers.Any(candidate =>
            string.Equals(
                candidate?.Trim(),
                provider?.Trim(),
                StringComparison.OrdinalIgnoreCase));

    private AiModelPriceOptions GetModelPrice(string model) =>
        _options.MonthlyBudget.Models.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Deployment?.Trim(),
                model?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"No monthly AI budget price is configured for deployment '{model}'.");

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
