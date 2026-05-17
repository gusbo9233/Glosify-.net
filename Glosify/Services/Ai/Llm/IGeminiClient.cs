namespace Glosify.Services;

public interface IGeminiClient
{
    Task<string> GenerateJsonAsync(
        string prompt,
        string? model = null,
        CancellationToken cancellationToken = default);

    Task<string> ExtractTextFromImageAsync(
        byte[] imageBytes,
        string contentType,
        string prompt,
        CancellationToken cancellationToken = default);

    Task<AgentTurnResult> RunAgentTurnAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default);
}
