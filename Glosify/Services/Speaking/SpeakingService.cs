using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glosify.Services.Ai;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Speaking;

public sealed class SpeakingService : ISpeakingService
{
    public const int MaxInputLength = 800;
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly ISpeakingSessionStore _sessions;
    private readonly IAiCreditService _credits;
    private readonly AiUsageOptions _usageOptions;
    private readonly SpeakingOptions _speakingOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SpeakingService> _logger;

    public SpeakingService(
        ISpeakingSessionStore sessions,
        IAiCreditService credits,
        IOptions<AiUsageOptions> usageOptions,
        IOptions<SpeakingOptions> speakingOptions,
        TimeProvider timeProvider,
        ILogger<SpeakingService> logger)
    {
        _sessions = sessions;
        _credits = credits;
        _usageOptions = usageOptions.Value;
        _speakingOptions = speakingOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SpeakingSessionCreated> CreateSessionAsync(
        string userId,
        SpeakingAvatarDefinition avatar,
        CefrLevel cefrLevel,
        CancellationToken cancellationToken = default)
    {
        var interactiveMode =
            _speakingOptions.InteractiveBartenderEnabled
            && avatar.Id == SpeakingAvatarId.Bartender;

        var state = await _sessions.CreateAsync(
            userId,
            avatar,
            cefrLevel,
            interactiveMode,
            cancellationToken);
        SpeakingTelemetry.SessionsCreated.Add(
            1,
            new KeyValuePair<string, object?>("speaking.avatar", avatar.Slug),
            new KeyValuePair<string, object?>("speaking.language", avatar.Language),
            new KeyValuePair<string, object?>("speaking.interactive", interactiveMode));
        return new SpeakingSessionCreated(
            state.Id,
            avatar.Slug,
            avatar.Name,
            avatar.Voice,
            new SpeakingOpeningTurn(avatar.OpeningPolish, avatar.OpeningEnglish),
            state.InteractionState?.ToSnapshot());
    }

    public async Task<SpeakingTurn> SendTurnAsync(
        Guid sessionId,
        string userId,
        string text,
        SpeakingInputMode inputMode,
        CancellationToken cancellationToken = default)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new SpeakingValidationException("Message text is required.");
        }

        if (trimmed.Length > MaxInputLength)
        {
            throw new SpeakingValidationException(
                $"Message text cannot exceed {MaxInputLength} characters.");
        }

