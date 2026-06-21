using System.ComponentModel.DataAnnotations;
using Xunit;

namespace Glosify.Tests;

public class RequestValidationTests
{
    [Fact]
    public void AddWordInput_RequiresWordAndTranslation()
    {
        var input = new AddWordInput { QuizId = Guid.NewGuid(), Word = "", Translation = "" };

        var errors = Validate(input).Select(r => r.MemberNames.FirstOrDefault()).ToArray();

        Assert.Contains(nameof(AddWordInput.Word), errors);
        Assert.Contains(nameof(AddWordInput.Translation), errors);
    }

    [Fact]
    public void AddWordInput_AcceptsValidPayload()
    {
        var input = new AddWordInput { QuizId = Guid.NewGuid(), Word = "casa", Translation = "house" };

        Assert.Empty(Validate(input));
    }

    [Fact]
    public void CreateQuizInput_RequiresAllNamedFields()
    {
        var input = new CreateQuizInput();

        var members = Validate(input).SelectMany(r => r.MemberNames).ToArray();

        Assert.Contains(nameof(CreateQuizInput.Name), members);
        Assert.Contains(nameof(CreateQuizInput.SourceLanguage), members);
        Assert.Contains(nameof(CreateQuizInput.TargetLanguage), members);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void QuizSessionSettings_RejectsOutOfRangeWordCount(int wordCount)
    {
        var settings = new QuizSessionSettings { WordCount = wordCount, Mode = "flashcards" };

        Assert.Contains(Validate(settings), r => r.MemberNames.Contains(nameof(QuizSessionSettings.WordCount)));
    }

    [Fact]
    public void QuizSessionSettings_AcceptsTypicalUsage()
    {
        var settings = new QuizSessionSettings { WordCount = 20, Mode = "flashcards" };

        Assert.Empty(Validate(settings));
    }

    [Theory]
    [InlineData(PracticeDirection.SourceToTarget)]
    [InlineData(PracticeDirection.TargetToSource)]
    public void QuizSessionSettings_AcceptsPracticeDirections(string practiceDirection)
    {
        var settings = new QuizSessionSettings
        {
            WordCount = 20,
            Mode = "flashcards",
            PracticeDirection = practiceDirection
        };

        Assert.Empty(Validate(settings));
    }

    [Fact]
    public void QuizSessionSettings_RejectsInvalidPracticeDirection()
    {
        var settings = new QuizSessionSettings
        {
            WordCount = 20,
            Mode = "flashcards",
            PracticeDirection = "sideways"
        };

        Assert.Contains(Validate(settings), r => r.MemberNames.Contains(nameof(QuizSessionSettings.PracticeDirection)));
    }

    [Theory]
    [InlineData(PracticeItemType.Words)]
    [InlineData(PracticeItemType.Sentences)]
    public void QuizSessionSettings_AcceptsPracticeItemTypes(string practiceItemType)
    {
        var settings = new QuizSessionSettings
        {
            WordCount = 20,
            Mode = "flashcards",
            PracticeItemType = practiceItemType
        };

        Assert.Empty(Validate(settings));
    }

    [Fact]
    public void QuizSessionSettings_RejectsInvalidPracticeItemType()
    {
        var settings = new QuizSessionSettings
        {
            WordCount = 20,
            Mode = "flashcards",
            PracticeItemType = "paragraphs"
        };

        Assert.Contains(Validate(settings), r => r.MemberNames.Contains(nameof(QuizSessionSettings.PracticeItemType)));
    }

    private static IReadOnlyList<ValidationResult> Validate(object instance)
    {
        var ctx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, ctx, results, validateAllProperties: true);
        return results;
    }
}
