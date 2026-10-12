#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
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
    bool IsRefusal)
{
    public IReadOnlyList<string> OutputItemsJson { get; init; } = [];
}

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
    public TimeSpan? RetryAfter { get; init; }
}

public sealed class OpenAiResponsesTransport : IOpenAiResponsesTransport
{
    private readonly GenerativeAiOptions _options;
    private readonly Lazy<ResponsesClient> _client;
    private readonly Lazy<ResponsesClient> _durableClient;

    public OpenAiResponsesTransport(IOptions<GenerativeAiOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<ResponsesClient>(() => CreateClient(false), LazyThreadSafetyMode.ExecutionAndPublication);
        _durableClient = new Lazy<ResponsesClient>(() => CreateClient(true), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<OpenAiResponseEnvelope> CreateResponseAsync(
        CreateResponseOptions request,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = request.Metadata.TryGetValue("assistant_runtime", out var runtime) && runtime == "durable" ? _durableClient : _client;
            var response = (await client.Value.CreateResponseAsync(request, cancellationToken)).Value;
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
                    ToInt(response.Usage.TotalTokenCount),
                    ToInt(response.Usage.InputTokenDetails?.CachedTokenCount));
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
                    && !status.Contains("completed", StringComparison.OrdinalIgnoreCase)))
            {
                // With store=false, callers must replay every output item on the next
                // request. This includes encrypted reasoning items as well as messages
                // and function calls, in the exact order returned by the API.
                OutputItemsJson = response.OutputItems
                    .Select(item => ModelReaderWriter.Write(item).ToString())
                    .ToArray(),
            };
        }
        catch (ClientResultException ex)
        {
            TimeSpan? retryAfter = null;
            if (ex.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var header) == true)
            {
                if (int.TryParse(header, out var seconds)) retryAfter = TimeSpan.FromSeconds(Math.Max(0, seconds));
                else if (DateTimeOffset.TryParse(header, out var date)) retryAfter = date - DateTimeOffset.UtcNow;
            }
            throw new OpenAiTransportException(
                ex.Status,
                "The OpenAI Responses API returned an unsuccessful status.",
                ex) { RetryAfter = retryAfter };
        }
    }

    private ResponsesClient CreateClient(bool durable)
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
        if (durable) clientOptions.RetryPolicy = new ClientRetryPolicy(0);
        return new ResponsesClient(
            new ApiKeyCredential(_options.ApiKey.Trim()),
            clientOptions);
    }

    private static int ToInt(int? value) => Math.Clamp(value ?? 0, 0, int.MaxValue);
}

#pragma warning restore OPENAI001
