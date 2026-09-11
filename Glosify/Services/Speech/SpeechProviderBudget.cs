using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;

namespace Glosify.Services.Speech;

public sealed class SpeechBudgetReservation
{
    public Guid Id { get; set; }
    public string PeriodKey { get; set; } = "";
    public long AmountMicros { get; set; }
    public long ActualMicros { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Settled { get; set; }
}

internal sealed class SpeechBudgetConfiguration : IEntityTypeConfiguration<SpeechBudgetReservation>
{
    public void Configure(EntityTypeBuilder<SpeechBudgetReservation> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.PeriodKey).HasMaxLength(7); b.HasIndex(x => x.ExpiresAt);
    }
}

public interface ISpeechProviderBudget
{
    Task<Guid> ReserveAsync(int characters, CancellationToken ct);
    Task SettleAsync(Guid id, bool charge, CancellationToken ct);
}

public sealed class SpeechProviderBudget(IDbContextFactory<GlosifyContext> factory, IOptions<AiUsageOptions> options, TimeProvider clock) : ISpeechProviderBudget
{
    public async Task<Guid> ReserveAsync(int characters, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var settings = options.Value.MonthlyBudget;
        var price = settings.FindModelPrice(ElevenLabsTextToSpeechService.Model)?.TextSekPerMillionCharacters;
        if (price is not > 0) throw new InvalidOperationException("Configure a positive ElevenLabs v3 per-character price before enabling speech.");
        await RetryAsync(async db =>
        {
            if (await db.Set<SpeechBudgetReservation>().AnyAsync(x => x.Id == id, ct)) return;
            var now = clock.GetUtcNow();
            var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
            var period = TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            var actual = (long)decimal.Ceiling(characters * price.Value);
            var reserved = (long)decimal.Ceiling(actual * settings.ReservationSafetyMultiplier);
            var budget = await db.AiMonthlyBudgets.FindAsync([period], ct);
            if (budget is null)
            {
                budget = new() { PeriodKey = period, CreatedAt = now, LimitMicros = (long)(settings.LimitSek * 1_000_000) };
                db.Add(budget);
            }
            budget.LimitMicros = (long)(settings.LimitSek * 1_000_000);
            if (settings.Enabled && (budget.ExhaustedAt != null || budget.AvailableMicros < reserved))
                throw new PaidServicesBudgetExhaustedException(PaidServiceGate.BudgetExhaustedReason,
                    new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
                        new DateTime(TimeZoneInfo.ConvertTime(now, zone).Year, TimeZoneInfo.ConvertTime(now, zone).Month, 1).AddMonths(1), zone)));
            budget.ReservedMicros += reserved;
            budget.UpdatedAt = now;
            db.Add(new SpeechBudgetReservation { Id = id, PeriodKey = period, AmountMicros = reserved,
                ActualMicros = actual, ExpiresAt = now.AddMinutes(5) });
            await db.SaveChangesAsync(ct);
        }, ct);
        return id;
    }

    public Task SettleAsync(Guid id, bool charge, CancellationToken ct) => RetryAsync(async db =>
    {
        var reservation = await db.Set<SpeechBudgetReservation>().FindAsync([id], ct);
        if (reservation is null || reservation.Settled) return;
        var budget = await db.AiMonthlyBudgets.SingleAsync(x => x.PeriodKey == reservation.PeriodKey, ct);
        budget.ReservedMicros = Math.Max(0, budget.ReservedMicros - reservation.AmountMicros);
        if (charge) budget.SpentMicros += reservation.ActualMicros;
        budget.UpdatedAt = clock.GetUtcNow();
        reservation.Settled = true;
        await db.SaveChangesAsync(ct);
    }, ct);

    private async Task RetryAsync(Func<GlosifyContext, Task> work, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            try
            {
                await ResourceAccounting.TransactionAsync(db, async () =>
                {
                    db.ChangeTracker.Clear();
                    await ResourceAccounting.LockAsync(db, "glosify:speech-budget", ct);
                    await work(db); return true;
                }, ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 4) { }
            // The token budget service can create the first row of a month at
            // the same time; its optimistic concurrency boundary is shared.
            catch (DbUpdateException ex) when (attempt < 4 && ex.InnerException is SqlException { Number: 2601 or 2627 }) { }
        }
    }
}
