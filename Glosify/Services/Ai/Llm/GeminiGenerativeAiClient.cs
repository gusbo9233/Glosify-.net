using System.Text.Json;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.Options;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace Glosify.Services.Ai.Llm;

public sealed class GeminiGenerativeAiClient : IGenerativeAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GeminiOptions _options;
    private readonly AiUsageOptions _aiUsageOptions;
    private readonly IAiCreditService _credits;
    private readonly IGeminiModelFactory _modelFactory;
    private readonly ILogger<GeminiGenerativeAiClient> _logger;

    public GeminiGenerativeAiClient(
        IOptions<GeminiOptions> options,
        IOptions<AiUsageOptions> aiUsageOptions,
        IAiCreditService credits,
        IGeminiModelFactory modelFactory,
        ILogger<GeminiGenerativeAiClient> logger)
    {
        _options = options.Value;
        _aiUsageOptions = aiUsageOptions.Value;
        _credits = credits;
        _modelFactory = modelFactory;
        _logger = logger;
    }

    public async Task<T> GenerateStructuredAsync<T>(
        string prompt,
        AiUsageContext usageContext,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var selectedModel = string.IsNullOrWhiteSpace(model) ? _options.StructuredModel : model;
        var generativeModel = GetModel(selectedModel, jsonMode: true);
        var outputTokenReserve = _aiUsageOptions.GetOutputReserve(usageContext.Feature);
        var generationConfig = CreateGenerationConfig(
            selectedModel,
            0.2f,
            outputTokenReserve,
            responseMimeType: "application/json");

        var apiRequest = new GenerateContentRequest
        {
            Contents = [new Content(prompt, role: "user")],
            GenerationConfig = generationConfig,
        };

        var response = await GenerateChargedAsync(
            generativeModel,
            selectedModel,
            usageContext,
            apiRequest,
            outputTokenReserve,
            cancellationToken);
        LogUsage("json", selectedModel, response);

        var text = response?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Gemini returned an empty response for a JSON request (model {Model}).", selectedModel);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return default!;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(StripJsonFences(text), JsonOptions)
                ?? default!;
        }
        catch (JsonException)
        {
            return default!;
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

        var generativeModel = GetModel(_options.VisionModel, jsonMode: false);
        var parts = new List<IPart>
        {
            new Part(prompt),
            new Part
            {
                InlineData = new InlineData
                {
                    Data = Convert.ToBase64String(imageBytes),
                    MimeType = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType,
                }
            }
        };

        var outputTokenReserve = _aiUsageOptions.GetOutputReserve(usageContext.Feature);
        var generationConfig = CreateGenerationConfig(_options.VisionModel, 0.1f, outputTokenReserve);
        var apiRequest = new GenerateContentRequest
        {
            Contents = [new Content(parts, role: "user")],
            GenerationConfig = generationConfig,
        };

        var response = await GenerateChargedAsync(
            generativeModel,
            _options.VisionModel,
            usageContext,
            apiRequest,
            outputTokenReserve,
            cancellationToken);
        LogUsage("image-text-extraction", _options.VisionModel, response);

        return response?.Text ?? string.Empty;
    }

    public async Task<AgentTurnResult> RunAgentTurnAsync(
        AgentRequest request,
        AiUsageContext usageContext,
        CancellationToken cancellationToken = default)
    {
        var assistantModel = string.IsNullOrWhiteSpace(request.Model)
            ? string.IsNullOrWhiteSpace(_options.AssistantModel)
                ? _options.StructuredModel
                : _options.AssistantModel
            : request.Model;
        var generativeModel = GetModel(assistantModel, jsonMode: false);

        var contents = request.History.Select(BuildContent).ToList();
        var tools = request.Tools.Count == 0 ? null : BuildTools(request.Tools);
        var systemInstruction = string.IsNullOrWhiteSpace(request.SystemInstruction)
            ? null
            : new Content(request.SystemInstruction, role: "system");

        var outputTokenReserve = _aiUsageOptions.GetOutputReserve(usageContext.Feature);
        var apiRequest = new GenerateContentRequest
        {
            Contents = contents,
            Tools = tools,
            SystemInstruction = systemInstruction,
            GenerationConfig = CreateGenerationConfig(assistantModel, 0.3f, outputTokenReserve),
        };

        var response = await GenerateChargedAsync(
            generativeModel,
            assistantModel,
            usageContext,
            apiRequest,
            outputTokenReserve,
            cancellationToken);
        LogUsage("agent-turn", assistantModel, response);

        var text = response?.Text ?? string.Empty;
        var functionCalls = new List<AgentFunctionCall>();
        var candidateParts = response?.Candidates?.FirstOrDefault()?.Content?.Parts ?? [];
        foreach (var part in candidateParts)
        {
            if (part.FunctionCall == null)
            {
                continue;
            }
            var argsJson = JsonSerializer.Serialize(part.FunctionCall.Args ?? new Dictionary<string, object?>());
            var signature = part.ThoughtSignature is { Length: > 0 }
                ? Convert.ToBase64String(part.ThoughtSignature)
                : null;
            functionCalls.Add(new AgentFunctionCall(
                part.FunctionCall.Name ?? string.Empty,
                argsJson,
                signature)
            {
                CallId = $"gemini-{Guid.NewGuid():N}",
            });
        }

        return new AgentTurnResult(text, functionCalls);
    }

    private static Content BuildContent(AgentTurn turn)
    {
        var parsed = JsonSerializer.Deserialize<StoredContent>(turn.ContentJson, JsonOptions) ?? new StoredContent();
        var parts = new List<IPart>();
        foreach (var part in parsed.Parts ?? [])
        {
            switch (part.Kind)
            {
                case "text":
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        parts.Add(new Part(part.Text));
                    }
                    break;
                case "function_call":
                {
                    var fcPart = new Part
                    {
                        FunctionCall = new FunctionCall
                        {
                            Name = part.Name ?? string.Empty,
                            Args = DeserializeJsonObject(part.ArgsJson),
                        }
                    };
                    if (!string.IsNullOrEmpty(part.ThoughtSignature))
                    {
                        try
                        {
                            fcPart.ThoughtSignature = Convert.FromBase64String(part.ThoughtSignature);
                        }
                        catch (FormatException) { }
                    }
                    parts.Add(fcPart);
                    break;
                }
                case "function_response":
                    parts.Add(new Part
                    {
                        FunctionResponse = new FunctionResponse
                        {
                            Name = part.Name ?? string.Empty,
                            Response = DeserializeJsonObject(part.ResponseJson),
                        }
                    });
                    break;
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new Part(string.Empty));
        }

        return new Content(parts, role: turn.Role);
    }

    private static Tools BuildTools(IReadOnlyList<AgentToolDeclaration> declarations)
    {
        var tools = new Tools();
        tools.Add(new Tool
        {
            FunctionDeclarations = declarations
                .Select(declaration => new FunctionDeclaration
                {
                    Name = declaration.Name,
                    Description = declaration.Description,
                    ParametersJsonSchema = declaration.ParametersJsonSchema,
                })
                .ToList()
        });
        return tools;
    }

    private static object? DeserializeJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>();
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static string StripJsonFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var newlineIndex = trimmed.IndexOf('\n');
        if (newlineIndex >= 0)
        {
            trimmed = trimmed[(newlineIndex + 1)..];
        }
        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }
        return trimmed.Trim();
    }

    private GenerativeModel GetModel(string modelName, bool jsonMode)
        => _modelFactory.GetModel(modelName, jsonMode);

    private GenerationConfig CreateGenerationConfig(
        string modelName,
        float temperature,
        int maxOutputTokens,
        string? responseMimeType = null)
    {
        var config = new GenerationConfig
        {
            MaxOutputTokens = maxOutputTokens,
            ResponseMimeType = responseMimeType,
            Temperature = temperature,
        };

        return config;
    }

    private async Task<GenerateContentResponse?> GenerateChargedAsync(
        GenerativeModel generativeModel,
        string modelName,
        AiUsageContext usageContext,
        GenerateContentRequest request,
        int outputTokenReserve,
        CancellationToken cancellationToken)
    {
        // Estimated locally (chars/4 plus a fixed cost per inline image) rather than via a
        // CountTokens API call: the estimate only sizes the credit reservation, and the commit
        // below reconciles against the real UsageMetadata, so an extra network round-trip per
        // generation is not worth the precision.
        var promptTokens = EstimatePromptTokens(request);
        var estimatedTokens = Math.Max(1, promptTokens) + Math.Max(0, outputTokenReserve);
        var reservation = await _credits.ReserveAsync(
            usageContext,
            "gemini",
            modelName,
            estimatedTokens,
            cancellationToken);

        try
        {
            var response = await generativeModel.GenerateContent(request, cancellationToken: cancellationToken);
            await _credits.CommitUsageAsync(
                reservation.ReservationId,
                ExtractUsage(response, estimatedTokens),
                cancellationToken);
            return response;
        }
        catch
        {
            await _credits.ReleaseAsync(reservation.ReservationId, CancellationToken.None);
            throw;
        }
    }

    // Gemini charges a flat per-tile cost for images (258 tokens/tile); four tiles covers
    // the photo sizes the app accepts without counting the base64 payload as text.
    private const int InlineDataTokenEstimate = 1032;

    private static int EstimatePromptTokens(GenerateContentRequest request)
    {
        var characterCount = 0;
        characterCount += EstimateSerializedLength(request.SystemInstruction);
        characterCount += EstimateSerializedLength(request.Tools);

        var inlineDataTokens = 0;
        foreach (var content in request.Contents ?? [])
        {
            foreach (var part in content.Parts ?? [])
            {
                if (part is Part { InlineData: not null })
                {
                    inlineDataTokens += InlineDataTokenEstimate;
                }
                else
                {
                    characterCount += EstimateSerializedLength(part);
                }
            }
        }

        return (int)Math.Ceiling(characterCount / 4.0) + inlineDataTokens;
    }

    private static int EstimateSerializedLength<T>(T value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            return JsonSerializer.Serialize(value, JsonOptions).Length;
        }
        catch (NotSupportedException)
        {
            return 0;
        }
    }

    private static AiTokenUsage ExtractUsage(GenerateContentResponse? response, int fallbackTotalTokens)
    {
        var usage = response?.UsageMetadata;
        if (usage == null)
        {
            return new AiTokenUsage(0, 0, 0, 0, fallbackTotalTokens);
        }

        var totalTokens = Convert.ToInt32(usage.TotalTokenCount);
        if (totalTokens <= 0)
        {
            totalTokens = fallbackTotalTokens;
        }

        return new AiTokenUsage(
            Convert.ToInt32(usage.PromptTokenCount),
            Convert.ToInt32(usage.CandidatesTokenCount),
            Convert.ToInt32(usage.ThoughtsTokenCount),
            Convert.ToInt32(usage.ToolUsePromptTokenCount),
            totalTokens);
    }

    private void LogUsage(string operation, string modelName, GenerateContentResponse? response)
    {
        var usage = response?.UsageMetadata;
        if (usage == null)
        {
            _logger.LogDebug("Gemini usage metadata was unavailable for {Operation} request (model {Model}).", operation, modelName);
            return;
        }

        _logger.LogInformation(
            "Gemini usage for {Operation} (model {Model}): prompt {PromptTokens}, candidates {CandidateTokens}, thoughts {ThoughtTokens}, tool prompt {ToolUsePromptTokens}, total {TotalTokens}.",
            operation,
            modelName,
            usage.PromptTokenCount,
            usage.CandidatesTokenCount,
            usage.ThoughtsTokenCount,
            usage.ToolUsePromptTokenCount,
            usage.TotalTokenCount);
    }

    private sealed class StoredContent
    {
        public List<StoredPart>? Parts { get; set; }
    }

    private sealed class StoredPart
    {
        public string Kind { get; set; } = "text";
        public string? Text { get; set; }
        public string? Name { get; set; }
        public string? ArgsJson { get; set; }
        public string? ResponseJson { get; set; }
        public string? CallId { get; set; }
        public string? ThoughtSignature { get; set; }
    }
}
