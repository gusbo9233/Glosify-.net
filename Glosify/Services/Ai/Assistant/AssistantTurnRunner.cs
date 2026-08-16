using System.Diagnostics;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Language;
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
    private readonly AssistantIntentResolver _intentResolver;
    private readonly IAssistantTurnLeaseService _turnLeases;
    private readonly AssistantAnalyticsStore _analytics;
    private readonly TimeProvider _timeProvider;
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
        AssistantIntentResolver intentResolver,
        IAssistantTurnLeaseService turnLeases,
        AssistantAnalyticsStore analytics,
        TimeProvider timeProvider,
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
        _intentResolver = intentResolver;
        _turnLeases = turnLeases;
        _analytics = analytics;
        _timeProvider = timeProvider;
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
        AssistantTranscriptPageContext? transcriptPageContext,
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
            bookDocumentId ?? thread.ContextBookDocumentId,
            transcriptPageContext);
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
            thread.ContextTranscriptId,
            thread.ContextBookDocumentId);
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
        Guid? bookDocumentId,
        AssistantTranscriptPageContext? transcriptPageContext = null)
    {
        var leaseId = await _turnLeases.TryAcquireAsync(thread.Id, userId, cancellationToken)
            ?? throw new AssistantTurnInProgressException();

        try
        {
            return await SendLeasedThreadAsync(
                thread,
                userId,
                userMessage,
                contextQuizId,
                focusedWordId,
                model,
                documentContext,
                customQuizId,
                leaseId,
                cancellationToken,
                transcriptId,
                bookDocumentId,
                transcriptPageContext);
        }
        finally
        {
            try
            {
                await _turnLeases.ReleaseAsync(thread.Id, leaseId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not release assistant turn lease {LeaseId} for thread {ThreadId}. " +
                    "The lease will expire automatically.",
                    leaseId,
                    thread.Id);
            }
        }
    }

    private async Task<AssistantTurnResponse> SendLeasedThreadAsync(
        AssistantThread thread,
        string userId,
        string userMessage,
        Guid? contextQuizId,
        string? focusedWordId,
        string? model,
        AssistantDocumentContext? documentContext,
        Guid? customQuizId,
        Guid leaseId,
        CancellationToken cancellationToken,
        Guid? transcriptId,
        Guid? bookDocumentId,
        AssistantTranscriptPageContext? transcriptPageContext = null)
    {
        var turnId = Guid.NewGuid();
        var turnStartedAt = Stopwatch.GetTimestamp();
        var now = _timeProvider.GetUtcNow();
        var preliminaryProfile = customQuizId.HasValue
            ? AssistantAgentProfile.CustomQuizBuilder
            : contextQuizId.HasValue
                ? AssistantAgentProfile.QuizAssistant
                : AssistantAgentProfile.Librarian;
        using var turnActivity = AssistantAnalyticsTelemetry.StartTurn(
            turnId,
            thread.Id,
            null,
            preliminaryProfile.ToString(),
            model);

        var turnEntity = new AssistantTurn
        {
            Id = turnId,
            ThreadId = thread.Id,
            Profile = preliminaryProfile.ToString(),
            RequestedModel = model,
            Status = AssistantTurnStatus.Started,
            StartedAt = now,
            TraceId = turnActivity?.TraceId.ToHexString(),
        };
        _context.AssistantTurns.Add(turnEntity);

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
            TurnId = turnId,
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

        thread.UpdatedAt = now;

        // This is deliberately the first fallible work after ownership and sequence
        // resolution. Context, model, prompt, provider, and tool failures must all leave
        // the submitted text and a finalizable turn behind.
        await _context.SaveChangesAsync(cancellationToken);

        var toolSequence = 0;
        AgentInvocationMetadata? lastMetadata = null;
        AgentToolContext? toolContext = null;
        string? resolvedModel = null;
        var analyticsInvocations = new List<AssistantModelInvocation>();
        var analyticsExecutions = new List<AssistantToolExecution>();

        try
        {
            var contextQuiz = await _contextResolver.ResolveQuizAsync(contextQuizId, userId, cancellationToken);
            var contextQuizIsFreestyle = QuizLanguageCatalog.IsFreestyle(contextQuiz?.TargetLanguage);
            var contextCustomQuiz = await ValidateCustomQuizAsync(customQuizId, contextQuiz, userId, cancellationToken);
            var focusedWord = contextQuiz is null
                ? null
                : await LoadFocusedWordAsync(contextQuiz.Id, focusedWordId, cancellationToken);
            var documentPage = documentContext is null
                ? null
                : await _contextResolver.ResolveDocumentPageAsync(documentContext, userId, cancellationToken);
            var transcriptContext = contextQuizIsFreestyle
                ? null
                : await _contextResolver.ResolveTranscriptAsync(
                    transcriptId,
                    transcriptPageContext,
                    userId,
                    cancellationToken);
            var bookContext = await _contextResolver.ResolveBookAsync(bookDocumentId, userId, cancellationToken);
            var selectedLanguageCode = await _contextResolver.ResolveLanguageCodeAsync(userId, cancellationToken);
            var currentLanguage = contextQuiz?.TargetLanguage
                ?? await _contextResolver.ResolveLanguageAsync(userId, cancellationToken);
            var sourceLanguage = await _contextResolver.ResolveSourceLanguageAsync(
                contextQuiz,
                userId,
                thread,
                cancellationToken);
            var replyLanguage = await _contextResolver.ResolveReplyLanguageAsync(
                userId,
                thread,
                cancellationToken);
            var isFreestyle = contextQuizIsFreestyle
                || QuizLanguageCatalog.IsFreestyle(currentLanguage);
            if (isFreestyle)
            {
                sourceLanguage = QuizLanguageCatalog.FreestyleName;
            }

            // The page the user is on selects the profile, which fixes both the tool set and
            // which authored agent supplies the instructions. Each profile falls back to the
            // in-code instruction and declarations when no agent is configured for it.
            var (profile, declarations) = (isFreestyle, contextCustomQuiz is not null, contextQuiz is not null) switch
            {
                (true, true, true) => (
                    AssistantAgentProfile.FreestyleCustomQuizBuilder,
                    _tools.FreestyleCustomQuizBuilderDeclarations),
                (true, _, true) => (
                    AssistantAgentProfile.FreestyleQuizAssistant,
                    _tools.FreestyleQuizAssistantDeclarations),
                (true, _, false) => (
                    AssistantAgentProfile.FreestyleLibrarian,
                    _tools.FreestyleLibrarianDeclarations),
                (false, true, true) => (
                    AssistantAgentProfile.CustomQuizBuilder,
                    _tools.CustomQuizBuilderDeclarations),
                (false, _, true) => (
                    AssistantAgentProfile.QuizAssistant,
                    _tools.QuizAssistantDeclarations),
                _ => (AssistantAgentProfile.Librarian, _tools.LibrarianDeclarations),
            };
            // The page says which surface is reachable; the request says which part of it the
            // user actually asked for. Narrowing on both is what stops "create a quiz" landing
            // in the custom builder and sentence requests landing in word storage.
            var intent = _intentResolver.Resolve(userMessage);
            var allowedToolNames = AssistantToolNarrowing.AllowedNames(declarations, intent, profile);
            var selectedModel = _modelResolver.ResolveAssistantModel(model);
            resolvedModel = selectedModel;
            var subjectId = await _context.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => (Guid?)user.AssistantTelemetrySubjectId)
                .SingleOrDefaultAsync(cancellationToken);
            turnActivity?.SetTag("enduser.pseudo.id", subjectId?.ToString());
            turnActivity?.SetTag("assistant.profile", profile.ToString());
            turnActivity?.SetTag("assistant.intent.artifact", intent.ArtifactKind.ToString());
            turnActivity?.SetTag("assistant.intent.content", intent.ContentKind.ToString());
            turnActivity?.SetTag("gen_ai.request.model", selectedModel);
            turnEntity.Profile = profile.ToString();
            RecordTurnInputs(
                turnEntity,
                intent,
                allowedToolNames,
                currentLanguage,
                sourceLanguage,
                replyLanguage);
            // Chats that predate the column, and any the store could not resolve a language
            // for, settle on one here rather than re-deciding every turn.
            thread.ConversationLanguage ??= replyLanguage;
            thread.ContextQuizId = contextQuizId;
            thread.ContextTranscriptId = transcriptContext?.Id;
            thread.ContextBookDocumentId = bookContext?.Id;

            toolContext = new AgentToolContext
            {
                QuizId = contextQuiz?.Id,
                CustomQuizId = contextCustomQuiz?.Id,
                UserId = userId,
                CurrentLanguage = currentLanguage,
                CurrentLanguageCode = selectedLanguageCode,
                IsFreestyle = isFreestyle,
                SourceLanguage = sourceLanguage,
                ReplyLanguage = replyLanguage,
                FocusedWordId = focusedWord?.Id,
                FocusedWordLabel = focusedWord == null ? null : $"{focusedWord.Lemma} -> {focusedWord.Translation}",
                TranscriptId = transcriptContext?.Id,
                BookDocumentId = bookContext?.Id,
                RequestedContentKind = intent.ContentKind,
            };

            var systemInstruction = _promptBuilder.BuildSystemInstruction(
                contextQuiz,
                focusedWord,
                documentPage,
                contextCustomQuiz,
                transcriptContext,
                bookContext,
                currentLanguage);

            var contextInstruction = _promptBuilder.BuildProfileContext(
                profile,
                contextQuiz,
                focusedWord,
                documentPage,
                contextCustomQuiz,
                transcriptContext,
                bookContext,
                currentLanguage,
                sourceLanguage,
                replyLanguage);
            var toolEvents = new List<AssistantToolEvent>();
            var completedTurnMessages = new List<AssistantMessage>();

            AgentTurnResult? finalTurn = null;
            for (var loop = 0; loop < MaxToolTurns; loop++)
            {
                if (!await _turnLeases.RenewAsync(thread.Id, leaseId, cancellationToken))
                {
                    throw new AssistantTurnInProgressException();
                }

                var agentRequest = new AgentRequest(
                    systemInstruction,
                    history,
                    declarations,
                    selectedModel,
                    profile,
                    contextInstruction,
                    allowedToolNames,
                    // Composing the effective request means serializing the instruction, the
                    // whole replayed history and every tool schema. Ask for it only when the
                    // store will keep it.
                    _analytics.CaptureContent);
                var invocationId = Guid.NewGuid();
                using var invocationActivity = AssistantAnalyticsTelemetry.StartInvocation(
                    turnId,
                    invocationId,
                    loop,
                    profile.ToString(),
                    selectedModel);
                var invocation = new AssistantModelInvocation
                {
                    Id = invocationId,
                    TurnId = turnId,
                    Sequence = loop,
                    Profile = profile.ToString(),
                    Provider = ResolveProviderName(),
                    RequestedModel = selectedModel,
                    ActualModel = selectedModel,
                    RequestJson = _analytics.SerializePayload(agentRequest),
                    Status = AssistantInvocationStatus.Started,
                    StartedAt = _timeProvider.GetUtcNow(),
                    TraceId = invocationActivity?.TraceId.ToHexString(),
                    SpanId = invocationActivity?.SpanId.ToHexString(),
                };
                analyticsInvocations.Add(invocation);

                AgentTurnResult turn;
                var invocationStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    turn = await _generativeAi.RunAgentTurnAsync(
                        agentRequest,
                        new AiUsageContext(
                            userId,
                            AiUsageFeatures.Assistant,
                            "assistant_turn",
                            invocationId,
                            "assistant_thread",
                            thread.Id.ToString(),
                            turnId),
                        cancellationToken);
                    _analytics.CompleteInvocation(
                        invocation,
                        turn,
                        Stopwatch.GetElapsedTime(invocationStartedAt).TotalMilliseconds,
                        invocationActivity);
                    AssistantAnalyticsTelemetry.CompleteInvocation(invocationActivity, turn);
                    lastMetadata = turn.Metadata;
                }
                catch (Exception ex)
                {
                    AssistantAnalyticsTelemetry.Fail(invocationActivity, ex);
                    _analytics.FailInvocation(
                        invocation,
                        ex,
                        Stopwatch.GetElapsedTime(invocationStartedAt).TotalMilliseconds,
                        invocationActivity);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "Generative AI turn failed for assistant thread {ThreadId}", thread.Id);
                    }
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
                completedTurnMessages.Add(new AssistantMessage
                {
                    Id = Guid.NewGuid(),
                    ThreadId = thread.Id,
                    TurnId = turnId,
                    ContextQuizId = contextQuizId,
                    Sequence = nextSequence++,
                    Role = AssistantMessageRole.Model,
                    ContentJson = modelTurn.ContentJson,
                    Status = AssistantMessageStatus.Active,
                    CreatedAt = _timeProvider.GetUtcNow(),
                });

                var responseParts = new List<StoredPart>();
                foreach (var call in turn.FunctionCalls)
                {
                    var safeArgumentsJson = AssistantAnalyticsJson.RedactSecrets(call.ArgsJson);
                    var executionId = Guid.NewGuid();
                    var changesBefore = toolContext.PendingChanges.Count;
                    using var toolActivity = AssistantAnalyticsTelemetry.StartTool(turnId, invocationId, call.Name);
                    var execution = new AssistantToolExecution
                    {
                        Id = executionId,
                        TurnId = turnId,
                        InvocationId = invocationId,
                        Sequence = toolSequence++,
                        ToolName = call.Name,
                        ArgumentsJson = _analytics.StoreJson(safeArgumentsJson),
                        Status = AssistantInvocationStatus.Started,
                        StartedAt = _timeProvider.GetUtcNow(),
                    };
                    analyticsExecutions.Add(execution);
                    var toolStartedAt = Stopwatch.GetTimestamp();
                    try
                    {
                        // History outlives tool surfaces, and an authored agent can declare a
                        // tool this turn never offered. A name outside the allowlist is
                        // answered, not dispatched, so the model can correct itself while the
                        // rejection stays visible in tool analytics.
                        var canonicalName = _tools.ResolveCanonicalName(call.Name);
                        var allowed = allowedToolNames.Contains(call.Name)
                            || canonicalName is not null && allowedToolNames.Contains(canonicalName);
                        if (!allowed)
                        {
                            toolActivity?.SetTag("assistant.tool.rejected", true);
                        }

                        var result = allowed
                            ? await _tools.ExecuteAsync(call.Name, call.ArgsJson, toolContext, cancellationToken)
                            : new { error = $"The tool {call.Name} is not available for this request." };
                        var resultJson = AssistantAnalyticsJson.Serialize(result);
                        var proposedChanges = Math.Max(0, toolContext.PendingChanges.Count - changesBefore);
                        _analytics.CompleteTool(
                            execution,
                            resultJson,
                            proposedChanges,
                            Stopwatch.GetElapsedTime(toolStartedAt).TotalMilliseconds);
                        toolActivity?.SetTag("assistant.outcome", "success");
                        toolActivity?.SetStatus(ActivityStatusCode.Ok);
                        toolEvents.Add(new AssistantToolEvent(call.Name, safeArgumentsJson, SummarizeResult(result)));
                        responseParts.Add(new StoredPart
                        {
                            Kind = "function_response",
                            Name = call.Name,
                            ResponseJson = resultJson,
                            CallId = call.CallId,
                        });
                    }
                    catch (Exception ex)
                    {
                        AssistantAnalyticsTelemetry.Fail(toolActivity, ex);
                        _analytics.FailTool(
                            execution,
                            ex,
                            Stopwatch.GetElapsedTime(toolStartedAt).TotalMilliseconds);
                        throw;
                    }
                }

                var toolTurn = new AgentTurn(AssistantMessageRole.User, SerializeContent(responseParts));
                history.Add(toolTurn);
                completedTurnMessages.Add(new AssistantMessage
                {
                    Id = Guid.NewGuid(),
                    ThreadId = thread.Id,
                    TurnId = turnId,
                    ContextQuizId = contextQuizId,
                    Sequence = nextSequence++,
                    Role = AssistantMessageRole.User,
                    ContentJson = toolTurn.ContentJson,
                    Status = AssistantMessageStatus.Active,
                    CreatedAt = _timeProvider.GetUtcNow(),
                });
            }

            var finalText = finalTurn?.Text ?? "I hit my tool-call limit before finishing. Please try a smaller request.";
            var pendingChangesJson = toolContext.PendingChanges.Count == 0
                ? null
                : JsonSerializer.Serialize(toolContext.PendingChanges, JsonOptions);
            var wordLabels = await _threads.LoadWordLabelsAsync(
                contextQuizId,
                toolContext.PendingChanges,
                cancellationToken);
            var pendingChangeViews = toolContext.PendingChanges
                .Select(change => _presenter.PresentPendingChange(change, wordLabels))
                .ToList();
            var assistantMessageId = Guid.NewGuid();
            var finalMessage = new AssistantMessage
            {
                Id = assistantMessageId,
                ThreadId = thread.Id,
                TurnId = turnId,
                ContextQuizId = contextQuizId,
                Sequence = nextSequence,
                Role = AssistantMessageRole.Model,
                ContentJson = SerializeContent([new StoredPart { Kind = "text", Text = finalText }]),
                PendingChangesJson = pendingChangesJson,
                Status = AssistantMessageStatus.Active,
                CreatedAt = _timeProvider.GetUtcNow(),
            };

            // Intermediate model/tool rows stay detached until the turn has a final
            // assistant message and durable PendingChangesJson. This prevents a separate
            // credit save on the shared context from committing an unreplayable prefix.
            _context.AssistantMessages.AddRange(completedTurnMessages);
            _context.AssistantMessages.Add(finalMessage);
            thread.UpdatedAt = finalMessage.CreatedAt;
            turnEntity.Status = AssistantTurnStatus.Completed;
            turnEntity.ErrorCategory = finalTurn is null ? "tool_limit_reached" : null;
            turnEntity.Provider = lastMetadata?.Provider ?? ResolveProviderName();
            turnEntity.ActualModel = lastMetadata?.Model ?? selectedModel;
            turnEntity.ProviderResponseId = lastMetadata?.ResponseId;
            turnEntity.ToolCallCount = toolSequence;
            turnEntity.ProposedChangeCount = toolContext.PendingChanges.Count;
            turnEntity.ChangeOutcome = toolContext.PendingChanges.Count == 0
                ? null
                : AssistantChangeOutcome.Proposed;
            turnEntity.FinalMessageId = assistantMessageId;
            turnEntity.CompletedAt = _timeProvider.GetUtcNow();
            turnEntity.ServerDurationMs = Stopwatch.GetElapsedTime(turnStartedAt).TotalMilliseconds;
            await _context.SaveChangesAsync(cancellationToken);
            await SaveAnalyticsSafelyAsync(
                turnId,
                analyticsInvocations,
                analyticsExecutions,
                cancellationToken);
            turnActivity?.SetTag("gen_ai.provider.name", turnEntity.Provider);
            turnActivity?.SetTag("gen_ai.response.model", turnEntity.ActualModel);
            turnActivity?.SetTag("gen_ai.response.id", turnEntity.ProviderResponseId);
            turnActivity?.SetTag("assistant.tool_call_count", turnEntity.ToolCallCount);
            turnActivity?.SetTag("assistant.proposed_change_count", turnEntity.ProposedChangeCount);
            turnActivity?.SetTag("assistant.outcome", turnEntity.ErrorCategory ?? "success");
            turnActivity?.SetStatus(ActivityStatusCode.Ok);

            return new AssistantTurnResponse(
                thread.Id,
                turnId,
                assistantMessageId,
                finalText,
                toolEvents,
                pendingChangeViews,
                AssistantMessageStatus.Active);
        }
        catch (Exception ex)
        {
            // If the completion save failed, its buffered message inserts remain tracked.
            // Detach only this turn's pending messages so failure finalization can persist
            // without retrying the write that already failed.
            foreach (var entry in _context.ChangeTracker
                .Entries<AssistantMessage>()
                .Where(entry => entry.State == EntityState.Added
                    && entry.Entity.TurnId == turnId)
                .ToList())
            {
                entry.State = EntityState.Detached;
            }

            turnEntity.Status = ex is OperationCanceledException
                ? AssistantTurnStatus.Cancelled
                : AssistantTurnStatus.Failed;
            turnEntity.ErrorCategory = AssistantAnalyticsErrors.Classify(ex);
            turnEntity.Provider = lastMetadata?.Provider ?? ResolveProviderName();
            turnEntity.ActualModel = lastMetadata?.Model ?? resolvedModel ?? model;
            turnEntity.ProviderResponseId = lastMetadata?.ResponseId;
            turnEntity.ToolCallCount = toolSequence;
            turnEntity.ProposedChangeCount = toolContext?.PendingChanges.Count ?? 0;
            turnEntity.CompletedAt = _timeProvider.GetUtcNow();
            turnEntity.ServerDurationMs = Stopwatch.GetElapsedTime(turnStartedAt).TotalMilliseconds;
            AssistantAnalyticsTelemetry.Fail(turnActivity, ex);
            try
            {
                await _context.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception analyticsException)
            {
                _logger.LogError(
                    analyticsException,
                    "Could not finalize failed assistant turn {TurnId}.",
                    turnId);
            }
            await SaveAnalyticsSafelyAsync(
                turnId,
                analyticsInvocations,
                analyticsExecutions,
                CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Records what the model was actually offered this turn onto the tracked turn row.
    /// </summary>
    /// <remarks>
    /// In-memory only: the turn row is already tracked, so these land on the save the turn
    /// was going to make anyway and cost no extra round trip. Nothing here can be worth
    /// failing a turn the user is waiting on, so a fault is logged and dropped.
    /// </remarks>
    private void RecordTurnInputs(
        AssistantTurn turnEntity,
        AssistantIntent intent,
        IReadOnlySet<string> allowedToolNames,
        string? targetLanguage,
        string? sourceLanguage,
        string? replyLanguage)
    {
        try
        {
            turnEntity.PromptVersion = AssistantPromptBuilder.Version;
            turnEntity.IntentArtifact = intent.ArtifactKind.ToString();
            turnEntity.IntentContent = intent.ContentKind.ToString();
            turnEntity.IntentOperation = intent.OperationKind.ToString();
            turnEntity.AllowedTools = FormatAllowedTools(allowedToolNames);
            turnEntity.TargetLanguage = Truncate(targetLanguage, LanguageMaxLength);
            turnEntity.SourceLanguage = Truncate(sourceLanguage, LanguageMaxLength);
            turnEntity.ReplyLanguage = Truncate(replyLanguage, LanguageMaxLength);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record turn inputs for assistant turn {TurnId}.", turnEntity.Id);
        }
    }

    // Sorted so two turns offering the same surface produce the same string, and truncated
    // on a separator so a longer future surface stays parseable instead of overflowing the
    // column and failing the save that finalizes the turn.
    private const int AllowedToolsMaxLength = 2048;

    // A language name reaching this length is already not a language name. Truncating keeps a
    // malformed preference from failing the save that finalizes the turn.
    private const int LanguageMaxLength = 64;

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= maxLength ? value
        : value[..maxLength];

    private static string? FormatAllowedTools(IReadOnlySet<string> allowedToolNames)
    {
        if (allowedToolNames.Count == 0)
        {
            return null;
        }

        var names = allowedToolNames.ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        var joined = string.Join(',', names);
        if (joined.Length <= AllowedToolsMaxLength)
        {
            return joined;
        }

        var cut = joined.LastIndexOf(',', AllowedToolsMaxLength - 1);
        return cut <= 0 ? joined[..AllowedToolsMaxLength] : joined[..cut];
    }

    private async Task SaveAnalyticsSafelyAsync(
        Guid turnId,
        IReadOnlyCollection<AssistantModelInvocation> invocations,
        IReadOnlyCollection<AssistantToolExecution> executions,
        CancellationToken cancellationToken)
    {
        try
        {
            await _analytics.SubmitBatchAsync(invocations, executions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist analytics for assistant turn {TurnId}.", turnId);
        }
    }

    private string ResolveProviderName() =>
        _generativeAi is Glosify.Services.Ai.Llm.GeminiGenerativeAiClient
            ? AiUsageProviders.Gemini
            : AiUsageProviders.Foundry;

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
