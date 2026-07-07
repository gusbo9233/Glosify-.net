namespace Glosify.Services.Communication;

public sealed class AcsOptions
{
    public const string SectionName = "Acs";

    /// <summary>
    /// Azure Communication Services connection string. Video calling is
    /// disabled when this is empty.
    /// </summary>
    public string? ConnectionString { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
