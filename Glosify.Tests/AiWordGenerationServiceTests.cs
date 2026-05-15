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
