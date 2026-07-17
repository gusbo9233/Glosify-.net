namespace Glosify.Services.Speaking;

public sealed class SpeakingOptions
{
    public const string SectionName = "Speaking";

    public string ProjectEndpoint { get; set; } = string.Empty;
    public string ModelDeployment { get; set; } = "gpt-5.4-mini";
    public int SessionTtlMinutes { get; set; } = 60;
    public int MaxSessionsPerUser { get; set; } = 3;
    public SpeakingAgentOptions Agents { get; set; } = new();

    public SpeakingAgentVersion GetAgent(SpeakingAvatarId avatar) => avatar switch
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
    public SpeakingAgentVersion Bartender { get; set; } = new("glosify-bartender", "1");
    public SpeakingAgentVersion Kasia { get; set; } = new("glosify-kasia", "1");
    public SpeakingAgentVersion Mietek { get; set; } = new("glosify-mietek", "1");
    public SpeakingAgentVersion Maarja { get; set; } = new("glosify-maarja", "1");
    public SpeakingAgentVersion Karl { get; set; } = new("glosify-karl", "1");
    public SpeakingAgentVersion Liis { get; set; } = new("glosify-liis", "1");
    public SpeakingAgentVersion Hanna { get; set; } = new("glosify-hanna", "1");
    public SpeakingAgentVersion Jonas { get; set; } = new("glosify-jonas", "1");
    public SpeakingAgentVersion FrauSchneider { get; set; } = new("glosify-frau-schneider", "1");
    public SpeakingAgentVersion Oksana { get; set; } = new("glosify-oksana", "1");
    public SpeakingAgentVersion Andriy { get; set; } = new("glosify-andriy", "1");
    public SpeakingAgentVersion PanMykola { get; set; } = new("glosify-pan-mykola", "1");
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
