using System.Text.Json;
using System.Text.RegularExpressions;
using Glosify.Controllers.Api;
using Glosify.Data;
using Glosify.Migrations;
using Glosify.Models.Entities;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizLanguagePreferenceTests
{
    [Fact]
    public void Catalog_ContainsProviderLanguagesAndFreestyleMode()
    {
        Assert.Equal(70, QuizLanguageCatalog.All.Count);
        Assert.Equal(69, QuizLanguageCatalog.LanguageLearning.Count);
        Assert.Equal(70, QuizLanguageCatalog.All.Select(language => language.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(69, QuizLanguageCatalog.LanguageLearning.Select(language => language.TranslatorCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(69, QuizLanguageCatalog.LanguageLearning.Select(language => language.ScribeCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(70, QuizLanguageCatalog.All.Select(language => language.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(QuizLanguageCatalog.LanguageLearning, language =>
        {
            Assert.NotEmpty(language.Code);
            Assert.True(language.Code.Length <= QuizLanguageCatalog.StorageCodeMaximumLength);
            Assert.NotEmpty(language.TranslatorCode);
            Assert.NotEmpty(language.ScribeCode);
            Assert.NotEmpty(language.Name);
            Assert.NotEmpty(language.NativeName);
            Assert.Matches(new Regex("^[A-Za-z]{2,3}(?:-[A-Za-z]{2,4}){1,2}$"), language.Locale);
            Assert.Matches(new Regex("^[A-Z]{2}$"), language.FlagRegion);
            Assert.NotEmpty(language.Flag);
        });
        var freestyle = Assert.Single(QuizLanguageCatalog.All, language => !language.IsLanguageLearning);
        Assert.Equal("free", freestyle.Code);
        Assert.Equal("Freestyle", freestyle.Name);
        Assert.True(QuizLanguageCatalog.IsFreestyle("free"));
        Assert.True(QuizLanguageCatalog.IsFreestyle("Freestyle"));
    }

    [Fact]
    public void Catalog_MatchesCheckedInProviderCapabilitySnapshot()
    {
        using var snapshot = JsonDocument.Parse(File.ReadAllText(CapabilitySnapshotPath));
        var root = snapshot.RootElement;
        Assert.Equal(QuizLanguageCatalog.Version, root.GetProperty("catalogVersion").GetString());
        var azureCodes = root.GetProperty("azureTranslator").GetProperty("codes")
            .EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scribe = root.GetProperty("elevenLabsScribeV2Realtime");
        Assert.Equal(
            ["excellent", "high accuracy", "good"],
            scribe.GetProperty("qualifyingPublishedTiers").EnumerateArray().Select(value => value.GetString()));
        var scribeCodes = scribe.GetProperty("codes")
            .EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(scribe.GetProperty("maximumPublishedWerPercent").GetInt32() <= 20);
        Assert.Equal(69, azureCodes.Count);
        Assert.Equal(69, scribeCodes.Count);
        Assert.All(QuizLanguageCatalog.LanguageLearning, language =>
        {
            Assert.Contains(language.TranslatorCode, azureCodes);
            Assert.Contains(language.ScribeCode, scribeCodes);
        });
    }

    [Theory]
    [InlineData("Bengali", "bn")]
    [InlineData("Myanmar", "my")]
    [InlineData("Farsi", "fa")]
    [InlineData("Maori", "mi")]
    [InlineData("Bokmål", "nb")]
    [InlineData("Mandarin", "zh-Hans")]
    [InlineData("Brazilian Portuguese", "pt")]
    [InlineData("Serbian", "sr-Latn")]
    public void Catalog_RecognizesPublishedAliases(string alias, string expectedCode)
    {
        Assert.Equal(expectedCode, QuizLanguageCatalog.Find(alias)?.Code);
    }

    [Theory]
    [InlineData("Polish", "pl")]
    [InlineData("fil", "fil")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("sr-Latn", "sr-Latn")]
    [InlineData("free", "free")]
    public async Task Preference_PersistsCanonicalCodes(string selection, string expectedCode)
    {
        await using var context = CreateContext();
        await AddUserAsync(context);
        var service = new QuizLanguagePreferenceService(context);

        var selected = await service.SetSelectedAsync("user-1", selection);

        Assert.Equal(expectedCode, selected.Code);
        Assert.Equal(expectedCode, (await context.Users.SingleAsync()).SelectedQuizLanguageCode);
        Assert.Equal(selected.Name, (await service.GetSelectedAsync("user-1"))?.Name);
    }

    [Fact]
    public async Task Preference_RejectsUnknownCodeWithoutChangingStoredSelection()
    {
        await using var context = CreateContext();
        await AddUserAsync(context);
        var service = new QuizLanguagePreferenceService(context);
        await service.SetSelectedAsync("user-1", "uk");

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetSelectedAsync("user-1", "tlh"));

        Assert.Equal("uk", (await context.Users.SingleAsync()).SelectedQuizLanguageCode);
    }

    [Fact]
    public void LanguagesApi_PreservesEnglishNameArrayContract()
    {
        var expected = QuizLanguageCatalog.All.Select(language => language.Name).ToArray();
        var controller = new LanguagesApiController(new FixedLanguageContext(expected));

        var result = Assert.IsType<OkObjectResult>(controller.List().Result);
        var values = Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Value);

        Assert.Equal(70, values.Count);
        Assert.Equal(expected, values);
    }

    [Fact]
    public void Migration_ReplacesOnlyThePreferenceConstraint()
    {
        var operations = new InspectableFreestyleMigration().GetUpOperations();
        var drop = Assert.IsType<DropCheckConstraintOperation>(operations[0]);
        var add = Assert.IsType<AddCheckConstraintOperation>(operations[1]);

        Assert.Equal(2, operations.Count);
        Assert.Equal("AspNetUsers", drop.Table);
        Assert.Equal("CK_AspNetUsers_SelectedQuizLanguageCode", drop.Name);
        Assert.Equal("AspNetUsers", add.Table);
        Assert.Equal("CK_AspNetUsers_SelectedQuizLanguageCode", add.Name);
        Assert.Equal(70, QuizLanguageCatalog.All.Count(language =>
            add.Sql.Contains($"'{language.Code}'", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Vocabulary_AndLanguageNames_RoundTripRepresentativeUnicodeScripts()
    {
        await using var context = CreateContext();
        await AddUserAsync(context);
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(new Quiz
        {
            Id = quizId,
            UserId = "user-1",
            Name = "العربية · हिन्दी · 日本語",
            SourceLanguage = "English",
            TargetLanguage = "Arabic",
            Language = "Arabic",
        });
        context.Words.AddRange(
            new Word { Id = "rtl", QuizId = quizId, Lemma = "مَرْحَبًا", Translation = "hello" },
            new Word { Id = "indic", QuizId = quizId, Lemma = "नमस्ते", Translation = "hello" },
            new Word { Id = "cjk", QuizId = quizId, Lemma = "こんにちは", Translation = "hello" },
            new Word { Id = "cyrillic", QuizId = quizId, Lemma = "привіт", Translation = "hello" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Equal("العربية · हिन्दी · 日本語", (await context.Quizzes.SingleAsync()).Name);
        Assert.Equal(
            ["こんにちは", "привіт", "नमस्ते", "مَرْحَبًا"],
            await context.Words.OrderBy(word => word.Id).Select(word => word.Lemma).ToArrayAsync());
        await using var sqlServerModel = new GlosifyContext(
            new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer("Server=localhost;Database=ModelInspection;Trusted_Connection=True;TrustServerCertificate=True")
                .Options);
        Assert.All(
            new[] { nameof(Word.Lemma), nameof(Word.Translation) },
            propertyName => Assert.StartsWith(
                "nvarchar",
                sqlServerModel.Model.FindEntityType(typeof(Word))!.FindProperty(propertyName)!
                    .GetRelationalTypeMapping().StoreType,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string CapabilitySnapshotPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "Glosify", "Services", "Language", "language-capabilities.json"));

    private static GlosifyContext CreateContext() => new(
        new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static async Task AddUserAsync(GlosifyContext context)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = "user-1",
            UserName = "user@example.test",
            Email = "user@example.test",
        });
        await context.SaveChangesAsync();
    }

    private sealed class FixedLanguageContext(IReadOnlyList<string> languages) : ILanguageContext
    {
        public string? CurrentLanguage => null;
        public bool HasLanguage => false;
        public IReadOnlyList<string> SupportedLanguages => languages;
        public bool TrySetLanguage(string language) => false;
        public void Clear() { }
    }

    private sealed class InspectableFreestyleMigration : AddFreestyleQuizMode
    {
        public IReadOnlyList<MigrationOperation> GetUpOperations()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            Up(builder);
            return builder.Operations;
        }
    }
}
