using System.Diagnostics.Metrics;
using Azure.AI.Projects;
using Azure.Identity;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class FoundryLiveSmokeTests
{
    private const string DefaultEndpoint =
        "https://glosify-foundry.services.ai.azure.com/api/projects/glosify-speaking";
    private const string OcrFixtureBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAUAAAAB4CAIAAAAMrLyJAAADZElEQVR42u3dTSvsbRzAcc6I8ZCcUIiIslFYSR5qdhZYoGxs7bEyL4F3YDMbRVl4moXxBhCl2MhG2YiF0SQLjcyc3R3/032a+2Dc8vns5mqu/+T39200TZfCbDZbAHxNP4wABAwIGBAwCBgQMCBgQMAgYEDAgIBBwICAAQEDAgYBAwIGBAwIGAQMCBgQMAgYEDAgYEDAIGBAwPkXDocLX9va2nqXK3d3dweuvLCw8IfnR6PRwg9WVVX1jqOLRCKB6y8tLWlGwICAQcBGAAIGBPzNzM/PZ99VKpUyVQEDAgYEDAgYBAwIGBAwCBgQMCBgQMAgYEDAwEcp+oY/89jYmBuPd2DeanFx8b1Ow2ppaTFPAQMCBgQMCDhoc3PzXc6v6erq8guEgAucifV2l5eX5ilgQMCAgAEBg4ABAZNndXV1f/3/R5+engxQwHxV6XQ6sFJUVGQsAuZreHx8DKyUl5cbi4DJ4w3+8eoWZzKZ3Pfe3t4GVsrKyoxUwORPOBx++fDh4SHHhjOZTDKZDCzW19cbqYDJn58/f758mM1mr66uctl4cXHx+4dYra2tRipg8qe5uTmwcnR0lMvG4+PjwEptbW1NTY2RCpj86ezsDKysrq7msnFtbS2wEolEzFPA5NXv1W1vbx8cHPx51+7u7tbWVmBxeHjYPAVMXg0ODgY+eXp+fp6YmDg5Ofm3LTs7O1NTU4HF6urqyclJ8xQwBR9xqN0/lpaWXr5EKBSamZkJvO719XVPT8/09HQikbi5uUmn06lU6vz8fGVlZWRkZGRk5O7uLrAlGo2Wlpa6ZQWOlSXPZmdnl5eXz87OCl5/TTIWi8VisVyu0N/fPzc3Z5LegfkEJSUl8Xi8sbHx77Z3dHRsb2+HQiGTFDCfo62t7fDwcHR09L9uHB8f39/fr66uNkMB85kaGhri8fjGxkZvb28uzx8YGEgkEuvr65WVlab3v1WYzWZN4bs5PT1NJBJ7e3vn5+fJZPL+/r64uLiioqKpqam9vb2vr29oaKi9vd2gBAz4ExoQMAgYEDAgYBAwIGBAwICAQcCAgAEBAwIGAQMCBgQMAgYEDAgYEDAIGBAwIGBAwCBgQMCAgEHAgIABAQMCBgEDAgYEDAI2AhAwIGBAwCBgQMCAgAEBg4ABAQNv9Ats+Jih2pO+zgAAAABJRU5ErkJggg==";

    [LiveFoundryFact]
    [Trait("Category", "LiveFoundry")]
    public async Task All_migrated_paths_emit_usage_and_keep_mutations_application_owned()
    {
        var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? DefaultEndpoint;
        var deployment = Environment.GetEnvironmentVariable("FOUNDRY_MODEL_DEPLOYMENT")
            ?? "gpt-5.4-mini";
        var projectClient = new AIProjectClient(
            new Uri(endpoint),
            new DefaultAzureCredential());
        var credits = new SmokeCredits();
        var options = new GenerativeAiOptions
        {
            Provider = GenerativeAiOptions.FoundryProvider,
            Foundry = new FoundryGenerativeAiOptions
            {
                ProjectEndpoint = endpoint,
                AssistantDeployment = deployment,
                StructuredDeployment = deployment,
                VisionDeployment = deployment,
                AllowedAssistantDeployments = [deployment],
                TimeoutSeconds = 180,
            },
        };
        var client = new FoundryGenerativeAiClient(
            new FoundryAgentInvoker(projectClient, NullLoggerFactory.Instance),
            Options.Create(options),
            Options.Create(new AiUsageOptions
            {
                RepairOutputTokenReserve = 512,
                ImageExtractionOutputTokenReserve = 512,
                AssistantOutputTokenReserve = 512,
            }),
            credits,
            NullLogger<FoundryGenerativeAiClient>.Instance);
        var telemetryRequests = 0L;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Glosify.GenerativeAi"
                    && instrument.Name == "glosify.ai.requests")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            telemetryRequests += measurement);
        meterListener.Start();

        var repaired = await client.GenerateStructuredAsync<RepairWordResult>(
            """
            Repair this vocabulary record and return the same id and quiz_id:
            {"word":{"id":"w1","word":"dmo","translation":"house","quiz_id":"q1"}}
            """,
            Usage(AiUsageFeatures.Repair, "live_repair"));
        Assert.Equal("w1", repaired.Word.Id);
        Assert.False(string.IsNullOrWhiteSpace(repaired.Word.Word));

        var extracted = await client.ExtractTextFromImageAsync(
            Convert.FromBase64String(OcrFixtureBase64),
            "image/png",
            "Return only the readable text in this image.",
            Usage(AiUsageFeatures.ImageExtraction, "live_ocr"));
        Assert.Contains("HEJ", extracted, StringComparison.OrdinalIgnoreCase);

        var readOnly = await client.RunAgentTurnAsync(
            ToolRequest(
                deployment,
                "Call get_quiz_summary exactly once for quiz q1.",
                new AgentToolDeclaration(
                    "get_quiz_summary",
                    "Reads a quiz summary without changing it.",
                    new
                    {
                        type = "object",
                        properties = new { quiz_id = new { type = "string" } },
                        required = new[] { "quiz_id" },
                    })),
            Usage(AiUsageFeatures.Assistant, "live_read_tool"));
        Assert.Equal("get_quiz_summary", Assert.Single(readOnly.FunctionCalls).Name);

        var mutation = await client.RunAgentTurnAsync(
            ToolRequest(
                deployment,
                "Call add_words exactly once to propose adding hej = hello. Do not claim it was applied.",
                new AgentToolDeclaration(
                    "add_words",
                    "Queues vocabulary changes for later user approval.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            words = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        word = new { type = "string" },
                                        translation = new { type = "string" },
                                    },
                                    required = new[] { "word", "translation" },
                                },
                            },
                        },
                        required = new[] { "words" },
                    })),
            Usage(AiUsageFeatures.Assistant, "live_queue_mutation"));
        // The smoke test intentionally does not execute the returned call. In the
        // application, AssistantOrchestrator turns it into a pending change.
        Assert.Equal("add_words", Assert.Single(mutation.FunctionCalls).Name);

        Assert.Equal(4, credits.Commits.Count);
        Assert.All(credits.Commits, commit => Assert.True(commit.Usage.TotalTokens > 0));
        Assert.True(telemetryRequests >= 4);
        Assert.Empty(credits.Releases);
    }

    [LiveFoundryFact]
    [Trait("Category", "LiveFoundry")]
    public async Task Selectable_assistant_deployments_support_the_application_owned_tool_loop()
    {
        var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? DefaultEndpoint;
        var deployments = (Environment.GetEnvironmentVariable("FOUNDRY_ASSISTANT_DEPLOYMENTS")
                ?? "grok-4-1-fast-non-reasoning,grok-4-1-fast-reasoning")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var projectClient = new AIProjectClient(
            new Uri(endpoint),
            new DefaultAzureCredential());

        foreach (var deployment in deployments)
        {
            var credits = new SmokeCredits();
            var client = new FoundryGenerativeAiClient(
                new FoundryAgentInvoker(projectClient, NullLoggerFactory.Instance),
                Options.Create(new GenerativeAiOptions
                {
                    Provider = GenerativeAiOptions.FoundryProvider,
                    Foundry = new FoundryGenerativeAiOptions
                    {
                        ProjectEndpoint = endpoint,
                        AssistantDeployment = deployment,
                        StructuredDeployment = "gpt-5.4-mini",
                        VisionDeployment = "gpt-5.4-mini",
                        AllowedAssistantDeployments = [deployment],
                        TimeoutSeconds = 180,
                    },
                }),
                Options.Create(new AiUsageOptions { AssistantOutputTokenReserve = 256 }),
                credits,
                NullLogger<FoundryGenerativeAiClient>.Instance);

            var turn = await client.RunAgentTurnAsync(
                ToolRequest(
                    deployment,
                    "Call get_quiz_summary exactly once for quiz q1.",
                    new AgentToolDeclaration(
                        "get_quiz_summary",
                        "Reads a quiz summary without changing it.",
                        new
                        {
                            type = "object",
                            properties = new { quiz_id = new { type = "string" } },
                            required = new[] { "quiz_id" },
                        })),
                Usage(AiUsageFeatures.Assistant, "live_selectable_model"));

            Assert.Equal("get_quiz_summary", Assert.Single(turn.FunctionCalls).Name);
            Assert.Single(credits.Commits);
            Assert.Empty(credits.Releases);
        }
    }

    private static AgentRequest ToolRequest(
        string deployment,
        string userMessage,
        AgentToolDeclaration declaration) =>
        new(
            "You must use the supplied function and return its call to the application. Do not simulate the result.",
            [new AgentTurn("user", Content(userMessage))],
            [declaration],
            deployment);

    private static string Content(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            parts = new[] { new { kind = "text", text } },
        });

    private static AiUsageContext Usage(string feature, string operation) =>
        new("foundry-smoke", feature, operation, Guid.NewGuid());

    private sealed class SmokeCredits : IAiCreditService
    {
        public List<(Guid Id, AiTokenUsage Usage)> Commits { get; } = [];
        public List<Guid> Releases { get; } = [];

        public Task<AiCreditReservation> ReserveAsync(
            AiUsageContext context,
            string provider,
            string model,
            int estimatedTokens,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            return Task.FromResult(new AiCreditReservation(
                id,
                context.UserId,
                1,
                estimatedTokens));
        }

        public Task CommitUsageAsync(
            Guid reservationId,
            AiTokenUsage usage,
            CancellationToken cancellationToken = default)
        {
            Commits.Add((reservationId, usage));
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
            Task.FromResult(new AiCreditAccountView(userId, 1_000, 0, 1_000, null));

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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

public sealed class LiveFoundryFactAttribute : FactAttribute
{
    public LiveFoundryFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_FOUNDRY_SMOKE_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set RUN_FOUNDRY_SMOKE_TESTS=true to call the live Foundry project.";
        }
    }
}
