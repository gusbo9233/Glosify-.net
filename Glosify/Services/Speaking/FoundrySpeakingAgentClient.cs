using System.Diagnostics;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Glosify.Services.Ai;
using OpenAI.Conversations;

namespace Glosify.Services.Speaking;

// Best-effort server conversation deletion uses the OpenAI conversations API,
// which is still marked experimental by the stable Foundry package.
#pragma warning disable OPENAI001
public sealed class FoundrySpeakingAgentClient : ISpeakingAgentClient
{
    private static readonly JsonSerializerOptions AgentJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SpeakingOptions _options;
    private readonly TokenCredential _credential;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FoundrySpeakingAgentClient> _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Lazy<AIProjectClient?> _client;

    public FoundrySpeakingAgentClient(
        IOptions<SpeakingOptions> options,
        TokenCredential credential,
        ILoggerFactory loggerFactory,
        ILogger<FoundrySpeakingAgentClient> logger,
        IServiceScopeFactory? scopeFactory = null)
    {
        _options = options.Value;
        _credential = credential;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _client = new Lazy<AIProjectClient?>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsConfigured =>
        Uri.TryCreate(_options.ProjectEndpoint, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(_options.ModelDeployment);

    public async Task<ISpeakingAgentConversation> CreateConversationAsync(
        SpeakingAvatarId avatar,
        bool interactiveMode = false,
        CancellationToken cancellationToken = default)
    {
        var client = _client.Value;
        if (client is null)
        {
            throw new SpeakingDependencyUnavailableException(
                "Azure AI Foundry is not configured for speaking practice.");
        }

        var configuredAgent = _options.GetAgent(avatar, interactiveMode);
        if (string.IsNullOrWhiteSpace(configuredAgent.Name)
            || string.IsNullOrWhiteSpace(configuredAgent.Version))
        {
            throw new SpeakingDependencyUnavailableException(
                "The selected speaking avatar is not configured.");
        }
        try
        {
            var result = await client
                .GetProjectAgentsClient()
                .GetAgentVersionAsync(
                configuredAgent.Name,
                configuredAgent.Version,
                cancellationToken);
            var sceneTools = interactiveMode
                ? new BartenderSceneToolRuntime()
                : null;
            var quizTools = SpeakingAvatarCatalog.IsTutor(avatar)
                ? new SpeakingQuizToolRuntime(
                    _scopeFactory
                    ?? throw new SpeakingDependencyUnavailableException(
                        "Quiz tools are not configured for speaking practice."))
                : null;
            var runtimeTools = sceneTools?.Tools ?? quizTools?.Tools ?? [];
            AIAgent agent = runtimeTools.Count == 0
                ? client.AsAIAgent(result.Value)
                : client.AsAIAgent(
                    result.Value,
                    runtimeTools.ToList(),
                    chatClient => new ChatClientBuilder(chatClient)
                        .UseFunctionInvocation(
                            _loggerFactory,
                            options =>
                            {
                                options.AllowConcurrentInvocation = false;
                                options.MaximumIterationsPerRequest = 5;
                                options.MaximumConsecutiveErrorsPerRequest = 2;
                                options.IncludeDetailedErrors = false;
                            })
                        .Build(null),
                    services: null);
            AgentSession session = agent is FoundryAgent foundryAgent
                ? await foundryAgent.CreateConversationSessionAsync(cancellationToken)
                : await agent.CreateSessionAsync(cancellationToken);
            var conversationClient = client
                .GetProjectOpenAIClient()
                .GetConversationClient();

            _logger.LogInformation(
                "Created {SpeakingMode} speaking conversation with agent {AgentName} version {AgentVersion}.",
                interactiveMode ? "interactive" : "standard",
                configuredAgent.Name,
                configuredAgent.Version);

            return new FoundrySpeakingConversation(
                agent,
                session,
                conversationClient,
                sceneTools,
                quizTools,
                _logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SpeakingTelemetry.FoundryFailures.Add(1);
            _logger.LogWarning(
                ex,
                "Could not create speaking conversation for agent {AgentName} version {AgentVersion}.",
                configuredAgent.Name,
                configuredAgent.Version);
            throw new SpeakingDependencyUnavailableException(
                "The Azure speaking coach is temporarily unavailable.",
                ex);
        }
    }

    private AIProjectClient? CreateClient()
    {
        if (!IsConfigured)
        {
            return null;
        }

        return new AIProjectClient(new Uri(_options.ProjectEndpoint), _credential);
    }

    internal static SpeakingAgentReply DeserializeTutorReply(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Foundry returned an invalid tutor response.");
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
            reply.Practice = practice.Deserialize<SpeakingAgentPracticeSuggestion>(AgentJsonOptions);
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

        var coach = coachElement.Deserialize<SpeakingCoach>(AgentJsonOptions)
            ?? new SpeakingCoach();
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

        var fields = string.Join(", ", root.EnumerateObject().Select(item =>
            $"{item.Name}:{item.Value.ValueKind}"));
        throw new InvalidDataException(
            $"Foundry tutor response was missing {legacyPropertyName}; fields were {fields}.");
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            var fields = root.ValueKind == JsonValueKind.Object
                ? string.Join(", ", root.EnumerateObject().Select(item =>
                    $"{item.Name}:{item.Value.ValueKind}"))
                : root.ValueKind.ToString();
            throw new InvalidDataException(
                $"Foundry tutor response was missing {propertyName}; fields were {fields}.");
        }

        return property.GetString() ?? string.Empty;
    }

    private sealed class FoundrySpeakingConversation(
        AIAgent agent,
        AgentSession session,
        ConversationClient conversationClient,
        BartenderSceneToolRuntime? sceneTools,
        SpeakingQuizToolRuntime? quizTools,
        ILogger logger) : ISpeakingAgentConversation
    {
        public async Task<SpeakingAgentTurn> RunTurnAsync(
            string message,
            BartenderInteractionState? interactionState = null,
            CancellationToken cancellationToken = default) =>
            await RunTurnAsync(message, interactionState, cancellationToken, quizContext: null);

        public async Task<SpeakingAgentTurn> RunTurnAsync(
            string message,
            BartenderInteractionState? interactionState,
            CancellationToken cancellationToken,
            SpeakingQuizContextState? quizContext)
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                if (sceneTools is not null || quizTools is not null)
                {
                    if (sceneTools is not null && interactionState is null)
                    {
                        throw new InvalidOperationException(
                            "Interactive bartender state is required for scene tools.");
                    }

                    if (quizTools is not null && quizContext is null)
                    {
                        throw new InvalidOperationException(
                            "Tutor quiz context is required for quiz tools.");
                    }

                    if (sceneTools is not null)
                    {
                        sceneTools.BeginTurn(interactionState!);
                    }
                    if (quizTools is not null)
                    {
                        quizTools.BeginTurn(quizContext!);
                    }
                    try
                    {
                        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
                            {
                                AllowMultipleToolCalls = false,
                            });
                        SpeakingAgentReply result;
                        Microsoft.Agents.AI.AgentResponse response;
                        if (quizTools is not null)
                        {
                            // The pinned tutor agent already owns the strict response schema.
                            // Asking Agent Framework for T as well would add a second generated
                            // schema; some non-OpenAI deployments produce incompatible nested
                            // values under that duplicate constraint.
                            response = await agent.RunAsync(
                                message,
                                session,
                                runOptions,
                                cancellationToken);
                            result = DeserializeTutorReply(response.Text);
                        }
                        else
                        {
                            var typedResponse = await agent.RunAsync<SpeakingAgentReply>(
                                message,
                                session,
                                AgentJsonOptions,
                                runOptions,
                                cancellationToken);
                            result = typedResponse.Result
                                ?? throw new InvalidDataException(
                                    "Foundry returned an empty interactive response.");
                            response = typedResponse;
                        }
                        var commands = sceneTools?.CompleteTurn();
                        quizTools?.CompleteTurn();
                        return new SpeakingAgentTurn(
                            result,
                            ToUsage(response.Usage),
                            commands);
                    }
                    catch
                    {
                        sceneTools?.AbortTurn();
                        quizTools?.AbortTurn();
                        throw;
                    }
                }

                var standardResponse = await agent.RunAsync<SpeakingAgentReply>(
                    message,
                    session,
                    AgentJsonOptions,
                    options: null,
                    cancellationToken);
                var standardResult = standardResponse.Result
                    ?? throw new InvalidDataException("Foundry returned an empty structured response.");
                return new SpeakingAgentTurn(standardResult, ToUsage(standardResponse.Usage));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SpeakingTelemetry.FoundryFailures.Add(1);
                throw new SpeakingUpstreamException(
                    "The avatar could not answer just now. Please try again.",
                    ex);
            }
            finally
            {
                SpeakingTelemetry.FoundryDuration.Record(
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
        }

        private static AiTokenUsage? ToUsage(Microsoft.Extensions.AI.UsageDetails? usage)
        {
            if (usage is null)
            {
                return null;
            }

            return new AiTokenUsage(
                ToInt(usage.InputTokenCount),
                ToInt(usage.OutputTokenCount),
                0,
                0,
                ToInt(usage.TotalTokenCount));
        }

        public async ValueTask DisposeAsync()
        {
            if (session is not ChatClientAgentSession { ConversationId: { Length: > 0 } conversationId })
            {
                return;
            }

            try
            {
                await conversationClient.DeleteConversationAsync(conversationId);
            }
            catch (Exception ex)
            {
                // Session deletion must always remove the user-bound local
                // reference even if Foundry cleanup is temporarily unavailable.
                logger.LogWarning(
                    ex,
                    "Could not delete Foundry speaking conversation {ConversationId}.",
                    conversationId);
            }
        }

        private static int ToInt(long? value) =>
            value is null ? 0 : (int)Math.Clamp(value.Value, 0, int.MaxValue);
    }
}
#pragma warning restore OPENAI001
