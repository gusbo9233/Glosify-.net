#pragma warning disable OPENAI001

using System.Diagnostics;
using System.Text.Json;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

namespace Glosify.Services.Speaking;

public sealed class OpenAiSpeakingAgentClient : ISpeakingAgentClient
{
    private const int MaximumToolIterations = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GenerativeAiOptions _options;
    private readonly AiUsageOptions _usageOptions;
    private readonly IOpenAiResponsesTransport _transport;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenAiSpeakingAgentClient> _logger;

    public OpenAiSpeakingAgentClient(
        IOptions<GenerativeAiOptions> options,
        IOptions<AiUsageOptions> usageOptions,
        IOpenAiResponsesTransport transport,
        IServiceScopeFactory scopeFactory,
        ILogger<OpenAiSpeakingAgentClient> logger)
    {
        _options = options.Value;
        _usageOptions = usageOptions.Value;
        _transport = transport;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public Task<ISpeakingAgentConversation> CreateConversationAsync(
        string userId,
        SpeakingAvatarId avatar,
        bool interactiveMode = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConfigured)
        {
            throw new SpeakingDependencyUnavailableException(
                "OpenAI is not configured for speaking practice.");
        }

        var profile = SpeakingPromptCatalog.Get(avatar, interactiveMode);
        BartenderSceneToolRuntime? sceneTools = profile.UsesSceneTools
            ? new BartenderSceneToolRuntime()
            : null;
        SpeakingQuizToolRuntime? quizTools = profile.UsesQuizTools
            ? new SpeakingQuizToolRuntime(_scopeFactory)
            : null;
        ISpeakingAgentConversation conversation = new OpenAiSpeakingConversation(
            userId,
            profile,
            _usageOptions.GetOutputReserve(AiUsageFeatures.Speaking),
            _transport,
            sceneTools,
            quizTools,
            _logger);
        return Task.FromResult(conversation);
    }

    internal static SpeakingAgentReply DeserializeReply(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SpeakingAgentReply>(json, JsonOptions)
                ?? throw new InvalidDataException("OpenAI returned an empty speaking response.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("OpenAI returned an invalid speaking response.", ex);
        }
    }

    internal static SpeakingAgentReply DeserializeTutorReply(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("OpenAI returned an invalid tutor response.");
        }

