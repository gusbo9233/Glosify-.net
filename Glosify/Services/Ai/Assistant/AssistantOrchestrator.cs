using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Glosify.Services.Ai.Llm;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;

namespace Glosify.Services.Ai.Assistant;

public sealed class AssistantOrchestrator : IAssistantOrchestrator
{
    private const int MaxToolTurns = 24;
    private const string NewChatTitle = "New chat";
    private const string InlineBlankMarker = "{{blank}}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;
    private readonly IGeminiClient _gemini;
    private readonly GeminiOptions _geminiOptions;
    private readonly IAssistantTools _tools;
    private readonly IChangeApplier _changeApplier;
    private readonly IBookDocumentService _books;
    private readonly ILanguageContext _languageContext;
    private readonly ILogger<AssistantOrchestrator> _logger;

    public AssistantOrchestrator(
        GlosifyContext context,
        IGeminiClient gemini,
        IOptions<GeminiOptions> geminiOptions,
        IAssistantTools tools,
        IChangeApplier changeApplier,
        IBookDocumentService books,
        ILanguageContext languageContext,
        ILogger<AssistantOrchestrator> logger)
    {
        _context = context;
        _gemini = gemini;
        _geminiOptions = geminiOptions.Value;
        _tools = tools;
        _changeApplier = changeApplier;
        _books = books;
        _languageContext = languageContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AssistantChatSummary>> ListChatsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var threads = await _context.AssistantThreads
            .Where(thread => thread.UserId == userId && thread.QuizId == null)
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToListAsync(cancellationToken);

        return await BuildChatSummariesAsync(threads, cancellationToken);
    }

    public async Task<AssistantChatSummary> CreateChatAsync(
        string userId,
        Guid? contextQuizId = null,
        CancellationToken cancellationToken = default)
    {
        await ValidateContextQuizAsync(contextQuizId, userId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            QuizId = null,
            ContextQuizId = contextQuizId,
            UserId = userId,
            Title = NewChatTitle,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _context.AssistantThreads.Add(thread);
        await _context.SaveChangesAsync(cancellationToken);

        return (await BuildChatSummariesAsync([thread], cancellationToken)).Single();
    }

    public async Task<AssistantChatSummary> UpdateChatAsync(
        Guid threadId,
        string userId,
        string? title = null,
        Guid? contextQuizId = null,
        bool updateContext = false,
        CancellationToken cancellationToken = default)
    {
        var thread = await LoadOwnedGlobalThreadAsync(threadId, userId, cancellationToken);

        if (title is not null)
        {
            thread.Title = BuildExplicitTitle(title);
        }

        if (updateContext)
        {
            await ValidateContextQuizAsync(contextQuizId, userId, cancellationToken);
            thread.ContextQuizId = contextQuizId;
        }

        thread.UpdatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return (await BuildChatSummariesAsync([thread], cancellationToken)).Single();
    }

    public async Task DeleteChatAsync(
        Guid threadId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var thread = await LoadOwnedGlobalThreadAsync(threadId, userId, cancellationToken);
        _context.AssistantThreads.Remove(thread);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantHistory> GetChatHistoryAsync(
        Guid threadId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var thread = await LoadOwnedGlobalThreadAsync(threadId, userId, cancellationToken);
        var messages = await LoadThreadMessagesAsync(thread.Id, cancellationToken);
        return new AssistantHistory(thread.Id, await MapMessageViewsAsync(messages, cancellationToken));
    }

    public async Task<AssistantTurnResponse> SendChatMessageAsync(
        Guid threadId,
        string userId,
        string userMessage,
        Guid? contextQuizId = null,
        string? focusedWordId = null,
        string? model = null,
        AssistantDocumentContext? documentContext = null,
        Guid? customQuizId = null,
        CancellationToken cancellationToken = default)
    {
        var thread = await LoadOwnedGlobalThreadAsync(threadId, userId, cancellationToken);
        return await SendInThreadAsync(
            thread,
            userId,
            userMessage,
            contextQuizId,
            focusedWordId,
            model,
            documentContext,
            customQuizId,
            cancellationToken);
    }

    public async Task<AssistantTurnResponse> SendMessageAsync(
        Guid quizId,
        string userId,
        string userMessage,
        string? focusedWordId = null,
        string? model = null,
        AssistantDocumentContext? documentContext = null,
        CancellationToken cancellationToken = default)
    {
        var thread = await GetOrCreateDefaultGlobalThreadAsync(userId, quizId, cancellationToken);
        return await SendInThreadAsync(
            thread,
            userId,
            userMessage,
            quizId,
            focusedWordId,
            model,
            documentContext,
            null,
            cancellationToken);
    }

    public async Task<AssistantTurnResponse> SendGlobalMessageAsync(
        string userId,
        string userMessage,
        string? model = null,
        AssistantDocumentContext? documentContext = null,
        CancellationToken cancellationToken = default)
    {
        var thread = await GetOrCreateDefaultGlobalThreadAsync(userId, null, cancellationToken);
        return await SendInThreadAsync(
            thread,
            userId,
            userMessage,
            thread.ContextQuizId,
            null,
            model,
            documentContext,
            null,
            cancellationToken);
    }

    public async Task<AssistantHistory> GetHistoryAsync(
        Guid quizId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var thread = await GetOrCreateDefaultGlobalThreadAsync(userId, quizId, cancellationToken);
        var messages = await LoadThreadMessagesAsync(thread.Id, cancellationToken);
        return new AssistantHistory(thread.Id, await MapMessageViewsAsync(messages, cancellationToken));
    }

    public async Task<AssistantHistory> GetGlobalHistoryAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var thread = await GetOrCreateDefaultGlobalThreadAsync(userId, null, cancellationToken);
        var messages = await LoadThreadMessagesAsync(thread.Id, cancellationToken);
        return new AssistantHistory(thread.Id, await MapMessageViewsAsync(messages, cancellationToken));
    }

    public async Task<AssistantApplyResult> ApplyPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        return await ApplyGlobalPendingChangesAsync(messageId, userId, cancellationToken);
    }

