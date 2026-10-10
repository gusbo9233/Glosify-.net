using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Ai;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Avatar;

public sealed class AvatarBilling(IDbContextFactory<GlosifyContext> factory, AvatarPricing pricing,
    IOptions<AiUsageOptions> options, TimeProvider clock)
{
    public async Task<Guid> ReserveAsync(string userId, Guid sessionId, string kind, decimal units, CancellationToken ct)
    {
        if (kind is not ("recognition" or "speech") || units <= 0 || units > 4000) throw new ArgumentOutOfRangeException(nameof(units));
        var id = Guid.NewGuid();
        await WriteAsync(id, async db =>
        {
            if (await db.Set<AvatarUsageOperation>().AnyAsync(x => x.Id == id, ct)) return;
            var now = clock.GetUtcNow();
            var settings = options.Value.MonthlyBudget;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var period = local.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            var creditRate = pricing.CreditRate(kind);
            var providerRate = pricing.ProviderSekRate(kind);
            var credits = CreditAmounts.RoundCharge(units * creditRate);
            var micros = (long)decimal.Ceiling(units * providerRate * 1_000_000m * settings.ReservationSafetyMultiplier);
            var account = await db.AiCreditAccounts.SingleAsync(x => x.UserId == userId, ct);
            if (account.AvailableCredits < credits) throw new InsufficientAiCreditsException(account.AvailableCredits, credits);
            var budget = await db.AiMonthlyBudgets.FindAsync([period], ct);
            if (budget is null) { budget = new() { PeriodKey = period, CreatedAt = now }; db.Add(budget); }
            budget.LimitMicros = (long)(settings.LimitSek * 1_000_000m);
            if (settings.Enabled && (budget.ExhaustedAt != null || budget.AvailableMicros < micros))
                throw new PaidServicesBudgetExhaustedException(PaidServiceGate.BudgetExhaustedReason,
                    new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(new DateTime(local.Year, local.Month, 1).AddMonths(1), zone)));
            account.ReservedCredits += credits; account.UpdatedAt = now;
            budget.ReservedMicros += micros; budget.UpdatedAt = now;
            var op = new AvatarUsageOperation { Id = id, UserId = userId, SessionId = sessionId, Kind = kind,
                PeriodKey = period, CreditRate = creditRate, ProviderSekRate = providerRate, ReservedCredits = credits,
                ReservedUnits = units, ReservedMicros = micros, ExpiresAt = now.AddMinutes(5) };
            db.Add(op);
            db.Add(Transaction(op, account, AiCreditTransactionKinds.Reservation, credits, now));
            await db.SaveChangesAsync(ct);
        }, ct);
        return id;
    }

    // Persist before submission: an ambiguous network outcome is billed for submitted
    // input, never for an entire unused reservation. Repeated counters are harmless.
    public Task SubmittedAsync(Guid id, decimal units, CancellationToken ct) => WriteAsync(id, async db =>
    {
        var op = await db.Set<AvatarUsageOperation>().SingleAsync(x => x.Id == id, ct);
        if (op.Settled || clock.GetUtcNow() >= op.ExpiresAt) throw new InvalidOperationException("Voice usage reservation expired.");
        if (units < op.SubmittedUnits || units > op.ReservedUnits) throw new InvalidOperationException("Voice usage limit exceeded.");
        op.SubmittedUnits = units;
        await db.SaveChangesAsync(ct);
    }, ct);

    public Task SettleAsync(Guid id, CancellationToken ct) => WriteAsync(id, async db =>
    {
        var op = await db.Set<AvatarUsageOperation>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (op is null || op.Settled) return;
        var account = await db.AiCreditAccounts.SingleAsync(x => x.UserId == op.UserId, ct);
        var budget = await db.AiMonthlyBudgets.SingleAsync(x => x.PeriodKey == op.PeriodKey, ct);
        var debit = Math.Min(op.ReservedCredits, CreditAmounts.RoundCharge(op.SubmittedUnits * op.CreditRate));
        var now = clock.GetUtcNow();
        account.ReservedCredits -= op.ReservedCredits;
        account.BalanceCredits -= debit; account.UpdatedAt = now;
        budget.ReservedMicros -= op.ReservedMicros;
        budget.SpentMicros += (long)decimal.Ceiling(op.SubmittedUnits * op.ProviderSekRate * 1_000_000m);
        budget.UpdatedAt = now;
        op.Settled = true;
        if (debit > 0) db.Add(Transaction(op, account, AiCreditTransactionKinds.UsageDebit, -debit, now));
        if (debit < op.ReservedCredits) db.Add(Transaction(op, account, AiCreditTransactionKinds.Release, op.ReservedCredits - debit, now));
        await db.SaveChangesAsync(ct);
    }, ct);

    public async Task<object> TotalsAsync(string userId, Guid sessionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = sessionId.ToString();
        var amounts = await db.AiCreditTransactions.AsNoTracking().Where(x => x.UserId == userId
            && x.RelatedEntityType == "AvatarSession" && x.RelatedEntityId == key && x.Kind == AiCreditTransactionKinds.UsageDebit)
            .Select(x => new { x.Operation, x.CreditAmount }).ToListAsync(ct);
        var account = await db.AiCreditAccounts.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
        var pending = await db.Set<AvatarUsageOperation>().AnyAsync(x => x.UserId == userId && x.SessionId == sessionId && !x.Settled, ct);
        return new { type = "usage", pending, recognition = -amounts.Where(x => x.Operation == "avatar.recognition").Sum(x => x.CreditAmount),
            replies = -amounts.Where(x => x.Operation == "avatar.reply").Sum(x => x.CreditAmount),
            speech = -amounts.Where(x => x.Operation == "avatar.speech").Sum(x => x.CreditAmount),
            total = -amounts.Sum(x => x.CreditAmount), available = account.AvailableCredits };
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        // Materialize dates for SQLite test compatibility; production filters in SQL.
        var pending = db.Set<AvatarUsageOperation>().AsNoTracking().Where(x => !x.Settled);
        if (db.Database.IsSqlServer()) pending = pending.Where(x => x.ExpiresAt <= now);
        var ids = (await pending.ToListAsync(ct)).Where(x => x.ExpiresAt <= now).Select(x => x.Id);
        foreach (var id in ids) await SettleAsync(id, ct);
    }

    private static AiCreditTransaction Transaction(AvatarUsageOperation op, AiCreditAccount account, string kind, decimal amount, DateTimeOffset now) => new()
    {
        UserId = op.UserId, ReservationId = op.Id, OperationId = op.Id, Kind = kind, CreditAmount = amount,
        BalanceAfterCredits = account.BalanceCredits, ReservedAfterCredits = account.ReservedCredits,
        Feature = "avatar", Operation = "avatar." + op.Kind, Provider = "elevenlabs",
        Model = op.Kind == "speech" ? AvatarOptions.SpeechModel : AvatarOptions.RecognitionModel,
        RelatedEntityType = "AvatarSession", RelatedEntityId = op.SessionId.ToString(),
        Note = $"Submitted {op.SubmittedUnits} {(op.Kind == "speech" ? "characters" : "audio seconds")}", CreatedAt = now,
    };

    private async Task WriteAsync(Guid operationId, Func<GlosifyContext, Task> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            try
            {
                await ResourceAccounting.TransactionAsync(db, async () =>
                {
                    db.ChangeTracker.Clear();
                    // Serialize submission and settlement only for this operation. Shared account
                    // and budget changes remain protected by row versions and the retries below.
                    await ResourceAccounting.LockAsync(db, $"glosify:avatar-billing:{operationId:N}", ct);
                    await action(db); return true;
                }, ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 4) { }
            catch (DbUpdateException ex) when (attempt < 4 && ex.InnerException is SqlException { Number: 2601 or 2627 }) { }
        }
    }
}
