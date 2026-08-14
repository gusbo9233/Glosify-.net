using Glosify.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Glosify.Tests;

public sealed class ApplicationServiceExtensionsTests
{
    [Fact]
    public void ElevenLabsApiKey_CanonicalSettingTakesPriorityOverLegacyAliases()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["RealtimeTranslation:ElevenLabs:ApiKey"] = "canonical-key",
            ["Elevenlabs_key"] = "legacy-key",
            ["ELEVENLABS_API_KEY"] = "environment-alias-key",
        });

        Assert.Equal(
            "canonical-key",
            ApplicationServiceExtensions.ResolveElevenLabsApiKey(configuration));
    }

    [Fact]
    public void ElevenLabsApiKey_LegacyAliasRemainsACompatibilityFallback()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Elevenlabs_key"] = "legacy-key",
            ["ELEVENLABS_API_KEY"] = "environment-alias-key",
        });

        Assert.Equal(
            "legacy-key",
            ApplicationServiceExtensions.ResolveElevenLabsApiKey(configuration));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
