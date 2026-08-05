namespace Glosify.Services.Auth;

public sealed class ExtensionAuthOptions
{
    public const string SectionName = "ExtensionAuth";

    public int AuthorizationCodeLifetimeSeconds { get; set; } = 120;
    public List<string> AllowedRedirectUris { get; set; } = [];

    public bool IsAllowedRedirectUri(string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.EndsWith(".chromiumapp.org", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return AllowedRedirectUris.Any(candidate =>
            string.Equals(candidate?.Trim(), redirectUri, StringComparison.Ordinal));
    }
}
