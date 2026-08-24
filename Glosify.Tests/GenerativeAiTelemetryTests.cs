using Glosify.Services.Ai.Generation;
using Xunit;

namespace Glosify.Tests;

public sealed class GenerativeAiTelemetryTests
{
    [Fact]
    public void Metric_tags_identify_the_direct_openai_model()
    {
        var tags = GenerativeAiTelemetry.Tags(
            "assistant",
            "openai",
            "gpt-5.6-luna");

        Assert.Contains(tags, tag =>
            tag.Key == "ai.model" && Equals(tag.Value, "gpt-5.6-luna"));
        Assert.DoesNotContain(tags, tag => tag.Key == "ai.deployment");
    }
}
