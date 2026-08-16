using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Glosify.Models.QuizImports;

public sealed class QuizJsonImportRequest
{
    [Required]
    [StringLength(65_536, MinimumLength = 1)]
    public string Json { get; set; } = string.Empty;

    public Guid? ParentCollectionId { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QuizJsonImportDocumentV1
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("source_language")]
    public string? SourceLanguage { get; set; }

    [JsonPropertyName("quizzes")]
    public List<QuizJsonImportQuizV1>? Quizzes { get; set; } = [];

    [JsonPropertyName("collections")]
    public List<QuizJsonImportCollectionV1>? Collections { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QuizJsonImportCollectionV1
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("quizzes")]
    public List<QuizJsonImportQuizV1>? Quizzes { get; set; } = [];

    [JsonPropertyName("collections")]
    public List<QuizJsonImportCollectionV1>? Collections { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QuizJsonImportQuizV1
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("source_language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceLanguage { get; set; }

    [JsonPropertyName("words")]
    public List<QuizJsonImportWordV1>? Words { get; set; } = [];

    [JsonPropertyName("sentences")]
    public List<QuizJsonImportSentenceV1>? Sentences { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QuizJsonImportWordV1
{
    [JsonPropertyName("word")]
    public string? Word { get; set; }

    [JsonPropertyName("translation")]
    public string? Translation { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QuizJsonImportSentenceV1
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("translation")]
    public string? Translation { get; set; }
}

public sealed record QuizJsonImportPreview(
    string CanonicalJson,
    bool WasAutoRepaired,
    string TargetLanguage,
    Guid? ParentCollectionId,
    QuizJsonImportTotals Totals,
    IReadOnlyList<QuizJsonImportQuizPreview> Quizzes,
    IReadOnlyList<QuizJsonImportCollectionPreview> Collections,
    IReadOnlyList<string> Warnings);

public sealed record QuizJsonImportTotals(
    int CollectionCount,
    int QuizCount,
    int WordCount,
    int SentenceCount);

public sealed record QuizJsonImportCollectionPreview(
    string Name,
    IReadOnlyList<QuizJsonImportQuizPreview> Quizzes,
    IReadOnlyList<QuizJsonImportCollectionPreview> Collections);

public sealed record QuizJsonImportQuizPreview(
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    int WordCount,
    int SentenceCount);

public sealed record QuizJsonImportResult(
    int CollectionCount,
    int QuizCount,
    int WordCount,
    int SentenceCount);

public sealed record QuizJsonImportApplyResponse(
    int CollectionCount,
    int QuizCount,
    int WordCount,
    int SentenceCount,
    string RedirectUrl);

public sealed record QuizJsonImportAiRepairEnvelope(
    [property: JsonPropertyName("repaired_json")] string RepairedJson);
