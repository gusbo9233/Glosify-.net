namespace Glosify.Services;

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-3-flash-preview";
    public string StructuredModel { get; set; } = "gemini-3.1-pro-preview";
    public string VisionModel { get; set; } = "gemini-3-flash-preview";
    public int TimeoutSeconds { get; set; } = 180;
}