    public async Task<AssistantApplyResult> ApplyGlobalPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var message = await LoadOwnedMessageAsync(messageId, userId, cancellationToken);
        if (message.Status != AssistantMessageStatus.Active)
        {
            return new AssistantApplyResult(0);
        }

        var changes = ParseStoredChanges(message.PendingChangesJson);
        if (changes.Count == 0)
        {
            return new AssistantApplyResult(0);
        }

        // Claim the message before applying so concurrent Apply requests (e.g. a
        // double-click) cannot run the same changes twice; revert the claim if
        // applying fails so the user can retry.
        message.Status = AssistantMessageStatus.Applied;
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            return await _changeApplier.ApplyAsync(message.ContextQuizId, userId, changes, cancellationToken);
        }
        catch
        {
            // Drop whatever the failed apply left in the change tracker, then put the
            // message back to Active with a token that survives client aborts.
            _context.ChangeTracker.Clear();
            var claimed = await _context.AssistantMessages
                .FirstOrDefaultAsync(m => m.Id == messageId, CancellationToken.None);
            if (claimed != null)
            {
                claimed.Status = AssistantMessageStatus.Active;
                await _context.SaveChangesAsync(CancellationToken.None);
            }
            throw;
        }
    }

    public async Task RejectPendingChangesAsync(
        Guid messageId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await RejectGlobalPendingChangesAsync(messageId, userId, cancellationToken);
    }

    public async Task RejectGlobalPendingChangesAsync(
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

    public async Task ResetGlobalSessionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await CreateChatAsync(userId, null, cancellationToken);
    }

    private async Task<AssistantTurnResponse> SendInThreadAsync(
        AssistantThread thread,
        string userId,
        string userMessage,
        Guid? contextQuizId,
        string? focusedWordId,
        string? model,
        AssistantDocumentContext? documentContext,
        Guid? customQuizId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var contextQuiz = await ValidateContextQuizAsync(contextQuizId, userId, cancellationToken);
        var contextCustomQuiz = await ValidateCustomQuizAsync(customQuizId, contextQuiz, userId, cancellationToken);
        var focusedWord = contextQuiz is null ? null : await LoadFocusedWordAsync(contextQuiz.Id, focusedWordId, cancellationToken);
        var documentPage = documentContext is null
            ? null
            : await LoadDocumentPageContextAsync(documentContext, userId, cancellationToken);

        var storedMessages = await LoadThreadMessagesAsync(thread.Id, cancellationToken);
        var history = WindowHistory(storedMessages).Select(MapToTurn).ToList();
        var nextSequence = storedMessages.Count == 0 ? 0 : storedMessages.Max(message => message.Sequence) + 1;

        var userTurnJson = SerializeContent([new StoredPart { Kind = "text", Text = userMessage }]);
        var userTurn = new AgentTurn(AssistantMessageRole.User, userTurnJson);
        history.Add(userTurn);
        _context.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            ContextQuizId = contextQuizId,
            Sequence = nextSequence++,
            Role = AssistantMessageRole.User,
            ContentJson = userTurnJson,
            Status = AssistantMessageStatus.Active,
            CreatedAt = now,
        });

        if (string.Equals(thread.Title, NewChatTitle, StringComparison.OrdinalIgnoreCase))
        {
            thread.Title = BuildAutoTitle(userMessage);
        }

        thread.ContextQuizId = contextQuizId;
        thread.UpdatedAt = now;

        // Persist the user's message (and title/context updates) before calling the
        // LLM so a failed turn does not erase what the user typed from history.
        await _context.SaveChangesAsync(cancellationToken);

        var toolContext = new AgentToolContext
        {
            QuizId = contextQuiz?.Id,
            CustomQuizId = contextCustomQuiz?.Id,
            UserId = userId,
            CurrentLanguage = contextQuiz?.TargetLanguage ?? _languageContext.CurrentLanguage,
            FocusedWordId = focusedWord?.Id,
            FocusedWordLabel = focusedWord == null ? null : $"{focusedWord.Lemma} -> {focusedWord.Translation}",
        };

        var systemInstruction = contextQuiz is null
            ? BuildGlobalSystemInstruction(_languageContext.CurrentLanguage, documentPage)
            : BuildSystemInstruction(contextQuiz, focusedWord, documentPage, contextCustomQuiz);
        var declarations = contextQuiz is null
            ? _tools.GlobalDeclarations
            : _tools.GlobalDeclarations.Concat(_tools.Declarations).ToList();
        var selectedModel = ResolveAssistantModel(model);
        var toolEvents = new List<AssistantToolEvent>();

        AgentTurnResult? finalTurn = null;
        for (var loop = 0; loop < MaxToolTurns; loop++)
        {
            var agentRequest = new AgentRequest(systemInstruction, history, declarations, selectedModel);

            AgentTurnResult turn;
            try
            {
                turn = await _gemini.RunAgentTurnAsync(
                    agentRequest,
                    new AiUsageContext(
                        userId,
                        AiUsageFeatures.Assistant,
                        "assistant_turn",
                        Guid.NewGuid(),
                        "assistant_thread",
                        thread.Id.ToString()),
                    cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Gemini agent turn failed for assistant thread {ThreadId}", thread.Id);
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

            var modelTurn = new AgentTurn(AssistantMessageRole.Model, SerializeContent(modelParts));
            history.Add(modelTurn);
            _context.AssistantMessages.Add(new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                ContextQuizId = contextQuizId,
                Sequence = nextSequence++,
                Role = AssistantMessageRole.Model,
                ContentJson = modelTurn.ContentJson,
                Status = AssistantMessageStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            });

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

            var toolTurn = new AgentTurn(AssistantMessageRole.User, SerializeContent(responseParts));
            history.Add(toolTurn);
            _context.AssistantMessages.Add(new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                ContextQuizId = contextQuizId,
                Sequence = nextSequence++,
                Role = AssistantMessageRole.User,
                ContentJson = toolTurn.ContentJson,
                Status = AssistantMessageStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var finalText = finalTurn?.Text ?? "I hit my tool-call limit before finishing. Please try a smaller request.";
        var pendingChangesJson = toolContext.PendingChanges.Count == 0
            ? null
            : JsonSerializer.Serialize(toolContext.PendingChanges, JsonOptions);
        var wordLabels = await LoadWordLabelsAsync(contextQuizId, toolContext.PendingChanges, cancellationToken);
        var pendingChangeViews = toolContext.PendingChanges.Select(change => MapPendingView(change, wordLabels)).ToList();
        var assistantMessageId = Guid.NewGuid();
        var finalMessage = new AssistantMessage
        {
            Id = assistantMessageId,
            ThreadId = thread.Id,
            ContextQuizId = contextQuizId,
            Sequence = nextSequence,
            Role = AssistantMessageRole.Model,
            ContentJson = SerializeContent([new StoredPart { Kind = "text", Text = finalText }]),
            PendingChangesJson = pendingChangesJson,
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _context.AssistantMessages.Add(finalMessage);
        thread.UpdatedAt = finalMessage.CreatedAt;
        await _context.SaveChangesAsync(cancellationToken);

        return new AssistantTurnResponse(
            thread.Id,
            assistantMessageId,
            finalText,
            toolEvents,
            pendingChangeViews,
            AssistantMessageStatus.Active);
    }

    private string ResolveAssistantModel(string? requestedModel)
    {
        var currentModel = string.IsNullOrWhiteSpace(_geminiOptions.AssistantModel)
            ? _geminiOptions.StructuredModel
            : _geminiOptions.AssistantModel;

        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return currentModel;
        }

        // Only configured models may be requested by the client.
        var trimmed = requestedModel.Trim();
        return string.Equals(trimmed, currentModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, _geminiOptions.Model, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : currentModel;
    }

    private async Task<AssistantThread> GetOrCreateDefaultGlobalThreadAsync(
        string userId,
        Guid? contextQuizId,
        CancellationToken cancellationToken)
    {
        await ValidateContextQuizAsync(contextQuizId, userId, cancellationToken);

        var thread = await _context.AssistantThreads
            .Where(t => t.UserId == userId && t.QuizId == null)
            .OrderByDescending(t => t.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (thread != null)
        {
            if (contextQuizId != thread.ContextQuizId)
            {
                thread.ContextQuizId = contextQuizId;
                thread.UpdatedAt = DateTimeOffset.UtcNow;
            }
            return thread;
        }

        var now = DateTimeOffset.UtcNow;
        thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            QuizId = null,
            ContextQuizId = contextQuizId,
            UserId = userId,
            Title = NewChatTitle,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.AssistantThreads.Add(thread);
        return thread;
    }

    private async Task<AssistantThread> LoadOwnedGlobalThreadAsync(Guid threadId, string userId, CancellationToken ct)
    {
        var thread = await _context.AssistantThreads
            .FirstOrDefaultAsync(t => t.Id == threadId && t.UserId == userId && t.QuizId == null, ct)
            ?? throw new InvalidOperationException("Chat not found.");
        return thread;
    }

    private async Task<AssistantMessage> LoadOwnedMessageAsync(Guid messageId, string userId, CancellationToken ct)
    {
        var message = await _context.AssistantMessages
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new InvalidOperationException("Message not found.");
        var thread = await _context.AssistantThreads
            .FirstOrDefaultAsync(t => t.Id == message.ThreadId, ct)
            ?? throw new InvalidOperationException("Chat not found.");
        if (thread.UserId != userId)
        {
            throw new UnauthorizedAccessException("Message belongs to a different user.");
        }
        return message;
    }

    private async Task<List<AssistantMessage>> LoadThreadMessagesAsync(Guid threadId, CancellationToken ct)
    {
        return await _context.AssistantMessages
            .AsNoTracking()
            .Where(message => message.ThreadId == threadId)
            .OrderBy(message => message.Sequence)
            .ToListAsync(ct);
    }

    private async Task<Quiz?> ValidateContextQuizAsync(Guid? quizId, string userId, CancellationToken cancellationToken)
    {
        if (!quizId.HasValue)
        {
            return null;
        }

        return await _context.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == quizId.Value && q.UserId == userId, cancellationToken)
            ?? throw new QuizNotFoundException();
    }

    private async Task<CustomQuiz?> ValidateCustomQuizAsync(
        Guid? customQuizId,
        Quiz? quiz,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!customQuizId.HasValue)
        {
            return null;
        }

        if (quiz == null)
        {
            throw new InvalidOperationException("Choose the source quiz for this custom quiz.");
        }

        return await _context.CustomQuizzes
            .AsNoTracking()
            .Include(item => item.Quiz)
            .FirstOrDefaultAsync(item => item.Id == customQuizId.Value
                && item.QuizId == quiz.Id
                && item.Quiz.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("That custom quiz was not found.");
    }

    private async Task<IReadOnlyList<AssistantChatSummary>> BuildChatSummariesAsync(
        IReadOnlyList<AssistantThread> threads,
        CancellationToken cancellationToken)
    {
        if (threads.Count == 0)
        {
            return [];
        }

        // "Visible" (HasVisibleContent) can only be decided client-side, but the latest
        // visible message is virtually always among the last few: tool call/response
        // turns come in short bursts and every assistant turn ends with a text message.
        // Fetching a small recent window per thread keeps this from loading entire
        // conversations just to build 90-character previews.
        var threadIds = threads.Select(thread => thread.Id).ToList();
        var recentByThread = await _context.AssistantThreads
            .AsNoTracking()
            .Where(thread => threadIds.Contains(thread.Id))
            .Select(thread => new
            {
                thread.Id,
                Recent = _context.AssistantMessages
                    .Where(message => message.ThreadId == thread.Id)
                    .OrderByDescending(message => message.Sequence)
                    .Take(8)
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        var latestByThread = recentByThread
            .ToDictionary(entry => entry.Id, entry => entry.Recent.FirstOrDefault(HasVisibleContent));

        var contextQuizIds = threads
            .Select(thread => thread.ContextQuizId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var quizNames = contextQuizIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Quizzes
                .Where(quiz => contextQuizIds.Contains(quiz.Id))
                .ToDictionaryAsync(quiz => quiz.Id, quiz => quiz.Name, cancellationToken);

        return threads
            .Select(thread =>
            {
                latestByThread.TryGetValue(thread.Id, out var latest);
                var preview = latest == null ? string.Empty : Truncate(ExtractVisibleText(latest), 90);
                var quizName = thread.ContextQuizId.HasValue && quizNames.TryGetValue(thread.ContextQuizId.Value, out var name)
                    ? name
                    : null;
                return new AssistantChatSummary(
                    thread.Id,
                    string.IsNullOrWhiteSpace(thread.Title) ? NewChatTitle : thread.Title,
                    thread.CreatedAt,
                    thread.UpdatedAt,
                    preview,
                    thread.ContextQuizId,
                    quizName);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<AssistantMessageView>> MapMessageViewsAsync(
        IReadOnlyList<AssistantMessage> messages,
        CancellationToken cancellationToken)
    {
        var parsed = messages
            .Select(message => (Message: message, Changes: ParseStoredChanges(message.PendingChangesJson)))
            .ToList();

        // One label query per distinct context quiz (almost always one) instead of
        // one query per message.
        var emptyLabels = (IReadOnlyDictionary<string, WordLabel>)new Dictionary<string, WordLabel>();
        var labelsByQuiz = new Dictionary<Guid, IReadOnlyDictionary<string, WordLabel>>();
        foreach (var group in parsed
            .Where(entry => entry.Message.ContextQuizId.HasValue && entry.Changes.Count > 0)
            .GroupBy(entry => entry.Message.ContextQuizId!.Value))
        {
            labelsByQuiz[group.Key] = await LoadWordLabelsAsync(
                group.Key,
                group.SelectMany(entry => entry.Changes),
                cancellationToken);
        }

        return parsed
            .Select(entry =>
            {
                var wordLabels = entry.Message.ContextQuizId.HasValue
                    ? labelsByQuiz.GetValueOrDefault(entry.Message.ContextQuizId.Value, emptyLabels)
                    : emptyLabels;
                return new AssistantMessageView(
                    entry.Message.Id,
                    entry.Message.Role,
                    ExtractVisibleText(entry.Message),
                    [],
                    entry.Changes.Select(change => MapPendingView(change, wordLabels)).ToList(),
                    entry.Message.Status,
                    entry.Message.CreatedAt);
            })
            .ToList();
    }

    private async Task<Word?> LoadFocusedWordAsync(Guid quizId, string? focusedWordId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(focusedWordId))
        {
            return null;
        }

        return await _context.Words
            .AsNoTracking()
            .FirstOrDefaultAsync(word => word.Id == focusedWordId && word.QuizId == quizId, ct);
    }

    private async Task<DocumentPageContext> LoadDocumentPageContextAsync(
        AssistantDocumentContext documentContext,
        string userId,
        CancellationToken cancellationToken)
    {
        if (documentContext.PageNumber < 1)
        {
            throw new InvalidOperationException("Choose a valid book page.");
        }

        var page = await _books.GetOwnedPageAsync(
            documentContext.DocumentId,
            documentContext.PageNumber,
            userId,
            cancellationToken);

        if (page == null)
        {
            throw new InvalidOperationException("That book page was not found.");
        }

        return new DocumentPageContext(
            page.BookDocument.Title,
            page.PageNumber,
            page.Text,
            page.ExtractionWarning);
    }

    private static string BuildSystemInstruction(
        Quiz quiz,
        Word? focusedWord,
        DocumentPageContext? documentPage,
        CustomQuiz? customQuiz)
    {
        var focusInstruction = focusedWord == null
            ? string.Empty
            : $"""

        Current page context:
        - The assistant is focused on "{focusedWord.Lemma}" -> "{focusedWord.Translation}".
        - Any mutating tool call that edits, deletes, or repairs content must target only this word id when a word id is required: {focusedWord.Id}.
        - Do not propose changes to other words unless the user leaves this context.
        """;
        var documentInstruction = documentPage == null
            ? string.Empty
            : BuildDocumentInstruction(documentPage);
        var customQuizInstruction = customQuiz == null
            ? string.Empty
            : $"""

        Current custom quiz creator context:
        - The open custom quiz is "{customQuiz.Name}" with id {customQuiz.Id}.
        - Use get_custom_quiz before changing its elements, then add, configure, or remove elements as requested.
        """;

        return $"""
        You are Glosify's language-learning assistant. The user is learning "{quiz.TargetLanguage}" as a speaker of "{quiz.SourceLanguage}", and is currently working in a quiz named "{quiz.Name}".

        You are a general language-learning companion: answer questions about grammar, vocabulary, usage, culture, and study strategy conversationally, and manage the quiz's content when the user asks for that. Use your own judgment about what the user wants; the guidance below describes defaults, and the user's explicit wishes always win.
        {focusInstruction}
        {documentInstruction}
        {customQuizInstruction}

        How tools work:
        - Read-only tools (list_words, search_words, get_word, get_quiz_summary, list_sentences, list_quizzes, list_collections, list_custom_quizzes, list_custom_quiz_templates, get_custom_quiz) execute immediately and return their results to you.
        - Mutating tools, including the custom quiz element tools, propose changes that are queued for the user to review and Apply. You do NOT need to call any commit tool. Because the user reviews everything, you can propose changes freely when they seem helpful.
        - When adding or editing more than one word, prefer add_words or edit_words over repeated single-word calls.
        - When adding or editing more than one sentence, prefer add_sentences or edit_sentences over repeated single-sentence calls.
        - Use list_words when you need to know what is already in the quiz before proposing edits or deletions.
        - Use search_words when looking for specific vocabulary and get_quiz_summary when the user asks about quiz size, language, collection, or visibility.
        - Use list_sentences before editing, repairing, or deleting quiz sentences. Prefer edit_sentence/edit_sentences for id-based edits; repair_sentence replaces every exact text match.
        - For library-level requests, use list_collections and list_quizzes to find existing structure before creating, moving, or renaming items. Never invent quiz or collection ids — ask the user if you cannot identify the item.
        - For custom quizzes, inspect an existing document first. Before creating or substantially redesigning one, call list_custom_quiz_templates and use the best template as visual and layout guidance. Pass its template_id during creation. Prefer the compact textbook exercise patterns represented by the Textbook drill template: a short heading and instruction followed by consecutive rows, with minimal card chrome. A playable document needs exactly one submit_button, exactly one feedback_message, and at least one answer control. Every answer control must have a specific learner-visible label that contains its question or gap; multiple answer controls must have distinct labels. Text inputs need either an expected word binding or literal expected_text; choice controls need at least two options and valid correct selections. Use stable descriptive element ids and non-overlapping 12-column layout coordinates.
        - "Custom quiz" is a specific interactive quiz-builder artifact, not a synonym for a vocabulary quiz. With the current backing quiz, first call create_custom_quiz to queue only its empty shell. Then call add_label, add_text_input, add_checkbox, add_choice, add_word_bank, add_submit_button, or add_feedback_message separately for every element. Use add_custom_quiz_element only for an element the typed tools cannot express. Never send a blocks array or a complete custom document in a creation or element call. create_vocabulary_quiz is only for standard word-and-translation quizzes.
        - A single-line text_input is a compact inline blank. Put {InlineBlankMarker} exactly where the input belongs in its label, for example "1. ja będ{InlineBlankMarker} jutro w domu." Never include underscore or dot runs: they create a fake blank beside the real control. For conjugation, cloze, and word transformation, normally use one text_input per compact row and do not add a separate prompt_label for the same item. Pack rows consecutively instead of making tall cards. For fill-in-the-ending questions, set expected_text to only the literal ending (for example "ę" or "esz"), not the full word unless the user asks for it.

        Defaults (override when the user asks for something different):
        - When extracting vocabulary from text, default to a complete extraction: every unique word except proper names, including closed-class words such as articles, pronouns, conjunctions, prepositions, particles, and auxiliary verbs. If the user asks for a selection instead (e.g. "the hard words", "just the verbs", "the ten most useful"), follow their criteria.
        - Convert inflected forms to a natural dictionary headword, merge repeated forms of the same headword, and keep first-appearance order, unless the user wants the exact forms.
        - Words go in add_word/add_words; full sentences go in add_sentence/add_sentences. Follow the user's intent about whether they want words, sentences, or both.
        - Good example sentences are short, grammatical, and context-rich; avoid pronunciation hints, dictionary glosses, fragments, or markup as sentence text.
        - For sentence repair, keep the same learning target where possible and prefer natural inflection over forcing the exact dictionary form.
        - Words are normally in {quiz.TargetLanguage} with translations in {quiz.SourceLanguage}; deviate only when the user clearly wants otherwise.
        - If the current book page has no selectable text, explain that Glosify cannot read this page and suggest choosing another page or pasting text.

        Style:
        - Match your response to the request: a short confirmation when you queued changes, a fuller conversational answer when the user asks a question or wants explanation.
        - Do not mention internal tool names, tool calls, word ids, JSON, or implementation details in your final response.
        """;
    }

    private static string BuildGlobalSystemInstruction(string? currentLanguage, DocumentPageContext? documentPage)
    {
        var languageInstruction = string.IsNullOrWhiteSpace(currentLanguage)
            ? "No current app language is selected. If the user wants to create a quiz or collection and did not name a target language, ask for the target language before using creation tools."
            : $"The current app language is \"{currentLanguage}\". Use it as the default target language for new quizzes and the default language for new collections unless the user clearly asks for another language.";
        var documentInstruction = documentPage == null
            ? string.Empty
            : BuildDocumentInstruction(documentPage);

        return $"""
        You are Glosify's app-wide language-learning assistant.

        You are a general language-learning companion: help the user with grammar, vocabulary, usage, culture, study planning, and any other language-learning question, and help them understand the app and organise their quiz library when asked. Use your own judgment about what the user wants; the guidance below describes defaults, and the user's explicit wishes always win.

        Current context:
        - {languageInstruction}
        {documentInstruction}

        How tools work:
        - Read-only tools (list_collections, list_quizzes, list_custom_quizzes, list_custom_quiz_templates, get_custom_quiz) execute immediately and return their results to you.
        - Mutating tools, including custom quiz creation and element tools, propose changes that are queued for the user to review and Apply. Because the user reviews everything, you can propose changes freely when they seem helpful.
        - Use list_collections and list_quizzes before proposing library changes unless the user gave an exact id through the UI.
        - Do not invent quiz or collection ids. If you cannot identify an item or destination unambiguously, ask the user to clarify.
        - "Custom quiz" means an interactive quiz-builder artifact. It is distinct from the standard word-and-translation quiz created by create_vocabulary_quiz.

        Defaults (override when the user asks for something different):
        - If the user asks for a standard vocabulary quiz with starter vocabulary, include those words in create_vocabulary_quiz.
        - If the user asks for a custom quiz from a book page or pasted text and no backing quiz exists yet, first call list_custom_quiz_templates, prefer the Textbook drill template for textbook-derived conjugation, cloze, and transformation work, and pass its template_id to create_custom_quiz_from_content. Then call add_label, add_text_input, add_checkbox, add_choice, add_word_bank, add_submit_button, or add_feedback_message once for each element, following that template's layout guidance. Bind word-backed elements to the exact word string in the starter words. Never send a blocks array or complete custom document in one call. Finish with exactly one submit button and one feedback message.
        - A single-line text_input is a compact inline blank. Put {InlineBlankMarker} exactly where the real input belongs in its label, for example "1. ja będ{InlineBlankMarker} jutro w domu." Never draw blanks with underscores or dots. For textbook conjugation, cloze, and transformation exercises, use one text_input per compact consecutive row and do not add a separate prompt label for the same item. For endings, expected_text is only the literal ending (for example "ę" or "esz"), not the whole word unless requested.
        - When extracting starter vocabulary from text, default to a complete extraction: every unique word except proper names, including closed-class words such as articles, pronouns, conjunctions, prepositions, particles, and auxiliary verbs. If the user asks for a selection instead, follow their criteria.
        - Convert inflected forms to dictionary headwords, merge repeated headwords, and preserve first-appearance order, unless the user wants the exact forms.
        - If the current book page has no selectable text, explain that Glosify cannot read this page and suggest choosing another page or pasting text.

        Style:
        - Match your response to the request: a short confirmation when you queued changes, a fuller conversational answer when the user asks a question or wants explanation.
        - Do not mention internal tool names, tool calls, ids, JSON, routes, or implementation details.
        """;
    }

    private static string BuildDocumentInstruction(DocumentPageContext documentPage)
    {
        var pageText = string.IsNullOrWhiteSpace(documentPage.Text)
            ? $"[{documentPage.Warning ?? "No selectable text found on this page."}]"
            : documentPage.Text;

        return $"""

        Current book page context:
        - Document: "{documentPage.Title}"
        - Page: {documentPage.PageNumber}
        - The user is reading this page now.
        - When the user says "this page", "here", "from the book", or "from what I am reading", they mean this page text.
        - You may combine the page with your general knowledge when explaining or answering questions, but words and sentences extracted "from the page" should actually come from it.

        Page text:
        ---
        {pageText}
        ---
        """;
    }

    // Replayed history per turn is capped so old threads don't grow token cost and
    // latency without bound. A single user turn can persist up to 1 + MaxToolTurns*2 + 1
    // messages, so the window must comfortably exceed that to keep at least the
    // previous full exchange.
    private const int MaxHistoryMessages = 80;

    private static IReadOnlyList<AssistantMessage> WindowHistory(List<AssistantMessage> messages)
    {
        if (messages.Count <= MaxHistoryMessages)
        {
            return messages;
        }

        var window = messages.Skip(messages.Count - MaxHistoryMessages).ToList();

        // Gemini rejects histories where a function response has no preceding call,
        // so advance the window start to the first plain-text user message.
        var start = window.FindIndex(message =>
            message.Role == AssistantMessageRole.User && !string.IsNullOrWhiteSpace(ExtractVisibleText(message)));
        return start <= 0 ? window : window.Skip(start).ToList();
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
        return json.Length > 240 ? json[..240] + "..." : json;
    }

    private static AssistantPendingChangeView MapPendingView(
        PendingChange change,
        IReadOnlyDictionary<string, WordLabel> wordLabels)
    {
        return new AssistantPendingChangeView(change.Kind, BuildSummary(change, wordLabels), change.Payload.GetRawText());
    }

    private async Task<IReadOnlyDictionary<string, WordLabel>> LoadWordLabelsAsync(
        Guid? quizId,
        IEnumerable<PendingChange> changes,
        CancellationToken cancellationToken)
    {
        if (!quizId.HasValue)
        {
            return new Dictionary<string, WordLabel>();
        }

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
            .Where(word => word.QuizId == quizId.Value && wordIds.Contains(word.Id))
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
                PendingChangeKinds.AddSentence => BuildAddSentenceSummary(change.Payload),
                PendingChangeKinds.EditWord => BuildEditWordSummary(change.Payload, wordLabels),
                PendingChangeKinds.EditSentence => BuildEditSentenceSummary(change.Payload),
                PendingChangeKinds.DeleteWord => $"Remove {GetWordDisplay(change.Payload, wordLabels)}",
                PendingChangeKinds.RepairSentence => BuildRepairSentenceSummary(change.Payload),
                PendingChangeKinds.DeleteSentence => BuildDeleteSentenceSummary(change.Payload),
                PendingChangeKinds.CreateQuiz => BuildCreateQuizSummary(change.Payload),
                PendingChangeKinds.CreateCollection => BuildCreateCollectionSummary(change.Payload),
                PendingChangeKinds.MoveQuiz => BuildMoveQuizSummary(change.Payload),
                PendingChangeKinds.RenameCollection => BuildRenameCollectionSummary(change.Payload),
                PendingChangeKinds.MoveCollection => BuildMoveCollectionSummary(change.Payload),
                PendingChangeKinds.CreateCustomQuiz => $"Create custom quiz \"{GetString(change.Payload, "name")}\"",
                PendingChangeKinds.AddCustomQuizElement => BuildAddCustomQuizElementSummary(change.Payload),
                PendingChangeKinds.AddCustomQuizElements => $"Add custom quiz elements to \"{GetString(change.Payload, "custom_quiz_name")}\"",
                PendingChangeKinds.ConfigureCustomQuizElement => $"Configure element {GetString(change.Payload, "block_id")} in \"{GetString(change.Payload, "custom_quiz_name")}\"",
                PendingChangeKinds.RemoveCustomQuizElement => $"Remove element {GetString(change.Payload, "block_id")} from \"{GetString(change.Payload, "custom_quiz_name")}\"",
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
        return $"Add {GetString(payload, "word")} -> {GetString(payload, "translation")}";
    }

    private static string BuildAddCustomQuizElementSummary(JsonElement payload)
    {
        if (!payload.TryGetProperty("block", out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return "Add custom quiz element";
        }
        var type = GetString(block, "type");
        var id = GetString(block, "id");
        var visible = GetString(block, "label");
        if (string.IsNullOrWhiteSpace(visible)) visible = GetString(block, "text");
        var detail = string.IsNullOrWhiteSpace(visible) ? id : Truncate(visible, 70);
        return $"Add {type} {detail} to \"{GetString(payload, "custom_quiz_name")}\"";
    }

    private static string BuildAddSentenceSummary(JsonElement payload)
    {
        var text = Truncate(GetString(payload, "text"), 90);
        var translation = Truncate(GetString(payload, "translation"), 90);
        return string.IsNullOrWhiteSpace(translation)
            ? $"Add sentence \"{text}\""
            : $"Add sentence \"{text}\" ({translation})";
    }

    private static string BuildEditWordSummary(
        JsonElement payload,
        IReadOnlyDictionary<string, WordLabel> wordLabels)
    {
        var wordId = GetString(payload, "word_id");
        wordLabels.TryGetValue(wordId, out var label);

        var originalWord = FirstNonEmpty(GetString(payload, "original_word"), label?.Word);
        var originalTranslation = FirstNonEmpty(GetString(payload, "original_translation"), label?.Translation);
        var newWord = FirstNonEmpty(GetString(payload, "word"), originalWord);
        var newTranslation = FirstNonEmpty(GetString(payload, "translation"), originalTranslation);

        var changes = new List<string>();
        if (!string.IsNullOrWhiteSpace(originalWord)
            && !string.IsNullOrWhiteSpace(newWord)
            && !string.Equals(originalWord, newWord, StringComparison.Ordinal))
        {
            changes.Add($"{originalWord} -> {newWord}");
        }

        if (!string.IsNullOrWhiteSpace(originalTranslation)
            && !string.IsNullOrWhiteSpace(newTranslation)
            && !string.Equals(originalTranslation, newTranslation, StringComparison.Ordinal))
        {
            changes.Add($"{originalTranslation} -> {newTranslation}");
        }

        if (changes.Count > 0)
        {
            return $"Edit {string.Join("; ", changes)}";
        }

        if (!string.IsNullOrWhiteSpace(originalWord) || !string.IsNullOrWhiteSpace(originalTranslation))
        {
            return $"Edit {FormatWordPair(originalWord, originalTranslation)}";
        }

        return $"Edit {GetWordDisplay(payload, wordLabels)}";
    }

    private static string BuildRepairSentenceSummary(JsonElement payload)
    {
        var original = Truncate(GetString(payload, "original_text"), 70);
        var replacement = Truncate(GetString(payload, "new_text"), 70);
        return $"Replace \"{original}\" with \"{replacement}\"";
    }

    private static string BuildEditSentenceSummary(JsonElement payload)
    {
        var originalText = Truncate(GetString(payload, "original_text"), 60);
        var newText = Truncate(FirstNonEmpty(GetString(payload, "text"), originalText), 60);
        var originalTranslation = Truncate(GetString(payload, "original_translation"), 60);
        var newTranslation = Truncate(
            FirstNonEmpty(GetString(payload, "translation"), originalTranslation),
            60);

        var changes = new List<string>();
        if (!string.Equals(originalText, newText, StringComparison.Ordinal))
        {
            changes.Add($"\"{originalText}\" -> \"{newText}\"");
        }
        if (!string.Equals(originalTranslation, newTranslation, StringComparison.Ordinal))
        {
            changes.Add($"\"{originalTranslation}\" -> \"{newTranslation}\"");
        }

        return changes.Count == 0
            ? $"Edit sentence \"{originalText}\""
            : $"Edit sentence {string.Join("; ", changes)}";
    }

    private static string BuildDeleteSentenceSummary(JsonElement payload)
    {
        var text = Truncate(GetString(payload, "text"), 90);
        return string.IsNullOrWhiteSpace(text)
            ? "Remove sentence"
            : $"Remove sentence \"{text}\"";
    }

    private static string BuildCreateQuizSummary(JsonElement payload)
    {
        var name = GetString(payload, "name");
        var source = GetString(payload, "source_language");
        var target = GetString(payload, "target_language");
        var includesCustomQuiz = payload.TryGetProperty("custom_quiz", out var customQuiz)
            && customQuiz.ValueKind == JsonValueKind.Object;
        return includesCustomQuiz
            ? $"Create quiz \"{name}\" and custom quiz \"{GetString(customQuiz, "name")}\" ({source} -> {target})"
            : $"Create quiz \"{name}\" ({source} -> {target})";
    }

    private static string BuildCreateCollectionSummary(JsonElement payload)
    {
        var name = GetString(payload, "name");
        var language = GetString(payload, "language");
        return $"Create collection \"{name}\" in {language}";
    }

    private static string BuildMoveQuizSummary(JsonElement payload)
    {
        var quizName = GetString(payload, "quiz_name");
        var collectionName = GetString(payload, "collection_name");
        return string.IsNullOrWhiteSpace(collectionName)
            ? $"Move quiz \"{quizName}\" to the library root"
            : $"Move quiz \"{quizName}\" to collection \"{collectionName}\"";
    }

    private static string BuildRenameCollectionSummary(JsonElement payload)
    {
        var originalName = GetString(payload, "original_name");
        var name = GetString(payload, "name");
        return $"Rename collection \"{originalName}\" to \"{name}\"";
    }

    private static string BuildMoveCollectionSummary(JsonElement payload)
    {
        var collectionName = GetString(payload, "collection_name");
        var parentName = GetString(payload, "parent_collection_name");
        return string.IsNullOrWhiteSpace(parentName)
            ? $"Move collection \"{collectionName}\" to the library root"
            : $"Move collection \"{collectionName}\" under \"{parentName}\"";
    }

    private static string GetWordDisplay(
        JsonElement payload,
        IReadOnlyDictionary<string, WordLabel> wordLabels)
    {
        var wordId = GetString(payload, "word_id");
        if (!string.IsNullOrWhiteSpace(wordId) && wordLabels.TryGetValue(wordId, out var label))
        {
            return $"{label.Word} -> {label.Translation}";
        }

        return "this word";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string FormatWordPair(string? word, string? translation)
    {
        if (!string.IsNullOrWhiteSpace(word) && !string.IsNullOrWhiteSpace(translation))
        {
            return $"{word} -> {translation}";
        }

        return string.IsNullOrWhiteSpace(word) ? translation ?? string.Empty : word;
    }

    private static string BuildAutoTitle(string message)
    {
        return BuildExplicitTitle(message);
    }

    private static string BuildExplicitTitle(string title)
    {
        var cleaned = string.Join(" ", title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? NewChatTitle : Truncate(cleaned, 64);
    }

    private static bool HasVisibleContent(AssistantMessage message)
    {
        return !string.IsNullOrWhiteSpace(ExtractVisibleText(message))
            || !string.IsNullOrWhiteSpace(message.PendingChangesJson);
    }

    private static string ExtractVisibleText(AssistantMessage message)
    {
        try
        {
            var content = JsonSerializer.Deserialize<StoredContent>(message.ContentJson, JsonOptions);
            var parts = content?.Parts ?? [];
            if (parts.Any(part => part.Kind != "text"))
            {
                return string.Empty;
            }

            return string.Join("\n", parts
                .Select(part => part.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + "...";
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;
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

    private sealed record WordLabel(string Id, string Word, string Translation);
    private sealed record DocumentPageContext(string Title, int PageNumber, string Text, string? Warning);
}
