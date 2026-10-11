using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Avatar;

public sealed record AvatarUsageFilter(DateTimeOffset From, DateTimeOffset To, string? Admin = null,
    string? Provider = null, string? Model = null, string? Operation = null, string? SessionId = null);

public sealed class AvatarUsageEntry
{
    public Guid Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string UserId { get; init; } = "";
    public string Email { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Status { get; init; } = "";
    public decimal Credits { get; init; }
    public decimal? AudioSeconds { get; init; }
    public decimal? Characters { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public long UnclassifiedTokens { get; init; }
    public decimal? SavedProviderSek { get; init; }
    public decimal? EstimatedSek { get; set; }
}

public sealed class AvatarUsageGroup
{
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string Operation { get; init; } = "";
    public int Calls { get; init; }
    public int Pending { get; init; }
    public int Unpriced { get; set; }
    public int TokenCalls { get; init; }
    public decimal Credits { get; init; }
    public decimal AudioSeconds { get; init; }
    public decimal Characters { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }
    public long UnclassifiedTokens { get; init; }
    public decimal EstimatedSek { get; set; }
}

public sealed record AvatarUsageReport(AvatarUsageFilter Filter, int Sessions,
    IReadOnlyList<AvatarUsageGroup> Breakdown, IReadOnlyList<AvatarUsageEntry> Entries)
{
    public int Calls => Breakdown.Sum(x => x.Calls);
    public int Pending => Breakdown.Sum(x => x.Pending);
    public int Unpriced => Breakdown.Sum(x => x.Unpriced);
    public decimal Credits => Breakdown.Sum(x => x.Credits);
    public decimal EstimatedSek => Breakdown.Sum(x => x.EstimatedSek);
    public decimal AudioSeconds => Breakdown.Sum(x => x.AudioSeconds);
    public decimal Characters => Breakdown.Sum(x => x.Characters);
    public long TotalTokens => Breakdown.Sum(x => x.TotalTokens);
}

// Read the durable accounting ledgers: one row per reservation, including old sessions.
// No new provider requests, transcript storage, or changes to customer billing.
public sealed class AvatarUsageReports(GlosifyContext db, IOptions<AiUsageOptions> prices)
{
    public async Task<AvatarUsageReport> ReadAsync(AvatarUsageFilter filter, CancellationToken ct)
    {
        var query = Query(filter);
        var sessions = await query.Select(x => x.SessionId).Distinct().CountAsync(ct);
        var groups = await Group(query).OrderBy(x => x.Provider).ThenBy(x => x.Model).ThenBy(x => x.Operation).ToListAsync(ct);
        foreach (var group in groups)
        {
            var cost = TokenCost(group.Provider, group.Model, group.InputTokens, group.OutputTokens, group.UnclassifiedTokens);
            if (cost.HasValue) group.EstimatedSek += cost.Value;
            else group.Unpriced += group.TokenCalls;
        }
        var entries = await query.OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).Take(500).ToListAsync(ct);
        foreach (var entry in entries)
            entry.EstimatedSek = entry.SavedProviderSek ?? (entry.TotalTokens.HasValue
                ? TokenCost(entry.Provider, entry.Model, entry.InputTokens ?? 0, entry.OutputTokens ?? 0, entry.UnclassifiedTokens) : null);
        return new(filter, sessions, groups, entries);
    }

    internal IQueryable<AvatarUsageEntry> Query(AvatarUsageFilter filter)
    {
        var transactions = db.AiCreditTransactions.AsNoTracking();
        var reservations = transactions.Where(x => x.Kind == AiCreditTransactionKinds.Reservation
            && x.RelatedEntityType == "AvatarSession" && x.RelatedEntityId != null && x.ReservationId != null
            && x.CreatedAt >= filter.From && x.CreatedAt <= filter.To);
        if (!string.IsNullOrWhiteSpace(filter.Provider)) reservations = reservations.Where(x => x.Provider == filter.Provider);
        if (!string.IsNullOrWhiteSpace(filter.Model)) reservations = reservations.Where(x => x.Model == filter.Model);
        if (!string.IsNullOrWhiteSpace(filter.Operation)) reservations = reservations.Where(x => x.Operation == filter.Operation);
        if (!string.IsNullOrWhiteSpace(filter.SessionId)) reservations = reservations.Where(x => x.RelatedEntityId == filter.SessionId);
        var debits = transactions.Where(x => x.Kind == AiCreditTransactionKinds.UsageDebit);
        // More than one release can belong to a reservation. It is still only one call.
        var releases = transactions.Where(x => x.Kind == AiCreditTransactionKinds.Release
                && reservations.Any(reservation => reservation.ReservationId == x.ReservationId && reservation.UserId == x.UserId))
            .GroupBy(x => new { x.ReservationId, x.UserId })
            .Select(g => new { g.Key.ReservationId, g.Key.UserId, Released = (int?)1 });
        var query = from reservation in reservations
                    join user in db.Users.AsNoTracking() on reservation.UserId equals user.Id into users
                    from user in users.DefaultIfEmpty()
                    join debit in debits on new { reservation.ReservationId, reservation.UserId }
                        equals new { debit.ReservationId, debit.UserId } into debited
                    from debit in debited.DefaultIfEmpty()
                    join release in releases on new { reservation.ReservationId, reservation.UserId }
                        equals new { release.ReservationId, release.UserId } into released
                    from release in released.DefaultIfEmpty()
                    join voice in db.Set<AvatarUsageOperation>().AsNoTracking()
                        on new { reservation.ReservationId, reservation.UserId } equals new { ReservationId = (Guid?)voice.Id, voice.UserId } into voices
                    from voice in voices.DefaultIfEmpty()
                    select new AvatarUsageEntry
                    {
                        Id = reservation.Id, StartedAt = reservation.CreatedAt, UserId = reservation.UserId,
                        Email = user == null ? reservation.UserId : user.Email ?? user.UserName ?? reservation.UserId,
                        SessionId = reservation.RelatedEntityId!, Provider = reservation.Provider ?? "Unknown",
                        Model = reservation.Model ?? "Unknown", Operation = reservation.Operation ?? "Unknown",
                        Status = voice != null ? (voice.Settled ? "Settled" : "Pending")
                            : debit != null ? "Settled" : release.Released != null ? "Released" : "Pending",
                        Credits = debit == null ? 0 : -debit.CreditAmount,
                        AudioSeconds = voice != null && voice.Kind == "recognition" ? (decimal?)voice.SubmittedUnits : null,
                        Characters = voice != null && voice.Kind == "speech" ? (decimal?)voice.SubmittedUnits : null,
                        InputTokens = debit == null || debit.TotalTokens == null ? null : (long?)(debit.PromptTokens ?? 0) + (debit.ToolPromptTokens ?? 0),
                        OutputTokens = debit == null ? null : (long?)debit.CandidateTokens,
                        TotalTokens = debit == null ? null : (long?)debit.TotalTokens,
                        UnclassifiedTokens = debit != null && (long)(debit.TotalTokens ?? 0) > (long)(debit.PromptTokens ?? 0) + (debit.ToolPromptTokens ?? 0) + (debit.CandidateTokens ?? 0)
                            ? (long)(debit.TotalTokens ?? 0) - (debit.PromptTokens ?? 0) - (debit.ToolPromptTokens ?? 0) - (debit.CandidateTokens ?? 0) : 0,
                        SavedProviderSek = voice == null ? null : (decimal?)(voice.SubmittedUnits * voice.ProviderSekRate)
                    };
        if (!string.IsNullOrWhiteSpace(filter.Admin)) query = query.Where(x => x.UserId == filter.Admin || x.Email == filter.Admin);
        return query;
    }

    internal static IQueryable<AvatarUsageGroup> Group(IQueryable<AvatarUsageEntry> query) => query
        .GroupBy(x => new { x.Provider, x.Model, x.Operation }).Select(g => new AvatarUsageGroup
        {
            Provider = g.Key.Provider, Model = g.Key.Model, Operation = g.Key.Operation,
            Calls = g.Count(), Pending = g.Count(x => x.Status == "Pending"),
            Unpriced = g.Count(x => x.SavedProviderSek == null && x.TotalTokens == null),
            TokenCalls = g.Count(x => x.SavedProviderSek == null && x.TotalTokens != null),
            Credits = g.Sum(x => x.Credits), AudioSeconds = g.Sum(x => x.AudioSeconds ?? 0), Characters = g.Sum(x => x.Characters ?? 0),
            InputTokens = g.Sum(x => x.InputTokens ?? 0), OutputTokens = g.Sum(x => x.OutputTokens ?? 0),
            TotalTokens = g.Sum(x => x.TotalTokens ?? 0), UnclassifiedTokens = g.Sum(x => x.UnclassifiedTokens),
            EstimatedSek = g.Sum(x => x.SavedProviderSek ?? 0)
        });

    private decimal? TokenCost(string provider, string model, long input, long output, long other)
    {
        var price = prices.Value.MonthlyBudget.FindModelPrice(model);
        if (provider != AiUsageProviders.OpenAi || price is not { InputSekPerMillionTokens: > 0, OutputSekPerMillionTokens: > 0 }) return null;
        return (input * price.InputSekPerMillionTokens + output * price.OutputSekPerMillionTokens
            + other * Math.Max(price.InputSekPerMillionTokens, price.OutputSekPerMillionTokens)) / 1_000_000m;
    }
}
