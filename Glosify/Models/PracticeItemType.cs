namespace Glosify.Models;

public static class PracticeItemType
{
    public const string Words = "words";
    public const string Sentences = "sentences";

    public static string Normalize(string? value)
    {
        return IsSentences(value) ? Sentences : Words;
    }

    public static bool IsValid(string? value)
    {
        return string.Equals(value, Words, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Sentences, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWords(string? value)
    {
        return string.Equals(Normalize(value), Words, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSentences(string? value)
    {
        return string.Equals(value, Sentences, StringComparison.OrdinalIgnoreCase);
    }

    public static string SingularLabel(string? value)
    {
        return IsSentences(value) ? "sentence" : "word";
    }

    public static string PluralLabel(string? value)
    {
        return IsSentences(value) ? "sentences" : "words";
    }

    public static string CardLabel(string? value)
    {
        return IsSentences(value) ? "Sentence" : "Word";
    }
}
