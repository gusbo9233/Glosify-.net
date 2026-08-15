using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly Fsrs6AnkiScheduler _scheduler = new();

    [Fact]
    public void New_card_uses_fixed_learning_steps_and_upstream_initial_fsrs_values()
    {
        var card = NewCard();
        var again = _scheduler.Schedule(card, AnkiRatings.Again, .9, Now);
        var hard = _scheduler.Schedule(card, AnkiRatings.Hard, .9, Now);
        var good = _scheduler.Schedule(card, AnkiRatings.Good, .9, Now);
        var easy = _scheduler.Schedule(card, AnkiRatings.Easy, .9, Now);

        Assert.Equal(Now.AddMinutes(1), again.DueAt);
        Assert.Equal(Now.AddMinutes(6), hard.DueAt);
        Assert.Equal(Now.AddMinutes(10), good.DueAt);
        Assert.Equal(0.212, again.Stability, 6);
        Assert.Equal(6.4133, again.Difficulty, 6);
        Assert.Equal(1.2931, hard.Stability, 6);
        Assert.Equal(5.1121707056, hard.Difficulty, 6);
        Assert.Equal(2.3065, good.Stability, 6);
        Assert.Equal(2.1181039705, good.Difficulty, 6);
        Assert.Equal(AnkiCardStates.Review, easy.State);
        Assert.Equal(8.2956, easy.Stability, 6);
        Assert.Equal(1, easy.Difficulty, 6);
    }

    [Fact]
    public void Reference_good_history_matches_fsrs6_equations()
    {
        var first = _scheduler.Schedule(NewCard(), AnkiRatings.Good, .9, Now);
        var learning = CardFrom(first, Now);
        var second = _scheduler.Schedule(learning, AnkiRatings.Good, .9, Now.AddMinutes(10));
        Assert.Equal(2.2938144017, second.Stability, 6);

        var review = CardFrom(second, Now.AddMinutes(10));
        var third = _scheduler.Schedule(review, AnkiRatings.Good, .9, Now.AddMinutes(10).AddDays(1));
        Assert.Equal(7.3033289053, third.Stability, 6);
        Assert.InRange(third.Retrievability, .94660, .94661);
    }

    [Fact]
    public void Same_day_success_uses_the_fsrs_short_term_stability_equation()
    {
        var card = ReviewCard(stability: 12, lastReviewedAt: Now.AddHours(-2));
        var result = _scheduler.Schedule(card, AnkiRatings.Good, .9, Now);
        Assert.InRange(result.Stability, 10.70, 10.71);
    }

    [Fact]
    public void Delayed_review_orders_success_grades_and_again_enters_relearning()
    {
        var card = ReviewCard(stability: 8, lastReviewedAt: Now.AddDays(-14));
        var hard = _scheduler.Schedule(card, AnkiRatings.Hard, .9, Now);
        var good = _scheduler.Schedule(card, AnkiRatings.Good, .9, Now);
        var easy = _scheduler.Schedule(card, AnkiRatings.Easy, .9, Now);
        var again = _scheduler.Schedule(card, AnkiRatings.Again, .9, Now);
        Assert.True(hard.Stability < good.Stability);
        Assert.True(good.Stability < easy.Stability);
        Assert.Equal(AnkiCardStates.Relearning, again.State);
        Assert.Equal(Now.AddMinutes(10), again.DueAt);
        Assert.Equal(card.LapseCount + 1, again.LapseCount);
    }

    [Fact]
    public void Higher_retention_shortens_future_interval_and_maximum_is_one_hundred_years()
    {
        var card = ReviewCard(stability: 500, lastReviewedAt: Now.AddDays(-30));
        var normal = _scheduler.Schedule(card, AnkiRatings.Good, .9, Now);
        var high = _scheduler.Schedule(card, AnkiRatings.Good, .97, Now);
        Assert.True(high.ScheduledDays < normal.ScheduledDays);

        card.Stability = 1_000_000_000;
        var capped = _scheduler.Schedule(card, AnkiRatings.Easy, .7, Now);
        Assert.InRange(capped.ScheduledDays, 1, 36_500);
    }

    [Fact]
    public void Interval_fuzz_is_repeatable_for_the_same_card_and_review()
    {
        var card = ReviewCard(stability: 80, lastReviewedAt: Now.AddDays(-20));
        var first = _scheduler.Schedule(card, AnkiRatings.Good, .9, Now);
        var second = _scheduler.Schedule(card, AnkiRatings.Good, .9, Now);
        Assert.Equal(first.ScheduledDays, second.ScheduledDays);
        Assert.Equal(first.DueAt, second.DueAt);
    }

    private static AnkiCard NewCard() => new() { Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), State = AnkiCardStates.New };

    private static AnkiCard ReviewCard(double stability, DateTimeOffset lastReviewedAt) => new()
    {
        Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        State = AnkiCardStates.Review,
        Stability = stability,
        Difficulty = 5,
        ReviewCount = 4,
        LapseCount = 1,
        LastReviewedAt = lastReviewedAt,
    };

    private static AnkiCard CardFrom(AnkiSchedulingResult result, DateTimeOffset reviewedAt) => new()
    {
        Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        State = result.State,
        DueAt = result.DueAt,
        Stability = result.Stability,
        Difficulty = result.Difficulty,
        LearningStep = result.LearningStep,
        ReviewCount = result.ReviewCount,
        LapseCount = result.LapseCount,
        LastReviewedAt = reviewedAt,
    };
}
