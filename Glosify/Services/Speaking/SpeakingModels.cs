using System.Text.Json.Serialization;
using Glosify.Services.Ai;

namespace Glosify.Services.Speaking;

public enum SpeakingAvatarId
{
    Bartender,
    Kasia,
    Mietek,
    Maarja,
    Karl,
    Liis,
    Hanna,
    Jonas,
    FrauSchneider,
    Oksana,
    Andriy,
    PanMykola,
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

public enum SpeakingInteractionAction
{
    Drink,
    TakeSnack,
    SubmitPayment,
}

public enum SpeakingProposedActionType
{
    ServeDrink,
    PresentBill,
    OfferSnack,
    ClearGlass,
    PolishGlass,
    WipeCounter,
    LastCall,
    MarkUnavailable,
}

public sealed record SpeakingAvatarDefinition(
    SpeakingAvatarId Id,
    string Slug,
    string Language,
    string Locale,
    string Name,
    string Scenario,
    string Voice,
    string OpeningPolish,
    string OpeningEnglish,
    string SsmlRate,
    string SsmlPitch,
    string SceneTemplate,
    string PortraitStyle,
    string SceneSign,
    string AccentColor)
{
    public string LanguageCode => Locale.Split('-', 2)[0];
}

public sealed record SpeakingOpeningTurn(string ReplyPolish, string ReplyEnglish);

public sealed record SpeakingSessionCreated(
    Guid SessionId,
    string AvatarId,
    string AvatarName,
    string Voice,
    SpeakingOpeningTurn OpeningTurn,
    SpeakingInteractionSnapshot? Interaction = null);

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

public class SpeakingAgentReply
{
    // These JSON names are retained for compatibility with the published
    // Polish prompt agents. For other avatars they contain the selected
    // practice language rather than Polish specifically.
    [JsonPropertyName("replyPolish")]
    public string ReplyPolish { get; set; } = string.Empty;

    [JsonPropertyName("replyEnglish")]
    public string ReplyEnglish { get; set; } = string.Empty;

    [JsonPropertyName("coach")]
    public SpeakingCoach Coach { get; set; } = new();
}

public sealed class SpeakingProposedAction
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<SpeakingProposedActionType>))]
    public SpeakingProposedActionType Type { get; set; }

    [JsonPropertyName("drinkId")]
    public string? DrinkId { get; set; }
}

public sealed class SpeakingTurn : SpeakingAgentReply
{
    [JsonPropertyName("suppressAvatarReaction")]
    public bool SuppressAvatarReaction { get; set; }

    [JsonPropertyName("sceneActions")]
    public IReadOnlyList<SpeakingSceneCommand> SceneActions { get; set; } = [];

    [JsonPropertyName("interaction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpeakingInteractionSnapshot? Interaction { get; set; }
}

public sealed record SpeakingAgentTurn(
    SpeakingAgentReply Reply,
    AiTokenUsage? Usage,
    IReadOnlyList<SpeakingSceneCommand>? SceneCommands = null);

public sealed record SpeakingDrinkSnapshot(
    string Id,
    string NamePolish,
    string NameEnglish,
    int Price,
    string Category);

public sealed record SpeakingActiveDrinkSnapshot(
    string Id,
    string NamePolish,
    string NameEnglish,
    int FillLevel,
    string Category);

public sealed record SpeakingWalletDenominationSnapshot(int Value, int Count);

public sealed record SpeakingInteractionSnapshot(
    IReadOnlyList<SpeakingDrinkSnapshot> Menu,
    IReadOnlyList<SpeakingWalletDenominationSnapshot> Wallet,
    int WalletBalance,
    int TabTotal,
    bool BillPresented,
    IReadOnlyList<SpeakingActiveDrinkSnapshot> ActiveDrinks,
    bool SnackOffered,
    IReadOnlyList<string> UnavailableDrinkIds,
    IReadOnlyList<string> AvailableActions);

public sealed record SpeakingSceneCommand(
    string Type,
    string? DrinkId = null,
    int? Amount = null,
    int? FillLevel = null);

public sealed record SpeakingPageAvatar(
    string Id,
    string Language,
    string Locale,
    string LanguageCode,
    string Name,
    string Scenario,
    string Voice,
    string OpeningPolish,
    string OpeningEnglish,
    string SsmlRate,
    string SsmlPitch,
    string SceneTemplate,
    string PortraitStyle,
    string SceneSign,
    string AccentColor);

public sealed record SpeakingPageViewModel(
    IReadOnlyList<SpeakingPageAvatar> Avatars,
    string Language,
    string Locale,
    string LanguageCode,
    string DefaultAvatarId,
    string DefaultCefrLevel,
    bool InteractiveBartenderEnabled);
