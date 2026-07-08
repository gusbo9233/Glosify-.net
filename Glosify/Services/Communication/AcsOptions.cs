namespace Glosify.Services.Communication;

public sealed class AcsOptions
{
    public const string SectionName = "Acs";

    /// <summary>
    /// Azure Communication Services endpoint URI. When set, the identity
    /// client authenticates with Entra ID (DefaultAzureCredential) and no
    /// access key is needed; the signed-in identity requires the Contributor
    /// role on the ACS resource. Takes precedence over the connection string.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Azure Communication Services connection string (access-key auth).
    /// Fallback for when <see cref="Endpoint"/> is not set. Video calling is
    /// disabled when both are empty.
    /// </summary>
    public string? ConnectionString { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) || !string.IsNullOrWhiteSpace(ConnectionString);
}
