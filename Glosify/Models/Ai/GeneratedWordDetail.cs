using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glosify.Models.Ai;

public class GeneratedWordDetail
{
    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement> Properties { get; set; } = [];

    [JsonPropertyName("variants")]
    public List<GeneratedWordVariant> Variants { get; set; } = [];

    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }

    [JsonPropertyName("example_sentence")]
    public string? ExampleSentence { get; set; }

    [JsonPropertyName("example_sentence_translation")]
    public string? ExampleSentenceTranslation { get; set; }
}

public class GeneratedWordVariant
{
    [JsonPropertyName("form")]
    public string? Form { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];
}
