namespace Glosify.Services.Ai.Assistant.Mcp;

/// <summary>
/// Settings for the MCP endpoint that Microsoft Foundry calls back into when an agent
/// runs an assistant tool. Disabled until a signing key is configured.
/// </summary>
public sealed class AssistantMcpOptions
{
    public const string SectionName = "Assistant:Mcp";

    /// <summary>
    /// Secret used to sign session tokens. Supply through Key Vault or app settings,
    /// never source control. Must be at least 32 characters.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional shared credential Foundry sends as a header, configured on the project
    /// connection. Defence in depth: the signed session token is the real authenticator.
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;

    public string SharedSecretHeader { get; set; } = "X-Glosify-Mcp-Key";

    /// <summary>
    /// How long a minted session stays valid. Kept short because the token travels in
    /// the URL Foundry calls, and URLs reach logs.
    /// </summary>
    public int SessionLifetimeMinutes { get; set; } = 30;

    public bool IsEnabled => SigningKey.Length >= 32;
}
