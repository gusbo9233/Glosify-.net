namespace Glosify.Services;

/// <summary>
/// Slices an already-ordered list down to a percentage window (e.g. "the newest
/// 20%"), used by the quiz-settings word-range slider to restrict the pool that
/// words/sentences get randomly sampled from.
/// </summary>
public static class PracticeRange
{
    public static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> orderedItems, int startPercent, int endPercent)
    {
        var total = orderedItems.Count;
        if (total == 0)
            return [];

        var start = Math.Clamp(startPercent, 0, 100);
        var end = Math.Clamp(endPercent, 0, 100);
        if (end < start)
            (start, end) = (end, start);

        var startIndex = Math.Clamp((int)Math.Floor(total * start / 100.0), 0, total);
        var endIndex = Math.Clamp((int)Math.Ceiling(total * end / 100.0), startIndex, total);

        // A narrow range (e.g. min==max) would otherwise select nothing.
        if (endIndex == startIndex)
        {
            endIndex = Math.Min(startIndex + 1, total);
            startIndex = Math.Max(endIndex - 1, 0);
        }

        return orderedItems.Skip(startIndex).Take(endIndex - startIndex).ToList();
    }
}
