namespace Glosify.Services.Anki;

public sealed record AnkiChartPoint(string Label, int Value);

public sealed record AnkiStatistics(
    AnkiCollectionCounts Counts,
    int ReviewsLast30Days,
    double RetentionPercent,
    IReadOnlyList<AnkiChartPoint> ReviewActivity,
    IReadOnlyList<AnkiChartPoint> DueForecast);

public interface IAnkiStatisticsService
{
    Task<AnkiStatistics?> GetAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default);
}
