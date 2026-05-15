using System.Reflection;
using Glosify.Services;
using Xunit;

namespace Glosify.Tests;

public class GeneratedVocabularyServiceTests
{
    [Fact]
    public void IsUsefulExampleSentence_AcceptsTargetLanguageSentenceContainingWord()
    {
        Assert.True(IsUsefulExampleSentence(
            "jesteś",
            "you are",
            "Czy jesteś gotowy?",
            "Are you ready?"));
    }

    [Fact]
    public void IsUsefulExampleSentence_RejectsGlossLineWithTranslationAndPronunciation()
    {
        Assert.False(IsUsefulExampleSentence(
            "jest",
            "he she it is",
            "he she it is jest yest",
            "he/she/it is"));
    }

    [Fact]
    public void IsUsefulExampleSentence_RejectsTranslationWordPronunciationLine()
    {
        Assert.False(IsUsefulExampleSentence(
            "jesteś",
            "you are",
            "you are jesteś yes tesh",
            "you are"));
    }

    [Fact]
    public void IsUsefulExampleSentence_RejectsSentenceMissingTargetWord()
    {
        Assert.False(IsUsefulExampleSentence(
            "jesteś",
            "you are",
            "Are you ready?",
            "Are you ready?"));
    }

    private static bool IsUsefulExampleSentence(
        string word,
        string translation,
        string exampleSentence,
        string exampleSentenceTranslation)
    {
        var method = typeof(GeneratedVocabularyService).GetMethod(
            "IsUsefulExampleSentence",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<bool>(method.Invoke(null, [
            word,
            translation,
            exampleSentence,
            exampleSentenceTranslation
        ]));
    }
}
