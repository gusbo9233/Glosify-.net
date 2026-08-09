using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantRuntime
{
    private const int MaxToolTurns = 24;
    private const string NewChatTitle = "New chat";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;
    private readonly IGenerativeAiClient _generativeAi;
    private readonly IGenerativeAiModelResolver _modelResolver;
    private readonly IAssistantTools _tools;
    private readonly IChangeApplier _changeApplier;
    private readonly IAssistantContextResolver _contextResolver;
    private readonly IAssistantMessagePresenter _presenter;
    private readonly AssistantPromptBuilder _promptBuilder;
    private readonly ILogger<AssistantRuntime> _logger;

    public AssistantRuntime(
        GlosifyContext context,
        IGenerativeAiClient generativeAi,
        IGenerativeAiModelResolver modelResolver,
        IAssistantTools tools,
        IChangeApplier changeApplier,
        IAssistantContextResolver contextResolver,
        IAssistantMessagePresenter presenter,
        AssistantPromptBuilder promptBuilder,
        ILogger<AssistantRuntime> logger)
    {
        _context = context;
        _generativeAi = generativeAi;
        _modelResolver = modelResolver;
        _tools = tools;
        _changeApplier = changeApplier;
        _contextResolver = contextResolver;
        _presenter = presenter;
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AssistantChatSummary>> ListChatsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var language = await _contextResolver.ResolveLanguageAsync(userId, cancellationToken);
        var query = _context.AssistantThreads
            .Where(thread => thread.UserId == userId && thread.QuizId == null);
        if (language != null)
        {
            query = query.Where(thread => thread.Language == language);
        }

        var threads = await query
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToListAsync(cancellationToken);

        return await BuildChatSummariesAsync(threads, cancellationToken);
    }

    public async Task<AssistantChatSummary> CreateChatAsync(
        string userId,
        Guid? contextQuizId = null,
        CancellationToken cancellationToken = default,
        Guid? contextTranscriptId = null,
        Guid? contextBookDocumentId = null)
    {
        await _contextResolver.ResolveQuizAsync(contextQuizId, userId, cancellationToken);
        await _contextResolver.ResolveTranscriptAsync(contextTranscriptId, userId, cancellationToken);
        await _contextResolver.ResolveBookAsync(contextBookDocumentId, userId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            QuizId = null,
            ContextQuizId = contextQuizId,
            ContextTranscriptId = contextTranscriptId,
            ContextBookDocumentId = contextBookDocumentId,
            UserId = userId,
            Language = await _contextResolver.ResolveLanguageAsync(userId, cancellationToken),
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
        CancellationToken cancellationToken = default,
        Guid? contextTranscriptId = null,
        Guid? contextBookDocumentId = null)
    {
        var thread = await LoadOwnedGlobalThreadAsync(threadId, userId, cancellationToken);

        if (title is not null)
        {
            thread.Title = _presenter.NormalizeTitle(title);
        }

        // Deliberately all-or-nothing: the caller sends the complete context it wants the
        // chat to have, so an omitted field clears rather than keeps the stored value.
        if (updateContext)
        {
            await _contextResolver.ResolveQuizAsync(contextQuizId, userId, cancellationToken);
            await _contextResolver.ResolveTranscriptAsync(contextTranscriptId, userId, cancellationToken);
            await _contextResolver.ResolveBookAsync(contextBookDocumentId, userId, cancellationToken);
            thread.ContextQuizId = contextQuizId;
            thread.ContextTranscriptId = contextTranscriptId;
            thread.ContextBookDocumentId = contextBookDocumentId;
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
        CancellationToken cancellationToken = default,
        Guid? transcriptId = null,
        Guid? bookDocumentId = null)
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
            cancellationToken,
            transcriptId ?? thread.ContextTranscriptId,
            bookDocumentId ?? thread.ContextBookDocumentId);
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
            cancellationToken,
            null,
            null);
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
            cancellationToken,
            thread.ContextTranscriptId,
            thread.ContextBookDocumentId);
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
        CancellationToken cancellationToken,
        Guid? transcriptId,
        Guid? bookDocumentId)
    {
        var now = DateTimeOffset.UtcNow;
        var contextQuiz = await _contextResolver.ResolveQuizAsync(contextQuizId, userId, cancellationToken);
        var contextCustomQuiz = await ValidateCustomQuizAsync(customQuizId, contextQuiz, userId, cancellationToken);
        var focusedWord = contextQuiz is null ? null : await LoadFocusedWordAsync(contextQuiz.Id, focusedWordId, cancellationToken);
        var documentPage = documentContext is null
            ? null
            : await _contextResolver.ResolveDocumentPageAsync(documentContext, userId, cancellationToken);
        var transcriptContext = await _contextResolver.ResolveTranscriptAsync(transcriptId, userId, cancellationToken);
        var bookContext = await _contextResolver.ResolveBookAsync(bookDocumentId, userId, cancellationToken);
        var selectedLanguageCode = await _contextResolver.ResolveLanguageCodeAsync(userId, cancellationToken);
        var currentLanguage = contextQuiz?.TargetLanguage
            ?? await _contextResolver.ResolveLanguageAsync(userId, cancellationToken);

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
            thread.Title = _presenter.NormalizeTitle(userMessage);
        }

        thread.ContextQuizId = contextQuizId;
        thread.ContextTranscriptId = transcriptContext?.Id;
        thread.ContextBookDocumentId = bookContext?.Id;
        thread.UpdatedAt = now;

        // Persist the user's message (and title/context updates) before calling the
        // LLM so a failed turn does not erase what the user typed from history.
        await _context.SaveChangesAsync(cancellationToken);

        var toolContext = new AgentToolContext
        {
            QuizId = contextQuiz?.Id,
            CustomQuizId = contextCustomQuiz?.Id,
            UserId = userId,
            CurrentLanguage = currentLanguage,
            CurrentLanguageCode = selectedLanguageCode,
            FocusedWordId = focusedWord?.Id,
            FocusedWordLabel = focusedWord == null ? null : $"{focusedWord.Lemma} -> {focusedWord.Translation}",
            TranscriptId = transcriptContext?.Id,
            BookDocumentId = bookContext?.Id,
        };

        var systemInstruction = _promptBuilder.BuildSystemInstruction(
            contextQuiz,
            focusedWord,
            documentPage,
            contextCustomQuiz,
            transcriptContext,
            bookContext,
            currentLanguage);

        // The page the user is on selects the profile, which fixes both the tool set and
        // which authored agent supplies the instructions. Each profile falls back to the
        // in-code instruction and declarations when no agent is configured for it.
        var (profile, declarations) = contextCustomQuiz is not null && contextQuiz is not null
            ? (AssistantAgentProfile.CustomQuizBuilder, _tools.CustomQuizBuilderDeclarations)
            : contextQuiz is not null
                ? (AssistantAgentProfile.QuizAssistant, _tools.QuizAssistantDeclarations)
                : (AssistantAgentProfile.Librarian, _tools.LibrarianDeclarations);

        var contextInstruction = _promptBuilder.BuildProfileContext(
            profile,
            contextQuiz,
            focusedWord,
            documentPage,
            contextCustomQuiz,
            transcriptContext,
            bookContext,
            currentLanguage);
        var selectedModel = _modelResolver.ResolveAssistantModel(model);
        var toolEvents = new List<AssistantToolEvent>();

        AgentTurnResult? finalTurn = null;
        for (var loop = 0; loop < MaxToolTurns; loop++)
        {
            var agentRequest = new AgentRequest(
                systemInstruction,
                history,
                declarations,
                selectedModel,
                profile,
                contextInstruction);

            AgentTurnResult turn;
            try
            {
                turn = await _generativeAi.RunAgentTurnAsync(
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
                _logger.LogWarning(ex, "Generative AI turn failed for assistant thread {ThreadId}", thread.Id);
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
                    CallId = call.CallId,
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
                    CallId = call.CallId,
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

    private async Task<AssistantThread> GetOrCreateDefaultGlobalThreadAsync(
        string userId,
        Guid? contextQuizId,
        CancellationToken cancellationToken)
    {
        await _contextResolver.ResolveQuizAsync(contextQuizId, userId, cancellationToken);

        // Only chats in the selected language can be resumed, so switching language
        // drops into a fresh thread instead of continuing the previous one.
        var language = await _contextResolver.ResolveLanguageAsync(userId, cancellationToken);
        var query = _context.AssistantThreads
            .Where(t => t.UserId == userId && t.QuizId == null);
        if (language != null)
        {
            query = query.Where(t => t.Language == language);
        }

        var thread = await query
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
            Language = language,
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

        // The chat list has already dropped this thread, so a request still pointing at
        // it comes from a page opened before the language changed.
        var language = await _contextResolver.ResolveLanguageAsync(userId, ct);
        if (language != null && thread.Language != language)
        {
            throw new InvalidOperationException(
                $"That chat belongs to another language. Reload the page to start a {language} chat.");
        }

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
            .ToDictionary(entry => entry.Id, entry => entry.Recent.FirstOrDefault(_presenter.HasVisibleContent));

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
        var selectedLanguageCode = await _contextResolver.ResolveLanguageCodeAsync(
            threads[0].UserId,
            cancellationToken);
        var contextTranscriptIds = threads
            .Select(thread => thread.ContextTranscriptId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var transcriptTitles = contextTranscriptIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.RealtimeTranslationTranscripts
                .Where(transcript => contextTranscriptIds.Contains(transcript.Id)
                    && selectedLanguageCode != null
                    && transcript.TargetLanguage == selectedLanguageCode)
                .ToDictionaryAsync(transcript => transcript.Id, transcript => transcript.Title, cancellationToken);
        var contextBookIds = threads
            .Select(thread => thread.ContextBookDocumentId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        // No language filter, matching ValidateBookContextAsync: a book chosen while
        // another language was selected still belongs to this chat and needs its title.
        var bookTitles = contextBookIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.BookDocuments
                .Where(book => contextBookIds.Contains(book.Id))
                .ToDictionaryAsync(book => book.Id, book => book.Title, cancellationToken);

        return threads
            .Select(thread =>
            {
                latestByThread.TryGetValue(thread.Id, out var latest);
                var preview = latest == null ? string.Empty : Truncate(_presenter.ExtractVisibleText(latest), 90);
                var quizName = thread.ContextQuizId.HasValue && quizNames.TryGetValue(thread.ContextQuizId.Value, out var name)
                    ? name
                    : null;
                var transcriptTitle = thread.ContextTranscriptId.HasValue
                    && transcriptTitles.TryGetValue(thread.ContextTranscriptId.Value, out var savedTitle)
                        ? savedTitle
                        : null;
                var bookTitle = thread.ContextBookDocumentId.HasValue
                    && bookTitles.TryGetValue(thread.ContextBookDocumentId.Value, out var storedTitle)
                        ? storedTitle
                        : null;
                return new AssistantChatSummary(
                    thread.Id,
                    string.IsNullOrWhiteSpace(thread.Title) ? NewChatTitle : thread.Title,
                    thread.CreatedAt,
                    thread.UpdatedAt,
                    preview,
                    thread.ContextQuizId,
                    quizName,
                    thread.ContextTranscriptId,
                    transcriptTitle,
                    thread.ContextBookDocumentId,
                    bookTitle);
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
                    _presenter.ExtractVisibleText(entry.Message),
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

    // Replayed history per turn is capped so old threads don't grow token cost and
    // latency without bound. A single user turn can persist up to 1 + MaxToolTurns*2 + 1
    // messages, so the window must comfortably exceed that to keep at least the
    // previous full exchange.
    private const int MaxHistoryMessages = 80;

    private IReadOnlyList<AssistantMessage> WindowHistory(List<AssistantMessage> messages)
    {
        if (messages.Count <= MaxHistoryMessages)
        {
            return messages;
        }

        var window = messages.Skip(messages.Count - MaxHistoryMessages).ToList();

        // Providers reject histories where a function response has no preceding call,
        // so advance the window start to the first plain-text user message.
        var start = window.FindIndex(message =>
            message.Role == AssistantMessageRole.User && !string.IsNullOrWhiteSpace(_presenter.ExtractVisibleText(message)));
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
        public string? CallId { get; set; }
        public string? ThoughtSignature { get; set; }
    }

    private sealed record WordLabel(string Id, string Word, string Translation);
}
