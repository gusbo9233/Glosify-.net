using System.Diagnostics;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AzureAI;
using Microsoft.Extensions.Options;
using Glosify.Services.Ai;
using OpenAI.Conversations;

namespace Glosify.Services.Speaking;

// The requested versioned Foundry Prompt Agent integration and best-effort
// server conversation deletion are prerelease APIs in the pinned SDK.
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
        });

    public async Task<ISpeakingAgentConversation> CreateConversationAsync(
        SpeakingAvatarId avatar,
        CancellationToken cancellationToken = default)
    {
        var client = _client.Value;
        if (client is null)
        {
            throw new SpeakingDependencyUnavailableException(
                "Azure AI Foundry is not configured for speaking practice.");
        }

        var configuredAgent = _options.GetAgent(avatar);
        try
        {
            var result = await client.Agents.GetAgentVersionAsync(
                configuredAgent.Name,
                configuredAgent.Version,
                cancellationToken);
            AIAgent agent = client.AsAIAgent(result.Value);
            AgentSession session = agent is FoundryAgent foundryAgent
                ? await foundryAgent.CreateConversationSessionAsync(cancellationToken)
                : await agent.CreateSessionAsync(cancellationToken);
            var conversationClient = client
                .GetProjectOpenAIClient()
                .GetConversationClient();

            _logger.LogInformation(
                "Created speaking conversation with agent {AgentName} version {AgentVersion}.",
                configuredAgent.Name,
                configuredAgent.Version);

            return new FoundrySpeakingConversation(
                agent,
                session,
                conversationClient,
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
        ILogger logger) : ISpeakingAgentConversation
    {
        public async Task<SpeakingAgentTurn> RunTurnAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                var response = await agent.RunAsync<SpeakingTurn>(
                    message,
                    session,
                    AgentJsonOptions,
                    options: null,
                    cancellationToken);
                var result = response.Result
                    ?? throw new InvalidDataException("Foundry returned an empty structured response.");
                var usage = response.Usage;
                var tokenUsage = usage is null
                    ? null
                    : new AiTokenUsage(
                        ToInt(usage.InputTokenCount),
                        ToInt(usage.OutputTokenCount),
                        0,
                        0,
                        ToInt(usage.TotalTokenCount));

                return new SpeakingAgentTurn(result, tokenUsage);
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
