namespace Glosify.Models.Ai;

public sealed record GeneratedVocabularyBatch(
    IReadOnlyDictionary<string, GeneratedWord> Words,
    IReadOnlyList<GeneratedSentence> Sentences);

public sealed record GeneratedSentence
{
    public string Text { get; init; } = string.Empty;
    public string Translation { get; init; } = string.Empty;
}
