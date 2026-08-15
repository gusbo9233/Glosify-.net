using Glosify.Models.Entities;

namespace Glosify.Services.Anki;

/// <summary>A compact, deterministic FSRS-6 scheduler using the upstream default weights.</summary>
public sealed class Fsrs6AnkiScheduler : IAnkiScheduler
{
    private const int MaximumIntervalDays = 36_500;
    private static readonly double[] W =
    [
        0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194,
        0.001, 1.8722, 0.1666, 0.796, 1.4835, 0.0614, 0.2629,
        1.6483, 0.6014, 1.8729, 0.5425, 0.0912, 0.0658, 0.1542,
    ];

    public string Version => "fsrs-6.0";

    public IReadOnlyDictionary<string, AnkiSchedulingResult> Preview(
        AnkiCard card,
        double desiredRetention,
        DateTimeOffset now) =>
        AnkiRatings.All.ToDictionary(
            rating => rating,
            rating => Schedule(card, rating, desiredRetention, now),
            StringComparer.Ordinal);

    public AnkiSchedulingResult Schedule(
        AnkiCard card,
        string rating,
        double desiredRetention,
        DateTimeOffset now)
    {
        var normalized = rating.Trim().ToLowerInvariant();
        var grade = AnkiRatings.Grade(normalized);
        var retention = Math.Clamp(desiredRetention, 0.70, 0.97);
        var elapsed = card.LastReviewedAt.HasValue
            ? Math.Max(0, (now - card.LastReviewedAt.Value).TotalDays)
            : 0;
        var retrievability = card.LastReviewedAt.HasValue && card.Stability > 0
            ? RecallProbability(elapsed, card.Stability)
            : 0;

        var stability = card.ReviewCount == 0 || card.Stability <= 0
            ? W[grade - 1]
            : NextStability(card.Stability, card.Difficulty, retrievability, grade, elapsed);
        var difficulty = card.ReviewCount == 0 || card.Difficulty <= 0
            ? InitialDifficulty(grade)
            : NextDifficulty(card.Difficulty, grade);

        var state = card.State;
        var learningStep = card.LearningStep;
        DateTimeOffset dueAt;
        int scheduledDays;

        if (card.State == AnkiCardStates.New)
        {
            (state, learningStep, dueAt, scheduledDays) = grade switch
            {
                1 => (AnkiCardStates.Learning, 0, now.AddMinutes(1), 0),
                2 => (AnkiCardStates.Learning, 0, now.AddMinutes(6), 0),
                3 => (AnkiCardStates.Learning, 1, now.AddMinutes(10), 0),
                _ => Graduate(card, grade, stability, retention, now),
            };
        }
        else if (card.State == AnkiCardStates.Learning)
        {
            (state, learningStep, dueAt, scheduledDays) = grade switch
            {
                1 => (AnkiCardStates.Learning, 0, now.AddMinutes(1), 0),
                2 => (AnkiCardStates.Learning, learningStep, now.AddMinutes(6), 0),
                3 when learningStep == 0 => (AnkiCardStates.Learning, 1, now.AddMinutes(10), 0),
                _ => Graduate(card, grade, stability, retention, now),
            };
        }
        else if (grade == 1)
        {
            state = AnkiCardStates.Relearning;
            learningStep = 0;
            dueAt = now.AddMinutes(10);
            scheduledDays = 0;
        }
        else if (card.State == AnkiCardStates.Relearning)
        {
            (state, learningStep, dueAt, scheduledDays) = grade == 2
                ? (AnkiCardStates.Relearning, 0, now.AddMinutes(15), 0)
                : Graduate(card, grade, stability, retention, now);
        }
        else
        {
            scheduledDays = FuzzedInterval(card, grade, Interval(stability, retention));
            dueAt = now.AddDays(scheduledDays);
            state = AnkiCardStates.Review;
            learningStep = 0;
        }

        return new AnkiSchedulingResult(
            state,
            dueAt,
            stability,
            difficulty,
            learningStep,
            scheduledDays,
            card.ReviewCount + 1,
            card.LapseCount + (grade == 1 && card.State is AnkiCardStates.Review or AnkiCardStates.Relearning ? 1 : 0),
            elapsed,
            retrievability,
            FormatInterval(dueAt - now));
    }