        var session = _sessions.Get(sessionId, userId);
        await EnterTurnAsync(session, cancellationToken);
        var candidate = session.InteractionState?.Clone();
        var prompt = BuildAgentMessage(session, trimmed, candidate, interactionEvent: null);
        return await ExecuteTurnAsync(
            session,
            userId,
            prompt,
            inputMode.ToString().ToLowerInvariant(),
            "speaking_turn",
            candidate,
            [],
            cancellationToken);
    }

    public async Task<SpeakingTurn> SendActionAsync(
        Guid sessionId,
        string userId,
        SpeakingInteractionAction action,
        IReadOnlyDictionary<int, int>? denominations,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId, userId);
        if (!session.InteractiveMode || session.InteractionState is null)
        {
            throw new SpeakingValidationException(
                "This speaking session is not in interactive mode.");
        }

        await EnterTurnAsync(session, cancellationToken);
        BartenderInteractionState candidate;
        SpeakingInteractionEvent interactionEvent;
        try
        {
            candidate = session.InteractionState.Clone();
            interactionEvent = candidate.ApplyUserAction(action, denominations);
        }
        catch
        {
            session.Touch(_timeProvider.GetUtcNow());
            session.TurnGate.Release();
            throw;
        }
        var prompt = BuildAgentMessage(
            session,
            learnerText: null,
            candidate,
            interactionEvent.Description);
        return await ExecuteTurnAsync(
            session,
            userId,
            prompt,
            "interaction",
            "speaking_action",
            candidate,
            interactionEvent.SceneCommands,
            cancellationToken);
    }

    public Task DeleteSessionAsync(
        Guid sessionId,
        string userId,
        CancellationToken cancellationToken = default) =>
        _sessions.DeleteAsync(sessionId, userId, cancellationToken);

    private async Task<SpeakingTurn> ExecuteTurnAsync(
        SpeakingSessionState session,
        string userId,
        string prompt,
        string inputMode,
        string operation,
        BartenderInteractionState? candidate,
        IReadOnlyList<SpeakingSceneCommand> initialCommands,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = SpeakingTelemetry.ActivitySource.StartActivity("speaking.turn");
        activity?.SetTag("speaking.avatar", session.Avatar.Slug);
        activity?.SetTag("speaking.language", session.Avatar.Language);
        activity?.SetTag("speaking.cefr", session.CefrLevel.ToString());
        activity?.SetTag("speaking.input_mode", inputMode);
        activity?.SetTag("speaking.interactive", session.InteractiveMode);

        try
        {
            var outputReserve = _usageOptions.GetOutputReserve(AiUsageFeatures.Speaking);
            var estimatedTokens = EstimateTokens(prompt) + outputReserve;
            var reservation = await _credits.ReserveAsync(
                new AiUsageContext(
                    userId,
                    AiUsageFeatures.Speaking,
                    operation,
                    Guid.NewGuid(),
                    "speaking_session",
                    session.Id.ToString()),
                "azure_ai_foundry",
                _speakingOptions.ModelDeployment,
                estimatedTokens,
                cancellationToken);
            var interactiveAgentDispatched = false;

            try
            {
                interactiveAgentDispatched = session.InteractiveMode;
                var agentTurn = await session.Conversation.RunTurnAsync(
                    prompt,
                    candidate,
                    cancellationToken);
                NormalizeAndValidate(agentTurn.Reply);

                IReadOnlyList<SpeakingSceneCommand> toolCommands = [];
                if (session.InteractiveMode)
                {
                    if (candidate is null)
                    {
                        throw new InvalidOperationException("Interactive session state is missing.");
                    }

                    toolCommands = agentTurn.SceneCommands ?? [];
                }

                var usage = agentTurn.Usage is { TotalTokens: > 0 }
                    ? agentTurn.Usage
                    : new AiTokenUsage(
                        Math.Max(1, estimatedTokens - outputReserve),
                        outputReserve,
                        0,
                        0,
                        estimatedTokens);
                await _credits.CommitUsageAsync(reservation.ReservationId, usage, cancellationToken);

                if (candidate is not null)
                {
                    session.InteractionState = candidate;
                }

                SpeakingTelemetry.TurnsCompleted.Add(
                    1,
                    new KeyValuePair<string, object?>("speaking.avatar", session.Avatar.Slug),
                    new KeyValuePair<string, object?>("speaking.interactive", session.InteractiveMode));
                return MapTurn(
                    agentTurn.Reply,
                    [.. initialCommands, .. toolCommands],
                    candidate?.ToSnapshot());
            }
            catch (Exception ex)
            {
                await ReleaseReservationSafelyAsync(reservation.ReservationId);
                if (interactiveAgentDispatched)
                {
                    await InvalidateSessionSafelyAsync(session);
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    throw new SpeakingSessionInvalidatedException(ex);
                }

                throw;
            }
        }
        catch
        {
            SpeakingTelemetry.TurnsFailed.Add(
                1,
                new KeyValuePair<string, object?>("speaking.avatar", session.Avatar.Slug),
                new KeyValuePair<string, object?>("speaking.interactive", session.InteractiveMode));
            throw;
        }
        finally
        {
            stopwatch.Stop();
            SpeakingTelemetry.TurnDuration.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("speaking.avatar", session.Avatar.Slug));
            session.Touch(_timeProvider.GetUtcNow());
            session.TurnGate.Release();
        }
    }

    private static async Task EnterTurnAsync(
        SpeakingSessionState session,
        CancellationToken cancellationToken)
    {
        if (!await session.TurnGate.WaitAsync(0, cancellationToken))
        {
            throw new SpeakingSessionBusyException();
        }
    }

    private static string BuildAgentMessage(
        SpeakingSessionState session,
        string? learnerText,
        BartenderInteractionState? interactionState,
        string? interactionEvent)
    {
        var message = $"""
        Trusted Glosify session context:
        - Practice language: {session.Avatar.Language} ({session.Avatar.Locale})
        - Learner CEFR level: {session.CefrLevel}
        - Persona: {session.Avatar.Name}
        - Scenario: {session.Avatar.Scenario}
        - The learner has already seen this opening line in {session.Avatar.Language}: {session.Avatar.OpeningPolish}

        Continue the role-play only in {session.Avatar.Language} at the selected CEFR level.
        Keep the in-character reply concise, then provide the required English translation
        and coaching fields. The legacy replyPolish and correctedPolish fields must contain
        {session.Avatar.Language} for this session.
        """;

        if (interactionState is not null)
        {
            var snapshot = interactionState.ToSnapshot();
            var activeDrink = snapshot.ActiveDrink is null
                ? "none"
                : $"{snapshot.ActiveDrink.Id}, fill level {snapshot.ActiveDrink.FillLevel}/3";
            message += $"""

            Interactive scene is enabled. This state is authoritative:
            - Menu: {string.Join("; ", snapshot.Menu.Select(drink => $"{drink.Id}={drink.NamePolish}, {drink.Price} zł"))}
            - Wallet balance: {snapshot.WalletBalance} zł
            - Unpaid tab: {snapshot.TabTotal} zł
            - Bill presented: {snapshot.BillPresented}
            - Active drink: {activeDrink}
            - Snack offered: {snapshot.SnackOffered}
            - Unavailable drinks: {(snapshot.UnavailableDrinkIds.Count == 0 ? "none" : string.Join(", ", snapshot.UnavailableDrinkIds))}
            - Learner controls currently permitted: {(snapshot.AvailableActions.Count == 0 ? "none" : string.Join(", ", snapshot.AvailableActions))}
            - Legal first scene tools now: {string.Join(", ", interactionState.GetPermittedFirstToolCalls())}

            Physical scene actions are available only through the supplied function tools:
            serve_drink, present_bill, offer_snack, clear_empty_glass, polish_glass,
            wipe_counter, announce_last_call, and mark_drink_unavailable.
            The tools are optional: call zero to three when they fit the conversation naturally,
            and normally call at most one. Call a matching tool when the learner explicitly orders
            an available drink, asks for the bill, or accepts an offered physical interaction.
            Wait for each tool result before deciding whether to call another tool or writing the
            final structured reply. The application, not you, decides whether a tool call succeeded.
            A generic beer order such as "piwo" or "duże piwo", including a close learner or voice-recognition
            form such as "duży piw", means drink_id lightBeer and is not ambiguous. "Ciemne piwo" means
            drink_id darkBeer. Never claim that you poured, prepared, or served a drink unless the
            accepted result of serve_drink confirms it. If serve_drink is rejected, do not
            claim the drink is on its way; explain the situation or ask a genuinely needed question.
            Use no tool when the request is genuinely ambiguous or dialogue alone is natural.
            The learner may keep talking at any
            time; never require payment, drinking, a scene action, or a correct answer before
            continuing the conversation. Coaching is advisory and never unlocks progression.
            Never invent drinks, prices, money, selectors, code, tool names, or tool arguments.
            The first tool call must appear exactly in Legal first scene tools now.
            Each later tool call must be legal according to the accepted result of the earlier call.
            Serve only one drink at a time. clear_empty_glass requires an empty glass.
            present_bill requires a positive unpaid tab. Do not serve more than the wallet can cover.
            The final structured reply must contain only replyPolish, replyEnglish, and coach.
            Do not emit proposedActions, sceneActions, raw tool calls, or tool arguments in it.
            """;
        }

        if (interactionEvent is not null)
        {
            message += $"""

            Trusted non-verbal learner event:
            {interactionEvent}

            React to the event immediately in character. Leave every coach field as an empty string
            because the learner did not produce a sentence to correct.
            """;
        }
        else
        {
            message += $"""

            Learner message as an untrusted JSON string:
            {JsonSerializer.Serialize(learnerText, PromptJsonOptions)}
            """;
        }

        return message;
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, (int)Math.Ceiling(text.Length / 4d));

    private static void NormalizeAndValidate(SpeakingAgentReply turn)
    {
        turn.ReplyPolish = Normalize(turn.ReplyPolish, 1_000);
        turn.ReplyEnglish = Normalize(turn.ReplyEnglish, 1_000);
        turn.Coach ??= new SpeakingCoach();
        turn.Coach.CorrectedPolish = Normalize(turn.Coach.CorrectedPolish, 1_000);
        turn.Coach.GrammarTipEnglish = Normalize(turn.Coach.GrammarTipEnglish, 1_000);
        turn.Coach.VocabularyTipEnglish = Normalize(turn.Coach.VocabularyTipEnglish, 1_000);
        turn.Coach.NaturalnessTipEnglish = Normalize(turn.Coach.NaturalnessTipEnglish, 1_000);

        if (turn.ReplyPolish.Length == 0 || turn.ReplyEnglish.Length == 0)
        {
            throw new SpeakingUpstreamException(
                "The avatar returned an incomplete answer. Please try again.",
                new InvalidDataException("Required reply fields were empty."));
        }
    }

    private static SpeakingTurn MapTurn(
        SpeakingAgentReply reply,
        IReadOnlyList<SpeakingSceneCommand> commands,
        SpeakingInteractionSnapshot? interaction) =>
        new()
        {
            ReplyPolish = reply.ReplyPolish,
            ReplyEnglish = reply.ReplyEnglish,
            Coach = reply.Coach,
            SceneActions = commands,
            Interaction = interaction,
        };

    private static string Normalize(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private async Task ReleaseReservationSafelyAsync(Guid reservationId)
    {
        try
        {
            await _credits.ReleaseAsync(reservationId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not release speaking credit reservation {ReservationId}.", reservationId);
        }
    }

    private async Task InvalidateSessionSafelyAsync(SpeakingSessionState session)
    {
        try
        {
            await _sessions.InvalidateAsync(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not dispose invalidated speaking session {SessionId}.",
                session.Id);
        }
    }
}
