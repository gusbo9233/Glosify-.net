using Microsoft.Extensions.Options;

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

    public const int MinimumSigningKeyLength = 32;

    public bool IsEnabled => SigningKey.Length >= MinimumSigningKeyLength;
}

/// <summary>
/// Catches a signing key that is present but too short. Without this the endpoint simply
/// serves 404s, which is indistinguishable from the feature being switched off — so a
/// truncated key looks like a deliberate configuration choice rather than a mistake.
/// </summary>
public sealed class AssistantMcpOptionsValidator : IValidateOptions<AssistantMcpOptions>
{
    public ValidateOptionsResult Validate(string? name, AssistantMcpOptions options)
    {
        var key = options.SigningKey;
        if (key.Length == 0 || key.Length >= AssistantMcpOptions.MinimumSigningKeyLength)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"Assistant:Mcp:SigningKey is {key.Length} characters; it must be empty to disable "
            + $"the MCP endpoint, or at least {AssistantMcpOptions.MinimumSigningKeyLength} "
            + "characters to enable it.");
    }
}