        var reply = new SpeakingAgentReply
        {
            ReplyPolish = ReadTargetLanguageString(root, "replyPolish", "reply"),
            ReplyEnglish = ReadRequiredString(root, "replyEnglish"),
            Coach = ReadCoach(root),
        };
        if (root.TryGetProperty("practice", out var practice)
            && practice.ValueKind == JsonValueKind.Object)
        {
            reply.Practice = practice.Deserialize<SpeakingAgentPracticeSuggestion>(JsonOptions);
        }
        return reply;
    }

    private static SpeakingCoach ReadCoach(JsonElement root)
    {
        if (!root.TryGetProperty("coach", out var coachElement)
            || coachElement.ValueKind != JsonValueKind.Object)
        {
            return new SpeakingCoach();
        }

        var coach = coachElement.Deserialize<SpeakingCoach>(JsonOptions) ?? new SpeakingCoach();
        if (string.IsNullOrWhiteSpace(coach.CorrectedPolish))
        {
            coach.CorrectedPolish = ReadTargetLanguageString(
                coachElement,
                "correctedPolish",
                "corrected",
                required: false);
        }
        return coach;
    }

    private static string ReadTargetLanguageString(
        JsonElement root,
        string legacyPropertyName,
        string prefix,
        bool required = true)
    {
        if (root.TryGetProperty(legacyPropertyName, out var legacy)
            && legacy.ValueKind == JsonValueKind.String)
        {
            return legacy.GetString() ?? string.Empty;
        }
        foreach (var language in new[] { "Estonian", "German", "Ukrainian" })
        {
            if (root.TryGetProperty(prefix + language, out var localized)
                && localized.ValueKind == JsonValueKind.String)
            {
                return localized.GetString() ?? string.Empty;
            }
        }
        if (!required)
        {
            return string.Empty;
        }
        throw new InvalidDataException(
            $"OpenAI tutor response was missing {legacyPropertyName}.");
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"OpenAI tutor response was missing {propertyName}.");
        }
        return property.GetString() ?? string.Empty;
    }

    private sealed class OpenAiSpeakingConversation(
        string userId,
        SpeakingPromptProfile profile,
        int outputReserve,
        IOpenAiResponsesTransport transport,
        BartenderSceneToolRuntime? sceneTools,
        SpeakingQuizToolRuntime? quizTools,
        ILogger logger) : ISpeakingAgentConversation
    {
        private readonly List<ResponseItem> _history = [];

        public Task<SpeakingAgentTurn> RunTurnAsync(
            string message,
            BartenderInteractionState? interactionState = null,
            CancellationToken cancellationToken = default) =>
            RunTurnAsync(message, interactionState, cancellationToken, quizContext: null);

        public async Task<SpeakingAgentTurn> RunTurnAsync(
            string message,
            BartenderInteractionState? interactionState,
            CancellationToken cancellationToken,
            SpeakingQuizContextState? quizContext)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var turnItems = new List<ResponseItem>
            {
                ResponseItem.CreateUserMessageItem(message),
            };
            try
            {
                if (sceneTools is not null)
                {
                    sceneTools.BeginTurn(interactionState
                        ?? throw new InvalidOperationException(
                            "Interactive bartender state is required for scene tools."));
                }
                if (quizTools is not null)
                {
                    quizTools.BeginTurn(quizContext
                        ?? throw new InvalidOperationException(
                            "Tutor quiz context is required for quiz tools."));
                }

                var totalUsage = new AiTokenUsage(0, 0, 0, 0, 0);
                for (var iteration = 0; iteration < MaximumToolIterations; iteration++)
                {
                    var request = OpenAiRequestFactory.Create(userId, Math.Max(1, outputReserve));
                    request.Instructions = profile.Instructions;
                    request.TextOptions = new ResponseTextOptions
                    {
                        TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                            profile.SchemaName,
                            BinaryData.FromString(profile.JsonSchema),
                            "Speaking reply and private coaching.",
                            true),
                    };
                    foreach (var item in _history)
                    {
                        request.InputItems.Add(item);
                    }
                    foreach (var item in turnItems)
                    {
                        request.InputItems.Add(item);
                    }

                    var functions = GetFunctions(sceneTools, quizTools);
                    foreach (var function in functions)
                    {
                        request.Tools.Add(ResponseTool.CreateFunctionTool(
                            function.Name,
                            BinaryData.FromString(function.JsonSchema.GetRawText()),
                            strictModeEnabled: false,
                            function.Description));
                    }

                    var response = await transport.CreateResponseAsync(request, cancellationToken);
                    totalUsage = AddUsage(totalUsage, response.Usage);
                    if (response.IsIncomplete)
                    {
                        throw new InvalidDataException(
                            "OpenAI could not finish the speaking response.");
                    }
                    if (response.IsRefusal)
                    {
                        throw new InvalidDataException(
                            "OpenAI declined the speaking response.");
                    }

                    if (response.FunctionCalls.Count > 0)
                    {
                        foreach (var call in response.FunctionCalls)
                        {
                            turnItems.Add(ResponseItem.CreateFunctionCallItem(
                                call.CallId,
                                call.Name,
                                BinaryData.FromString(call.ArgumentsJson)));
                            var result = await InvokeFunctionAsync(
                                functions,
                                call,
                                cancellationToken);
                            turnItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                                call.CallId,
                                JsonSerializer.Serialize(result, JsonOptions)));
                        }
                        continue;
                    }

                    var reply = profile.UsesQuizTools
                        ? DeserializeTutorReply(response.Text)
                        : DeserializeReply(response.Text);
                    turnItems.Add(ResponseItem.CreateAssistantMessageItem(response.Text, []));
                    _history.AddRange(turnItems);
                    var commands = sceneTools?.CompleteTurn();
                    quizTools?.CompleteTurn();
                    return new SpeakingAgentTurn(reply, totalUsage, commands);
                }

                throw new InvalidDataException(
                    "OpenAI exceeded the five-iteration speaking tool limit.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                sceneTools?.AbortTurn();
                quizTools?.AbortTurn();
                throw;
            }
            catch (Exception ex)
            {
                sceneTools?.AbortTurn();
                quizTools?.AbortTurn();
                SpeakingTelemetry.OpenAiFailures.Add(1);
                logger.LogWarning(ex, "Direct OpenAI speaking turn failed.");
                throw new SpeakingUpstreamException(
                    "The avatar could not answer just now. Please try again.",
                    ex);
            }
            finally
            {
                SpeakingTelemetry.OpenAiDuration.Record(
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
        }

        public ValueTask DisposeAsync()
        {
            _history.Clear();
            return ValueTask.CompletedTask;
        }

        private static IReadOnlyList<AIFunction> GetFunctions(
            BartenderSceneToolRuntime? bartender,
            SpeakingQuizToolRuntime? quiz) =>
            (bartender?.Tools ?? quiz?.Tools ?? [])
                .OfType<AIFunction>()
                .ToArray();

        private static async Task<object?> InvokeFunctionAsync(
            IReadOnlyList<AIFunction> functions,
            OpenAiFunctionCall call,
            CancellationToken cancellationToken)
        {
            var function = functions.FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                call.Name,
                StringComparison.Ordinal));
            if (function is null)
            {
                return new { error = $"The tool {call.Name} is unavailable." };
            }

            var arguments = new AIFunctionArguments();
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return new { error = "Tool arguments must be a JSON object." };
                }
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    arguments[property.Name] = property.Value.Clone();
                }
            }
            catch (JsonException)
            {
                return new { error = "Tool arguments were invalid JSON." };
            }

            return await function.InvokeAsync(arguments, cancellationToken);
        }

        private static AiTokenUsage AddUsage(AiTokenUsage left, AiTokenUsage right) =>
            new(
                left.PromptTokens + right.PromptTokens,
                left.CandidateTokens + right.CandidateTokens,
                left.ThoughtTokens + right.ThoughtTokens,
                left.ToolPromptTokens + right.ToolPromptTokens,
                left.TotalTokens + right.TotalTokens);
    }
}

#pragma warning restore OPENAI001
