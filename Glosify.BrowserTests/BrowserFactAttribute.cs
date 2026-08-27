using Xunit;

namespace Glosify.BrowserTests;

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (!BrowserTestConfiguration.IsRequired
            && string.IsNullOrWhiteSpace(BrowserTestConfiguration.ConfiguredBaseUrl))
        {
            Skip = "Set GLOSIFY_BROWSER_BASE_URL and GLOSIFY_BROWSER_RUN_TOKEN to run browser journeys.";
        }
    }
}

internal sealed record BrowserTestSettings(
    Uri BaseUri,
    string RunToken,
    string? TraceDirectory);

internal static class BrowserTestConfiguration
{
    internal const string RunTokenHeader = "X-Glosify-Browser-Test-Token";

    internal static string? ConfiguredBaseUrl =>
        Environment.GetEnvironmentVariable("GLOSIFY_BROWSER_BASE_URL");

    internal static bool IsRequired => string.Equals(
        Environment.GetEnvironmentVariable("REQUIRE_BROWSER_TESTS"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    internal static BrowserTestSettings Load()
    {
        var configuredBaseUrl = ConfiguredBaseUrl;
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            throw new InvalidOperationException(
                IsRequired
                    ? "GLOSIFY_BROWSER_BASE_URL is required because REQUIRE_BROWSER_TESTS=true."
                    : "GLOSIFY_BROWSER_BASE_URL is required to run browser journeys.");
        }

        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri)
            || (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !baseUri.IsLoopback
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || baseUri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new InvalidOperationException(
                "GLOSIFY_BROWSER_BASE_URL must be an origin-only absolute HTTP(S) loopback URL.");
        }

        var runToken = Environment.GetEnvironmentVariable("GLOSIFY_BROWSER_RUN_TOKEN");
        if (string.IsNullOrWhiteSpace(runToken))
        {
            throw new InvalidOperationException(
                "GLOSIFY_BROWSER_RUN_TOKEN is required when browser journeys are enabled.");
        }

        return new BrowserTestSettings(
            baseUri,
            runToken,
            Environment.GetEnvironmentVariable("GLOSIFY_BROWSER_TRACE_DIRECTORY"));
    }
}
