using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.LanguageConfig;
using Glosify.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Glosify.Tests;

public class EstonianQuizWordDetailTests
{
    private static readonly Guid TestUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly HashSet<string> NamesToSkip = new(StringComparer.Ordinal)
    {
        "Eesti",
        "Hiina",
        "Venemaa",
        "Päts",
        "Stalin"
    };
    private static readonly Dictionary<string, string> EnglishTranslations = new(StringComparer.Ordinal)
    {
        ["riik"] = "state",
        ["käib"] = "goes",
        ["vastu"] = "against",
        ["müüri"] = "wall",
        ["on"] = "is",
        ["väike"] = "small",
        ["osariik"] = "state",
        ["Varsti"] = "soon",
        ["juhib"] = "leads",
        ["me"] = "our",
        ["riigi"] = "state",
        ["tüüri"] = "helm",
        ["otsib"] = "searches",
        ["kus"] = "where",
        ["varjupaik"] = "shelter"
    };
    private const string EstonianText = """
        Eesti riik käib vastu Hiina müüri
        Venemaa on väike osariik
        Varsti juhib Päts me riigi tüüri
        Stalin otsib, kus on varjupaik
        """;

    [Fact]
    public async Task AddWordsFromEstonianText_ActualSqlServer_CreatesDetailsThatPopulateFromDictionary()
    {
        await using var context = CreateContext();
        await CleanupPriorRunsAsync(context);

        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = $"Estonian SQL detail test {Guid.NewGuid():N}",
            UserId = TestUserId,
            SourceLanguage = "English",
            TargetLanguage = "Estonian",
            Language = "Estonian",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = "Ready"
        };
        var terms = ExtractDistinctWords(EstonianText);

