using Glosify.Services;
using Xunit;

namespace Glosify.Tests;

public class VocabularyInputCleanerTests
{
    [Theory]
    [InlineData("m/f pol Czy mówisz", "Czy mówisz")]
    [InlineData("Czy ktoś mówi po (angielsku)?", "Czy ktoś mówi po angielsku?")]
    [InlineData("angielsku pronunciation of Angielsku", "angielsku")]
    [InlineData("chi pronunciation of nie", "chi")]
    public void CleanForVocabulary_RemovesLearnerNoteArtifacts(string input, string expected)
    {
        Assert.Equal(expected, VocabularyInputCleaner.CleanForVocabulary(input));
    }
}
