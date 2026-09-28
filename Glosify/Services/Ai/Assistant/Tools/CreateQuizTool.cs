using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class CreateQuizTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "create_vocabulary_quiz",
        "Build one standard quiz proposal across as many batches as needed. First call creates a draft; subsequent calls with its draft_id append content to that same quiz. Send at most 100 words and 100 sentences per call. Set complete only after covering the entire request. The quiz is saved only when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["name"] = StringProp("Quiz name."),
            ["draft_id"] = StringProp("Id returned by an earlier call in this turn. Omit to start a quiz; supply to append to that draft. Name and language fields are only needed on the first call."),
            ["complete"] = CompletionProperty,
            ["source_language"] = StringProp(
                "Language the user already knows. Defaults to the translation language the "
                + "conversation has established, so it can be omitted rather than asked about."),
            ["target_language"] = StringProp("Language being learned. Defaults to the current app language when available."),
            ["collection_id"] = StringProp("Optional id of the collection that should contain the quiz."),
            ["words"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Optional starter vocabulary for the new quiz.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["word"] = StringProp("Word or short phrase in the target language."),
                        ["translation"] = StringProp("Translation in the source language."),
                    },
                    ["required"] = new[] { "word", "translation" },
                },
            },
            ["sentences"] = SentenceArrayProp(
                "Optional standalone example sentences for the new quiz. Full sentences belong "
                + "here, never in words."),
        }));

    internal static object CompletionProperty => new Dictionary<string, object>
    {
        ["type"] = "boolean",
        ["description"] = "Defaults to false. Set true only when all requested material has been processed. For a large request, keep false while appending batches, then true on the last batch or an empty completion call. Check skipped items before finishing.",
    };

    /// <summary>
    /// The most words or sentences one batch may carry.
    /// </summary>
    /// <remarks>
    /// A whole-chapter extraction can otherwise put hundreds of generated items into one
    /// tool argument. Overflow is reported as skipped so the model can append it in a later
    /// batch to the same proposal.
    /// </remarks>
    private const int MaxStarterItems = 100;

    public AgentToolDeclaration Declaration => DeclarationValue;

    /// <summary>
    /// The declared name is create_vocabulary_quiz; create_quiz is an undeclared second
    /// name the old dispatcher also accepted. Kept so a saved chat mid-tool-call still
    /// resolves.
    /// </summary>
    public IReadOnlyList<string> Aliases => ["create_quiz"];

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueCreateQuiz(args, context));

    private static object QueueCreateQuiz(JsonElement args, AgentToolContext context)
    {
        var draftId = GetString(args, "draft_id");
        var draftIndex = string.IsNullOrWhiteSpace(draftId) ? -1 : context.PendingChanges.FindIndex(change =>
            change.Kind == PendingChangeKinds.CreateQuiz && GetString(change.Payload, "draft_id") == draftId);
        if (!string.IsNullOrWhiteSpace(draftId) && draftIndex < 0)
        {
            return new { error = "draft_id must identify a quiz draft created in this turn." };
        }

        // Only the draft's original metadata controls its destination. Continuation calls
        // cannot silently change its language or collection.
        var metadata = draftIndex < 0 ? args : context.PendingChanges[draftIndex].Payload;
        var name = GetString(metadata, "name");
        var sourceLanguage = context.IsFreestyle
            ? Glosify.Services.Language.QuizLanguageCatalog.FreestyleName
            : FirstNonBlank(GetString(metadata, "source_language"), context.SourceLanguage);
        var targetLanguage = context.IsFreestyle
            ? Glosify.Services.Language.QuizLanguageCatalog.FreestyleName
            : FirstNonBlank(GetString(metadata, "target_language"), context.CurrentLanguage);
        var collectionId = GetNullableGuidString(metadata, "collection_id");
        // Every skipped report is in request-array coordinates. The source map has to be built
        // from the parse failures alone, before any later stage adds entries of its own:
        // mixing coordinate systems in one list is what made the reported positions wrong.
        var wordProperty = args.TryGetProperty("items", out _) ? "items" : "words";
        var parsedWords = GetWordDrafts(args, wordProperty);
        var wordIndexes = SourceIndexes(parsedWords.Words.Count, parsedWords.Skipped);
        var (words, skippedWords) = Cap(parsedWords.Words, parsedWords.Skipped, wordIndexes);

        var parsedSentences = context.IsFreestyle
            ? (Sentences: (IReadOnlyList<SentenceDraft>)[], Skipped: (IReadOnlyList<SkippedItem>)[])
            : GetSentenceDrafts(args, "sentences");
        var sentenceIndexes = SourceIndexes(parsedSentences.Sentences.Count, parsedSentences.Skipped);
        var (sentences, skippedSentences) =
            Cap(parsedSentences.Sentences, parsedSentences.Skipped, sentenceIndexes);

        (words, skippedWords) =
            DropWordsAlreadyProposedAsSentences(words, sentences, skippedWords, wordIndexes);

        // Creation carries both content types in one call, so it needs the same guard the add
        // tools have: without it, this is the one path that can still file content under a type
        // the user did not ask for.
        if (sentences.Count > 0 && WrongContentKind(context, AssistantContentKind.Sentences) is { } sentenceMismatch)
        {
            return sentenceMismatch;
        }

        if (words.Count > 0
            && WrongContentKind(context, AssistantContentKind.Words) is { } wordMismatch)
        {
            return wordMismatch;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return new { error = "name and source_language are required." };
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return new { error = "target_language is required when no current app language is selected." };
        }

        if (collectionId.Invalid)
        {
            return new { error = "collection_id must be a valid id." };
        }

        // Keep the per-call limit, not a whole-quiz limit. Replayed batches and repeated
        // choruses keep their first translation and their first-appearance order.
        var previousWords = draftIndex < 0 ? [] : GetWordDrafts(metadata, "words").Words;
        var previousSentences = draftIndex < 0 ? [] : GetSentenceDrafts(metadata, "sentences").Sentences;
        sentences = previousSentences.Concat(sentences)
            .DistinctBy(sentence => NormalizeForDuplicateMatch(sentence.Text), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sentenceKeys = sentences.Select(sentence => NormalizeForDuplicateMatch(sentence.Text))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        words = previousWords.Concat(words)
            .DistinctBy(word => NormalizeForDuplicateMatch(word.Word), StringComparer.OrdinalIgnoreCase)
            .Where(word => !sentenceKeys.Contains(NormalizeForDuplicateMatch(word.Word)))
            .ToArray();
        if (string.IsNullOrWhiteSpace(draftId))
        {
            draftId = Guid.NewGuid().ToString();
        }
        var complete = GetBool(args, "complete") && skippedWords.Count == 0 && skippedSentences.Count == 0;
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateQuiz,
            draft_id = draftId,
            complete,
            name = name.Trim(),
            source_language = sourceLanguage.Trim(),
            target_language = targetLanguage.Trim(),
            collection_id = collectionId.Value,
            words,
            sentences,
        }, JsonOptions);

        var change = new PendingChange(PendingChangeKinds.CreateQuiz, payload);
        if (draftIndex < 0)
        {
            context.PendingChanges.Add(change);
        }
        else
        {
            context.PendingChanges[draftIndex] = change;
        }
        return new
        {
            queued = true,
            draft_id = draftId,
            complete,
            next_action = complete ? "Review ready. Summarize the proposed quiz."
                : "Continue processing the remaining source and append batches using this draft_id. Correct skipped items. Set complete=true only after checking coverage of the entire request.",
            kind = PendingChangeKinds.CreateQuiz,
            name = name.Trim(),
            word_count = words.Count,
            sentence_count = sentences.Count,
            skipped_words = skippedWords,
            skipped_sentences = skippedSentences,
        };
    }

    /// <summary>
    /// Removes starter words that the same proposal already carries as sentences.
    /// </summary>
    /// <remarks>
    /// Each collection deduplicates against itself, so without this a sentence sent in both
    /// arrays is stored twice — once as vocabulary and once as a sentence. The content guard
    /// does not catch it, because a request for words <em>and</em> sentences resolves to
    /// <see cref="AssistantContentKind.Both"/> and legitimately permits either kind.
    /// <para>
    /// Exact text matching only. Nothing here judges whether a string looks like a sentence:
    /// a multiword phrase the model sent only as a word stays vocabulary, which is the
    /// behaviour that must not regress.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<WordDraft> Words, IReadOnlyList<SkippedItem> Skipped)
        DropWordsAlreadyProposedAsSentences(
            IReadOnlyList<WordDraft> words,
            IReadOnlyList<SentenceDraft> sentences,
            IReadOnlyList<SkippedItem> skipped,
            int[] sourceIndexes)
    {
        if (words.Count == 0 || sentences.Count == 0)
        {
            return (words, skipped);
        }

        var sentenceTexts = sentences
            .Select(sentence => NormalizeForDuplicateMatch(sentence.Text))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kept = new List<WordDraft>(words.Count);
        var dropped = new List<SkippedItem>();
        for (var index = 0; index < words.Count; index++)
        {
            if (sentenceTexts.Contains(NormalizeForDuplicateMatch(words[index].Word)))
            {
                dropped.Add(new SkippedItem(
                    sourceIndexes[index],
                    $"\"{words[index].Word}\" is already proposed as a sentence. "
                    + "A sentence is stored once, as a sentence.", Code: "already_sentence"));
                continue;
            }

            kept.Add(words[index]);
        }

        return dropped.Count == 0
            ? (words, skipped)
            : (kept, [.. skipped, .. dropped]);
    }

    private static (IReadOnlyList<T> Kept, IReadOnlyList<SkippedItem> Skipped) Cap<T>(
        IReadOnlyList<T> items,
        IReadOnlyList<SkippedItem> skipped,
        int[] sourceIndexes)
    {
        if (items.Count <= MaxStarterItems)
        {
            return (items, skipped);
        }

        var capped = skipped.ToList();
        for (var index = MaxStarterItems; index < items.Count; index++)
        {
            capped.Add(new SkippedItem(
                sourceIndexes[index],
                $"Only the first {MaxStarterItems} items are accepted in one batch. Append this item in the next call using draft_id."));
        }

        return (items.Take(MaxStarterItems).ToArray(), capped);
    }
}
