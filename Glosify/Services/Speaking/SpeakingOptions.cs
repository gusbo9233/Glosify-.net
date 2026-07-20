namespace Glosify.Services.Speaking;

public sealed class SpeakingOptions
{
    public const string SectionName = "Speaking";

    public string ProjectEndpoint { get; set; } = string.Empty;
    public string ModelDeployment { get; set; } = "grok-4-1-fast-non-reasoning";
    public int SessionTtlMinutes { get; set; } = 60;
    public int MaxSessionsPerUser { get; set; } = 3;
    public bool InteractiveBartenderEnabled { get; set; }
    public SpeakingAgentOptions Agents { get; set; } = new();

    public SpeakingAgentVersion GetAgent(
        SpeakingAvatarId avatar,
        bool interactiveMode = false) =>
        interactiveMode && avatar == SpeakingAvatarId.Bartender
            ? Agents.BartenderInteractive
            : avatar switch
    {
        SpeakingAvatarId.Bartender => Agents.Bartender,
        SpeakingAvatarId.Kasia => Agents.Kasia,
        SpeakingAvatarId.Mietek => Agents.Mietek,
        SpeakingAvatarId.Maarja => Agents.Maarja,
        SpeakingAvatarId.Karl => Agents.Karl,
        SpeakingAvatarId.Liis => Agents.Liis,
        SpeakingAvatarId.Hanna => Agents.Hanna,
        SpeakingAvatarId.Jonas => Agents.Jonas,
        SpeakingAvatarId.FrauSchneider => Agents.FrauSchneider,
        SpeakingAvatarId.Oksana => Agents.Oksana,
        SpeakingAvatarId.Andriy => Agents.Andriy,
        SpeakingAvatarId.PanMykola => Agents.PanMykola,
        _ => throw new ArgumentOutOfRangeException(nameof(avatar)),
    };
}

public sealed class SpeakingAgentOptions
{
    public SpeakingAgentVersion Bartender { get; set; } = new("glosify-bartender", "2");
    public SpeakingAgentVersion BartenderInteractive { get; set; } =
        new("glosify-bartender-interactive", "2");
    public SpeakingAgentVersion Kasia { get; set; } = new("glosify-kasia", "2");
    public SpeakingAgentVersion Mietek { get; set; } = new("glosify-mietek", "2");
    public SpeakingAgentVersion Maarja { get; set; } = new("glosify-maarja", "2");
    public SpeakingAgentVersion Karl { get; set; } = new("glosify-karl", "2");
    public SpeakingAgentVersion Liis { get; set; } = new("glosify-liis", "2");
    public SpeakingAgentVersion Hanna { get; set; } = new("glosify-hanna", "2");
    public SpeakingAgentVersion Jonas { get; set; } = new("glosify-jonas", "2");
    public SpeakingAgentVersion FrauSchneider { get; set; } = new("glosify-frau-schneider", "2");
    public SpeakingAgentVersion Oksana { get; set; } = new("glosify-oksana", "2");
    public SpeakingAgentVersion Andriy { get; set; } = new("glosify-andriy", "2");
    public SpeakingAgentVersion PanMykola { get; set; } = new("glosify-pan-mykola", "2");
}

public sealed class SpeakingAgentVersion
{
    public SpeakingAgentVersion()
    {
    }

    public SpeakingAgentVersion(string name, string version)
    {
        Name = name;
        Version = version;
    }

    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}
