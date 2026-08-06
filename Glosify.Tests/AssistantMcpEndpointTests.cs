using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Glosify.Services.Ai.Assistant.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Glosify.Tests;

public class AssistantMcpEndpointTests
{
    private const string SigningKey = "an-endpoint-signing-key-long-enough!";

    // Foundry authenticates as one shared identity, so an unsigned or expired route must
    // never reach the tool surface.
    [Fact]
    public async Task Rejects_a_request_without_a_valid_session()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/assistant/mcp/s/forged.token",
            JsonRpc("initialize"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_request_missing_the_shared_secret()
    {
        using var factory = CreateFactory(sharedSecret: "expected-secret");
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/assistant/mcp/s/{MintToken(factory)}",
            JsonRpc("initialize"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The endpoint stays absent entirely until a signing key is configured, so a
    // deployment that has not set one exposes nothing.
    [Fact]
    public async Task Is_not_found_when_no_signing_key_is_configured()
    {
        using var factory = CreateFactory(signingKey: string.Empty);
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/assistant/mcp/s/anything.atall",
            JsonRpc("initialize"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lists_only_the_tools_exposed_over_mcp()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var route = $"/assistant/mcp/s/{MintToken(factory)}";

        var initialize = await client.SendAsync(McpRequest(route, JsonRpc(
            "initialize",
            """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}""")));
        initialize.EnsureSuccessStatusCode();
        var sessionId = initialize.Headers.TryGetValues("Mcp-Session-Id", out var ids)
            ? ids.FirstOrDefault()
            : null;

        var request = McpRequest(route, JsonRpc("tools/list", "{}", id: 2));
        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }
        var listed = await client.SendAsync(request);
        listed.EnsureSuccessStatusCode();

        var body = await ReadResultAsync(listed);
        var names = body.GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("get_custom_quiz", names);
        Assert.Contains("list_words", names);
        // Mutating tools are live now that a queued change is persisted per conversation.
        Assert.Contains("add_text_input", names);
        Assert.Contains("configure_custom_quiz_element", names);
        // Quiz creation stays off: with a custom quiz open, creating another can only
        // strand the editor the user is looking at.
        Assert.DoesNotContain("create_custom_quiz", names);
        Assert.DoesNotContain("create_quiz", names);
    }

    private static string MintToken(WebApplicationFactory<Program> factory)
    {
        var codec = factory.Services.GetRequiredService<IAssistantMcpSessionCodec>();
        return codec.Protect(new AssistantMcpSession
        {
            UserId = "user-1",
            QuizId = Guid.NewGuid(),
            CustomQuizId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
        });
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string signingKey = SigningKey,
        string sharedSecret = "") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Assistant:Mcp:SigningKey"] = signingKey,
                    ["Assistant:Mcp:SharedSecret"] = sharedSecret,
                })));

    // Streamable HTTP requires the client to accept both a JSON reply and an SSE stream.
    private static HttpRequestMessage McpRequest(string route, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static StringContent JsonRpc(string method, string paramsJson = "{}", int id = 1) =>
        new(
            $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""",
            Encoding.UTF8,
            new MediaTypeHeaderValue("application/json"));

    private static async Task<JsonElement> ReadResultAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        // Streamable HTTP may answer as a single SSE event rather than plain JSON.
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                raw = line["data:".Length..].Trim();
                break;
            }
        }

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("result").Clone();
    }
}
