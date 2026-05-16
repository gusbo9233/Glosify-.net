using Glosify.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;
using Xunit;

namespace Glosify.Tests;

public class AiWordGenerationServiceTests
{
    [Fact]
    public void ValidateResponse_AcceptsStructurallyValidResponseWithCleanableArtifacts()
    {
        var service = new AiWordGenerationService(
            Options.Create(new GeminiOptions()),
            NullLogger<AiWordGenerationService>.Instance);

        var json = """
        {
          "angielsku": {
            "translation": "English (language)",
            "example_sentence": "Czy ktoś mówi po (angielsku)?",
            "example_sentence_translation": "Does anyone speak English?"
          }
        }
        """;

        Assert.True(service.ValidateResponse(json));
    }

    [Fact]
    public void ValidateResponse_AcceptsWordsArrayShape()
    {
        var service = new AiWordGenerationService(
            Options.Create(new GeminiOptions()),
            NullLogger<AiWordGenerationService>.Instance);

        var json = """
        {
          "words": [
            {
              "lemma": "angielsku",
              "translation": "English",
              "example_sentence": "Czy ktoś mówi po angielsku?",
              "example_sentence_translation": "Does anyone speak English?"
            }
          ]
        }
        """;

        Assert.True(service.ValidateResponse(json));
    }

