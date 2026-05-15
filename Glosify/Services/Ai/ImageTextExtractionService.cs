using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;

namespace Glosify.Services;

public sealed class ImageTextExtractionService : IImageTextExtractionService
{
    private const string SystemInstruction = """
You extract text from learner-provided photos for language study.
Return only the visible text. Preserve line breaks when useful.
Do not describe the image, add commentary, translate, summarize, or wrap the result in markdown.
""";

    private readonly string _apiKey;
    private readonly string _model;

    public ImageTextExtractionService(IOptions<GeminiOptions> options)
    {
        var settings = options.Value;
        _apiKey = settings.ApiKey;
        _model = settings.Model;
    }

    public async Task<string> ExtractTextAsync(
        Stream imageStream,
        string contentType,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        using var memory = new MemoryStream();
        await imageStream.CopyToAsync(memory, cancellationToken);
        var imageBytes = memory.ToArray();

        if (imageBytes.Length == 0)
        {
            return string.Empty;
        }

        var prompt = $"""
Extract all readable text from this image.

Context:
- The learner knows {sourceLanguage}.
- The learner is studying {targetLanguage}.
- Prefer the original visible text exactly as written, especially {targetLanguage} text.
- Ignore navigation chrome, camera UI, timestamps, and decorative labels unless they are part of the photographed material.
""";

        var client = new Client(apiKey: _apiKey);
        var response = await client.Models.GenerateContentAsync(
            model: _model,
            contents: new Content
            {
                Parts =
                [
                    new Part { Text = prompt },
                    new Part
                    {
                        InlineData = new Blob
                        {
                            Data = imageBytes,
                            MimeType = contentType
                        }
                    }
                ]
            },
            config: new GenerateContentConfig
            {
                SystemInstruction = new Content
                {
                    Parts = [new Part { Text = SystemInstruction }]
                },
                Temperature = 0.1f,
                MaxOutputTokens = 2_000
            },
            cancellationToken: cancellationToken);

        return CleanExtractedText(response.Candidates?[0].Content?.Parts?[0].Text ?? string.Empty);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException(
                "Gemini API key is not configured. Set Gemini:ApiKey via configuration, user secrets, Gemini__ApiKey, or GEMINI_API_KEY.");
        }
    }

    private static string CleanExtractedText(string text)
    {
        return text
            .Replace("```text", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "")
            .Trim();
    }
}
