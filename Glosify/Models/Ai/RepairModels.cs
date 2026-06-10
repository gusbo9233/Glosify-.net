using System.Text.Json.Serialization;

namespace Glosify.Models.Ai;

public sealed class RepairQuizData
{
    public RepairQuiz Quiz { get; set; } = new();
    public List<RepairWord> Words { get; set; } = [];
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
    [JsonPropertyName("word")]
    public string Word { get; set; } = string.Empty;
    [JsonPropertyName("lemma")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyLemma { get; set; }
    [JsonIgnore]
    public string Lemma
    {
        get => string.IsNullOrWhiteSpace(Word) ? LegacyLemma ?? string.Empty : Word;
        set => Word = value;
    }
    public string Translation { get; set; } = string.Empty;
    [JsonPropertyName("quiz_id")]
    public string QuizId { get; set; } = string.Empty;
}

public sealed class RepairSentence
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    [JsonPropertyName("quiz_id")]
    public string QuizId { get; set; } = string.Empty;
}

public sealed class RepairWordResult
{
    public RepairWord Word { get; set; } = new();
}

public sealed class RepairSentenceResult
{
    public RepairSentence Sentence { get; set; } = new();
}
