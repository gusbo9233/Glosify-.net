using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace Glosify.Services;

public sealed class GeminiClient : IGeminiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiClient> _logger;
    private readonly Lazy<GoogleAI> _googleAi;
    private readonly ConcurrentDictionary<(string Model, bool Json), GenerativeModel> _models = new();

    public GeminiClient(IOptions<GeminiOptions> options, ILogger<GeminiClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _googleAi = new Lazy<GoogleAI>(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                throw new InvalidOperationException(
                    "Gemini API key is not configured. Set Gemini:ApiKey (user-secrets) or the GEMINI_API_KEY environment variable.");
            }
            return new GoogleAI(apiKey: _options.ApiKey);
        });
    }

    public async Task<string> GenerateJsonAsync(
        string prompt,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var selectedModel = string.IsNullOrWhiteSpace(model) ? _options.StructuredModel : model;
        var generativeModel = GetModel(selectedModel, jsonMode: true);

        var generationConfig = CreateGenerationConfig(selectedModel, 0.2f, responseMimeType: "application/json");

        var response = await generativeModel.GenerateContent(
            prompt,
            generationConfig: generationConfig,
            cancellationToken: cancellationToken);
        LogUsage("json", selectedModel, response);

        var text = response?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Gemini returned an empty response for a JSON request (model {Model}).", selectedModel);
        }

        return text;
    }

    public async Task<string> ExtractTextFromImageAsync(
        byte[] imageBytes,
        string contentType,
        string prompt,
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

        var generationConfig = CreateGenerationConfig(_options.VisionModel, 0.1f);

        var response = await generativeModel.GenerateContent(
            parts,
            generationConfig: generationConfig,
            cancellationToken: cancellationToken);
        LogUsage("image-text-extraction", _options.VisionModel, response);

        return response?.Text ?? string.Empty;
    }

    public async Task<AgentTurnResult> RunAgentTurnAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default)
    {
        var assistantModel = string.IsNullOrWhiteSpace(_options.AssistantModel)
            ? _options.StructuredModel
            : _options.AssistantModel;
        var generativeModel = GetModel(assistantModel, jsonMode: false);

        var contents = request.History.Select(BuildContent).ToList();
        var tools = request.Tools.Count == 0 ? null : BuildTools(request.Tools);
        var systemInstruction = string.IsNullOrWhiteSpace(request.SystemInstruction)
            ? null
            : new Content(request.SystemInstruction, role: "system");

        var apiRequest = new GenerateContentRequest
        {
            Contents = contents,
            Tools = tools,
            SystemInstruction = systemInstruction,
            GenerationConfig = CreateGenerationConfig(assistantModel, 0.3f),
        };

        var response = await generativeModel.GenerateContent(apiRequest, cancellationToken: cancellationToken);
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
                signature));
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

    private GenerativeModel GetModel(string modelName, bool jsonMode)
    {
        return _models.GetOrAdd((modelName, jsonMode), key =>
        {
            var model = _googleAi.Value.GenerativeModel(model: key.Model);
            model.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
            if (key.Json)
            {
                model.UseJsonMode = true;
            }
            return model;
        });
    }

    private GenerationConfig CreateGenerationConfig(
        string modelName,
        float temperature,
        string? responseMimeType = null)
    {
        var config = new GenerationConfig
        {
            ResponseMimeType = responseMimeType,
            Temperature = temperature,
        };

        if (SupportsThinkingLevel(modelName) && TryGetThinkingLevel(out var thinkingLevel))
        {
            config.ThinkingLevel = thinkingLevel;
        }

        return config;
    }

    private static bool SupportsThinkingLevel(string modelName)
    {
        return modelName.Contains("gemini-3", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetThinkingLevel(out ThinkingLevel thinkingLevel)
    {
        thinkingLevel = _options.ThinkingLevel.Trim().ToLowerInvariant() switch
        {
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            _ => ThinkingLevel.ThinkingLevelUnspecified,
        };

        return thinkingLevel != ThinkingLevel.ThinkingLevelUnspecified;
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
        public string? ThoughtSignature { get; set; }
    }
}
