namespace Glosify.Services;

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-3.1-pro-preview";
}
