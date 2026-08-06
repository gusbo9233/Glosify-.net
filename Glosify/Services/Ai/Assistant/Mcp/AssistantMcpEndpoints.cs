using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Glosify.Services.Ai.Assistant.Mcp;

/// <summary>
/// Hosts the assistant's tools as an MCP server so agents in Microsoft Foundry can call
/// them. Foundry only reaches remote endpoints, and it authenticates as one shared
/// identity, so the acting user travels in a signed token inside the route.
/// </summary>
public static class AssistantMcpEndpoints
{
    public const string SessionRouteValue = "sessionToken";
    public const string RoutePattern = $"/assistant/mcp/s/{{{SessionRouteValue}}}";

    public static IServiceCollection AddAssistantMcp(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AssistantMcpOptions>(configuration.GetSection(AssistantMcpOptions.SectionName));
        services.AddSingleton<IAssistantMcpSessionCodec, AssistantMcpSessionCodec>();
        services.AddScoped<IAssistantMcpToolSurface, AssistantMcpToolSurface>();

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "glosify-assistant",
                    Version = "1.0.0",
                };
                options.Handlers.ListToolsHandler = ListToolsAsync;
                options.Handlers.CallToolHandler = CallToolAsync;
            })
            .WithHttpTransport();

        return services;
    }

    public static IEndpointConventionBuilder MapAssistantMcp(this IEndpointRouteBuilder endpoints)
    {
        return endpoints
            .MapMcp(RoutePattern)
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var options = http.RequestServices
                    .GetRequiredService<IOptions<AssistantMcpOptions>>().Value;
                if (!options.IsEnabled)
                {
                    return Results.NotFound();
                }

                if (!string.IsNullOrWhiteSpace(options.SharedSecret)
                    && !string.Equals(
                        http.Request.Headers[options.SharedSecretHeader].ToString(),
                        options.SharedSecret,
                        StringComparison.Ordinal))
                {
                    return Results.Unauthorized();
                }

                // Reject a bad or expired session before the protocol handshake, so a
                // guessed URL cannot even enumerate the tool list.
                if (ResolveSession(http) is null)
                {
                    return Results.Unauthorized();
                }

                return await next(context);
            })
            .AllowAnonymous();
    }

    private static ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
    {
        var surface = request.Services!.GetRequiredService<IAssistantMcpToolSurface>();
        var tools = surface.Declarations
            .Select(declaration => new Tool
            {
                Name = declaration.Name,
                Description = declaration.Description,
                InputSchema = JsonSerializer.SerializeToElement(
                    declaration.ParametersJsonSchema,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            })
            .ToList();

        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    private static async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        var session = http is null ? null : ResolveSession(http);
        if (session is null)
        {
            return Error("This tool session has expired. Ask the user to send their message again.");
        }

        var toolName = request.Params?.Name;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Error("A tool name is required.");
        }

        var surface = services.GetRequiredService<IAssistantMcpToolSurface>();
        if (!surface.Allows(toolName))
        {
            return Error($"Tool {toolName} is not available.");
        }

        var arguments = request.Params?.Arguments is null
            ? "{}"
            : JsonSerializer.Serialize(request.Params.Arguments);
        var result = await surface.ExecuteAsync(toolName, arguments, session, cancellationToken);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = AssistantMcpToolSurface.SerializeResult(result) }],
        };
    }

    private static AssistantMcpSession? ResolveSession(HttpContext http)
    {
        var token = http.Request.RouteValues.TryGetValue(SessionRouteValue, out var value)
            ? value?.ToString()
            : null;
        return string.IsNullOrWhiteSpace(token)
            ? null
            : http.RequestServices
                .GetRequiredService<IAssistantMcpSessionCodec>()
                .Unprotect(token);
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
