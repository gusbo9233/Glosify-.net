#pragma warning disable OPENAI001

using System.ClientModel;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

namespace Glosify.Services.Ai.Generation;

public interface IOpenAiResponsesTransport
{
    Task<OpenAiResponseEnvelope> CreateResponseAsync(
        CreateResponseOptions request,
        CancellationToken cancellationToken);
}

public sealed record OpenAiResponseEnvelope(
    string Text,
    IReadOnlyList<OpenAiFunctionCall> FunctionCalls,
    string? ResponseId,
    string Model,
    AiTokenUsage Usage,
    bool IsIncomplete,
    bool IsRefusal);

public sealed record OpenAiFunctionCall(
    string CallId,
    string Name,
    string ArgumentsJson);

public sealed class OpenAiTransportException(
    int statusCode,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class OpenAiResponsesTransport : IOpenAiResponsesTransport
{
    private readonly GenerativeAiOptions _options;
    private readonly Lazy<ResponsesClient> _client;

    public OpenAiResponsesTransport(IOptions<GenerativeAiOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<ResponsesClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<OpenAiResponseEnvelope> CreateResponseAsync(
        CreateResponseOptions request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = (await _client.Value.CreateResponseAsync(request, cancellationToken)).Value;
            var calls = response.OutputItems
                .OfType<FunctionCallResponseItem>()
                .Select(call => new OpenAiFunctionCall(
                    call.CallId,
                    call.FunctionName,
                    call.FunctionArguments.ToString()))
                .ToArray();
            var usage = response.Usage is null
                ? new AiTokenUsage(0, 0, 0, 0, 0)
                : new AiTokenUsage(
                    ToInt(response.Usage.InputTokenCount),
                    ToInt(response.Usage.OutputTokenCount),
                    ToInt(response.Usage.OutputTokenDetails?.ReasoningTokenCount),
                    0,
                    ToInt(response.Usage.TotalTokenCount));
            var status = response.Status?.ToString() ?? string.Empty;
            var text = response.GetOutputText() ?? string.Empty;
            var hasRefusal = response.OutputItems
                .OfType<MessageResponseItem>()
                .SelectMany(message => message.Content)
                .Any(content => content.Kind == ResponseContentPartKind.Refusal
                    || !string.IsNullOrWhiteSpace(content.Refusal));

            return new OpenAiResponseEnvelope(
                text,
                calls,
                response.Id,
                string.IsNullOrWhiteSpace(response.Model) ? OpenAiModels.Luna : response.Model,
                usage,
                response.IncompleteStatusDetails is not null
                    || status.Contains("incomplete", StringComparison.OrdinalIgnoreCase),
                hasRefusal
                    || response.Error is not null
                    || (string.IsNullOrWhiteSpace(text)
                        && calls.Length == 0
                        && !status.Contains("completed", StringComparison.OrdinalIgnoreCase)));
        }
        catch (ClientResultException ex)
        {
            throw new OpenAiTransportException(
                ex.Status,
                "The OpenAI Responses API returned an unsuccessful status.",
                ex);
        }
    }

    private ResponsesClient CreateClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new GenerativeAiValidationException(
                "The OpenAI API key is not configured. Set OPENAI_SECRET_KEY.");
        }

        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        };
        return new ResponsesClient(
            new ApiKeyCredential(_options.ApiKey.Trim()),
            clientOptions);
    }

    private static int ToInt(int? value) => Math.Clamp(value ?? 0, 0, int.MaxValue);
}

#pragma warning restore OPENAI001
