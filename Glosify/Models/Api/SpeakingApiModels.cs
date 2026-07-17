namespace Glosify.Models.Api;

public sealed record CreateSpeakingSessionRequest(string? AvatarId, string? CefrLevel);

public sealed record SendSpeakingTurnRequest(string? Text, string? InputMode);
