using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Glosify.Services;

public sealed class ImageTextExtractionService : IImageTextExtractionService
{
    private readonly HttpClient _httpClient;

    public ImageTextExtractionService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> ExtractTextAsync(
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

        var request = new ImageTextExtractionRequest(
            Convert.ToBase64String(imageBytes),
            contentType,
            sourceLanguage,
            targetLanguage);
        using var response = await _httpClient.PostAsJsonAsync("images/extract-text", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Quiz server image text extraction failed with HTTP {(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync<ImageTextExtractionResponse>(cancellationToken);
        return CleanExtractedText(result?.Text ?? string.Empty);
    }

    private static string CleanExtractedText(string text)
    {
        return text
            .Replace("```text", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "")
            .Trim();
    }

    private sealed record ImageTextExtractionRequest(
        [property: JsonPropertyName("image_base64")] string ImageBase64,
        [property: JsonPropertyName("content_type")] string ContentType,
        [property: JsonPropertyName("source_language")] string SourceLanguage,
        [property: JsonPropertyName("target_language")] string TargetLanguage);

    private sealed record ImageTextExtractionResponse(string Text);
}
