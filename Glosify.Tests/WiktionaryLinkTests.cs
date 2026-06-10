using Xunit;

namespace Glosify.Tests;

public class WiktionaryLinkTests
{
    [Fact]
    public void ForWord_BuildsPolishWiktionaryUrl()
    {
        var url = WiktionaryLink.ForWord("Polish", "jak");

        Assert.Equal("https://pl.wiktionary.org/wiki/jak", url);
    }

    [Fact]
    public void ForWord_EncodesSpacesAndDiacritics()
    {
        var url = WiktionaryLink.ForWord("Polish", "robi\u0107 co\u015B");

        Assert.Equal("https://pl.wiktionary.org/wiki/robi%C4%87_co%C5%9B", url);
    }

    [Fact]
    public void ForWord_ReturnsNullForUnknownLanguage()
    {
        var url = WiktionaryLink.ForWord("Klingon", "qapla");

        Assert.Null(url);
    }
}
