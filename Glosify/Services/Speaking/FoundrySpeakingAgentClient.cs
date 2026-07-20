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
    private readonly Lazy<AIProjectClient?> _client;

    public FoundrySpeakingAgentClient(
        IOptions<SpeakingOptions> options,
        TokenCredential credential,
        ILoggerFactory loggerFactory,
        ILogger<FoundrySpeakingAgentClient> logger)
    {
        _options = options.Value;
        _credential = credential;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _client = new Lazy<AIProjectClient?>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsConfigured =>
        Uri.TryCreate(_options.ProjectEndpoint, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(_options.ModelDeployment)
        && SpeakingAvatarCatalog.All.All(avatar =>
        {
            var agent = _options.GetAgent(avatar.Id);
            return !string.IsNullOrWhiteSpace(agent.Name)
                && !string.IsNullOrWhiteSpace(agent.Version);
        })
        && (!_options.InteractiveBartenderEnabled
            || (!string.IsNullOrWhiteSpace(_options.Agents.BartenderInteractive.Name)
                && !string.IsNullOrWhiteSpace(_options.Agents.BartenderInteractive.Version)));

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
            AIAgent agent = sceneTools is null
                ? client.AsAIAgent(result.Value)
                : client.AsAIAgent(
                    result.Value,
                    sceneTools.Tools.ToList(),
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

    private sealed class FoundrySpeakingConversation(
        AIAgent agent,
        AgentSession session,
        ConversationClient conversationClient,
        BartenderSceneToolRuntime? sceneTools,
        ILogger logger) : ISpeakingAgentConversation
    {
        public async Task<SpeakingAgentTurn> RunTurnAsync(
            string message,
            BartenderInteractionState? interactionState = null,
            CancellationToken cancellationToken = default)
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                if (sceneTools is not null)
                {
                    if (interactionState is null)
                    {
                        throw new InvalidOperationException(
                            "Interactive bartender state is required for scene tools.");
                    }

                    sceneTools.BeginTurn(interactionState);
                    try
                    {
                        var response = await agent.RunAsync<SpeakingAgentReply>(
                            message,
                            session,
                            AgentJsonOptions,
                            new ChatClientAgentRunOptions(new ChatOptions
                            {
                                AllowMultipleToolCalls = false,
                            }),
                            cancellationToken);
                        var result = response.Result
                            ?? throw new InvalidDataException(
                                "Foundry returned an empty interactive response.");
                        return new SpeakingAgentTurn(
                            result,
                            ToUsage(response.Usage),
                            sceneTools.CompleteTurn());
                    }
                    catch
                    {
                        sceneTools.AbortTurn();
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
