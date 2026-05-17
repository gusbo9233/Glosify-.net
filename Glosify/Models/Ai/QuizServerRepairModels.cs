using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glosify.Models.Ai;

public sealed class QuizServerRepairQuizData
{
    public QuizServerRepairQuiz Quiz { get; set; } = new();
    public List<QuizServerRepairWord> Words { get; set; } = [];
    [JsonPropertyName("word_details")]
    public List<QuizServerRepairWordDetail> WordDetails { get; set; } = [];
    public List<QuizServerRepairSentence> Sentences { get; set; } = [];
}

public sealed class QuizServerRepairQuiz
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("source_language")]
    public string SourceLanguage { get; set; } = string.Empty;
    [JsonPropertyName("target_language")]
    public string TargetLanguage { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    [JsonPropertyName("processing_status")]
    public string ProcessingStatus { get; set; } = "completed";
    [JsonPropertyName("processing_message")]
    public string? ProcessingMessage { get; set; }
}

public sealed class QuizServerRepairWord
{
    public string Id { get; set; } = string.Empty;
    public string Lemma { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    [JsonPropertyName("word_detail_id")]
    public string WordDetailId { get; set; } = string.Empty;
    [JsonPropertyName("quiz_id")]
    public string QuizId { get; set; } = string.Empty;
}

public sealed class QuizServerRepairWordDetail
{
    public string Id { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Properties { get; set; } = [];
    [JsonPropertyName("example_sentence")]
    public string ExampleSentence { get; set; } = string.Empty;
    [JsonPropertyName("example_sentence_translation")]
    public string ExampleSentenceTranslation { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public List<GeneratedWordVariant> Variants { get; set; } = [];
    public string Language { get; set; } = string.Empty;
}

public sealed class QuizServerRepairSentence
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    [JsonPropertyName("quiz_id")]
    public string QuizId { get; set; } = string.Empty;
}

public sealed class QuizServerRepairQuizResult
{
    [JsonPropertyName("quiz_data")]
    public QuizServerRepairQuizData QuizData { get; set; } = new();
}

public sealed class QuizServerRepairWordResult
{
    public QuizServerRepairWord Word { get; set; } = new();
    [JsonPropertyName("word_detail")]
    public QuizServerRepairWordDetail WordDetail { get; set; } = new();
}

public sealed class QuizServerRepairSentenceResult
{
    public QuizServerRepairSentence Sentence { get; set; } = new();
}
