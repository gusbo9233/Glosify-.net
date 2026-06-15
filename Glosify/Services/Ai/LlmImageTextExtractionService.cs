namespace Glosify.Services;

public sealed class LlmImageTextExtractionService : IImageTextExtractionService
{
    private readonly IGeminiClient _gemini;

    public LlmImageTextExtractionService(IGeminiClient gemini)
    {
        _gemini = gemini;
    }

    public async Task<string> ExtractTextAsync(
        string userId,
        Stream imageStream,
        string contentType,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream();
        await imageStream.CopyToAsync(memory, cancellationToken);
        var imageBytes = memory.ToArray();
        if (imageBytes.Length == 0)
        {
            return string.Empty;
        }

        var prompt = BuildPrompt(sourceLanguage, targetLanguage);
        var extracted = await _gemini.ExtractTextFromImageAsync(
            imageBytes,
            contentType,
            prompt,
            new AiUsageContext(
                userId,
                AiUsageFeatures.ImageExtraction,
                "extract_text_from_image",
                Guid.NewGuid()),
            cancellationToken);

        return CleanExtractedText(extracted);
    }

    private static string BuildPrompt(string sourceLanguage, string targetLanguage)
    {
        return $$"""
        Extract every word of readable text from this image. The text is likely in {{targetLanguage}}, possibly with some {{sourceLanguage}}.

        Rules:
        - Preserve the original line breaks and reading order.
        - Do not translate. Do not summarize. Do not add commentary.
        - Do not wrap the output in markdown fences or code blocks.
        - If the image contains no readable text, return an empty string.
        """;
    }

    private static string CleanExtractedText(string text)
    {
        return text
            .Replace("```text", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "")
            .Trim();
    }
}
