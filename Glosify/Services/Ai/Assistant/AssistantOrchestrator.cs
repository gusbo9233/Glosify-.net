using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public sealed class AssistantOrchestrator : IAssistantOrchestrator
{
    private const int MaxToolTurns = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;
    private readonly IGeminiClient _gemini;
    private readonly IAssistantTools _tools;
    private readonly IChangeApplier _changeApplier;
    private readonly ILogger<AssistantOrchestrator> _logger;

    public AssistantOrchestrator(
        GlosifyContext context,
        IGeminiClient gemini,
        IAssistantTools tools,
        IChangeApplier changeApplier,
        ILogger<AssistantOrchestrator> logger)
    {
        _context = context;
        _gemini = gemini;
        _tools = tools;
        _changeApplier = changeApplier;
        _logger = logger;
    }

    public async Task<AssistantTurnResponse> SendMessageAsync(
        Guid quizId,
        string userId,
        string userMessage,
        string? focusedWordId = null,
        CancellationToken cancellationToken = default)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == quizId && q.UserId == userId, cancellationToken)
            ?? throw new QuizNotFoundException();

        var focusedWord = await LoadFocusedWordAsync(quizId, focusedWordId, cancellationToken);

        var thread = await GetOrCreateThreadAsync(quizId, userId, cancellationToken);

        var history = await _context.AssistantMessages
            .Where(m => m.ThreadId == thread.Id)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        var nextSequence = history.Count == 0 ? 0 : history.Max(m => m.Sequence) + 1;

        var userTurnJson = SerializeContent([new StoredPart { Kind = "text", Text = userMessage }]);
        var userMessageEntity = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Sequence = nextSequence++,
            Role = AssistantMessageRole.User,
            ContentJson = userTurnJson,
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _context.AssistantMessages.Add(userMessageEntity);
        history.Add(userMessageEntity);

        var toolContext = new AgentToolContext
        {
            QuizId = quizId,
            UserId = userId,
            Quiz = quiz,
            FocusedWordId = focusedWord?.Id,
            FocusedWordLabel = focusedWord == null ? null : $"{focusedWord.Lemma} -> {focusedWord.Translation}",
        };

        var systemInstruction = BuildSystemInstruction(quiz, focusedWord);
        var toolEvents = new List<AssistantToolEvent>();

        AgentTurnResult? finalTurn = null;
        for (var loop = 0; loop < MaxToolTurns; loop++)
        {
            var agentRequest = new AgentRequest(
                systemInstruction,
                history.Select(MapToTurn).ToList(),
                _tools.Declarations);

            AgentTurnResult turn;
            try
            {
                turn = await _gemini.RunAgentTurnAsync(agentRequest, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Gemini agent turn failed for quiz {QuizId}, thread {ThreadId}", quizId, thread.Id);
                throw;
            }

            if (turn.FunctionCalls.Count == 0)
            {
                finalTurn = turn;
                break;
            }

            var modelParts = new List<StoredPart>();
            if (!string.IsNullOrWhiteSpace(turn.Text))
            {
                modelParts.Add(new StoredPart { Kind = "text", Text = turn.Text });
            }
            foreach (var call in turn.FunctionCalls)
            {
                modelParts.Add(new StoredPart
                {
                    Kind = "function_call",
                    Name = call.Name,
                    ArgsJson = call.ArgsJson,
                    ThoughtSignature = call.ThoughtSignature,
                });
            }
            var modelMessageEntity = new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Sequence = nextSequence++,
                Role = AssistantMessageRole.Model,
                ContentJson = SerializeContent(modelParts),
                Status = AssistantMessageStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _context.AssistantMessages.Add(modelMessageEntity);
            history.Add(modelMessageEntity);

            var responseParts = new List<StoredPart>();
            foreach (var call in turn.FunctionCalls)
            {
                var result = await _tools.ExecuteAsync(call.Name, call.ArgsJson, toolContext, cancellationToken);
                var resultJson = JsonSerializer.Serialize(result, JsonOptions);
                toolEvents.Add(new AssistantToolEvent(call.Name, call.ArgsJson, SummarizeResult(result)));
                responseParts.Add(new StoredPart
                {
                    Kind = "function_response",
                    Name = call.Name,
                    ResponseJson = resultJson,
                });
            }

            var toolResponseEntity = new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Sequence = nextSequence++,
                Role = AssistantMessageRole.User,
                ContentJson = SerializeContent(responseParts),
                Status = AssistantMessageStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _context.AssistantMessages.Add(toolResponseEntity);
            history.Add(toolResponseEntity);
        }

        var finalText = finalTurn?.Text ?? "I hit my tool-call limit before finishing. Please try a smaller request.";
        var wordLabels = await LoadWordLabelsAsync(quizId, toolContext.PendingChanges, cancellationToken);
        var pendingChangeViews = toolContext.PendingChanges.Select(change => MapPendingView(change, wordLabels)).ToList();
        var pendingChangesJson = toolContext.PendingChanges.Count == 0
            ? null
            : JsonSerializer.Serialize(toolContext.PendingChanges, JsonOptions);

        var finalMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Sequence = nextSequence,
            Role = AssistantMessageRole.Model,
            ContentJson = SerializeContent([new StoredPart { Kind = "text", Text = finalText }]),
            PendingChangesJson = pendingChangesJson,
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _context.AssistantMessages.Add(finalMessage);
        thread.UpdatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new AssistantTurnResponse(
            thread.Id,
            finalMessage.Id,
            finalText,
            toolEvents,
            pendingChangeViews,
            finalMessage.Status);
    }

    public async Task<AssistantHistory> GetHistoryAsync(
        Guid quizId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var thread = await _context.AssistantThreads
            .FirstOrDefaultAsync(t => t.QuizId == quizId && t.UserId == userId, cancellationToken);
        if (thread == null)
        {
            return new AssistantHistory(null, []);
        }

        var messages = await _context.AssistantMessages
            .Where(m => m.ThreadId == thread.Id)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        var views = new List<AssistantMessageView>();
        foreach (var msg in messages)
        {
            var stored = JsonSerializer.Deserialize<StoredContent>(msg.ContentJson, JsonOptions) ?? new StoredContent();
            var text = string.Concat((stored.Parts ?? []).Where(p => p.Kind == "text").Select(p => p.Text ?? ""));
            var pendingChanges = ParseStoredChanges(msg.PendingChangesJson);
            var wordLabels = await LoadWordLabelsAsync(thread.QuizId, pendingChanges, cancellationToken);
            var pendingChangeViews = pendingChanges.Select(change => MapPendingView(change, wordLabels)).ToList();

            if (string.IsNullOrEmpty(text) && pendingChangeViews.Count == 0)
            {
                continue;
            }

            views.Add(new AssistantMessageView(
                msg.Id,
                msg.Role,
                text,
                [],
                pendingChangeViews,
                msg.Status,
                msg.CreatedAt));
        }

        return new AssistantHistory(thread.Id, views);
    }

    public async Task<int> ApplyPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var message = await LoadOwnedMessageAsync(messageId, userId, cancellationToken);
        if (string.IsNullOrWhiteSpace(message.PendingChangesJson))
        {
            return 0;
        }
        if (message.Status != AssistantMessageStatus.Active)
        {
            return 0;
        }

        var changes = ParseStoredChanges(message.PendingChangesJson);
        var thread = await _context.AssistantThreads.FirstAsync(t => t.Id == message.ThreadId, cancellationToken);
        var applied = await _changeApplier.ApplyAsync(thread.QuizId, userId, changes, cancellationToken);

        message.Status = AssistantMessageStatus.Applied;
        await _context.SaveChangesAsync(cancellationToken);
        return applied;
    }

    public async Task RejectPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var message = await LoadOwnedMessageAsync(messageId, userId, cancellationToken);
        if (message.Status != AssistantMessageStatus.Active)
        {
            return;
        }
        message.Status = AssistantMessageStatus.Rejected;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<AssistantMessage> LoadOwnedMessageAsync(Guid messageId, string userId, CancellationToken ct)
    {
        var message = await _context.AssistantMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new InvalidOperationException("Message not found.");
        var thread = await _context.AssistantThreads
            .FirstOrDefaultAsync(t => t.Id == message.ThreadId, ct)
            ?? throw new InvalidOperationException("Thread not found.");
        if (thread.UserId != userId)
        {
            throw new UnauthorizedAccessException("Message belongs to a different user.");
        }
        return message;
    }

    private async Task<AssistantThread> GetOrCreateThreadAsync(Guid quizId, string userId, CancellationToken ct)
    {
        var thread = await _context.AssistantThreads
            .FirstOrDefaultAsync(t => t.QuizId == quizId && t.UserId == userId, ct);
        if (thread != null)
        {
            return thread;
        }

        thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            UserId = userId,
            Title = "Quiz assistant",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.AssistantThreads.Add(thread);
        return thread;
    }

    private async Task<Word?> LoadFocusedWordAsync(Guid quizId, string? focusedWordId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(focusedWordId))
        {
            return null;
        }

        return await _context.Words
            .FirstOrDefaultAsync(word => word.Id == focusedWordId && word.QuizId == quizId, ct);
    }

    private static string BuildSystemInstruction(Quiz quiz, Word? focusedWord)
    {
        var focusInstruction = focusedWord == null
            ? string.Empty
            : $"""

        Current page context:
        - The assistant is opened from the word detail page for "{focusedWord.Lemma}" -> "{focusedWord.Translation}".
        - Treat this as a focused word-detail session. Any mutating tool call that edits, deletes, repairs, or updates word details must target only this word id: {focusedWord.Id}.
        - Do not propose changes to other words or other word details unless the user leaves this page and opens the assistant elsewhere.
        """;

        return $"""
        You are Glosify's language-learning assistant. You help the user manage a quiz that teaches "{quiz.TargetLanguage}" to a speaker of "{quiz.SourceLanguage}". The current quiz is named "{quiz.Name}".
        {focusInstruction}

        Rules:
        - Read-only tools (list_words, get_word) execute immediately. Their results are returned to you.
        - Mutating tools (add_word, edit_word, delete_word, set_word_detail, repair_sentence) propose changes that are queued for the user to review and Apply. You do NOT need to call any commit tool.
        - When the user gives you text to extract vocabulary from, extract meaningful words yourself and call add_word once per word. Skip closed-class words (articles, basic prepositions) unless they are central to the text.
        - When adding words, include a natural full example sentence and translation whenever you can. The sentence must use the new word's lemma or a natural inflected form.
        - When the user asks for sentences, call list_words first, then use set_word_detail for specific existing words instead of inventing disconnected standalone sentences.
        - When the user asks for grammar details, properties, conjugations, declensions, cases, forms, or variants for existing words, call get_word or list_words first, then use set_word_detail with structured properties and variants. For each variant, provide the exact display label and optional display group that should appear on the word detail page. Tags are optional compatibility metadata; when present, keep them separate and normalized, such as "nominative", "singular", "present", "first-person", "masculine-personal", or "plural".
        - Good example sentences are short, grammatical, and context-rich. Do not write pronunciation hints, gender notes, slash-separated alternatives, dictionary glosses, fragments, or markup as example sentences.
        - For sentence repair, keep the same learning target where possible and use natural inflection instead of forcing the exact dictionary form.
        - Use list_words first if you need to check what is already in the quiz before proposing edits or deletions.
        - Keep your final response concise and user-facing: one or two sentences summarising what you queued.
        - Do not mention internal tool names, tool calls, word ids, JSON, or implementation details in your final response.
        - All lemmas stay in {quiz.TargetLanguage}; all translations stay in {quiz.SourceLanguage}.
        """;
    }

    private static AgentTurn MapToTurn(AssistantMessage message)
    {
        return new AgentTurn(message.Role, message.ContentJson);
    }

    private static string SerializeContent(IReadOnlyList<StoredPart> parts)
    {
        return JsonSerializer.Serialize(new StoredContent { Parts = parts.ToList() }, JsonOptions);
    }

    private static string SummarizeResult(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return json.Length > 240 ? json[..240] + "…" : json;
    }

    private static AssistantPendingChangeView MapPendingView(
        PendingChange change,
        IReadOnlyDictionary<string, WordLabel> wordLabels)
    {
        return new AssistantPendingChangeView(change.Kind, BuildSummary(change, wordLabels), change.Payload.GetRawText());
    }

    private async Task<IReadOnlyDictionary<string, WordLabel>> LoadWordLabelsAsync(
        Guid quizId,
        IEnumerable<PendingChange> changes,
        CancellationToken cancellationToken)
    {
        var wordIds = changes
            .Select(change => GetString(change.Payload, "word_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        if (wordIds.Count == 0)
        {
            return new Dictionary<string, WordLabel>();
        }

        return await _context.Words
            .Where(word => word.QuizId == quizId && wordIds.Contains(word.Id))
            .Select(word => new WordLabel(word.Id, word.Lemma, word.Translation))
            .ToDictionaryAsync(word => word.Id, cancellationToken);
    }

    private static string BuildSummary(
        PendingChange change,
        IReadOnlyDictionary<string, WordLabel> wordLabels)
    {
        try
        {
            return change.Kind switch
            {
                PendingChangeKinds.AddWord => BuildAddWordSummary(change.Payload),
                PendingChangeKinds.EditWord => $"Edit {GetWordDisplay(change.Payload, wordLabels)}",
                PendingChangeKinds.DeleteWord => $"Remove {GetWordDisplay(change.Payload, wordLabels)}",
                PendingChangeKinds.SetWordDetail => BuildSetWordDetailSummary(change.Payload, wordLabels),
                PendingChangeKinds.RepairSentence => BuildRepairSentenceSummary(change.Payload),
                _ => change.Kind,
            };
        }
        catch
        {
            return change.Kind;
        }
    }

    private static string BuildAddWordSummary(JsonElement payload)
    {
        var summary = $"Add {GetString(payload, "lemma")} -> {GetString(payload, "translation")}";
        var sentence = GetString(payload, "example_sentence");
        return string.IsNullOrWhiteSpace(sentence)
            ? summary
            : $"{summary}: \"{Truncate(sentence, 90)}\"";
    }

    private static string BuildSetWordDetailSummary(
        JsonElement payload,
        IReadOnlyDictionary<string, WordLabel> wordLabels)
    {
        var display = GetWordDisplay(payload, wordLabels);
        var sentence = GetString(payload, "example_sentence");
        var translation = GetString(payload, "example_sentence_translation");
        var hasGrammarDetails = HasObject(payload, "properties") || HasArray(payload, "variants");

        if (!string.IsNullOrWhiteSpace(sentence) && !string.IsNullOrWhiteSpace(translation))
        {
            var prefix = hasGrammarDetails ? $"Update grammar details for {display}" : display;
            return $"{prefix}: \"{Truncate(sentence, 90)}\" ({Truncate(translation, 90)})";
        }

        if (!string.IsNullOrWhiteSpace(sentence))
        {
            var prefix = hasGrammarDetails ? $"Update grammar details for {display}" : display;
            return $"{prefix}: \"{Truncate(sentence, 90)}\"";
        }

        if (hasGrammarDetails)
        {
            return $"Update grammar details for {display}";
        }

        return $"Update {display}";
    }

    private static string BuildRepairSentenceSummary(JsonElement payload)
    {
        var original = Truncate(GetString(payload, "original_text"), 70);
        var replacement = Truncate(GetString(payload, "new_text"), 70);
        return $"Replace \"{original}\" with \"{replacement}\"";
    }

    private static string GetWordDisplay(
        JsonElement payload,
        IReadOnlyDictionary<string, WordLabel> wordLabels)
    {
        var wordId = GetString(payload, "word_id");
        if (!string.IsNullOrWhiteSpace(wordId) && wordLabels.TryGetValue(wordId, out var label))
        {
            return $"{label.Lemma} -> {label.Translation}";
        }

        return "this word";
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool HasObject(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Object && p.EnumerateObject().Any();
    }

    private static bool HasArray(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0;
    }

    private static IReadOnlyList<PendingChange> ParseStoredChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<PendingChange>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class StoredContent
    {
        public List<StoredPart>? Parts { get; set; }
    }

    private sealed class StoredPart
    {
        public string Kind { get; set; } = "text";
        public string? Text { get; set; }
        public string? Name { get; set; }
        public string? ArgsJson { get; set; }
        public string? ResponseJson { get; set; }
        public string? ThoughtSignature { get; set; }
    }

    private sealed record WordLabel(string Id, string Lemma, string Translation);
}
