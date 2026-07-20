namespace Glosify.Models.Api;

public sealed record CreateSpeakingSessionRequest(
    string? AvatarId,
    string? CefrLevel);

public sealed record SendSpeakingTurnRequest(string? Text, string? InputMode);

public sealed record SendSpeakingActionRequest(
    string? Action,
    Dictionary<int, int>? Denominations);
