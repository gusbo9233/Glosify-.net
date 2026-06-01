using System.Reflection;
using Glosify.Models.Entities;
using Glosify.Services;
using Xunit;

namespace Glosify.Tests;

public class WordDetailServiceTests
{
    [Fact]
    public void HasVariant_MatchesNormalizedSurfaceWord()
    {
        var detail = new WordDetail
        {
            Word = "hablar",
            Variants = """[{"form":"hablo","tags":["present","first-person"]}]"""
        };

        Assert.True(HasVariant(detail, WordDetailKey.Normalize("Hablo")));
        Assert.False(HasVariant(detail, WordDetailKey.Normalize("habla")));
    }

    private static bool HasVariant(WordDetail detail, string normalizedWord)
    {
        var method = typeof(WordDetailService).GetMethod(
            "HasVariant",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<bool>(method.Invoke(null, [detail, normalizedWord]));
    }
}