    [Fact]
    public void NormalizeGeneratedWords_SalvagesMalformedJsonLikeResponse()
    {
        var method = typeof(AiWordGenerationService).GetMethod(
            "NormalizeGeneratedWords",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var brokenJson = """
        {
          "angielsku": {
            "translation": "English",
            "example_sentence": "Czy ktoś mówi po angielsku?",
            "example_sentence_translation": "Does anyone speak English?"
          },
        """;

        var result = Assert.IsAssignableFrom<IReadOnlyDictionary<string, GeneratedWord>>(
            method.Invoke(null, [brokenJson, "Czy ktoś mówi po angielsku?"]));

        var word = Assert.Single(result);
        Assert.Equal("angielsku", word.Key);
        Assert.Equal("English", word.Value.Translation);
        Assert.Equal("Czy ktoś mówi po angielsku?", word.Value.ExampleSentence);
    }

    [Fact]
    public void BuildWordDetailPrompt_OnlyIncludesTargetLanguageGrammarGuidance()
    {
        var prompt = BuildWordDetailPrompt("olema", "to be", "English", "Estonian");

        Assert.Contains("Language-specific grammar rules for Estonian", prompt);
        Assert.Contains("Estonian verbs", prompt);
        Assert.Contains("ma-infinitive", prompt);
        Assert.DoesNotContain("Polish verbs", prompt);
        Assert.DoesNotContain("masculine-personal", prompt);
        Assert.DoesNotContain("Ukrainian verbs", prompt);
        Assert.DoesNotContain("German verbs", prompt);
    }

    [Fact]
    public void BuildWordDetailPrompt_IncludesPolishPluralGenderRulesOnlyForPolish()
    {
        var prompt = BuildWordDetailPrompt("byli", "they were", "English", "Polish");

        Assert.Contains("Language-specific grammar rules for Polish", prompt);
        Assert.Contains("Polish verbs", prompt);
        Assert.Contains("masculine-personal", prompt);
        Assert.Contains("non-masculine-personal", prompt);
        Assert.DoesNotContain("Estonian verbs", prompt);
        Assert.DoesNotContain("ma-infinitive", prompt);
    }

    [Fact]
    public void BuildWordExtractionPrompt_AsksForBroadCoverageForPhrasebookPaste()
    {
        const string input = """
Do you speak (English)?
Czy pan/pani mówi chi pan/pa-nee moo-vee
po (angielsku)? m/f pol po (an-gyel-skoo)
Czy mówisz chi moo-veesh
po (angielsku)? inf po (an-gyel-skoo)

Does anyone speak (English)?
Czy ktoś mówi chi ktosh moo-vee
po (angielsku)? po (an-gyel-skoo)

Do you understand (me)?
Czy pan/pani (mnie) chi pan/pa-nee (mnye)
rozumie? m/f pol ro-zoo-mye
Czy (mnie) rozumiesz? inf chi (mnye) ro-zoo-myesh

Yes, I understand.
Tak, rozumiem. tak ro-zoo-myem

No, I don't understand.
Nie, nie rozumiem. nye nye ro-zoo-myem

I (don't) understand.
(Nie) Rozumiem. (nye) ro-zoo-myem

Pardon?
Proszę? pro-she

I speak (English).
Mówię po (angielsku). moo-vyem po (an-gyel-skoo)

I don't speak (Polish).
Nie mówię po (polsku). nye moo-vyem po (pol-skoo)

I speak a little.
Mówię trochę. moo-vyem tro-khe

Let's speak (Polish).
Rozmawiajmy po (polsku). roz-mav-yai-mi po (pol-skoo)

What does (nieczynne) mean?
Co to znaczy (nieczynne)? tso to zna-chi (nye-chi-ne)
""";

        var candidates = ExtractCandidateWords(input);
        var prompt = BuildWordExtractionPrompt(input, "English", "Polish");

        Assert.True(candidates.Count >= 15);
        Assert.Contains("mówi", candidates);
        Assert.Contains("rozumiem", candidates);
        Assert.Contains("Rozmawiajmy", candidates);
        Assert.DoesNotContain("speak", candidates);
        Assert.DoesNotContain("moo", candidates);
        Assert.Contains("Return broad coverage", prompt);
        Assert.Contains("TARGET_COUNT", prompt);
        Assert.Contains("at minimum", prompt);
        Assert.Contains("Every JSON key MUST exactly match one line from TOKENS", prompt);
        Assert.Contains("do not stop after 3 items", prompt);
        Assert.Contains("Never use an unrelated stock translation", prompt);
        Assert.Contains("lightly rewrite them into clear natural Polish sentences", prompt);
        Assert.Contains("never merge both alternatives into one sentence", prompt);
    }

    [Fact]
    public void ShouldExpandCoverage_WhenLargeCandidateListUnderGenerates()
    {
        Assert.True(ShouldExpandCoverage(candidateCount: 18, generatedCount: 3));
        Assert.False(ShouldExpandCoverage(candidateCount: 18, generatedCount: 10));
        Assert.False(ShouldExpandCoverage(candidateCount: 4, generatedCount: 1));
    }

    [Fact]
    public void BuildCoverageExpansionPrompt_RequiresExactMissingTokenKeys()
    {
        var prompt = BuildCoverageExpansionPrompt(
            "Czy pan pani mówi\npo angielsku?",
            "English",
            "Polish",
            ["mówi", "angielsku"]);

        Assert.Contains("Every JSON key MUST exactly match one line from TOKENS", prompt);
        Assert.Contains("- mówi", prompt);
        Assert.Contains("- angielsku", prompt);
        Assert.Contains("previous vocabulary extraction returned too few items", prompt);
        Assert.Contains("Never use an unrelated stock translation", prompt);
        Assert.Contains("lightly rewrite it into a natural Polish sentence", prompt);
    }

    [Fact]
    public void SelectMissingCoverageCandidates_UsesPhrasebookWordsAfterThreeItemResult()
    {
        var candidates = ExtractCandidateWords(FullPhrasebookPaste);
        var initialGenerated = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase)
        {
            ["Czy"] = new() { Translation = "question marker" },
            ["pan"] = new() { Translation = "sir" },
            ["pani"] = new() { Translation = "madam" }
        };

        var missing = SelectMissingCoverageCandidates(candidates, initialGenerated);

        Assert.DoesNotContain("Czy", missing);
        Assert.DoesNotContain("pan", missing);
        Assert.DoesNotContain("pani", missing);
        Assert.Contains("mówi", missing);
        Assert.Contains("angielsku", missing);
        Assert.Contains("Mówię", missing);
        Assert.Contains("trochę", missing);
        Assert.Contains("Rozmawiajmy", missing);
        Assert.DoesNotContain("speak", missing);
        Assert.DoesNotContain("moo", missing);
    }

    [Fact]
    public void BuildCoverageExpansionPrompt_ForThreeItemPhrasebookResultIncludesLaterMissingTokens()
    {
        var candidates = ExtractCandidateWords(FullPhrasebookPaste);
        var initialGenerated = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase)
        {
            ["Czy"] = new() { Translation = "question marker" },
            ["pan"] = new() { Translation = "sir" },
            ["pani"] = new() { Translation = "madam" }
        };
        var missing = SelectMissingCoverageCandidates(candidates, initialGenerated);

