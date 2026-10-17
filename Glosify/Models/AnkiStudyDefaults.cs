namespace Glosify.Models;

// One study policy for every learner and language. Daily allowances are per direction.
public static class AnkiStudyDefaults
{
    public const double DesiredRetention = 0.9;
    public const int NewCardsPerDay = 20;
    public const int MaximumReviewsPerDay = 200;
}
