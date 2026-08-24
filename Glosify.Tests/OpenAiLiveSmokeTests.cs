using System.Net.WebSockets;
using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class OpenAiLiveSmokeTests
{
    [LiveOpenAiFact]
    [Trait("Category", "LiveOpenAI")]
    public async Task Realtime_translation_accepts_the_production_session_configuration()
    {
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var headers = OpenAiTranslationProtocol.CreateRequestHeaders(
            ReadLocalApiKey()!,
            "live-realtime-smoke-learner");
        socket.Options.SetRequestHeader("Authorization", headers.Authorization);
        socket.Options.SetRequestHeader("OpenAI-Safety-Identifier", headers.SafetyIdentifier);

        await socket.ConnectAsync(OpenAiTranslationProtocol.BuildWebSocketUri(), timeout.Token);
        await socket.SendAsync(
            OpenAiTranslationProtocol.CreateSessionUpdate("es"),
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token);

        while (true)
        {
            var message = await ReceiveTextAsync(socket, timeout.Token);
            using var document = JsonDocument.Parse(message);
            var type = document.RootElement.GetProperty("type").GetString();
            if (type == "session.updated")
            {
                break;
            }
            Assert.NotEqual("error", type);
        }

        await socket.SendAsync(
            OpenAiTranslationProtocol.CreateSessionClose(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token);
    }

    [LiveOpenAiFact]
    [Trait("Category", "LiveOpenAI")]
    public async Task Luna_returns_strict_structured_output_with_usage()
    {
        var credits = new SmokeCredits();
        var client = CreateClient(credits);

        var result = await client.GenerateStructuredAsync<SmokeReply>(
            "Set value to exactly ok.",
            Usage("live_structured"));

        Assert.Equal("ok", result.Value);
        Assert.Single(credits.Commits);
        Assert.True(credits.Commits[0].TotalTokens > 0);
    }

    [LiveOpenAiFact]
    [Trait("Category", "LiveOpenAI")]
    public async Task Luna_completes_store_false_code_owned_function_loop()
    {
        var credits = new SmokeCredits();
        var client = CreateClient(credits);
        var turn = await client.RunAgentTurnAsync(
            new AgentRequest(
                "Call lookup_word exactly once for hej. Do not answer from memory.",
                [new AgentTurn("user", """{"parts":[{"kind":"text","text":"Translate hej."}]}""")],
                [
                    new AgentToolDeclaration(
                        "lookup_word",
                        "Looks up one word.",
                        new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new { word = new { type = "string" } },
                            required = new[] { "word" },
                        }),
                ]),
            Usage("live_function_call"));

        var call = Assert.Single(turn.FunctionCalls);
        Assert.Equal("lookup_word", call.Name);
        Assert.False(string.IsNullOrWhiteSpace(call.CallId));
        Assert.Contains("hej", call.ArgsJson, StringComparison.OrdinalIgnoreCase);

        var continued = await client.RunAgentTurnAsync(
            new AgentRequest(
                "Call lookup_word when needed and use its result to answer.",
                [
                    new AgentTurn("user", """{"parts":[{"kind":"text","text":"Translate hej."}]}"""),
                    new AgentTurn("assistant", System.Text.Json.JsonSerializer.Serialize(new
                    {
                        parts = new[]
                        {
                            new
                            {
                                kind = "function_call",
                                name = call.Name,
                                argsJson = call.ArgsJson,
                                callId = call.CallId,
                            },
                        },
                        outputItemsJson = turn.OutputItemsJson,
                    })),
                    new AgentTurn("user", System.Text.Json.JsonSerializer.Serialize(new
                    {
                        parts = new[]
                        {
                            new
                            {
                                kind = "function_response",
                                name = call.Name,
                                responseJson = """{"translation":"hello"}""",
                                callId = call.CallId,
                            },
                        },
                    })),
                ],
                []),
            Usage("live_function_result"));

        Assert.Empty(continued.FunctionCalls);
        Assert.Contains("hello", continued.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, credits.Commits.Count);
    }

    private static OpenAiGenerativeAiClient CreateClient(SmokeCredits credits)
    {
        var options = Options.Create(new GenerativeAiOptions
        {
            ApiKey = ReadLocalApiKey()!,
            TimeoutSeconds = 180,
        });
        return new OpenAiGenerativeAiClient(
            new OpenAiResponsesTransport(options),
            options,
            Options.Create(new AiUsageOptions
            {
                AssistantOutputTokenReserve = 4096,
                JsonImportRepairOutputTokenReserve = 4096,
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }),
            credits,
            NullLogger<OpenAiGenerativeAiClient>.Instance);
    }

    private static AiUsageContext Usage(string operation) =>
        new("live-smoke-learner", AiUsageFeatures.Assistant, operation, Guid.NewGuid());

    private static async Task<byte[]> ReceiveTextAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            Assert.True(message.Length <= 256 * 1024, "OpenAI returned an oversized smoke-test message.");
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    internal static string? ReadLocalApiKey()
    {
        var environmentKey = Environment.GetEnvironmentVariable("OPENAI_SECRET_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            return environmentKey.Trim();
        }

        return new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .Build()["OPENAI_SECRET_KEY"]?
            .Trim();
    }

    private sealed class SmokeReply
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class SmokeCredits : IAiCreditService
    {
        public List<AiTokenUsage> Commits { get; } = [];

        public Task<AiCreditReservation> ReserveAsync(
            AiUsageContext context,
            string provider,
            string model,
            int estimatedTokens,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(AiUsageProviders.OpenAi, provider);
            Assert.Equal(OpenAiModels.Luna, model);
            return Task.FromResult(new AiCreditReservation(Guid.NewGuid(), context.UserId, 1, estimatedTokens));
        }

        public Task CommitUsageAsync(Guid reservationId, AiTokenUsage usage, CancellationToken cancellationToken = default)
        {
            Commits.Add(usage);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AiCreditAccountView> GetOrCreateAccountAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiCreditAccountView(userId, 100, 0, 100, null));

        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(string userId, int count = 25, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiCreditTransaction>>([]);

        public Task GrantAsync(string adminUserId, string targetUserId, int credits, string note, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

internal sealed class LiveOpenAiFactAttribute : FactAttribute
{
    public LiveOpenAiFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_OPENAI_SMOKE_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set RUN_OPENAI_SMOKE_TESTS=true to run direct OpenAI smoke tests.";
        }
        else if (string.IsNullOrWhiteSpace(OpenAiLiveSmokeTests.ReadLocalApiKey()))
        {
            Skip = "Set OPENAI_SECRET_KEY in the environment or Glosify user secrets to run direct OpenAI smoke tests.";
        }
    }
}
