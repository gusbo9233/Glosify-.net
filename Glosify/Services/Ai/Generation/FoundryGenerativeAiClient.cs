using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.ClientModel;

namespace Glosify.Services.Ai.Generation;

public sealed class FoundryGenerativeAiClient : IGenerativeAiClient
{
    private const string Provider = "foundry";
    private const int ImageTokenEstimate = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IFoundryAgentInvoker _invoker;
    private readonly FoundryGenerativeAiOptions _options;
    private readonly AiUsageOptions _usageOptions;
    private readonly IAiCreditService _credits;
    private readonly ILogger<FoundryGenerativeAiClient> _logger;

    public FoundryGenerativeAiClient(
        IFoundryAgentInvoker invoker,
        IOptions<GenerativeAiOptions> options,
        IOptions<AiUsageOptions> usageOptions,
        IAiCreditService credits,
        ILogger<FoundryGenerativeAiClient> logger)
    {
        _invoker = invoker;
        _options = options.Value.Foundry;
        _usageOptions = usageOptions.Value;
        _credits = credits;
        _logger = logger;
    }

    public async Task<T> GenerateStructuredAsync<T>(
        string prompt,
        AiUsageContext usageContext,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var deployment = string.IsNullOrWhiteSpace(model)
            ? _options.StructuredDeployment
            : model.Trim();
        var outputReserve = _usageOptions.GetOutputReserve(usageContext.Feature);
        T? result = default;
        await ExecuteChargedAsync(
            deployment,
            usageContext,
            EstimateTokens(prompt),
            outputReserve,
            token => _invoker.RunStructuredAsync<T>(
                deployment,
                "Return only a response that conforms exactly to the requested JSON schema.",
                prompt,
                outputReserve,
                token),
            cancellationToken,
            validateResponse: structuredResponse =>
            {
                if (IsRefusal(structuredResponse))
                {
                    throw new GenerativeAiStructuredOutputException(
                        "The AI service could not produce a valid structured response.");
                }

                try
                {
                    result = structuredResponse.Result;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    throw new GenerativeAiStructuredOutputException(
                        "The AI service could not produce a valid structured response.",
                        ex);
                }

                if (result is null)
                {
                    throw new GenerativeAiStructuredOutputException(
                        "The AI service could not produce a valid structured response.");
                }
            });

        return result!;
    }

    public async Task<string> ExtractTextFromImageAsync(
        byte[] imageBytes,
        string contentType,
        string prompt,
        AiUsageContext usageContext,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes.Length == 0)
        {
            return string.Empty;
        }

        var normalizedContentType = NormalizeImageContentType(contentType);
        var deployment = _options.VisionDeployment;
        var outputReserve = _usageOptions.GetOutputReserve(usageContext.Feature);
        var messages = new[]
        {
            new ChatMessage(
                ChatRole.User,
                [
                    new TextContent(prompt),
                    new DataContent(imageBytes, normalizedContentType),
                ]),
        };

        var response = await ExecuteChargedAsync(
            deployment,
            usageContext,
            EstimateTokens(prompt) + ImageTokenEstimate,
            outputReserve,
            token => _invoker.RunAsync(
                deployment,
                "Extract text from the supplied image according to the user instructions.",
                messages,
                [],
                outputReserve,
                token),
            cancellationToken);

