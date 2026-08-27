using Glosify.Models.ViewModels;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizSettingsPresentationTests
{
    [Fact]
    public void Create_normalizes_empty_counts_and_uses_fallback_labels()
    {
        var presentation = QuizSettingsPresentation.Create(
            selectedQuiz: null,
            availableWordCount: 0,
            availableSentenceCount: 0,
            selectedWordCount: 1,
            defaultQuizName: "Default quiz",
            defaultSourceLanguage: "Source",
            defaultTargetLanguage: "Target",
            wordsLabel: "Words");

        Assert.Equal("Default quiz", presentation.QuizName);
        Assert.Equal("Source", presentation.SourceLanguage);
        Assert.Equal("Target", presentation.TargetLanguage);
        Assert.Equal(new QuizSettingsLengthOption(1, 1, true), presentation.QuickLength);
        Assert.Equal(new QuizSettingsLengthOption(1, 1, false), presentation.StandardLength);
        Assert.Equal(new QuizSettingsLengthOption(1, 1, false), presentation.AllLength);
    }

    [Fact]
    public void Create_caps_quick_lengths_and_selects_the_requested_length_band()
    {
        var quiz = new QuizCard(
            Guid.NewGuid(),
            "Polish practice",
            "English",
            "Polish",
            "Ready",
            DateTimeOffset.UtcNow,
            null,
            false);

        var presentation = QuizSettingsPresentation.Create(
            quiz,
            availableWordCount: 42,
            availableSentenceCount: 13,
            selectedWordCount: 20,
            defaultQuizName: "Default quiz",
            defaultSourceLanguage: "Source",
            defaultTargetLanguage: "Target",
            wordsLabel: "Words");

        Assert.Equal(new QuizSettingsLengthOption(10, 10, false), presentation.QuickLength);
        Assert.Equal(new QuizSettingsLengthOption(20, 13, true), presentation.StandardLength);
        Assert.Equal(new QuizSettingsLengthOption(42, 13, false), presentation.AllLength);
        Assert.Equal("Words", presentation.ItemLabel);
    }

    [Fact]
    public void Create_uses_subject_labels_for_freestyle_quizzes()
    {
        var quiz = new QuizCard(
            Guid.NewGuid(),
            "History",
            "Freestyle",
            "Freestyle",
            "Ready",
            DateTimeOffset.UtcNow,
            null,
            false);

        var presentation = QuizSettingsPresentation.Create(
            quiz,
            availableWordCount: 4,
            availableSentenceCount: 0,
            selectedWordCount: 4,
            defaultQuizName: "Default quiz",
            defaultSourceLanguage: "Source",
            defaultTargetLanguage: "Target",
            wordsLabel: "Words");

        Assert.True(presentation.IsFreestyle);
        Assert.Equal("Items", presentation.ItemLabel);
        Assert.Equal("Prompt", presentation.PromptLabel);
        Assert.Equal("Answer", presentation.AnswerLabel);
    }
}
