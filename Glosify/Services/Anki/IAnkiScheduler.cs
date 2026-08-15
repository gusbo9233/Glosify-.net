using Glosify.Models.Entities;

namespace Glosify.Services.Anki;

public static class AnkiRatings
{
    public const string Again = "again";
    public const string Hard = "hard";
    public const string Good = "good";
    public const string Easy = "easy";

    public static readonly string[] All = [Again, Hard, Good, Easy];

    public static int Grade(string rating) => rating switch
    {
        Again => 1,
        Hard => 2,
        Good => 3,
        Easy => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(rating), "Choose Again, Hard, Good, or Easy."),
    };
}

public sealed record AnkiSchedulingResult(
    string State,
    DateTimeOffset DueAt,
    double Stability,
    double Difficulty,
    int LearningStep,
    int ScheduledDays,
    int ReviewCount,
    int LapseCount,
    double ElapsedDays,
    double Retrievability,
    string IntervalLabel);

public interface IAnkiScheduler
{
    string Version { get; }
    AnkiSchedulingResult Schedule(AnkiCard card, string rating, double desiredRetention, DateTimeOffset now);
    IReadOnlyDictionary<string, AnkiSchedulingResult> Preview(AnkiCard card, double desiredRetention, DateTimeOffset now);
    double Retrievability(AnkiCard card, DateTimeOffset now);
}
