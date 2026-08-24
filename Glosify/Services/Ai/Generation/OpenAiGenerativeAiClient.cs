#pragma warning disable OPENAI001

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

namespace Glosify.Services.Ai.Generation;

public sealed class OpenAiGenerativeAiClient : IGenerativeAiClient
{
    private const string Provider = AiUsageProviders.OpenAi;
    private const int ImageTokenEstimate = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AIJsonSchemaCreateOptions StrictSchemaOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            DisallowAdditionalProperties = true,
            RequireAllProperties = true,
        },
    };

    private readonly IOpenAiResponsesTransport _transport;
    private readonly GenerativeAiOptions _options;
    private readonly AiUsageOptions _usageOptions;
    private readonly IAiCreditService _credits;
    private readonly ILogger<OpenAiGenerativeAiClient> _logger;

    public OpenAiGenerativeAiClient(
        IOpenAiResponsesTransport transport,
        IOptions<GenerativeAiOptions> options,
        IOptions<AiUsageOptions> usageOptions,
        IAiCreditService credits,
        ILogger<OpenAiGenerativeAiClient> logger)
    {
        _transport = transport;
        _options = options.Value;
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
        ValidateModel(model);
        var outputReserve = _usageOptions.GetOutputReserve(usageContext.Feature);
        var request = CreateRequest(usageContext, outputReserve);
        request.Instructions =
            "Return only a response that conforms exactly to the requested JSON schema.";
        request.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));
        var schema = AIJsonUtilities.CreateJsonSchema(
            typeof(T),
            serializerOptions: JsonOptions,
            inferenceOptions: StrictSchemaOptions);
        request.TextOptions = new ResponseTextOptions
        {
            TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                typeof(T).Name,
                BinaryData.FromString(schema.GetRawText()),
                null,
                true),
        };

        var response = await ExecuteChargedAsync(
            usageContext,
            EstimateTokens(prompt),
            outputReserve,
            request,
            cancellationToken);
        ValidateStructuredResponse(response);

        try
        {
            return JsonSerializer.Deserialize<T>(response.Text, JsonOptions)
                ?? throw new GenerativeAiStructuredOutputException(
                    "The AI service could not produce a valid structured response.");
        }
        catch (JsonException ex)
        {
            throw new GenerativeAiStructuredOutputException(
                "The AI service could not produce a valid structured response.",
                ex);
        }
    }

    public async Task<T> GenerateJsonAsync<T>(
        string prompt,
        AiUsageContext usageContext,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        ValidateModel(model);
        var outputReserve = _usageOptions.GetOutputReserve(usageContext.Feature);
        var request = CreateRequest(usageContext, outputReserve);
        request.Instructions =
            "Return only valid JSON matching the object shape requested by the user. "
            + "Do not use markdown fences or explanatory text.";
        request.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));
        request.TextOptions = new ResponseTextOptions
        {
            TextFormat = ResponseTextFormat.CreateJsonObjectFormat(),
        };

        var response = await ExecuteChargedAsync(
            usageContext,
            EstimateTokens(prompt),
            outputReserve,
            request,
            cancellationToken);
        ValidateStructuredResponse(response);

        try
        {
            return JsonSerializer.Deserialize<T>(StripJsonFences(response.Text), JsonOptions)
                ?? throw new GenerativeAiStructuredOutputException(
                    "The AI service could not produce a valid JSON response.");
        }
        catch (JsonException ex)
        {
            throw new GenerativeAiStructuredOutputException(
                "The AI service could not produce a valid JSON response.",
                ex);
        }
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
        var outputReserve = _usageOptions.GetOutputReserve(usageContext.Feature);
        var request = CreateRequest(usageContext, outputReserve);
        request.Instructions =
            "Extract text from the supplied image according to the user instructions.";
        request.InputItems.Add(ResponseItem.CreateUserMessageItem(
        [
            ResponseContentPart.CreateInputTextPart(prompt),
            ResponseContentPart.CreateInputImagePart(
                BinaryData.FromBytes(imageBytes, normalizedContentType),
                ResponseImageDetailLevel.Auto),
        ]));

        var response = await ExecuteChargedAsync(
            usageContext,
            EstimateTokens(prompt) + ImageTokenEstimate,
            outputReserve,
            request,
            cancellationToken);
        ValidateTextResponse(response);
        return response.Text.Trim();
    }

    public async Task<AgentTurnResult> RunAgentTurnAsync(
        AgentRequest request,
        AiUsageContext usageContext,
        CancellationToken cancellationToken = default)
    {
        var declarations = AgentToolFilter.Narrow(
            request.Tools,
            request.AllowedToolNames);
        var outputReserve = _usageOptions.GetOutputReserve(usageContext.Feature);
        var openAiRequest = CreateRequest(usageContext, outputReserve);
        openAiRequest.Instructions = string.IsNullOrWhiteSpace(request.SystemInstruction)
            ? "Help the user with their language-learning request."
            : request.SystemInstruction;
        if (!string.IsNullOrWhiteSpace(request.ContextInstruction))
        {
            openAiRequest.Instructions += "\n\n" + request.ContextInstruction;
        }
        OpenAiMessageMapper.AddHistory(openAiRequest.InputItems, request.History);
        foreach (var declaration in declarations)
        {
            openAiRequest.Tools.Add(OpenAiMessageMapper.MapTool(declaration));
        }

        var estimatedPromptTokens =
            EstimateTokens(openAiRequest.Instructions)
            + EstimateTokens(JsonSerializer.Serialize(request.History, JsonOptions))
            + EstimateTokens(JsonSerializer.Serialize(declarations, JsonOptions));
        var response = await ExecuteChargedAsync(
            usageContext,
            estimatedPromptTokens,
            outputReserve,
            openAiRequest,
            cancellationToken);
        ValidateAgentResponse(response);

        var calls = response.FunctionCalls
            .Select(call => new AgentFunctionCall(call.Name, ValidateToolArguments(call.ArgumentsJson))
            {
                CallId = call.CallId,
            })
            .ToArray();
        if (calls.Length > 0)
        {
            GenerativeAiTelemetry.ToolTurns.Add(
                calls.Length,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    OpenAiModels.Luna));
        }

        return new AgentTurnResult(response.Text, calls)
        {
            OutputItemsJson = response.OutputItemsJson,
            Metadata = new AgentInvocationMetadata(
                Provider,
                OpenAiModels.Luna,
                response.ResponseId,
                response.Usage,
                EffectiveRequestJson: request.CaptureEffectiveRequest
                    ? JsonSerializer.Serialize(new
                    {
                        instructions = openAiRequest.Instructions,
                        history = request.History,
                        tools = declarations,
                        model = OpenAiModels.Luna,
                        profile = request.Profile.ToString(),
                        store = false,
                    }, JsonOptions)
                    : null),
        };
    }

    private static CreateResponseOptions CreateRequest(
        AiUsageContext usageContext,
        int outputReserve) =>
        OpenAiRequestFactory.Create(usageContext.UserId, Math.Max(1, outputReserve));

    private async Task<OpenAiResponseEnvelope> ExecuteChargedAsync(
        AiUsageContext usageContext,
        int promptTokenEstimate,
        int outputTokenReserve,
        CreateResponseOptions request,
        CancellationToken cancellationToken)
    {
        var estimatedTokens = Math.Max(1, promptTokenEstimate) + Math.Max(0, outputTokenReserve);
        var outcome = "failure";
        GenerativeAiTelemetry.Requests.Add(
            1,
            GenerativeAiTelemetry.Tags(
                usageContext.Feature,
                Provider,
                OpenAiModels.Luna,
                "started"));
        using var activity = GenerativeAiTelemetry.ActivitySource.StartActivity(
            $"generative-ai.{usageContext.Feature}",
            ActivityKind.Client);
        activity?.SetTag("ai.feature", usageContext.Feature);
        activity?.SetTag("ai.provider", Provider);
        activity?.SetTag("ai.model", OpenAiModels.Luna);

        var startedAt = Stopwatch.GetTimestamp();
        AiCreditReservation reservation;
        try
        {
            reservation = await _credits.ReserveAsync(
                usageContext,
                Provider,
                OpenAiModels.Luna,
                estimatedTokens,
                cancellationToken);
            GenerativeAiTelemetry.CreditReservations.Add(
                1,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    OpenAiModels.Luna,
                    "reserved"));
        }
        catch
        {
            RecordDuration(startedAt, usageContext.Feature, "reservation_failed");
            throw;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        OpenAiResponseEnvelope? response = null;
        var normalUsageSettled = false;
        try
        {
            response = await _transport.CreateResponseAsync(request, timeout.Token);
            var usage = NormalizeUsage(response.Usage, estimatedTokens);
            response = response with { Usage = usage };
            await _credits.CommitUsageAsync(
                reservation.ReservationId,
                usage,
                cancellationToken);
            normalUsageSettled = true;
            outcome = "success";
            var tags = GenerativeAiTelemetry.Tags(
                usageContext.Feature,
                Provider,
                OpenAiModels.Luna,
                outcome);
            RecordUsage(usage, tags);
            GenerativeAiTelemetry.CreditCommits.Add(
                1,
                GenerativeAiTelemetry.Tags(
                    usageContext.Feature,
                    Provider,
                    OpenAiModels.Luna,
                    "committed"));
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
                if (response is not null && !normalUsageSettled)
                {
                    await _credits.CommitUsageIndependentlyAsync(
                        reservation.ReservationId,
                        response.Usage,
                        CancellationToken.None);
                    GenerativeAiTelemetry.CreditCommits.Add(
                        1,
                        GenerativeAiTelemetry.Tags(
                            usageContext.Feature,
                            Provider,
                            OpenAiModels.Luna,
                            "failure_usage_committed"));
                }
                else if (!normalUsageSettled)
                {
                    await _credits.ReleaseAsync(
                        reservation.ReservationId,
                        CancellationToken.None);
                    GenerativeAiTelemetry.CreditReleases.Add(
                        1,
                        GenerativeAiTelemetry.Tags(
                            usageContext.Feature,
                            Provider,
                            OpenAiModels.Luna,
                            "released"));
                }
            }
            catch (Exception settlementException)
            {
                _logger.LogError(
                    settlementException,
                    "Could not settle a failed OpenAI credit reservation.");
            }

            var translated = TranslateException(
                ex,
                cancellationToken,
                usageContext.Feature);
            outcome = translated switch
            {
                OperationCanceledException => "cancelled",
                GenerativeAiTimeoutException => "timeout",
                GenerativeAiDependencyUnavailableException => "unavailable",
                GenerativeAiValidationException => "validation_error",
                GenerativeAiStructuredOutputException => "schema_error",
                _ => "upstream_failure",
            };
            activity?.SetTag("ai.outcome", outcome);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw translated;
        }
        finally
        {
            RecordDuration(startedAt, usageContext.Feature, outcome);
        }
    }

    private Exception TranslateException(
        Exception exception,
        CancellationToken callerToken,
        string feature)
    {
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return exception;
        }

        if (exception is OperationCanceledException
            or OpenAiTransportException { StatusCode: (int)HttpStatusCode.RequestTimeout })
        {
            GenerativeAiTelemetry.Timeouts.Add(
                1,
                GenerativeAiTelemetry.Tags(
                    feature,
                    Provider,
                    OpenAiModels.Luna,
                    "timeout"));
            return new GenerativeAiTimeoutException(
                "The AI service timed out. Please try again.",
                exception);
        }

        if (exception is OpenAiTransportException { StatusCode: 429 } throttled)
        {
            GenerativeAiTelemetry.Throttles.Add(
                1,
                GenerativeAiTelemetry.Tags(
                    feature,
                    Provider,
                    OpenAiModels.Luna,
                    "throttled"));
            return new GenerativeAiDependencyUnavailableException(
                "The AI service is temporarily busy. Please try again.",
                throttled);
        }

        if (exception is OpenAiTransportException { StatusCode: >= 500 }
            || exception is HttpRequestException)
        {
            return new GenerativeAiDependencyUnavailableException(
                "The AI service is temporarily unavailable. Please try again.",
                exception);
        }

        if (exception is GenerativeAiUpstreamException
            or GenerativeAiValidationException
            or GenerativeAiStructuredOutputException
            or InsufficientAiCreditsException)
        {
            return exception;
        }

        _logger.LogWarning(exception, "OpenAI returned an unsuccessful response.");
        return new GenerativeAiUpstreamException(
            "The AI service could not complete the request. Please try again.",
            exception);
    }

    private static void ValidateTextResponse(OpenAiResponseEnvelope response)
    {
        if (response.IsIncomplete)
        {
            throw new GenerativeAiStructuredOutputException(
                "The AI response was too large to finish. Please try a smaller request.");
        }
        if (response.IsRefusal || string.IsNullOrWhiteSpace(response.Text))
        {
            throw new GenerativeAiStructuredOutputException(
                "The AI service could not produce a response.");
        }
    }

    private static void ValidateStructuredResponse(OpenAiResponseEnvelope response)
    {
        if (response.IsIncomplete || response.IsRefusal || string.IsNullOrWhiteSpace(response.Text))
        {
            throw new GenerativeAiStructuredOutputException(
                "The AI service could not produce a valid structured response.");
        }
    }

    private static void ValidateAgentResponse(OpenAiResponseEnvelope response)
    {
        if (response.IsIncomplete)
        {
            throw new GenerativeAiStructuredOutputException(
                "The assistant response was too large to finish. Please try a smaller request.");
        }
        if (response.IsRefusal)
        {
            throw new GenerativeAiStructuredOutputException(
                "The assistant could not complete that request.");
        }
    }

    private static string ValidateToolArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Function arguments must be a JSON object.");
            }
            return document.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new GenerativeAiStructuredOutputException(
                "The assistant could not finish preparing that action. Please try again.",
                ex);
        }
    }

    private static void ValidateModel(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model)
            && !string.Equals(model.Trim(), OpenAiModels.Luna, StringComparison.OrdinalIgnoreCase))
        {
            throw new GenerativeAiValidationException(
                $"Only {OpenAiModels.Luna} is available.");
        }
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

    private static AiTokenUsage NormalizeUsage(AiTokenUsage usage, int fallbackTotalTokens) =>
        usage.TotalTokens > 0
            ? usage
            : usage with { TotalTokens = fallbackTotalTokens };

    private static string StripJsonFences(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)
            || !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }
        var firstLineEnd = trimmed.IndexOf('\n');
        return firstLineEnd < 0
            ? string.Empty
            : trimmed[(firstLineEnd + 1)..^3].Trim();
    }

    private static void RecordUsage(AiTokenUsage usage, in TagList tags)
    {
        GenerativeAiTelemetry.InputTokens.Add(usage.PromptTokens, tags);
        GenerativeAiTelemetry.OutputTokens.Add(usage.CandidateTokens, tags);
        GenerativeAiTelemetry.TotalTokens.Add(usage.TotalTokens, tags);
    }

    private static void RecordDuration(long startedAt, string feature, string outcome) =>
        GenerativeAiTelemetry.Duration.Record(
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            GenerativeAiTelemetry.Tags(
                feature,
                Provider,
                OpenAiModels.Luna,
                outcome));
}

#pragma warning restore OPENAI001
