using System.Text.Json;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Glosify.Services.Ai.Generation;

public interface IFoundryAgentInvoker
{
    Task<AgentResponse> RunAsync(
        string deployment,
        string instructions,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AITool> tools,
        int maxOutputTokens,
        CancellationToken cancellationToken);

    Task<AgentResponse<T>> RunStructuredAsync<T>(
        string deployment,
        string instructions,
        string prompt,
        int maxOutputTokens,
        CancellationToken cancellationToken);
}

internal sealed class FoundryAgentInvoker(
    AIProjectClient projectClient,
    ILoggerFactory loggerFactory) : IFoundryAgentInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<AgentResponse> RunAsync(
        string deployment,
        string instructions,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AITool> tools,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var agent = CreateAgent(deployment, instructions, tools, maxOutputTokens);
        return agent.RunAsync(
            messages,
            session: null,
            options: null,
            cancellationToken);
    }

    public Task<AgentResponse<T>> RunStructuredAsync<T>(
        string deployment,
        string instructions,
        string prompt,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var agent = CreateAgent(deployment, instructions, [], maxOutputTokens);
        return agent.RunAsync<T>(
            prompt,
            session: null,
            JsonOptions,
            options: null,
            cancellationToken);
    }

    private ChatClientAgent CreateAgent(
        string deployment,
        string instructions,
        IReadOnlyList<AITool> tools,
        int maxOutputTokens)
    {
        var options = new ChatClientAgentOptions
        {
            Name = "GlosifyGenerativeAi",
            Description = "Stateless Glosify request through the Microsoft Foundry Responses API.",
            ChatOptions = new ChatOptions
            {
                ModelId = deployment,
                Instructions = instructions,
                MaxOutputTokens = maxOutputTokens,
                AllowMultipleToolCalls = true,
                Tools = tools.ToList(),
            },
        };

        return projectClient.AsAIAgent(
            options,
            clientFactory: null,
            loggerFactory,
            services: null);
    }
}
