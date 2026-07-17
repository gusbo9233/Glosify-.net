using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Xunit;

namespace Glosify.Tests;

public sealed class VocabularyGenerationServiceTests
{
    [Fact]
    public async Task Repair_word_uses_typed_generation_and_accepts_semantically_valid_output()
    {
        var quizData = QuizData();
        var expected = new RepairWordResult
        {
            Word = new RepairWord
            {
                Id = "word-1",
                Word = "dom",
                Translation = "house",
                QuizId = quizData.Quiz.Id,
            },
        };
        var client = new TypedClient(expected);
        var service = new LlmVocabularyGenerationService(client);

        var result = await service.RepairWordAsync(
            quizData,
            "word-1",
            "user-1");

        Assert.Same(expected, result);
        Assert.Equal(typeof(RepairWordResult), client.RequestedType);
        Assert.Equal(AiUsageFeatures.Repair, client.UsageContext?.Feature);
    }

    [Theory]
    [InlineData("wrong-id", "dom", "house", "quiz-1")]
    [InlineData("word-1", "", "house", "quiz-1")]
    [InlineData("word-1", "dom", "", "quiz-1")]
    [InlineData("word-1", "dom", "house", "wrong-quiz")]
    public async Task Repair_word_rejects_semantically_invalid_typed_output(
        string wordId,
        string word,
        string translation,
        string quizId)
    {
        var service = new LlmVocabularyGenerationService(new TypedClient(
            new RepairWordResult
            {
                Word = new RepairWord
                {
                    Id = wordId,
                    Word = word,
                    Translation = translation,
                    QuizId = quizId,
                },
            }));

        var result = await service.RepairWordAsync(
            QuizData(),
            "word-1",
            "user-1");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("", "A house.", "quiz-1")]
    [InlineData("To jest dom.", "", "quiz-1")]
    [InlineData("To jest dom.", "A house.", "wrong-quiz")]
    public async Task Repair_sentence_rejects_semantically_invalid_typed_output(
        string text,
        string translation,
        string quizId)
    {
        var service = new LlmVocabularyGenerationService(new TypedClient(
            new RepairSentenceResult
            {
                Sentence = new RepairSentence
                {
                    Id = "sentence-1",
                    Text = text,
                    Translation = translation,
                    QuizId = quizId,
                },
            }));

        var result = await service.RepairSentenceAsync(
            QuizData(),
            "To jest dom.",
            "user-1");

        Assert.Null(result);
    }

    private static RepairQuizData QuizData() => new()
    {
        Quiz = new RepairQuiz
        {
            Id = "quiz-1",
            SourceLanguage = "English",
            TargetLanguage = "Polish",
        },
    };

    private sealed class TypedClient(object result) : IGenerativeAiClient
    {
        public Type? RequestedType { get; private set; }
        public AiUsageContext? UsageContext { get; private set; }

        public Task<T> GenerateStructuredAsync<T>(
            string prompt,
            AiUsageContext usageContext,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            RequestedType = typeof(T);
            UsageContext = usageContext;
            return Task.FromResult((T)result);
        }

        public Task<string> ExtractTextFromImageAsync(
            byte[] imageBytes,
            string contentType,
            string prompt,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new NotSupportedException());

        public Task<AgentTurnResult> RunAgentTurnAsync(
            AgentRequest request,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentTurnResult>(new NotSupportedException());
    }
}
