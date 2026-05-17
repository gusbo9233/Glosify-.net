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

    [Fact]
    public void IsUsefulExampleSentence_AcceptsInflectedSentenceWhenUsedFormIsProvided()
    {
        Assert.True(IsUsefulExampleSentence(
            "mówić",
            "to speak",
            "Ona mówi po polsku.",
            "She speaks Polish.",
            "mówi"));
    }

    [Fact]
    public void IsUsefulExampleSentence_RejectsUnrelatedStockTranslation()
    {
        Assert.False(IsUsefulExampleSentence(
            "mówi",
            "speaks",
            "Czy pan mówi",
            "Is that true?"));
    }

    [Fact]
    public void IsUsefulExampleSentence_AllowsIsThatTrueWhenSentenceMeansThat()
    {
        Assert.True(IsUsefulExampleSentence(
            "prawda",
            "truth",
            "Czy to prawda?",
            "Is that true?"));
    }

    [Theory]
    [InlineData("rozumiesz", "you understand", "m/f pol Czy (mnie) rozumiesz?", "Do you understand me?")]
    [InlineData("mówi", "speaks", "Czy pan/pani mówi?", "Does he or she speak?")]
    [InlineData("angielsku", "in English", "po (angielsku)? inf po an-gyel-skoo", "in English")]
    public void IsUsefulExampleSentence_RejectsLearnerNoteArtifacts(
        string word,
        string translation,
        string exampleSentence,
        string exampleSentenceTranslation)
    {
        Assert.False(IsUsefulExampleSentence(
            word,
            translation,
            exampleSentence,
            exampleSentenceTranslation));
    }

    [Fact]
    public void ShouldReplaceExampleSentence_ReplacesExistingLearnerNoteArtifacts()
    {
        Assert.True(ShouldReplaceExampleSentence(
            "rozumiesz",
            "you understand",
            "m/f pol Czy (mnie) rozumiesz?",
            "Do you understand me?"));
    }

    [Fact]
    public void ShouldReplaceExampleSentence_KeepsExistingCleanSentence()
    {
        Assert.False(ShouldReplaceExampleSentence(
            "rozumiesz",
            "you understand",
            "Czy mnie rozumiesz?",
            "Do you understand me?"));
    }

    private static bool IsUsefulExampleSentence(
        string word,
        string translation,
        string exampleSentence,
        string exampleSentenceTranslation,
        string? exampleSentenceWord = null)
    {
        var method = typeof(GeneratedVocabularyService).GetMethod(
            string.IsNullOrWhiteSpace(exampleSentenceWord) ? "IsUsefulExampleSentence" : "IsUsefulExampleSentenceCore",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        object?[] args = string.IsNullOrWhiteSpace(exampleSentenceWord)
            ? [word, translation, exampleSentence, exampleSentenceTranslation]
            : [word, translation, exampleSentence, exampleSentenceTranslation, exampleSentenceWord];

        return Assert.IsType<bool>(method.Invoke(null, args));
    }

    private static bool ShouldReplaceExampleSentence(
        string word,
        string translation,
        string exampleSentence,
        string exampleSentenceTranslation)
    {
        var method = typeof(GeneratedVocabularyService).GetMethod(
            "ShouldReplaceExampleSentence",
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
