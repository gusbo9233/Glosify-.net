using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
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

    /// <summary>
    /// Reads an agent authored in the Foundry portal. Returns null when the agent is
    /// missing or is not a prompt agent, so callers fall back to in-code instructions.
    /// </summary>
    Task<FoundryAuthoredAgent?> TryGetAuthoredAgentAsync(
        string name,
        string version,
        CancellationToken cancellationToken);
}

/// <summary>The authored parts of a persisted Foundry prompt agent.</summary>
/// <param name="Tools">
/// Function tools declared on the agent. Glosify still executes them — they act on the
/// calling user's rows — but which tools exist, and how they are described to the model,
/// is owned in Foundry. Empty when the agent declares none, in which case the caller
/// falls back to the declarations built in code.
/// </param>
public sealed record FoundryAuthoredAgent(
    string Instructions,
    string? Model,
    IReadOnlyList<AgentToolDeclaration> Tools);

internal sealed class FoundryAgentInvoker(
    AIProjectClient projectClient,
    ILoggerFactory loggerFactory) : IFoundryAgentInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Authored agents are pinned to an explicit version, so a fetched definition can be
    // cached for the process lifetime. Publishing a new version means bumping config.
    private readonly ConcurrentDictionary<string, FoundryAuthoredAgent?> _authoredAgents = new(StringComparer.Ordinal);
    private readonly ILogger _logger = loggerFactory.CreateLogger<FoundryAgentInvoker>();

    public async Task<FoundryAuthoredAgent?> TryGetAuthoredAgentAsync(
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        var key = $"{name}@{version}";
        if (_authoredAgents.TryGetValue(key, out var cached))
        {
            return cached;
        }

        FoundryAuthoredAgent? authored = null;
        try
        {
            var result = await projectClient
                .GetProjectAgentsClient()
                .GetAgentVersionAsync(name, version, cancellationToken);
            if (result.Value.Definition is DeclarativeAgentDefinition declarative
                && !string.IsNullOrWhiteSpace(declarative.Instructions))
            {
                authored = new FoundryAuthoredAgent(
                    declarative.Instructions,
                    declarative.Model,
                    ReadFunctionTools(result.Value));
            }
            else
            {
                _logger.LogWarning(
                    "Foundry agent {AgentName} version {AgentVersion} has no prompt instructions; using in-code instructions.",
                    name,
                    version);
            }
        }
        catch (Exception ex)
        {
            // A missing or unreachable agent must not break the assistant: the caller
            // still has the full in-code instruction to fall back to.
            _logger.LogWarning(
                ex,
                "Could not read Foundry agent {AgentName} version {AgentVersion}; using in-code instructions.",
                name,
                version);
        }

        _authoredAgents[key] = authored;
        return authored;
    }

    /// <summary>
    /// Reads the agent's function tool declarations straight from its serialized
    /// definition. Going through the JSON rather than the typed tool models keeps the
    /// name, description, and parameter schema exactly as they were authored, which is
    /// what the model sees when choosing a tool.
    /// </summary>
    private IReadOnlyList<AgentToolDeclaration> ReadFunctionTools(ProjectsAgentVersion version)
    {
        var declarations = new List<AgentToolDeclaration>();
        try
        {
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(version));
            if (!document.RootElement.TryGetProperty("definition", out var definition)
                || !definition.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array)
            {
                return declarations;
            }

            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object
                    || GetString(tool, "type") != "function")
                {
                    continue;
                }

                var name = GetString(tool, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var parameters = tool.TryGetProperty("parameters", out var schema)
                    ? schema.Clone()
                    : JsonSerializer.SerializeToElement(new { type = "object" }, JsonOptions);
                declarations.Add(new AgentToolDeclaration(
                    name,
                    GetString(tool, "description") ?? string.Empty,
                    parameters));
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            _logger.LogWarning(
                ex,
                "Could not read tool declarations from Foundry agent {AgentName}; using in-code declarations.",
                version.Name);
            return [];
        }

        return declarations;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
