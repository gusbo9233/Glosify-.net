using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
class GeminiCaller
{
    private string MODEL = "gemini-2.5-flash-lite";
    private string GEMINI_API_KEY = "";

    public GeminiCaller()
    {
        // Constructor code, if needed
        LoadApiKey();
    }
    
    public async Task<string> ExtractWords(string input, string knownLanguage, string targetLanguage)
    {
        string prompt = Prompts.WordExtractionPrompt(input, knownLanguage, targetLanguage);
        var client = new Client(apiKey: GEMINI_API_KEY);
        var response = await client.Models.GenerateContentAsync(
            model: MODEL,
            contents: prompt,
            config: new GenerateContentConfig { ResponseMimeType = "application/json" }
        );
        return response.Candidates?[0].Content?.Parts?[0].Text ?? string.Empty;
    }

    public bool ValidateResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var val = entry.Value;
                if (val.ValueKind != JsonValueKind.Object) return false;
                if (!val.TryGetProperty("translation", out var t) || t.ValueKind != JsonValueKind.String) return false;
                if (!val.TryGetProperty("example_sentence", out var es) || es.ValueKind != JsonValueKind.String) return false;
                if (!val.TryGetProperty("example_sentence_translation", out var est) || est.ValueKind != JsonValueKind.String) return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
    

    public void LoadApiKey()
    {
        var envFile = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env");
        if (System.IO.File.Exists(envFile))
        {
            foreach (var line in System.IO.File.ReadAllLines(envFile))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                    System.Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
            }
        }

        GEMINI_API_KEY = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
        if (string.IsNullOrEmpty(GEMINI_API_KEY))
            Console.WriteLine("API key not found. Please set the GEMINI_API_KEY environment variable.");
    }
}
