using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Api;

public sealed record CreateSpeakingSessionRequest(
    [param: StringLength(80)] string? AvatarId,
    [param: StringLength(8)] string? CefrLevel,
    Guid? QuizId = null);

public sealed record SendSpeakingTurnRequest(
    [param: StringLength(4000)] string? Text,
    [param: StringLength(32)] string? InputMode,
    Guid? PracticePromptId = null);

public sealed record SelectSpeakingQuizRequest(Guid? QuizId);

public sealed record SendSpeakingActionRequest(
    [param: StringLength(64)] string? Action,
    Dictionary<int, int>? Denominations,
    [param: StringLength(80)] string? DrinkId);
