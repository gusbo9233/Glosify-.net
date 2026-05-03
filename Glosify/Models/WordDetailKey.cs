using System.Security.Cryptography;
using System.Text;

namespace Glosify.Models;

public sealed record WordDetailKey(
    string SourceLanguage,
    string TargetLanguage,
    string Word,
    string Translation,
    string NormalizedWord,
    string NormalizedTranslation,
    string NormalizedWordHash,
    string NormalizedTranslationHash)
{
    public string Id => string.Join(
        ":",
        "wd",
        Hash(Normalize(SourceLanguage)),
        Hash(Normalize(TargetLanguage)),
        NormalizedWordHash,
        NormalizedTranslationHash);

    public static WordDetailKey Create(
        string sourceLanguage,
        string targetLanguage,
        string word,
        string translation)
    {
        var normalizedWord = Normalize(word);
        var normalizedTranslation = Normalize(translation);

        return new WordDetailKey(
            sourceLanguage.Trim(),
            targetLanguage.Trim(),
            word.Trim(),
            translation.Trim(),
            normalizedWord,
            normalizedTranslation,
            Hash(normalizedWord),
            Hash(normalizedTranslation));
    }

    public static string Normalize(string value)
    {
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
