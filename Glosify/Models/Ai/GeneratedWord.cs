using System.Text.Json.Serialization;

namespace Glosify.Models.Ai;

public class GeneratedWord
{
    [JsonPropertyName("translation")]
    public string? Translation { get; set; }

    [JsonPropertyName("example_sentence")]
    public string? ExampleSentence { get; set; }

    [JsonPropertyName("example_sentence_translation")]
    public string? ExampleSentenceTranslation { get; set; }

    [JsonPropertyName("example_sentence_word")]
    public string? ExampleSentenceWord { get; set; }
}
