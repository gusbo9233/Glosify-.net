namespace Glosify.Models.Api;

public sealed record CreateSpeakingSessionRequest(
    string? AvatarId,
    string? CefrLevel,
    Guid? QuizId = null);

public sealed record SendSpeakingTurnRequest(
    string? Text,
    string? InputMode,
    Guid? PracticePromptId = null);

public sealed record SelectSpeakingQuizRequest(Guid? QuizId);

public sealed record SendSpeakingActionRequest(
    string? Action,
    Dictionary<int, int>? Denominations,
    string? DrinkId);