        try
        {
            var dictionaryCandidates = terms.SelectMany(GetDictionaryCandidates).Distinct(StringComparer.Ordinal).ToArray();
            var availableDictionaryWords = await context.DictionaryEntries
                .Where(entry => entry.LangCode == "et" && dictionaryCandidates.Contains(entry.Word))
                .Select(entry => entry.Word)
                .Distinct()
                .ToListAsync();

            Assert.All(terms, term => Assert.Contains(availableDictionaryWords, word => GetDictionaryCandidates(term).Contains(word)));

            context.Quizzes.Add(quiz);
            await context.SaveChangesAsync();

            var dictionary = new DictionaryService(context, LanguageConfigsFixture.All);
            var enrichment = new SynchronousEnrichmentQueue(context, dictionary);
            var quizService = new QuizService(context, new FixedLanguageContext("Estonian"));
            var wordService = new WordService(context, enrichment);
            var aiService = new AiWordGenerationService();
            var generatedVocabulary = new GeneratedVocabularyService(context, quizService, aiService, enrichment);
            var quizController = WithUser(new QuizController(
                context,
                quizService,
                wordService,
                generatedVocabulary,
                new FixedLanguageContext("Estonian")));

            foreach (var term in terms)
            {
                await quizController.AddWord(quiz.Id, term, EnglishTranslations[term]);
            }

            var words = await context.Words
                .Where(word => word.QuizId == quiz.Id)
                .OrderBy(word => word.Lemma)
                .ToListAsync();
            var wordDetails = await context.WordDetails
                .Join(
                    context.Words.Where(word => word.QuizId == quiz.Id),
                    detail => detail.Id,
                    word => word.WordDetailId,
                    (detail, _) => detail)
                .Distinct()
                .ToListAsync();

            Assert.Equal(terms.Count, words.Count);
            Assert.All(terms, term => Assert.Contains(words, word => word.Lemma == term));
            Assert.All(words, word => Assert.False(string.IsNullOrWhiteSpace(word.WordDetailId)));
            Assert.Equal(terms.Count, wordDetails.Count);
            Assert.All(wordDetails, detail => Assert.Equal("Estonian", detail.Language));
            Assert.All(wordDetails, detail => Assert.NotEqual("{}", detail.Properties));
            Assert.All(wordDetails, detail => Assert.NotEqual("[]", detail.Variants));
            Assert.All(wordDetails, detail => Assert.False(string.IsNullOrWhiteSpace(detail.Explanation)));
            Assert.All(wordDetails, detail => Assert.False(string.IsNullOrWhiteSpace(detail.ExampleSentence)));

            var viewModelService = new WordDetailViewModelService(context, dictionary, LanguageConfigsFixture.All);
            var detailsController = WithUser(new WordDetailsController(context, viewModelService));
            foreach (var word in words)
            {
                var result = await detailsController.Details(word.WordDetailId);
                var view = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<WordDetailViewModel>(view.Model);

                Assert.Equal(word.Id, model.Word?.Id);
                Assert.Equal(quiz.Id, model.Quiz.Id);
                Assert.True(model.HasDictionaryMatch);
                Assert.Equal(word.Lemma, model.DictionaryMatch!.Word);
                Assert.False(string.IsNullOrWhiteSpace(model.PartOfSpeech));
                Assert.False(string.IsNullOrWhiteSpace(model.Explanation));
                Assert.False(string.IsNullOrWhiteSpace(model.ExampleSentence));
                Assert.NotEmpty(model.Variants);
            }
        }
        finally
        {
            await CleanupAsync(context, quiz.Id);
        }
    }

    [Fact]
    public async Task AddInflectedEstonianWord_ActualSqlServer_PopulatesFromVariantDictionaryEntry()
    {
        await AssertInflectedWordResolvesToParentAsync(
            word: "vahelt",
            translation: "from between",
            expectedParent: "vahe",
            expectedTags: ["ablative", "singular"]);
    }

    [Fact]
    public async Task AddInflectedEstonianVerb_ActualSqlServer_PrefersParentParadigmOverInflectionStub()
    {
        await AssertInflectedWordResolvesToParentAsync(
            word: "saanud",
            translation: "become",
            expectedParent: "saama",
            expectedTags: ["active", "participle", "past"]);
    }

    private async Task AssertInflectedWordResolvesToParentAsync(
        string word,
        string translation,
        string expectedParent,
        string[] expectedTags)
    {
        await using var context = CreateContext();
        await CleanupPriorRunsAsync(context);

        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = $"Estonian SQL detail test {Guid.NewGuid():N}",
            UserId = TestUserId,
            SourceLanguage = "English",
            TargetLanguage = "Estonian",
            Language = "Estonian",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = "Ready"
        };

        try
        {
            context.Quizzes.Add(quiz);
            await context.SaveChangesAsync();

            var dictionary = new DictionaryService(context, LanguageConfigsFixture.All);
            var enrichment = new SynchronousEnrichmentQueue(context, dictionary);
            var quizService = new QuizService(context, new FixedLanguageContext("Estonian"));
            var wordService = new WordService(context, enrichment);
            var aiService = new AiWordGenerationService();
            var generatedVocabulary = new GeneratedVocabularyService(context, quizService, aiService, enrichment);
            var quizController = WithUser(new QuizController(
                context,
                quizService,
                wordService,
                generatedVocabulary,
                new FixedLanguageContext("Estonian")));
            await quizController.AddWord(quiz.Id, word, translation);

            var savedWord = await context.Words.SingleAsync(word => word.QuizId == quiz.Id);
            var detail = await context.WordDetails.SingleAsync(detail => detail.Id == savedWord.WordDetailId);

            Assert.Equal(word, savedWord.Lemma);
            Assert.NotEqual("{}", detail.Properties);
            Assert.NotEqual("[]", detail.Variants);
            Assert.False(string.IsNullOrWhiteSpace(detail.Explanation));

            var viewModelService = new WordDetailViewModelService(context, dictionary, LanguageConfigsFixture.All);
            var detailsController = WithUser(new WordDetailsController(context, viewModelService));
            var result = await detailsController.Details(savedWord.WordDetailId);
            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<WordDetailViewModel>(view.Model);

            Assert.Equal(expectedParent, model.DictionaryMatch?.Word);
            Assert.Contains(model.Variants, variant => variant.Form == word && variant.HasAnyTag(expectedTags));
        }
        finally
        {
            await CleanupAsync(context, quiz.Id);
        }
    }

    private static GlosifyContext CreateContext()
    {
        var connectionString = LoadConnectionString();
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new GlosifyContext(options);
    }

    private static string LoadConnectionString()
    {
        var repoRoot = FindRepoRoot();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(repoRoot, "Glosify"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Glosify.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Glosify repository root.");
    }

    private static IReadOnlyList<string> ExtractDistinctWords(string input)
    {
        return input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.Trim(',', '.', '!', '?', ';', ':'))
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .Where(word => !NamesToSkip.Contains(word))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> GetDictionaryCandidates(string word)
    {
        var trimmed = word.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        return new[]
        {
            trimmed,
            string.Concat(char.ToUpperInvariant(trimmed[0]).ToString(), trimmed[1..]),
            string.Concat(char.ToLowerInvariant(trimmed[0]).ToString(), trimmed[1..])
        }.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static async Task CleanupAsync(GlosifyContext context, Guid quizId)
    {
        var detailIds = await context.Words
            .Where(word => word.QuizId == quizId)
            .Select(word => word.WordDetailId)
            .ToListAsync();

        await context.Words
            .Where(word => word.QuizId == quizId)
            .ExecuteDeleteAsync();
        if (detailIds.Count > 0)
        {
            await context.WordDetails
                .Where(detail => detailIds.Contains(detail.Id))
                .ExecuteDeleteAsync();
        }
        await context.Quizzes
            .Where(quiz => quiz.Id == quizId)
            .ExecuteDeleteAsync();
    }

    private static async Task CleanupPriorRunsAsync(GlosifyContext context)
    {
        var staleQuizIds = await context.Quizzes
            .Where(quiz => quiz.Name.StartsWith("Estonian SQL detail test "))
            .Select(quiz => quiz.Id)
            .ToListAsync();

        if (staleQuizIds.Count > 0)
        {
            var staleDetailIds = await context.Words
                .Where(word => staleQuizIds.Contains(word.QuizId))
                .Select(word => word.WordDetailId)
                .ToListAsync();

            await context.Words
                .Where(word => staleQuizIds.Contains(word.QuizId))
                .ExecuteDeleteAsync();

            if (staleDetailIds.Count > 0)
            {
                await context.WordDetails
                    .Where(detail => staleDetailIds.Contains(detail.Id))
                    .ExecuteDeleteAsync();
            }
            await context.Quizzes
                .Where(quiz => staleQuizIds.Contains(quiz.Id))
                .ExecuteDeleteAsync();
        }

    }

    private static T WithUser<T>(T controller) where T : Controller
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, TestUserId.ToString())],
                    "TestAuth"))
            }
        };

        return controller;
    }

    private sealed class FixedLanguageContext(string language) : ILanguageContext
    {
        public string? CurrentLanguage => language;
        public bool HasLanguage => true;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Estonian", "German", "Polish", "Ukrainian"];
        public bool TrySetLanguage(string language) => true;
        public void Clear()
        {
        }
    }
}
