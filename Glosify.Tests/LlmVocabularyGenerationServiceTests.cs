using Glosify.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glosify.Tests;

public class LlmVocabularyGenerationServiceTests
{
    [Fact]
    public async Task GenerateWordsFromTextAsync_ParsesWordsAndStandaloneSentences()
    {
        var gemini = new FakeGeminiClient
        {
            JsonResponse = """
            {
              "words": [
                { "word": "Haus", "translation": "house" },
                { "word": "Garten", "translation": "garden" }
              ],
              "sentences": [
                { "text": "Das Haus ist alt.", "translation": "The house is old." },
                { "text": "Der Garten ist schoen.", "translation": "The garden is pretty." }
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

        Assert.Equal(2, result.Words.Count);
        Assert.Equal("house", result.Words["Haus"].Translation);
        Assert.Equal(2, result.Sentences.Count);
        Assert.Contains(result.Sentences, sentence =>
            sentence.Text == "Das Haus ist alt."
            && sentence.Translation == "The house is old.");
        Assert.Contains(result.Sentences, sentence => sentence.Text == "Der Garten ist schoen.");
    }

    [Fact]
    public async Task GenerateWordsFromTextAsync_PrefersSurfaceWordOverLegacyLemma()
    {
        var gemini = new FakeGeminiClient
        {
            JsonResponse = """
            {
              "words": [
                { "word": "mowi", "lemma": "mowic", "translation": "speaks" }
              ],
              "sentences": [
                { "text": "Ona mowi po polsku.", "translation": "She speaks Polish." }
              ]
            }
            """
        };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        var result = await service.GenerateWordsFromTextAsync("Ona mowi po polsku.", "English", "Polish", null);

        Assert.True(result.Words.ContainsKey("mowi"));
        Assert.False(result.Words.ContainsKey("mowic"));
    }

    [Fact]
    public async Task GenerateWordsFromTextAsync_PreservesLegacyWordScopedSentence()
    {
        var gemini = new FakeGeminiClient
        {
            JsonResponse = """
            {
              "words": [
                {
                  "lemma": "mowic",
                  "translation": "to speak",
                  "example_sentence": "Ona mowi po polsku.",
                  "example_sentence_translation": "She speaks Polish.",
                  "example_sentence_word": "mowi"
                }
              ],
              "sentences": [
                { "text": "Mowic jest wazne.", "translation": "Speaking is important." }
              ]
            }
            """
        };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        var result = await service.GenerateWordsFromTextAsync("Ona mowi po polsku.", "English", "Polish", null);

        Assert.Single(result.Words);
        Assert.Equal("to speak", result.Words["mowic"].Translation);
        Assert.Contains(result.Sentences, sentence =>
            sentence.Text == "Ona mowi po polsku."
            && sentence.Translation == "She speaks Polish.");
    }

    [Fact]
    public async Task GenerateWordsFromTextAsync_AsksForStandaloneQuizSentences()
    {
        var gemini = new FakeGeminiClient { JsonResponse = """{ "words": [{ "lemma": "Hund", "translation": "dog" }], "sentences": [] }""" };
        var service = new LlmVocabularyGenerationService(gemini, NullLogger<LlmVocabularyGenerationService>.Instance);

        await service.GenerateWordsFromTextAsync("Der Hund schlaeft.", "English", "German", null);

        Assert.Contains("\"sentences\"", gemini.LastPrompt);
        Assert.Contains("\"word\"", gemini.LastPrompt);
        Assert.DoesNotContain("\"lemma\"", gemini.LastPrompt);
        Assert.Contains("Prefer the surface form from the input text", gemini.LastPrompt);
        Assert.Contains("Do not convert input words to dictionary/base/infinitive forms", gemini.LastPrompt);
        Assert.Contains("standalone quiz sentences separately from words", gemini.LastPrompt);
        Assert.Contains("fewer standalone quiz sentences than words", gemini.LastPrompt);
        Assert.Contains("Do not write dictionary-style word-detail sentences", gemini.LastPrompt);
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

        Assert.Single(result.Words);
        Assert.Equal("dog", result.Words["Hund"].Translation);
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