    public double Retrievability(AnkiCard card, DateTimeOffset now)
    {
        if (!card.LastReviewedAt.HasValue || card.Stability <= 0)
            return card.State == AnkiCardStates.New ? 0 : 1;

        return RecallProbability(Math.Max(0, (now - card.LastReviewedAt.Value).TotalDays), card.Stability);
    }

    private static (string State, int Step, DateTimeOffset DueAt, int Days) Graduate(
        AnkiCard card,
        int grade,
        double stability,
        double retention,
        DateTimeOffset now)
    {
        var days = FuzzedInterval(card, grade, Interval(stability, retention));
        return (AnkiCardStates.Review, 0, now.AddDays(days), days);
    }

    private static double InitialDifficulty(int grade) =>
        ClampDifficulty(W[4] - Math.Exp(W[5] * (grade - 1)) + 1);

    private static double NextDifficulty(double difficulty, int grade)
    {
        var damped = difficulty - W[6] * (grade - 3) * (10 - difficulty) / 9;
        var meanReverted = W[7] * InitialDifficulty(4) + (1 - W[7]) * damped;
        return ClampDifficulty(meanReverted);
    }

    private static double NextStability(
        double stability,
        double difficulty,
        double retrievability,
        int grade,
        double elapsedDays)
    {
        if (elapsedDays < 1)
        {
            var sameDay = stability
                * Math.Exp(W[17] * (grade - 3 + W[18]))
                * Math.Pow(stability, -W[19]);
            return Math.Max(0.01, sameDay);
        }

        if (grade == 1)
        {
            return Math.Max(
                0.01,
                W[11]
                * Math.Pow(difficulty, -W[12])
                * (Math.Pow(stability + 1, W[13]) - 1)
                * Math.Exp(W[14] * (1 - retrievability)));
        }

        var hardPenalty = grade == 2 ? W[15] : 1;
        var easyBonus = grade == 4 ? W[16] : 1;
        var increase = Math.Exp(W[8])
            * (11 - difficulty)
            * Math.Pow(stability, -W[9])
            * (Math.Exp(W[10] * (1 - retrievability)) - 1)
            * hardPenalty
            * easyBonus;
        return Math.Max(stability, stability * (increase + 1));
    }

    private static double RecallProbability(double elapsedDays, double stability)
    {
        var factor = Math.Pow(0.9, -1 / W[20]) - 1;
        return Math.Pow(1 + factor * elapsedDays / stability, -W[20]);
    }

    private static int Interval(double stability, double retention)
    {
        var factor = Math.Pow(0.9, -1 / W[20]) - 1;
        var interval = stability / factor * (Math.Pow(retention, -1 / W[20]) - 1);
        return Math.Clamp((int)Math.Round(interval), 1, MaximumIntervalDays);
    }

    private static int FuzzedInterval(AnkiCard card, int grade, int interval)
    {
        if (interval < 3)
            return interval;

        var spread = interval switch
        {
            < 7 => 1,
            < 30 => Math.Max(2, (int)Math.Round(interval * 0.10)),
            _ => Math.Max(3, (int)Math.Round(interval * 0.05)),
        };
        // System.HashCode is salted per process. A small FNV-1a hash keeps fuzz stable
        // across app restarts while still separating cards, reviews, and grades.
        ulong hash = 14695981039346656037;
        foreach (var value in card.Id.ToByteArray())
        {
            hash ^= value;
            hash *= 1099511628211;
        }
        hash ^= (uint)card.ReviewCount;
        hash *= 1099511628211;
        hash ^= (uint)grade;
        var offset = (int)(hash % (uint)(spread * 2 + 1)) - spread;
        return Math.Clamp(interval + offset, 1, MaximumIntervalDays);
    }

    private static double ClampDifficulty(double value) => Math.Clamp(value, 1, 10);

    private static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalMinutes < 60)
            return $"{Math.Max(1, (int)Math.Round(interval.TotalMinutes))}m";
        if (interval.TotalHours < 24)
            return $"{Math.Max(1, (int)Math.Round(interval.TotalHours))}h";
        if (interval.TotalDays < 365)
            return $"{Math.Max(1, (int)Math.Round(interval.TotalDays))}d";
        return $"{interval.TotalDays / 365:0.#}y";
    }
}
