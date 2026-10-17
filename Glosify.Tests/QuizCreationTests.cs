using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public class QuizCreationTests
{
    [Fact]
    public async Task CreateFreestyleQuiz_ForcesFreestyleSourceAndTarget()
    {
        await using var context = CreateContext();
        var service = new QuizService(context, new TestLanguageContext("Freestyle"), Anki(context));

        var quiz = await service.CreateQuizAsync("Anatomy", "English", "Freestyle", "owner");

        Assert.Equal("Freestyle", quiz.SourceLanguage);
        Assert.Equal("Freestyle", quiz.TargetLanguage);
        Assert.Equal("Freestyle", quiz.Language);
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static Glosify.Services.Anki.IAnkiCollectionService Anki(GlosifyContext context) =>
        new Glosify.Services.Anki.AnkiCollectionService(
            context,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

    private sealed class TestLanguageContext : ILanguageContext
    {
        public TestLanguageContext(string? currentLanguage)
        {
            CurrentLanguage = currentLanguage;
        }

        public string? CurrentLanguage { get; }
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish"];
        public bool TrySetLanguage(string language) => false;
        public void Clear()
        {
        }
    }
}
