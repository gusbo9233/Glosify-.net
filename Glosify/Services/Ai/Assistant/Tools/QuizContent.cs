using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>Shared rules for tools that read or change a quiz's words and sentences.</summary>
internal static partial class QuizContent
{
    /// <summary>The most words or sentences one call may carry.</summary>
    /// <remarks>
    /// A whole-chapter extraction can otherwise put hundreds of generated items into one tool
    /// argument, which is slow to generate and fails as a unit. Overflow is reported back so
    /// the model sends it in the next call.
    /// </remarks>
    public const int MaxItemsPerCall = 100;

    public const int PageSize = 200;

    internal static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Resolves the quiz a content tool acts on: an explicit quiz_id, otherwise the chat's quiz.
    /// </summary>
    public static async Task<QuizResolution> ResolveAsync(
        GlosifyContext db,
        ToolContext context,
        string? quizId,
        CancellationToken cancellationToken)
    {
        Guid? id = context.QuizId;
        if (!string.IsNullOrWhiteSpace(quizId))
        {
            if (!Guid.TryParse(quizId, out var parsed))
            {
                return QuizResolution.Failed("quiz_id must be a quiz id returned by list_library or create_quiz.");
            }

            id = parsed;
        }

        if (id is null)
        {
            return QuizResolution.Failed(
                "No quiz is selected. Pass quiz_id from list_library or create_quiz, or ask the user which quiz to use.");
        }

        var quiz = await db.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == context.UserId, cancellationToken);
        if (quiz is null)
        {
            return QuizResolution.Failed("That quiz was not found. Use list_library to find the user's quizzes.");
        }

        if (QuizLanguageCatalog.IsFreestyle(quiz.TargetLanguage) != context.IsFreestyle)
        {
            return QuizResolution.Failed(context.IsFreestyle
                ? "That is a language quiz. Freestyle mode only works with prompt-and-answer quizzes."
                : "That is a Freestyle quiz. Switch to Freestyle to change it.");
        }

        return new QuizResolution(quiz, null);
    }

    /// <summary>
    /// Refuses content filed under the type the user did not ask for, or null when allowed.
    /// </summary>
    /// <remarks>
    /// Full sentences stored as vocabulary were a real, repeated failure. The request's own
    /// wording decides; an unrecognised phrasing leaves the choice to the model.
    /// </remarks>
    public static string? WrongContentKind(ToolContext context, bool hasWords, bool hasSentences)
    {
        return context.RequestedContentKind switch
        {
            AssistantContentKind.Sentences when hasWords =>
                "The user asked for sentences. Put full sentences in sentences, not words.",
            AssistantContentKind.Words when hasSentences =>
                "The user asked for words. Put vocabulary in words, not sentences.",
            _ => null,
        };
    }

    /// <summary>
    /// The comparison key for deciding whether two strings are the same content.
    /// </summary>
    /// <remarks>
    /// Trailing sentence punctuation and repeated inner whitespace are ignored, because a
    /// model that repeats itself across two fields rarely repeats itself character for
    /// character. This never inspects shape: a string is only dropped because an actual
    /// sentence matches it, so multiword vocabulary is unaffected.
    /// </remarks>
    public static string NormalizeForDuplicateMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Whitespace().Replace(value.Trim(), " ").TrimEnd('.', '!', '?', '…', ' ');
    }

    public static bool IsOutsideFocus(ToolContext context, string wordId) =>
        !string.IsNullOrWhiteSpace(context.FocusedWordId)
        && !string.Equals(wordId, context.FocusedWordId, StringComparison.Ordinal);

    public static string FocusError(ToolContext context) =>
        $"This chat is focused on {context.FocusedWordLabel ?? "one word"}. Edits and deletions may only target that word.";

    public static PendingChange Change(string kind, object payload) =>
        new(kind, JsonSerializer.SerializeToElement(payload, PayloadOptions));

    public static string Describe(int words, int sentences, bool freestyle)
    {
        if (freestyle)
        {
            return Count(words, "item");
        }

        return (words, sentences) switch
        {
            ( > 0, > 0) => $"{Count(words, "word")} and {Count(sentences, "sentence")}",
            ( > 0, _) => Count(words, "word"),
            _ => Count(sentences, "sentence"),
        };
    }

    public static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";

    /// <summary>
    /// Cleans the requested words and sentences: blanks and repeats are reported, overflow past
    /// the per-call limit is reported, and text sent both as a word and a sentence stays a sentence.
    /// </summary>
    public static CleanedContent Clean(
        IReadOnlyList<WordInput>? words,
        IReadOnlyList<SentenceInput>? sentences)
    {
        var skipped = new List<object>();
        var keptSentences = new List<SentenceInput>();
        var sentenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sentence, index) in (sentences ?? []).Select((item, index) => (item, index)))
        {
            if (string.IsNullOrWhiteSpace(sentence.Text) || string.IsNullOrWhiteSpace(sentence.Translation))
            {
                skipped.Add(new { sentence_index = index, reason = "text and translation are both required." });
                continue;
            }

            if (keptSentences.Count >= MaxItemsPerCall)
            {
                skipped.Add(new { sentence_index = index, reason = $"Only {MaxItemsPerCall} sentences are accepted per call. Send it in another call." });
                continue;
            }

            if (!sentenceKeys.Add(NormalizeForDuplicateMatch(sentence.Text)))
            {
                continue;
            }

            keptSentences.Add(new SentenceInput(sentence.Text.Trim(), sentence.Translation.Trim()));
        }

        var keptWords = new List<WordInput>();
        var wordKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (word, index) in (words ?? []).Select((item, index) => (item, index)))
        {
            if (string.IsNullOrWhiteSpace(word.Word) || string.IsNullOrWhiteSpace(word.Translation))
            {
                skipped.Add(new { word_index = index, reason = "word and translation are both required." });
                continue;
            }

            if (keptWords.Count >= MaxItemsPerCall)
            {
                skipped.Add(new { word_index = index, reason = $"Only {MaxItemsPerCall} words are accepted per call. Send it in another call." });
                continue;
            }

            var key = NormalizeForDuplicateMatch(word.Word);
            if (sentenceKeys.Contains(key))
            {
                skipped.Add(new { word_index = index, reason = $"\"{word.Word.Trim()}\" is also in sentences. A sentence is stored once, as a sentence." });
                continue;
            }

            if (!wordKeys.Add(key))
            {
                continue;
            }

            keptWords.Add(new WordInput(word.Word.Trim(), word.Translation.Trim()));
        }

        return new CleanedContent(keptWords, keptSentences, skipped);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

internal sealed record QuizResolution(Quiz? Quiz, string? Error)
{
    public static QuizResolution Failed(string error) => new(null, error);
}

internal sealed record CleanedContent(
    IReadOnlyList<WordInput> Words,
    IReadOnlyList<SentenceInput> Sentences,
    IReadOnlyList<object> Skipped);

internal sealed record WordInput(
    [property: Description("Word or short phrase in the target language, in the form the learner should practice.")] string Word,
    [property: Description("Translation in the source language.")] string Translation);

internal sealed record SentenceInput(
    [property: Description("A natural full sentence in the target language. No notes, glosses, slash alternatives, or fragments.")] string Text,
    [property: Description("A natural translation in the source language.")] string Translation);

internal sealed record ItemInput(
    [property: Description("The question, term, or cue shown first.")] string Prompt,
    [property: Description("The correct answer, definition, or explanation.")] string Answer);

internal enum QuizItemKind
{
    Words,
    Sentences,
}
