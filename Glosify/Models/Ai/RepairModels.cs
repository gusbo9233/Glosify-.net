using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glosify.Models.Ai;

public sealed class RepairQuizData
{
    public RepairQuiz Quiz { get; set; } = new();
    public List<RepairWord> Words { get; set; } = [];
    [JsonPropertyName("word_details")]
    public List<RepairWordDetail> WordDetails { get; set; } = [];
    public List<RepairSentence> Sentences { get; set; } = [];
}

public sealed class RepairQuiz
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

public sealed class RepairWord
{
    public string Id { get; set; } = string.Empty;
    public string Lemma { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    [JsonPropertyName("word_detail_id")]
    public string WordDetailId { get; set; } = string.Empty;
    [JsonPropertyName("quiz_id")]
    public string QuizId { get; set; } = string.Empty;
}

public sealed class RepairWordDetail
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

public sealed class RepairSentence
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    [JsonPropertyName("quiz_id")]
    public string QuizId { get; set; } = string.Empty;
}

public sealed class RepairQuizResult
{
    [JsonPropertyName("quiz_data")]
    public RepairQuizData QuizData { get; set; } = new();
}

public sealed class RepairWordResult
{
    public RepairWord Word { get; set; } = new();
    [JsonPropertyName("word_detail")]
    public RepairWordDetail WordDetail { get; set; } = new();
}

public sealed class RepairSentenceResult
{
    public RepairSentence Sentence { get; set; } = new();
}
