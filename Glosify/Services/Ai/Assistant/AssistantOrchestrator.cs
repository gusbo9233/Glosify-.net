using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glosify.Services;

public sealed class AssistantOrchestrator : IAssistantOrchestrator
{
    private const int MaxToolTurns = 24;
    private const string NewChatTitle = "New chat";
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
        var changes = ParseStoredChanges(message.PendingChangesJson);
        if (changes.Count == 0)
        {
            return new AssistantApplyResult(0);
        }

        var result = await _changeApplier.ApplyAsync(message.ContextQuizId, userId, changes, cancellationToken);
        message.Status = AssistantMessageStatus.Applied;
        await _context.SaveChangesAsync(cancellationToken);
        return result;
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
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var contextQuiz = await ValidateContextQuizAsync(contextQuizId, userId, cancellationToken);
        var focusedWord = contextQuiz is null ? null : await LoadFocusedWordAsync(contextQuiz.Id, focusedWordId, cancellationToken);
        var documentPage = documentContext is null
            ? null
            : await LoadDocumentPageContextAsync(documentContext, userId, cancellationToken);

        var storedMessages = await LoadThreadMessagesAsync(thread.Id, cancellationToken);
        var history = storedMessages.Select(MapToTurn).ToList();
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

        var toolContext = new AgentToolContext
        {
            QuizId = contextQuiz?.Id,
            UserId = userId,
            Quiz = contextQuiz,
            CurrentLanguage = contextQuiz?.TargetLanguage ?? _languageContext.CurrentLanguage,
            FocusedWordId = focusedWord?.Id,
            FocusedWordLabel = focusedWord == null ? null : $"{focusedWord.Lemma} -> {focusedWord.Translation}",
        };

        var systemInstruction = contextQuiz is null
            ? BuildGlobalSystemInstruction(_languageContext.CurrentLanguage, documentPage)
            : BuildSystemInstruction(contextQuiz, focusedWord, documentPage);
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

