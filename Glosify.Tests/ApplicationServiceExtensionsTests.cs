using Glosify.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Glosify.Tests;

public sealed class ApplicationServiceExtensionsTests
{
    [Fact]
    public void OpenAiApiKey_UsesOnlyTheExactAppServiceSetting()
    {
        var exactConfiguration = Configuration(new Dictionary<string, string?>
        {
            ["OPENAI_SECRET_KEY"] = "  direct-openai-key  ",
            ["GenerativeAi:ApiKey"] = "legacy-bound-key",
            ["OPENAI_API_KEY"] = "common-alias-key",
        });
        var legacyOnlyConfiguration = Configuration(new Dictionary<string, string?>
        {
            ["GenerativeAi:ApiKey"] = "legacy-bound-key",
            ["OPENAI_API_KEY"] = "common-alias-key",
        });

        Assert.Equal(
            "direct-openai-key",
            ApplicationServiceExtensions.ResolveOpenAiApiKey(exactConfiguration));
        Assert.Empty(ApplicationServiceExtensions.ResolveOpenAiApiKey(legacyOnlyConfiguration));
    }

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

    [Fact]
    public void ElevenLabsApiKey_EnvironmentAliasRemainsACompatibilityFallback()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ELEVENLABS_API_KEY"] = "environment-alias-key",
        });

        Assert.Equal(
            "environment-alias-key",
            ApplicationServiceExtensions.ResolveElevenLabsApiKey(configuration));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
