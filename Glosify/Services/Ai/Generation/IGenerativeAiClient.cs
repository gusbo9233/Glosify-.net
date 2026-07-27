namespace Glosify.Services.Ai.Generation;

public interface IGenerativeAiClient
{
    Task<T> GenerateStructuredAsync<T>(
        string prompt,
        AiUsageContext usageContext,
        string? model = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates JSON without requiring the provider's native structured-output mode.
    /// Providers whose schema mode is reliable can use the default implementation;
    /// providers with model-specific schema limitations can override it.
    /// </summary>
    Task<T> GenerateJsonAsync<T>(
        string prompt,
        AiUsageContext usageContext,
        string? model = null,
        CancellationToken cancellationToken = default) =>
        GenerateStructuredAsync<T>(prompt, usageContext, model, cancellationToken);

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