        var trimmed = requestedModel.Trim();
        return string.Equals(trimmed, currentModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "gemini-3.5-flash", StringComparison.OrdinalIgnoreCase)
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
            .FirstOrDefaultAsync(q => q.Id == quizId.Value && q.UserId == userId, cancellationToken)
            ?? throw new QuizNotFoundException();
    }

    private async Task<IReadOnlyList<AssistantChatSummary>> BuildChatSummariesAsync(
        IReadOnlyList<AssistantThread> threads,
        CancellationToken cancellationToken)
    {
        if (threads.Count == 0)
        {
            return [];
        }

        var threadIds = threads.Select(thread => thread.Id).ToList();
        var messages = await _context.AssistantMessages
            .Where(message => threadIds.Contains(message.ThreadId))
            .OrderByDescending(message => message.Sequence)
            .ToListAsync(cancellationToken);

        var latestByThread = messages
            .GroupBy(message => message.ThreadId)
            .ToDictionary(group => group.Key, group => group.FirstOrDefault(HasVisibleContent));

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
        var views = new List<AssistantMessageView>();
        foreach (var message in messages)
        {
            var pendingChanges = ParseStoredChanges(message.PendingChangesJson);
            var wordLabels = await LoadWordLabelsAsync(message.ContextQuizId, pendingChanges, cancellationToken);
            var pendingViews = pendingChanges.Select(change => MapPendingView(change, wordLabels)).ToList();
            views.Add(new AssistantMessageView(
                message.Id,
                message.Role,
                ExtractVisibleText(message),
                [],
                pendingViews,
                message.Status,
                message.CreatedAt));
        }

        return views;
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

    private static string BuildSystemInstruction(Quiz quiz, Word? focusedWord, DocumentPageContext? documentPage)
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

        return $"""
        You are Glosify's language-learning assistant. You help the user manage a quiz that teaches "{quiz.TargetLanguage}" to a speaker of "{quiz.SourceLanguage}". The current quiz is named "{quiz.Name}".
        {focusInstruction}
        {documentInstruction}

        Rules:
        - Read-only tools (list_words, get_word) execute immediately. Their results are returned to you.
        - Mutating tools (add_word, add_words, add_sentence, edit_word, edit_words, delete_word, repair_sentence) propose changes that are queued for the user to review and Apply. You do NOT need to call any commit tool.
        - When adding or editing more than one word, prefer add_words or edit_words instead of repeated single-word calls.
        - When the user gives you text to extract vocabulary from, extract meaningful words yourself and call add_words with all useful words. Skip closed-class words (articles, basic prepositions) unless they are central to the text.
        - When the user asks to make a quiz from the current book page, extract useful vocabulary from the current page text.
        - Do not add a sentence when the user only asks for words.
        - Do not put sentence text in add_word. If the user asks for standalone quiz sentences, or pasted text already contains natural full sentences, call add_sentence once per sentence.
        - If current page text is available and the user asks for sentences from it, call add_sentence for natural full sentences from that page.
        - If the current book page has no selectable text, explain that Glosify cannot read this page in v1 and ask the user to choose another page or paste text.
        - When the user asks for grammar details, properties, conjugations, declensions, cases, forms, or variants, answer conversationally and recommend the Wiktionary link on the word card for dictionary detail.
        - When the user asks to add or update example sentences, use add_sentence or repair_sentence.
        - Good example sentences are short, grammatical, and context-rich. Do not write pronunciation hints, gender notes, slash-separated alternatives, dictionary glosses, fragments, or markup as example sentences.
        - For sentence repair, keep the same learning target where possible and use natural inflection instead of forcing the exact dictionary form.
        - Use list_words first if you need to check what is already in the quiz before proposing edits or deletions.
        - Keep your final response concise and user-facing: one or two sentences summarising what you queued.
        - Do not mention internal tool names, tool calls, word ids, JSON, or implementation details in your final response.
        - All words stay in {quiz.TargetLanguage}; all translations stay in {quiz.SourceLanguage}.
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

        Help the user understand the app, plan study sessions, think through language-learning questions, and create quiz-library structure when asked.

        Current context:
        - {languageInstruction}
        {documentInstruction}

        Rules:
        - Read-only tools (list_collections, list_quizzes) execute immediately. Their results are returned to you.
        - Mutating tools (create_collection, create_quiz) propose changes that are queued for the user to review and Apply.
        - Use list_collections before proposing a nested collection or placing a quiz into an existing collection unless the user gave an exact id through the UI.
        - Use list_quizzes if you need to check for duplicate quiz names.
        - If the user asks to create a quiz with starter vocabulary, include those words in the create_quiz tool call.
        - When the user asks to make a quiz from the current book page, extract useful starter vocabulary from the current page text.
        - If current page text is available and the user asks for sentences from it, use natural full sentences from that page.
        - If the current book page has no selectable text, explain that Glosify cannot read this page in v1 and ask the user to choose another page or paste text.
        - Do not invent collection ids. If you cannot identify the collection, ask the user to clarify.
        - Keep your final response concise and user-facing: one or two sentences summarising what you queued or what detail you still need.
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
        - When the user says "this page", "here", "from the book", or "from what I am reading", use this page text.
        - Use only this page text unless the user asks for something else.
        - Keep generated words and sentences queued for review.

        Page text:
        ---
        {pageText}
        ---
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
                PendingChangeKinds.DeleteWord => $"Remove {GetWordDisplay(change.Payload, wordLabels)}",
                PendingChangeKinds.RepairSentence => BuildRepairSentenceSummary(change.Payload),
                PendingChangeKinds.CreateQuiz => BuildCreateQuizSummary(change.Payload),
                PendingChangeKinds.CreateCollection => BuildCreateCollectionSummary(change.Payload),
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
        return $"Add {GetString(payload, "word", "lemma")} -> {GetString(payload, "translation")}";
    }

    private static string BuildAddSentenceSummary(JsonElement payload)
    {
        var text = Truncate(GetString(payload, "text", "sentence"), 90);
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
        var newWord = FirstNonEmpty(GetString(payload, "word", "lemma"), originalWord);
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

    private static string BuildCreateQuizSummary(JsonElement payload)
    {
        var name = GetString(payload, "name");
        var source = GetString(payload, "source_language");
        var target = GetString(payload, "target_language");
        return $"Create quiz \"{name}\" ({source} -> {target})";
    }

    private static string BuildCreateCollectionSummary(JsonElement payload)
    {
        var name = GetString(payload, "name");
        var language = GetString(payload, "language");
        return $"Create collection \"{name}\" in {language}";
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

    private static string GetString(JsonElement element, string preferredProperty, string legacyProperty)
    {
        var preferred = GetString(element, preferredProperty);
        return string.IsNullOrWhiteSpace(preferred) ? GetString(element, legacyProperty) : preferred;
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
