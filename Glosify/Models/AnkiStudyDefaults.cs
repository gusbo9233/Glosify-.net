namespace Glosify.Models;

// Study has no daily quota. Each language collection has a storage allowance.
public static class AnkiStudyDefaults
{
    public const double DesiredRetention = 0.9;
    public const int MaximumCardsPerCollection = 10_000;
}
