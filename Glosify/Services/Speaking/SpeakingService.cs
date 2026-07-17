using System.Diagnostics;
using Glosify.Services.Ai;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Speaking;

public sealed class SpeakingService : ISpeakingService
{
    public const int MaxInputLength = 800;

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
        var state = await _sessions.CreateAsync(userId, avatar, cefrLevel, cancellationToken);
        SpeakingTelemetry.SessionsCreated.Add(
            1,
            new KeyValuePair<string, object?>("speaking.avatar", avatar.Slug));
        return new SpeakingSessionCreated(
            state.Id,
            avatar.Slug,
            avatar.Name,
            avatar.Voice,
            new SpeakingOpeningTurn(avatar.OpeningPolish, avatar.OpeningEnglish));
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
        if (!await session.TurnGate.WaitAsync(0, cancellationToken))
        {
            throw new SpeakingSessionBusyException();
        }

        var stopwatch = Stopwatch.StartNew();
        using var activity = SpeakingTelemetry.ActivitySource.StartActivity("speaking.turn");
        activity?.SetTag("speaking.avatar", session.Avatar.Slug);
        activity?.SetTag("speaking.cefr", session.CefrLevel.ToString());
        activity?.SetTag("speaking.input_mode", inputMode.ToString().ToLowerInvariant());

        try
        {
            var prompt = BuildAgentMessage(session, trimmed);
            var outputReserve = _usageOptions.GetOutputReserve(AiUsageFeatures.Speaking);
            var estimatedTokens = EstimateTokens(prompt) + outputReserve;
            var operationId = Guid.NewGuid();
            var reservation = await _credits.ReserveAsync(
                new AiUsageContext(
                    userId,
                    AiUsageFeatures.Speaking,
                    "speaking_turn",
                    operationId,
                    "speaking_session",
                    sessionId.ToString()),
                "azure_ai_foundry",
                _speakingOptions.ModelDeployment,
                estimatedTokens,
                cancellationToken);

            SpeakingAgentTurn agentTurn;
            try
            {
                agentTurn = await session.Conversation.RunTurnAsync(prompt, cancellationToken);
                NormalizeAndValidate(agentTurn.Turn);

                var usage = agentTurn.Usage is { TotalTokens: > 0 }
                    ? agentTurn.Usage
                    : new AiTokenUsage(
                        Math.Max(1, estimatedTokens - outputReserve),
                        outputReserve,
                        0,
                        0,
                        estimatedTokens);
                await _credits.CommitUsageAsync(reservation.ReservationId, usage, cancellationToken);
            }
            catch
            {
                await ReleaseReservationSafelyAsync(reservation.ReservationId);
                throw;
            }

            SpeakingTelemetry.TurnsCompleted.Add(
                1,
                new KeyValuePair<string, object?>("speaking.avatar", session.Avatar.Slug));
            return agentTurn.Turn;
        }
        catch
        {
            SpeakingTelemetry.TurnsFailed.Add(
                1,
                new KeyValuePair<string, object?>("speaking.avatar", session.Avatar.Slug));
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

    public Task DeleteSessionAsync(
        Guid sessionId,
        string userId,
        CancellationToken cancellationToken = default) =>
        _sessions.DeleteAsync(sessionId, userId, cancellationToken);

    private static string BuildAgentMessage(SpeakingSessionState session, string learnerText) =>
        $"""
        Trusted Glosify session context:
        - Learner CEFR level: {session.CefrLevel}
        - Persona: {session.Avatar.Name}
        - Scenario: {session.Avatar.Scenario}
        - The learner has already seen this opening line: {session.Avatar.OpeningPolish}

        Continue the role-play in Polish at the selected CEFR level. Keep the in-character
        reply concise, then provide the required English translation and coaching fields.

        Learner message:
        {learnerText}
        """;

    private static int EstimateTokens(string text) =>
        Math.Max(1, (int)Math.Ceiling(text.Length / 4d));

    private static void NormalizeAndValidate(SpeakingTurn turn)
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
}