        var prompt = BuildCoverageExpansionPrompt(
            VocabularyInputCleaner.CleanForVocabulary(FullPhrasebookPaste),
            "English",
            "Polish",
            missing);

        Assert.Contains("- mówi", prompt);
        Assert.Contains("- angielsku", prompt);
        Assert.Contains("- Mówię", prompt);
        Assert.Contains("- Rozmawiajmy", prompt);
        Assert.DoesNotContain("- Czy", prompt);
        Assert.DoesNotContain("- pan", prompt);
        Assert.DoesNotContain("moo-vee", prompt);
        Assert.DoesNotContain("Do you speak", prompt);
    }

    private const string FullPhrasebookPaste = """
Do you speak (English)?
Czy pan/pani mówi chi pan/pa-nee moo-vee
po (angielsku)? m/f pol po (an-gyel-skoo)
Czy mówisz chi moo-veesh
po (angielsku)? inf po (an-gyel-skoo)

Does anyone speak (English)?
Czy ktoś mówi chi ktosh moo-vee
po (angielsku)? po (an-gyel-skoo)

Do you understand (me)?
Czy pan/pani (mnie) chi pan/pa-nee (mnye)
rozumie? m/f pol ro-zoo-mye
Czy (mnie) rozumiesz? inf chi (mnye) ro-zoo-myesh

Yes, I understand.
Tak, rozumiem. tak ro-zoo-myem

No, I don't understand.
Nie, nie rozumiem. nye nye ro-zoo-myem

I (don't) understand.
(Nie) Rozumiem. (nye) ro-zoo-myem

Pardon?
Proszę? pro-she

I speak (English).
Mówię po (angielsku). moo-vyem po (an-gyel-skoo)

I don't speak (Polish).
Nie mówię po (polsku). nye moo-vyem po (pol-skoo)

I speak a little.
Mówię trochę. moo-vyem tro-khe

Let's speak (Polish).
Rozmawiajmy po (polsku). roz-mav-yai-mi po (pol-skoo)

What does (nieczynne) mean?
Co to znaczy (nieczynne)? tso to zna-chi (nye-chi-ne)
""";

    private static IReadOnlyList<string> ExtractCandidateWords(string input)
    {
        var method = typeof(AiWordGenerationService).GetMethod(
            "ExtractCandidateWords",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsAssignableFrom<IReadOnlyList<string>>(method.Invoke(null, [input]));
    }

    private static string BuildWordExtractionPrompt(
        string input,
        string knownLanguage,
        string targetLanguage)
    {
        var service = new AiWordGenerationService(
            Options.Create(new GeminiOptions()),
            NullLogger<AiWordGenerationService>.Instance);
        var method = typeof(AiWordGenerationService).GetMethod(
            "BuildWordExtractionPrompt",
            BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(string), typeof(string), typeof(string)]);
        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(service, [
            input,
            knownLanguage,
            targetLanguage
        ]));
    }

    private static bool ShouldExpandCoverage(int candidateCount, int generatedCount)
    {
        var method = typeof(AiWordGenerationService).GetMethod(
            "ShouldExpandCoverage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<bool>(method.Invoke(null, [candidateCount, generatedCount]));
    }

    private static string BuildCoverageExpansionPrompt(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> missingCandidates)
    {
        var method = typeof(AiWordGenerationService).GetMethod(
            "BuildCoverageExpansionPrompt",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(null, [
            input,
            knownLanguage,
            targetLanguage,
            missingCandidates
        ]));
    }

    private static IReadOnlyList<string> SelectMissingCoverageCandidates(
        IReadOnlyList<string> candidateWords,
        IReadOnlyDictionary<string, GeneratedWord> generatedWords)
    {
        var method = typeof(AiWordGenerationService).GetMethod(
            "SelectMissingCoverageCandidates",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsAssignableFrom<IReadOnlyList<string>>(method.Invoke(null, [
            candidateWords,
            generatedWords
        ]));
    }

    private static string BuildWordDetailPrompt(
        string word,
        string translation,
        string knownLanguage,
        string targetLanguage)
    {
        var method = typeof(AiWordGenerationService).GetMethod(
            "BuildWordDetailPrompt",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(null, [
            word,
            translation,
            knownLanguage,
            targetLanguage
        ]));
    }
}
