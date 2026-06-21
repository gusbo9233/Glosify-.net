namespace Glosify.Models;

public static class PracticeDirection
{
    public const string SourceToTarget = "source-to-target";
    public const string TargetToSource = "target-to-source";

    public static string Normalize(string? value)
    {
        return IsTargetToSource(value) ? TargetToSource : SourceToTarget;
    }

    public static bool IsValid(string? value)
    {
        return string.Equals(value, SourceToTarget, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, TargetToSource, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSourceToTarget(string? value)
    {
        return string.Equals(Normalize(value), SourceToTarget, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTargetToSource(string? value)
    {
        return string.Equals(value, TargetToSource, StringComparison.OrdinalIgnoreCase);
    }

    public static string PromptLanguage(string? value, string sourceLanguage, string targetLanguage)
    {
        return IsSourceToTarget(value) ? sourceLanguage : targetLanguage;
    }

    public static string AnswerLanguage(string? value, string sourceLanguage, string targetLanguage)
    {
        return IsSourceToTarget(value) ? targetLanguage : sourceLanguage;
    }

    public static string Label(string? value, string sourceLanguage, string targetLanguage)
    {
        return $"{PromptLanguage(value, sourceLanguage, targetLanguage)} -> {AnswerLanguage(value, sourceLanguage, targetLanguage)}";
    }
}
