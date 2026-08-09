using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantTurnRunner
{
    private const int MaxToolTurns = 24;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;
    private readonly IGenerativeAiClient _generativeAi;
    private readonly IGenerativeAiModelResolver _modelResolver;
    private readonly IAssistantTools _tools;
    private readonly AssistantThreadStore _threads;
    private readonly AssistantContextResolver _contextResolver;
    private readonly AssistantMessagePresenter _presenter;
    private readonly AssistantPromptBuilder _promptBuilder;
    private readonly ILogger<AssistantTurnRunner> _logger;

    public AssistantTurnRunner(
        GlosifyContext context,
        IGenerativeAiClient generativeAi,
        IGenerativeAiModelResolver modelResolver,
        IAssistantTools tools,
        AssistantThreadStore threads,
        AssistantContextResolver contextResolver,
        AssistantMessagePresenter presenter,
        AssistantPromptBuilder promptBuilder,
        ILogger<AssistantTurnRunner> logger)
    {
        _context = context;
        _generativeAi = generativeAi;
        _modelResolver = modelResolver;
        _tools = tools;
        _threads = threads;
        _contextResolver = contextResolver;
        _presenter = presenter;
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    public async Task<AssistantTurnResponse> RunChatAsync(
        Guid threadId,
        string userId,
        string userMessage,
        Guid? contextQuizId,
        string? focusedWordId,
        string? model,
        AssistantDocumentContext? documentContext,
        Guid? customQuizId,
        Guid? transcriptId,
        Guid? bookDocumentId,
        CancellationToken cancellationToken)
    {
        var thread = await _threads.GetOwnedAsync(threadId, userId, cancellationToken);
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

    public async Task<AssistantTurnResponse> RunQuizAsync(
        Guid quizId,
        string userId,
        string userMessage,
        string? focusedWordId,
        string? model,
        AssistantDocumentContext? documentContext,
        CancellationToken cancellationToken)
    {
        var thread = await _threads.GetOrCreateDefaultAsync(userId, quizId, cancellationToken);
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

    public async Task<AssistantTurnResponse> RunGlobalAsync(
        string userId,
        string userMessage,
        string? model,
        AssistantDocumentContext? documentContext,
        CancellationToken cancellationToken)
    {
        var thread = await _threads.GetOrCreateDefaultAsync(userId, null, cancellationToken);
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

        var storedMessages = await _threads.LoadMessagesAsync(thread.Id, cancellationToken);
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

        if (string.Equals(thread.Title, AssistantThreadDefaults.NewChatTitle, StringComparison.OrdinalIgnoreCase))
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
        var pendingChangeViews = toolContext.PendingChanges
            .Select(change => _presenter.PresentPendingChange(change, wordLabels))
            .ToList();
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

    private async Task<IReadOnlyDictionary<string, AssistantWordLabel>> LoadWordLabelsAsync(
        Guid? quizId,
        IEnumerable<PendingChange> changes,
        CancellationToken cancellationToken)
    {
        if (!quizId.HasValue)
        {
            return new Dictionary<string, AssistantWordLabel>();
        }

        var wordIds = _presenter.GetReferencedWordIds(changes);

        if (wordIds.Count == 0)
        {
            return new Dictionary<string, AssistantWordLabel>();
        }

        return await _context.Words
            .Where(word => word.QuizId == quizId.Value && wordIds.Contains(word.Id))
            .Select(word => new AssistantWordLabel(word.Id, word.Lemma, word.Translation))
            .ToDictionaryAsync(word => word.Id, cancellationToken);
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

}