        return response.Text?.Trim() ?? string.Empty;
    }

    public async Task<AgentTurnResult> RunAgentTurnAsync(
        AgentRequest request,
        AiUsageContext usageContext,
        CancellationToken cancellationToken = default)
    {
        var deployment = string.IsNullOrWhiteSpace(request.Model)
            ? _options.AssistantDeployment
            : request.Model.Trim();
        if (!_options.AllowedAssistantDeployments.Contains(
                deployment,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new GenerativeAiValidationException("The requested assistant model is not available.");
        }

        var messages = FoundryMessageMapper.MapHistory(request.History);
        var tools = FoundryMessageMapper.MapTools(request.Tools);
        var outputReserve = _usageOptions.GetOutputReserve(usageContext.Feature);
        var estimatedPromptTokens =
            EstimateTokens(request.SystemInstruction)
            + EstimateTokens(JsonSerializer.Serialize(request.History, JsonOptions))
            + EstimateTokens(JsonSerializer.Serialize(request.Tools, JsonOptions));

        var response = await ExecuteChargedAsync(
            deployment,
            usageContext,
            estimatedPromptTokens,
            outputReserve,
            token => _invoker.RunAsync(
                deployment,
                string.IsNullOrWhiteSpace(request.SystemInstruction)
                    ? "Help the user with their language-learning request."
                    : request.SystemInstruction,
                messages,
                tools,
                outputReserve,
                token),
            cancellationToken);

        var calls = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Select((call, index) => new AgentFunctionCall(
                call.Name,
                JsonSerializer.Serialize(call.Arguments, JsonOptions))
            {
                CallId = string.IsNullOrWhiteSpace(call.CallId)
                    ? $"foundry-call-{index}"
                    : call.CallId,
            })
            .ToArray();

        if (calls.Length > 0)
        {
            GenerativeAiTelemetry.ToolTurns.Add(
                calls.Length,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    deployment));
        }

        return new AgentTurnResult(response.Text ?? string.Empty, calls);
    }

    private async Task<TResponse> ExecuteChargedAsync<TResponse>(
        string deployment,
        AiUsageContext usageContext,
        int promptTokenEstimate,
        int outputTokenReserve,
        Func<CancellationToken, Task<TResponse>> operation,
        CancellationToken cancellationToken,
        Action<TResponse>? validateResponse = null)
        where TResponse : AgentResponse
    {
        var estimatedTokens = Math.Max(1, promptTokenEstimate) + Math.Max(0, outputTokenReserve);
        var outcome = "failure";
        var requestTags = GenerativeAiTelemetry.Tags(
            usageContext.Feature,
            Provider,
            deployment,
            "started");
        GenerativeAiTelemetry.Requests.Add(1, requestTags);

        using var activity = GenerativeAiTelemetry.ActivitySource.StartActivity(
            $"generative-ai.{usageContext.Feature}",
            ActivityKind.Client);
        activity?.SetTag("ai.feature", usageContext.Feature);
        activity?.SetTag("ai.provider", Provider);
        activity?.SetTag("ai.deployment", deployment);

        var startedAt = Stopwatch.GetTimestamp();
        AiCreditReservation reservation;
        try
        {
            reservation = await _credits.ReserveAsync(
                usageContext,
                Provider,
                deployment,
                estimatedTokens,
                cancellationToken);
            GenerativeAiTelemetry.CreditReservations.Add(
                1,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    deployment,
                    "reserved"));
        }
        catch
        {
            outcome = "reservation_failed";
            GenerativeAiTelemetry.CreditReservations.Add(
                1,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    deployment,
                    outcome));
            GenerativeAiTelemetry.Duration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    deployment,
                    outcome));
            activity?.SetTag("ai.outcome", outcome);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            var response = await operation(timeout.Token);
            validateResponse?.Invoke(response);
            var usage = ExtractUsage(response.Usage, estimatedTokens);
            try
            {
                await _credits.CommitUsageAsync(
                    reservation.ReservationId,
                    usage,
                    cancellationToken);
            }
            catch
            {
                GenerativeAiTelemetry.CreditCommits.Add(
                    1,
                    GenerativeAiTelemetry.Tags(
                        usageContext.Feature,
                        Provider,
                        deployment,
                        "commit_failed"));
                throw;
            }
            outcome = "success";
            var successTags = GenerativeAiTelemetry.Tags(
                usageContext.Feature,
                Provider,
                deployment,
                outcome);
            GenerativeAiTelemetry.CreditCommits.Add(
                1,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    deployment,
                    "committed"));
            RecordUsage(usage, successTags);
            activity?.SetTag("ai.input_tokens", usage.PromptTokens);
            activity?.SetTag("ai.output_tokens", usage.CandidateTokens);
            activity?.SetTag("ai.total_tokens", usage.TotalTokens);
            activity?.SetTag("ai.outcome", outcome);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            try
            {
                await _credits.ReleaseAsync(reservation.ReservationId, CancellationToken.None);
                GenerativeAiTelemetry.CreditReleases.Add(
                    1,
                    GenerativeAiTelemetry.Tags(
                        usageContext.Feature,
                        Provider,
                        deployment,
                        "released"));
            }
            catch (Exception releaseException)
            {
                GenerativeAiTelemetry.CreditReleases.Add(
                    1,
                    GenerativeAiTelemetry.Tags(
                        usageContext.Feature,
                        Provider,
                        deployment,
                        "release_failed"));
                _logger.LogError(
                    releaseException,
                    "Could not release a failed Microsoft Foundry credit reservation.");
            }
            var translated = TranslateException(
                ex,
                cancellationToken,
                usageContext.Feature,
                deployment);
            outcome = translated switch
            {
                OperationCanceledException => "cancelled",
                GenerativeAiTimeoutException => "timeout",
                GenerativeAiDependencyUnavailableException => "unavailable",
                GenerativeAiValidationException => "validation_error",
                GenerativeAiStructuredOutputException => "schema_error",
                _ => "upstream_failure",
            };
            if (translated is GenerativeAiUpstreamException
                or GenerativeAiDependencyUnavailableException)
            {
                GenerativeAiTelemetry.Failures.Add(
                    1,
                    GenerativeAiTelemetry.Tags(
                        usageContext.Feature,
                        Provider,
                        deployment,
                        outcome));
            }
            activity?.SetTag("ai.outcome", outcome);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw translated;
        }
        finally
        {
            GenerativeAiTelemetry.Duration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    deployment,
                    outcome));
        }
    }

    private Exception TranslateException(
        Exception exception,
        CancellationToken callerToken,
        string feature,
        string deployment)
    {
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return exception;
        }

        if (exception is OperationCanceledException
            or ClientResultException { Status: (int)HttpStatusCode.RequestTimeout })
        {
            GenerativeAiTelemetry.Timeouts.Add(
                1,
                GenerativeAiTelemetry.Tags(feature, Provider, deployment, "timeout"));
            _logger.LogWarning("Microsoft Foundry request timed out.");
            return new GenerativeAiTimeoutException(
                "The AI service timed out. Please try again.",
                exception);
        }

        if (exception is ClientResultException { Status: 429 } throttled)
        {
            GenerativeAiTelemetry.Throttles.Add(
                1,
                GenerativeAiTelemetry.Tags(feature, Provider, deployment, "throttled"));
            _logger.LogWarning("Microsoft Foundry throttled a request.");
            return new GenerativeAiDependencyUnavailableException(
                "The AI service is temporarily busy. Please try again.",
                throttled);
        }

        if (exception is ClientResultException { Status: >= 500 } unavailable
            || exception is HttpRequestException)
        {
            _logger.LogWarning("Microsoft Foundry was temporarily unavailable.");
            return new GenerativeAiDependencyUnavailableException(
                "The AI service is temporarily unavailable. Please try again.",
                exception);
        }

        if (exception is GenerativeAiUpstreamException
            or GenerativeAiValidationException
            or InsufficientAiCreditsException)
        {
            return exception;
        }

        _logger.LogWarning("Microsoft Foundry returned an unsuccessful response.");
        return new GenerativeAiUpstreamException(
            "The AI service could not complete the request. Please try again.",
            exception);
    }

    private static bool IsRefusal(AgentResponse response)
    {
        var finishReason = response.FinishReason?.ToString();
        return finishReason?.Contains("content", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string NormalizeImageContentType(string contentType) =>
        contentType.Trim().ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpeg" or "image/jpg" => "image/jpeg",
            _ => throw new GenerativeAiValidationException(
                "Only PNG and JPEG images are supported."),
        };

    private static int EstimateTokens(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : (int)Math.Ceiling(value.Length / 4.0);

    private static AiTokenUsage ExtractUsage(UsageDetails? usage, int fallbackTotalTokens)
    {
        if (usage is null)
        {
            return new AiTokenUsage(0, 0, 0, 0, fallbackTotalTokens);
        }

        var input = ToInt(usage.InputTokenCount);
        var output = ToInt(usage.OutputTokenCount);
        var reasoning = ToInt(usage.ReasoningTokenCount);
        var total = ToInt(usage.TotalTokenCount);
        if (total <= 0)
        {
            total = fallbackTotalTokens;
        }

        return new AiTokenUsage(input, output, reasoning, 0, total);
    }

    private static int ToInt(long? value) =>
        value is null ? 0 : (int)Math.Clamp(value.Value, 0, int.MaxValue);

    private static void RecordUsage(AiTokenUsage usage, in TagList tags)
    {
        GenerativeAiTelemetry.InputTokens.Add(usage.PromptTokens, tags);
        GenerativeAiTelemetry.OutputTokens.Add(usage.CandidateTokens, tags);
        GenerativeAiTelemetry.TotalTokens.Add(usage.TotalTokens, tags);
    }
}
