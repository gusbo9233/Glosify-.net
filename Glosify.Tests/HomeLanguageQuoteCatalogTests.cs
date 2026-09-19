using Glosify.Models;
using Glosify.Services.Language;
using Xunit;

namespace Glosify.Tests;

public sealed class HomeLanguageQuoteCatalogTests
{
    [Fact]
    public void EveryLearningLanguageHasACompleteQuote()
    {
        var expectedCodes = QuizLanguageCatalog.LanguageLearning
            .Select(language => language.Code)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualCodes = HomeLanguageQuoteCatalog.LanguageCodes
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedCodes, actualCodes);

        foreach (var language in QuizLanguageCatalog.LanguageLearning)
        {
            var quote = Assert.IsType<HomeLanguageQuote>(HomeLanguageQuoteCatalog.Find(language.Code));
            Assert.False(string.IsNullOrWhiteSpace(quote.Original));
            Assert.False(string.IsNullOrWhiteSpace(quote.English));
            Assert.False(string.IsNullOrWhiteSpace(quote.Attribution));
        }
    }

    [Fact]
    public void FreestyleDoesNotUseALanguageQuote()
    {
        Assert.Null(HomeLanguageQuoteCatalog.Find(QuizLanguageCatalog.FreestyleCode));
    }
}
