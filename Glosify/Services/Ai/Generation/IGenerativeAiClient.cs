namespace Glosify.Services.Ai.Generation;

public interface IGenerativeAiClient
{
    Task<T> GenerateStructuredAsync<T>(
        string prompt,
        AiUsageContext usageContext,
        string? model = null,
        CancellationToken cancellationToken = default);

    Task<string> ExtractTextFromImageAsync(
        byte[] imageBytes,
        string contentType,
        string prompt,
        AiUsageContext usageContext,
        CancellationToken cancellationToken = default);

    Task<AgentTurnResult> RunAgentTurnAsync(
        AgentRequest request,
        AiUsageContext usageContext,
        CancellationToken cancellationToken = default);
}
