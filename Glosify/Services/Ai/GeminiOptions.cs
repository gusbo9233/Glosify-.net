namespace Glosify.Services;

public sealed class GeminiOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gemini-2.5-flash-lite";
}
