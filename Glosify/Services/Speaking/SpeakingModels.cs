using System.Text.Json.Serialization;
using Glosify.Services.Ai;

namespace Glosify.Services.Speaking;

public enum SpeakingAvatarId
{
    Bartender,
    Kasia,
    Mietek,
}

public enum CefrLevel
{
    A1,
    A2,
    B1,
    B2,
    C1,
}

public enum SpeakingInputMode
{
    Text,
    Voice,
}

public sealed record SpeakingAvatarDefinition(
    SpeakingAvatarId Id,
    string Slug,
    string Name,
    string Scenario,
    string Voice,
    string OpeningPolish,
    string OpeningEnglish,
    string SsmlRate,
    string SsmlPitch);

public sealed record SpeakingOpeningTurn(string ReplyPolish, string ReplyEnglish);

public sealed record SpeakingSessionCreated(
    Guid SessionId,
    string AvatarId,
    string AvatarName,
    string Voice,
    SpeakingOpeningTurn OpeningTurn);

public sealed class SpeakingCoach
{
    [JsonPropertyName("correctedPolish")]
    public string CorrectedPolish { get; set; } = string.Empty;

    [JsonPropertyName("grammarTipEnglish")]
    public string GrammarTipEnglish { get; set; } = string.Empty;

    [JsonPropertyName("vocabularyTipEnglish")]
    public string VocabularyTipEnglish { get; set; } = string.Empty;

    [JsonPropertyName("naturalnessTipEnglish")]
    public string NaturalnessTipEnglish { get; set; } = string.Empty;
}

public sealed class SpeakingTurn
{
    [JsonPropertyName("replyPolish")]
    public string ReplyPolish { get; set; } = string.Empty;

    [JsonPropertyName("replyEnglish")]
    public string ReplyEnglish { get; set; } = string.Empty;

    [JsonPropertyName("coach")]
    public SpeakingCoach Coach { get; set; } = new();
}

public sealed record SpeakingAgentTurn(SpeakingTurn Turn, AiTokenUsage? Usage);

public sealed record SpeakingPageAvatar(
    string Id,
    string Name,
    string Scenario,
    string Voice,
    string OpeningPolish,
    string OpeningEnglish,
    string SsmlRate,
    string SsmlPitch);

public sealed record SpeakingPageViewModel(
    IReadOnlyList<SpeakingPageAvatar> Avatars,
    string DefaultAvatarId,
    string DefaultCefrLevel);
