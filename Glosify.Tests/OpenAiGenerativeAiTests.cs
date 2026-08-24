#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using Xunit;

namespace Glosify.Tests;

public sealed class OpenAiGenerativeAiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Request_factory_fixes_model_privacy_reasoning_and_safety_identifier()
    {
        var request = OpenAiRequestFactory.Create("Learner-42", 321);

        Assert.Equal(OpenAiModels.Luna, request.Model);
        Assert.False(request.StoredOutputEnabled ?? true);
        Assert.True(request.ParallelToolCallsEnabled ?? false);
        Assert.Equal(321, request.MaxOutputTokenCount);
        Assert.Equal(
            "29f7a00b590225049350e2383123ff246d85a813562cbfe9d613e29912b55915",
            request.SafetyIdentifier);
        Assert.Equal(
            ResponseReasoningEffortLevel.Medium,
            request.ReasoningOptions?.ReasoningEffortLevel);
    }

    [Fact]
    public async Task Structured_generation_sends_strict_schema_and_commits_exact_usage()
    {
        var transport = new RecordingTransport
        {
            Response = Envelope("""{"value":"ok"}""", usage: new(11, 7, 2, 0, 18)),
        };
        var credits = new RecordingCredits();
        var client = CreateClient(transport, credits);

        var result = await client.GenerateStructuredAsync<StructuredFixture>(
            "Return a value.",
            Usage(AiUsageFeatures.JsonImportRepair));

        Assert.Equal("ok", result.Value);
        var json = SerializeRequest(Assert.Single(transport.Requests));
        Assert.Equal(OpenAiModels.Luna, json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("store").GetBoolean());
        Assert.Equal("medium", json.GetProperty("reasoning").GetProperty("effort").GetString());
        var format = json.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal(11, Assert.Single(credits.Commits).Usage.PromptTokens);
        Assert.Equal(18, Assert.Single(credits.Commits).Usage.TotalTokens);
        Assert.Equal(AiUsageProviders.OpenAi, Assert.Single(credits.Reservations).Provider);
        Assert.Equal(OpenAiModels.Luna, Assert.Single(credits.Reservations).Model);
    }

    [Fact]
    public async Task Image_generation_sends_image_input_without_exposing_a_model_choice()
    {
        var transport = new RecordingTransport { Response = Envelope("hej") };
        var client = CreateClient(transport, new RecordingCredits());

        var text = await client.ExtractTextFromImageAsync(
            [1, 2, 3],
            "image/png",
            "Read it.",
            Usage(AiUsageFeatures.ImageExtraction));

        Assert.Equal("hej", text);
        var requestJson = SerializeRequest(Assert.Single(transport.Requests)).GetRawText();
        Assert.Contains("input_image", requestJson, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,AQID", requestJson, StringComparison.Ordinal);
        Assert.Contains(OpenAiModels.Luna, requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_turn_replays_manual_history_and_preserves_function_call_ids()
    {
        var transport = new RecordingTransport
        {
            Response = Envelope(
                "Checking.",
                [
                    new OpenAiFunctionCall("call-new-a", "lookup", """{"word":"hej"}"""),
                    new OpenAiFunctionCall("call-new-b", "lookup", """{"word":"tack"}"""),
                ]),
        };
        var client = CreateClient(transport, new RecordingCredits());
        var request = new AgentRequest(
            "Help.",
            [
                new AgentTurn("user", Content(new { kind = "text", text = "Hello" })),
                new AgentTurn("assistant", Content(new
                {
                    kind = "function_call",
                    name = "lookup",
                    argsJson = """{"word":"hej"}""",
                    callId = "call-old",
                })),
                new AgentTurn("user", Content(new
                {
                    kind = "function_response",
                    name = "lookup",
                    responseJson = """{"translation":"hello"}""",
                    callId = "call-old",
                })),
            ],
            [
                new AgentToolDeclaration(
                    "lookup",
                    "Looks up a word.",
                    new
                    {
                        type = "object",
                        properties = new { word = new { type = "string" } },
                        required = new[] { "word" },
                    }),
            ]);

        var result = await client.RunAgentTurnAsync(
            request,
            Usage(AiUsageFeatures.Assistant));

        Assert.Collection(
            result.FunctionCalls,
            call =>
            {
                Assert.Equal("call-new-a", call.CallId);
                Assert.Equal("lookup", call.Name);
            },
            call => Assert.Equal("call-new-b", call.CallId));
        Assert.Equal(AiUsageProviders.OpenAi, result.Metadata?.Provider);
        Assert.Equal(OpenAiModels.Luna, result.Metadata?.Model);
        var requestJson = SerializeRequest(Assert.Single(transport.Requests)).GetRawText();
        Assert.Contains("call-old", requestJson, StringComparison.Ordinal);
        Assert.Contains("function_call_output", requestJson, StringComparison.Ordinal);
        Assert.Contains("lookup", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("previous_response_id", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_model_is_rejected_before_reserving_credits()
    {
        var credits = new RecordingCredits();
        var client = CreateClient(new RecordingTransport(), credits);

        await Assert.ThrowsAsync<GenerativeAiValidationException>(() =>
            client.GenerateStructuredAsync<StructuredFixture>(
                "test",
                Usage(AiUsageFeatures.Assistant),
                "an-alternative-model"));

        Assert.Empty(credits.Reservations);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Retryable_http_failures_release_reservation_and_map_to_dependency_error(
        int statusCode)
    {
        var transport = new RecordingTransport
        {
            Error = new OpenAiTransportException(statusCode, "upstream"),
        };
        var credits = new RecordingCredits();
        var client = CreateClient(transport, credits);

        await Assert.ThrowsAsync<GenerativeAiDependencyUnavailableException>(() =>
            client.GenerateJsonAsync<StructuredFixture>(
                "test",
                Usage(AiUsageFeatures.JsonImportRepair)));

        Assert.Single(credits.Releases);
        Assert.Empty(credits.Commits);
    }

    [Fact]
    public async Task Timeout_releases_reservation_and_maps_to_timeout_error()
    {
        var transport = new RecordingTransport { WaitForCancellation = true };
        var credits = new RecordingCredits();
        var client = CreateClient(transport, credits, timeoutSeconds: 1);

        await Assert.ThrowsAsync<GenerativeAiTimeoutException>(() =>
            client.GenerateJsonAsync<StructuredFixture>(
                "test",
                Usage(AiUsageFeatures.JsonImportRepair)));

        Assert.Single(credits.Releases);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_and_releases_reservation()
    {
        var transport = new RecordingTransport { WaitForCancellation = true };
        var credits = new RecordingCredits();
        var client = CreateClient(transport, credits, timeoutSeconds: 30);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GenerateJsonAsync<StructuredFixture>(
                "test",
                Usage(AiUsageFeatures.JsonImportRepair),
                cancellationToken: cancellation.Token));

        Assert.Single(credits.Releases);
    }

    [Fact]
    public async Task Confirmed_usage_is_settled_independently_if_normal_commit_fails()
    {
        var transport = new RecordingTransport
        {
            Response = Envelope("""{"value":"ok"}""", usage: new(5, 3, 0, 0, 8)),
        };
        var credits = new RecordingCredits { CommitError = new InvalidOperationException("db") };
        var client = CreateClient(transport, credits);

        await Assert.ThrowsAsync<GenerativeAiUpstreamException>(() =>
            client.GenerateJsonAsync<StructuredFixture>(
                "test",
                Usage(AiUsageFeatures.JsonImportRepair)));

        Assert.Single(credits.IndependentCommits);
        Assert.Empty(credits.Releases);
        Assert.Equal(8, Assert.Single(credits.IndependentCommits).Usage.TotalTokens);
    }

    [Fact]
    public async Task Transport_requires_openai_secret_key_before_network_use()
    {
        var transport = new OpenAiResponsesTransport(Options.Create(new GenerativeAiOptions
        {
            ApiKey = "",
            TimeoutSeconds = 180,
        }));

        await Assert.ThrowsAsync<GenerativeAiValidationException>(() =>
            transport.CreateResponseAsync(
                OpenAiRequestFactory.Create("learner", 32),
                CancellationToken.None));
    }

    [Fact]
    public void Options_validator_requires_key_in_production_and_luna_budget_price()
    {
        var usage = new AiUsageOptions
        {
            MonthlyBudget = new AiMonthlyBudgetOptions
            {
                Enabled = true,
                Providers = [AiUsageProviders.OpenAi],
                Models = [],
            },
        };
        var validator = new GenerativeAiOptionsValidator(
            new TestEnvironment { EnvironmentName = Environments.Production },
            Options.Create(usage));

        var result = validator.Validate(null, new GenerativeAiOptions());

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("OPENAI_SECRET_KEY", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains(OpenAiModels.Luna, StringComparison.Ordinal));
    }

    private static OpenAiGenerativeAiClient CreateClient(
        IOpenAiResponsesTransport transport,
        RecordingCredits credits,
        int timeoutSeconds = 10) =>
        new(
            transport,
            Options.Create(new GenerativeAiOptions
            {
                ApiKey = "test-key",
                TimeoutSeconds = timeoutSeconds,
            }),
            Options.Create(new AiUsageOptions
            {
                AssistantOutputTokenReserve = 50,
                JsonImportRepairOutputTokenReserve = 40,
                ImageExtractionOutputTokenReserve = 30,
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }),
            credits,
            NullLogger<OpenAiGenerativeAiClient>.Instance);

    private static OpenAiResponseEnvelope Envelope(
        string text,
        IReadOnlyList<OpenAiFunctionCall>? calls = null,
        AiTokenUsage? usage = null) =>
        new(
            text,
            calls ?? [],
            "resp-test",
            OpenAiModels.Luna,
            usage ?? new AiTokenUsage(3, 2, 0, 0, 5),
            IsIncomplete: false,
            IsRefusal: false);

    private static JsonElement SerializeRequest(CreateResponseOptions request)
    {
        using var document = JsonDocument.Parse(ModelReaderWriter.Write(request));
        return document.RootElement.Clone();
    }

    private static AiUsageContext Usage(string feature) =>
        new("learner-42", feature, "test", Guid.NewGuid());

    private static string Content(params object[] parts) =>
        JsonSerializer.Serialize(new { parts }, JsonOptions);

    private sealed class StructuredFixture
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class RecordingTransport : IOpenAiResponsesTransport
    {
        public List<CreateResponseOptions> Requests { get; } = [];
        public OpenAiResponseEnvelope Response { get; set; } = Envelope("done");
        public Exception? Error { get; init; }
        public bool WaitForCancellation { get; init; }

        public async Task<OpenAiResponseEnvelope> CreateResponseAsync(
            CreateResponseOptions request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (Error is not null)
            {
                throw Error;
            }
            return Response;
        }
    }

    private sealed class RecordingCredits : IAiCreditService
    {
        public List<(Guid Id, AiUsageContext Context, string Provider, string Model, int Tokens)>
            Reservations { get; } = [];
        public List<(Guid Id, AiTokenUsage Usage)> Commits { get; } = [];
        public List<(Guid Id, AiTokenUsage Usage)> IndependentCommits { get; } = [];
        public List<Guid> Releases { get; } = [];
        public Exception? CommitError { get; init; }

        public Task<AiCreditReservation> ReserveAsync(
            AiUsageContext context,
            string provider,
            string model,
            int estimatedTokens,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            Reservations.Add((id, context, provider, model, estimatedTokens));
            return Task.FromResult(new AiCreditReservation(id, context.UserId, 1, estimatedTokens));
        }

        public Task CommitUsageAsync(
            Guid reservationId,
            AiTokenUsage usage,
            CancellationToken cancellationToken = default)
        {
            if (CommitError is not null)
            {
                return Task.FromException(CommitError);
            }
            Commits.Add((reservationId, usage));
            return Task.CompletedTask;
        }

        public Task CommitUsageIndependentlyAsync(
            Guid reservationId,
            AiTokenUsage usage,
            CancellationToken cancellationToken = default)
        {
            IndependentCommits.Add((reservationId, usage));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default)
        {
            Releases.Add(reservationId);
            return Task.CompletedTask;
        }

        public Task<AiCreditAccountView> GetOrCreateAccountAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiCreditAccountView(userId, 100, 0, 100, null));

        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(
            string userId,
            int count = 25,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiCreditTransaction>>([]);

        public Task GrantAsync(
            string adminUserId,
            string targetUserId,
            int credits,
            string note,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Glosify.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

#pragma warning restore OPENAI001
