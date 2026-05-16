using System.Reflection;
using Glosify.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public class SimpleAiWordGenerationServiceTests
{
    [Fact]
    public void BuildSimpleVocabularyPrompt_AsksForExistingJsonFormatWithoutNoise()
    {
        var prompt = BuildSimpleVocabularyPrompt(
            "Czy mówisz po angielsku?",
            "English",
            "Polish",
            ["Czy mówisz po angielsku?"]);

        Assert.Contains("\"translation\"", prompt);
        Assert.Contains("\"example_sentence\"", prompt);
        Assert.Contains("\"example_sentence_translation\"", prompt);
        Assert.Contains("Use plain words and plain sentences", prompt);
        Assert.Contains("Do not include punctuation, brackets, parentheses, slashes", prompt);
        Assert.Contains("Do not add explanations or fields outside", prompt);
    }

    [Fact]
    public void ValidateResponse_AcceptsExistingJsonFormatAndRejectsNoisyKeys()
    {
        var service = CreateService();

        var cleanJson = """
        {
          "mówisz": {
            "translation": "you speak",
            "example_sentence": "Czy mówisz po angielsku?",
            "example_sentence_translation": "Do you speak English?"
          }
        }
        """;

        var noisyJson = """
        {
          "(mówisz)": {
            "translation": "you speak",
            "example_sentence": "Czy (mówisz) po angielsku?",
            "example_sentence_translation": "Do you speak English?"
          }
        }
        """;

        Assert.True(service.ValidateResponse(cleanJson));
        Assert.False(service.ValidateResponse(noisyJson));
    }

    private static SimpleAiWordGenerationService CreateService()
    {
        var detailedService = new AiWordGenerationService(
            Options.Create(new GeminiOptions()),
            NullLogger<AiWordGenerationService>.Instance);

        return new SimpleAiWordGenerationService(
            Options.Create(new GeminiOptions()),
            detailedService,
            NullLogger<SimpleAiWordGenerationService>.Instance);
    }

    private static string BuildSimpleVocabularyPrompt(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences)
    {
        var method = typeof(SimpleAiWordGenerationService).GetMethod(
            "BuildSimpleVocabularyPrompt",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(null, [
            input,
            knownLanguage,
            targetLanguage,
            sourceSentences
        ]));
    }
}
