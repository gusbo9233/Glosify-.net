namespace Glosify.Models;

/// <summary>
/// Parses the comma-separated word IDs carried through the quiz-settings
/// "choose words individually" flow (query string and hidden form field).
/// </summary>
public static class WordIdList
{
    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
