using Glosify.Models;
using Glosify.Services;
using Xunit;

namespace Glosify.Tests;

public class WordDetailEnrichmentServiceTests
{
    [Fact]
    public async Task EnrichAsync_ReplacesThinSeedExplanationWithGeneratedWordDetail()
    {
        var detail = new WordDetail
        {
            Properties = "{}",
            Variants = "[]",
            Explanation = "The house is big.",
            ExampleSentence = "Das Haus ist gross.",
            Language = "German",
            TargetLanguage = "German"
        };
        var service = CreateService(new GeneratedWordDetail
        {
            Properties = JsonProperties("""{"part_of_speech":"noun","grammatical gender":"neuter"}"""),
            Variants =
            [
                new GeneratedWordVariant
                {
                    Form = "Haus",
                    Label = "nominative singular",
                    Group = "Singular",
                    Tags = ["nominative singular"]
                }
            ],
            Explanation = "A neuter German noun for a building or home.",
            ExampleSentence = "Das Haus steht am Fluss."
        });

        var changed = await service.EnrichAsync(
            detail,
            new Word { Lemma = "Haus", Translation = "house" },
            new Quiz { SourceLanguage = "English", TargetLanguage = "German" },
            "Haus",
            "German");

        Assert.True(changed);
        Assert.Contains("\"pos\":\"noun\"", detail.Properties);
        Assert.Contains("\"gender\":\"neuter\"", detail.Properties);
        Assert.Equal("A neuter German noun for a building or home.", detail.Explanation);
        Assert.Equal("Das Haus ist gross.", detail.ExampleSentence);

        var variant = Assert.Single(WordDetailJsonReader.ReadVariants(detail.Variants));
        Assert.Equal("Haus", variant.Form);
        Assert.Equal("nominative singular", variant.Label);
        Assert.Equal("Singular", variant.Group);
        Assert.Contains("nominative singular", variant.Tags);
    }

    [Fact]
    public async Task EnrichAsync_LeavesCompletedDetailsUntouched()
    {
        var detail = new WordDetail
        {
            Properties = """{"pos":"noun"}""",
            Variants = """[{"form":"Haus","tags":["nominative","singular"]}]""",
            Explanation = "Manual explanation.",
            ExampleSentence = "Manual example.",
            Language = "German",
            TargetLanguage = "German"
        };
        var vocabularyGenerator = new FakeVocabularyGenerationService
        {
            WordDetail = new GeneratedWordDetail
            {
                Properties = JsonProperties("""{"pos":"verb"}"""),
                Explanation = "Generated explanation.",
                ExampleSentence = "Generated example."
            }
        };
        var service = new WordDetailEnrichmentService(vocabularyGenerator);

        var changed = await service.EnrichAsync(
            detail,
            new Word { Lemma = "Haus", Translation = "house" },
            new Quiz { SourceLanguage = "English", TargetLanguage = "German" },
            "Haus",
            "German");

        Assert.False(changed);
        Assert.False(vocabularyGenerator.WasCalled);
        Assert.Equal("""{"pos":"noun"}""", detail.Properties);
        Assert.Equal("Manual explanation.", detail.Explanation);
        Assert.Equal("Manual example.", detail.ExampleSentence);
    }

    [Fact]
    public async Task EnrichAsync_WhenForced_ReplacesCompletedDetails()
    {
        var detail = new WordDetail
        {
            Properties = """{"pos":"noun"}""",
            Variants = """[{"form":"old","tags":["nominative"]}]""",
            Explanation = "Old explanation.",
            ExampleSentence = "Old example.",
            Language = "German",
            TargetLanguage = "German"
        };
        var service = CreateService(new GeneratedWordDetail
        {
            Properties = JsonProperties("""{"pos":"pronoun"}"""),
            Variants =
            [
                new GeneratedWordVariant { Form = "er", Tags = ["nominative"] },
            ],
            Explanation = "Generated explanation.",
            ExampleSentence = "Generated example."
        });

        var changed = await service.EnrichAsync(
            detail,
            new Word { Lemma = "er", Translation = "he" },
            new Quiz { SourceLanguage = "English", TargetLanguage = "German" },
            "er",
            "German",
            force: true);

        Assert.True(changed);
        Assert.Contains("\"pos\":\"pronoun\"", detail.Properties);
        Assert.Contains("er", detail.Variants);
        Assert.Equal("Generated explanation.", detail.Explanation);
        Assert.Equal("Generated example.", detail.ExampleSentence);
    }

