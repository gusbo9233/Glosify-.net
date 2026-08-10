using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai;

public sealed record PaidServiceStatus(bool Available, string? Reason, DateTimeOffset ResetsAtUtc);

public interface IPaidServiceGate
{
    Task<PaidServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task EnsureAvailableAsync(CancellationToken cancellationToken = default);
}

public sealed class PaidServiceGate : IPaidServiceGate
{
    public const string BudgetExhaustedReason =
        "Paid features are unavailable because Glosify's monthly application budget has been reached.";

    private readonly GlosifyContext _context;
    private readonly AiUsageOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _timeZone;

    public PaidServiceGate(
        GlosifyContext context,
        IOptions<AiUsageOptions> options,
        TimeProvider timeProvider)
    {
        _context = context;
        _options = options.Value;
        _timeProvider = timeProvider;
        _timeZone = _options.MonthlyBudget.Enabled
            ? TimeZoneInfo.FindSystemTimeZoneById(_options.MonthlyBudget.TimeZoneId)
            : TimeZoneInfo.Utc;
    }

    public async Task<PaidServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        var periodKey = $"{local.Year:D4}-{local.Month:D2}";
        var resetsAtUtc = GetNextPeriodStartUtc(local);

        if (!_options.MonthlyBudget.Enabled)
        {
            return new PaidServiceStatus(true, null, resetsAtUtc);
        }

        var budget = await _context.AiMonthlyBudgets
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.PeriodKey == periodKey, cancellationToken);
        return budget?.ExhaustedAt is null
            ? new PaidServiceStatus(true, null, resetsAtUtc)
            : new PaidServiceStatus(
                false,
                string.IsNullOrWhiteSpace(budget.ExhaustedReason)
                    ? BudgetExhaustedReason
                    : budget.ExhaustedReason,
                resetsAtUtc);
    }

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Available)
        {
            throw new PaidServicesBudgetExhaustedException(
                status.Reason ?? BudgetExhaustedReason,
                status.ResetsAtUtc);
        }
    }

    private DateTimeOffset GetNextPeriodStartUtc(DateTimeOffset localNow)
    {
        var nextMonth = new DateTime(
            localNow.Year,
            localNow.Month,
            1,
            0,
            0,
            0,
            DateTimeKind.Unspecified).AddMonths(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextMonth, _timeZone), TimeSpan.Zero);
    }
}
