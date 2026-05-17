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
        CancellationToken cancellationToken = default)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == quizId && q.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Quiz not found or not owned by this user.");

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
        };

        var systemInstruction = BuildSystemInstruction(quiz);
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
        var pendingChangeViews = toolContext.PendingChanges.Select(MapPendingView).ToList();
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
            var toolEvents = (stored.Parts ?? [])
                .Where(p => p.Kind == "function_call")
                .Select(p => new AssistantToolEvent(p.Name ?? "", p.ArgsJson ?? "{}", "queued"))
                .ToList();

            var pendingChanges = ParsePendingChanges(msg.PendingChangesJson);

            if (string.IsNullOrEmpty(text) && toolEvents.Count == 0 && pendingChanges.Count == 0)
            {
                continue;
            }

            views.Add(new AssistantMessageView(
                msg.Id,
                msg.Role,
                text,
                toolEvents,
                pendingChanges,
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

    private static string BuildSystemInstruction(Quiz quiz)
    {
        return $"""
        You are Glosify's language-learning assistant. You help the user manage a quiz that teaches "{quiz.TargetLanguage}" to a speaker of "{quiz.SourceLanguage}". The current quiz is named "{quiz.Name}".

        Rules:
        - Read-only tools (list_words, get_word) execute immediately. Their results are returned to you.
        - Mutating tools (add_word, edit_word, delete_word, set_word_detail, repair_sentence) propose changes that are queued for the user to review and Apply. You do NOT need to call any commit tool.
        - When the user gives you text to extract vocabulary from, extract meaningful words yourself and call add_word once per word. Skip closed-class words (articles, basic prepositions) unless they are central to the text.
        - Use list_words first if you need to check what is already in the quiz before proposing edits or deletions.
        - Keep your final response concise: one or two sentences summarising what you queued.
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

    private static AssistantPendingChangeView MapPendingView(PendingChange change)
    {
        return new AssistantPendingChangeView(change.Kind, BuildSummary(change), change.Payload.GetRawText());
    }

    private static string BuildSummary(PendingChange change)
    {
        try
        {
            return change.Kind switch
            {
                PendingChangeKinds.AddWord => $"Add word: {GetString(change.Payload, "lemma")} → {GetString(change.Payload, "translation")}",
                PendingChangeKinds.EditWord => $"Edit word {GetString(change.Payload, "word_id")}",
                PendingChangeKinds.DeleteWord => $"Delete word {GetString(change.Payload, "word_id")}",
                PendingChangeKinds.SetWordDetail => $"Update detail for word {GetString(change.Payload, "word_id")}",
                PendingChangeKinds.RepairSentence => $"Repair sentence: {Truncate(GetString(change.Payload, "original_text"), 60)}",
                _ => change.Kind,
            };
        }
        catch
        {
            return change.Kind;
        }
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

    private static IReadOnlyList<AssistantPendingChangeView> ParsePendingChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            var changes = JsonSerializer.Deserialize<List<PendingChange>>(json, JsonOptions) ?? [];
            return changes.Select(MapPendingView).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<PendingChange> ParseStoredChanges(string json)
    {
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
}
