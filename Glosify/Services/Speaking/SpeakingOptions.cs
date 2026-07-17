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
        _ => throw new ArgumentOutOfRangeException(nameof(avatar)),
    };
}

public sealed class SpeakingAgentOptions
{
    public SpeakingAgentVersion Bartender { get; set; } = new("glosify-bartender", "1");
    public SpeakingAgentVersion Kasia { get; set; } = new("glosify-kasia", "1");
    public SpeakingAgentVersion Mietek { get; set; } = new("glosify-mietek", "1");
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