    [Fact]
    public async Task EnrichAsync_DoesNotStoreExplanationAsExampleSentence()
    {
        var detail = new WordDetail
        {
            Properties = "{}",
            Variants = "[]",
            Language = "German",
            TargetLanguage = "German"
        };
        var service = CreateService(new GeneratedWordDetail
        {
            Properties = JsonProperties("""{"pos":"noun"}"""),
            Variants =
            [
                new GeneratedWordVariant { Form = "Haus", Tags = ["nominative", "singular"] },
            ],
            Explanation = "A German noun meaning house.",
            ExampleSentence = "A German noun meaning house."
        });

        var changed = await service.EnrichAsync(
            detail,
            new Word { Lemma = "Haus", Translation = "house" },
            new Quiz { SourceLanguage = "English", TargetLanguage = "German" },
            "Haus",
            "German");

        Assert.True(changed);
        Assert.Equal("A German noun meaning house.", detail.Explanation);
        Assert.Empty(detail.ExampleSentence);
    }

    [Fact]
    public async Task EnrichAsync_DoesNotStoreOneWordExampleSentence()
    {
        var detail = new WordDetail
        {
            Properties = "{}",
            Variants = "[]",
            Language = "German",
            TargetLanguage = "German"
        };
        var service = CreateService(new GeneratedWordDetail
        {
            Properties = JsonProperties("""{"pos":"noun"}"""),
            Variants =
            [
                new GeneratedWordVariant { Form = "Haus", Tags = ["nominative", "singular"] },
            ],
            Explanation = "A German noun meaning house.",
            ExampleSentence = "Haus"
        });

        var changed = await service.EnrichAsync(
            detail,
            new Word { Lemma = "Haus", Translation = "house" },
            new Quiz { SourceLanguage = "English", TargetLanguage = "German" },
            "Haus",
            "German");

        Assert.True(changed);
        Assert.Equal("A German noun meaning house.", detail.Explanation);
        Assert.Empty(detail.ExampleSentence);
    }

    [Fact]
    public async Task EnrichAsync_PreservesAiVariantLabelsAndGroups()
    {
        var detail = new WordDetail
        {
            Properties = "{}",
            Variants = "[]",
            Language = "Polish",
            TargetLanguage = "Polish"
        };
        var service = CreateService(new GeneratedWordDetail
        {
            Properties = JsonProperties("""{"pos":"verb"}"""),
            Variants =
            [
                new GeneratedWordVariant
                {
                    Form = "robie",
                    Label = "first-person singular",
                    Group = "Present",
                    Tags = ["non past first person singular"]
                },
                new GeneratedWordVariant
                {
                    Form = "robili",
                    Label = "3rd masculine personal",
                    Group = "Past Plural",
                    Tags = ["past masculine personal plural"]
                },
                new GeneratedWordVariant { Form = "były", Tags = ["past female plural third person"] },
                new GeneratedWordVariant
                {
                    Form = "byÅ‚y",
                    Label = "3rd non-masculine personal",
                    Group = "Past Plural",
                    Tags = ["past female plural third person"]
                },
            ],
            Explanation = "A common Polish verb meaning to do or make.",
            ExampleSentence = "Robie kawe."
        });

        var changed = await service.EnrichAsync(
            detail,
            new Word { Lemma = "robic", Translation = "do" },
            new Quiz { SourceLanguage = "English", TargetLanguage = "Polish" },
            "robic",
            "Polish");

        Assert.True(changed);
        var variants = WordDetailJsonReader.ReadVariants(detail.Variants);
        Assert.Contains(variants, variant =>
            variant.Form == "robie"
            && variant.Label == "first-person singular"
            && variant.Group == "Present"
            && variant.Tags.Contains("non past first person singular"));
        Assert.Contains(variants, variant =>
            variant.Form == "robili"
            && variant.Label == "3rd masculine personal"
            && variant.Group == "Past Plural"
            && variant.Tags.Contains("past masculine personal plural"));
        Assert.Contains(variants, variant =>
            variant.Label == "3rd non-masculine personal"
            && variant.Group == "Past Plural");
    }

    private static WordDetailEnrichmentService CreateService(GeneratedWordDetail generated)
        => new(new FakeVocabularyGenerationService { WordDetail = generated });

    private static Dictionary<string, System.Text.Json.JsonElement> JsonProperties(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private sealed class FakeVocabularyGenerationService : IVocabularyGenerationService
    {
        public GeneratedWordDetail? WordDetail { get; init; }
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
            string input,
            string sourceLanguage,
            string targetLanguage,
            string? quizName,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeneratedWordDetail?> GenerateWordDetailAsync(
            string word,
            string translation,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(WordDetail);
        }

        public Task<RepairQuizResult?> RepairQuizAsync(
            RepairQuizData quizData,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RepairWordResult?> RepairWordAsync(
            RepairQuizData quizData,
            string wordId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RepairSentenceResult?> RepairSentenceAsync(
            RepairQuizData quizData,
            string sentenceText,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
