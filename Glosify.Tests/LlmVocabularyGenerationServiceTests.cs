using Glosify.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glosify.Tests;

public class LlmVocabularyGenerationServiceTests
{
    [Fact]
    public async Task GenerateWordsFromTextAsync_ParsesWordsAndAttachesSentences()
    {
        var gemini = new FakeGeminiClient
        {
            JsonResponse = """
            {
              "words": [
                { "lemma": "Haus", "translation": "house" },
                { "lemma": "Garten", "translation": "garden" }
              ],
              "sentences": [
                { "text": "Das Haus ist alt.", "translation": "The house is old." },
                { "text": "Der Garten ist schön.", "translation": "The garden is pretty." }
              ]
            }
            """
        };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        var result = await service.GenerateWordsFromTextAsync(
            input: "Das Haus und der Garten.",
            sourceLanguage: "English",
            targetLanguage: "German",
            quizName: null);

        Assert.Equal(2, result.Count);
        Assert.Equal("house", result["Haus"].Translation);
        Assert.Equal("Das Haus ist alt.", result["Haus"].ExampleSentence);
        Assert.Equal("The house is old.", result["Haus"].ExampleSentenceTranslation);
        Assert.Equal("Der Garten ist schön.", result["Garten"].ExampleSentence);
    }

    [Fact]
    public async Task GenerateWordsFromTextAsync_PrefersWordScopedSentenceForInflectedUsage()
    {
        var gemini = new FakeGeminiClient
        {
            JsonResponse = """
            {
              "words": [
                {
                  "lemma": "mówić",
                  "translation": "to speak",
                  "example_sentence": "Ona mówi po polsku.",
                  "example_sentence_translation": "She speaks Polish.",
                  "example_sentence_word": "mówi"
                }
              ],
              "sentences": [
                { "text": "Mówić jest ważne.", "translation": "Speaking is important." }
              ]
            }
            """
        };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        var result = await service.GenerateWordsFromTextAsync("Ona mówi po polsku.", "English", "Polish", null);

        Assert.Single(result);
        Assert.Equal("Ona mówi po polsku.", result["mówić"].ExampleSentence);
        Assert.Equal("She speaks Polish.", result["mówić"].ExampleSentenceTranslation);
        Assert.Equal("mówi", result["mówić"].ExampleSentenceWord);
    }

    [Fact]
    public async Task GenerateWordsFromTextAsync_AsksForWordScopedExampleSentences()
    {
        var gemini = new FakeGeminiClient { JsonResponse = """{ "words": [{ "lemma": "Hund", "translation": "dog" }], "sentences": [] }""" };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        await service.GenerateWordsFromTextAsync("Der Hund schläft.", "English", "German", null);

        Assert.Contains("\"example_sentence\"", gemini.LastPrompt);
        Assert.Contains("\"example_sentence_word\"", gemini.LastPrompt);
        Assert.Contains("uses that lemma or a natural inflected form", gemini.LastPrompt);
        Assert.Contains("no pronunciation hints", gemini.LastPrompt);
    }

    [Fact]
    public async Task GenerateWordsFromTextAsync_StripsMarkdownFences()
    {
        var gemini = new FakeGeminiClient
        {
            JsonResponse = """
            ```json
            {
              "words": [{ "lemma": "Hund", "translation": "dog" }],
              "sentences": []
            }
            ```
            """
        };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        var result = await service.GenerateWordsFromTextAsync("Der Hund.", "English", "German", null);

        Assert.Single(result);
        Assert.Equal("dog", result["Hund"].Translation);
    }

    [Fact]
    public async Task GenerateWordsFromTextAsync_ThrowsWhenNoWordsReturned()
    {
        var gemini = new FakeGeminiClient { JsonResponse = """{ "words": [], "sentences": [] }""" };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateWordsFromTextAsync("ein", "English", "German", null));
    }

    [Fact]
    public async Task GenerateWordDetailAsync_ParsesAllFields()
    {
        var gemini = new FakeGeminiClient
        {
            JsonResponse = """
            {
              "properties": { "pos": "noun", "gender": "neuter" },
              "variants": [{ "form": "Hauses", "tags": ["genitive", "singular"] }],
              "explanation": "Building where someone lives.",
              "example_sentence": "Das Haus ist alt.",
              "example_sentence_translation": "The house is old."
            }
            """
        };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        var detail = await service.GenerateWordDetailAsync("Haus", "house", "English", "German");

        Assert.NotNull(detail);
        Assert.Equal("Building where someone lives.", detail!.Explanation);
        Assert.Equal("Das Haus ist alt.", detail.ExampleSentence);
        Assert.Equal("The house is old.", detail.ExampleSentenceTranslation);
        var variant = Assert.Single(detail.Variants);
        Assert.Equal("Hauses", variant.Form);
        Assert.Contains("genitive", variant.Tags);
        Assert.Contains("singular", variant.Tags);
    }

    private sealed class FakeGeminiClient : IGeminiClient
    {
        public string JsonResponse { get; set; } = string.Empty;
        public string ImageResponse { get; set; } = string.Empty;
        public string LastPrompt { get; private set; } = string.Empty;

        public Task<string> GenerateJsonAsync(string prompt, string? model = null, CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(JsonResponse);
        }

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(ImageResponse);

        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentTurnResult(string.Empty, []));
    }
}
