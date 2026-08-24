using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class OpenAiLiveSmokeTests
{
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
    public async Task Luna_returns_code_owned_function_call()
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
        Assert.Single(credits.Commits);
    }

    private static OpenAiGenerativeAiClient CreateClient(SmokeCredits credits)
    {
        var options = Options.Create(new GenerativeAiOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("OPENAI_SECRET_KEY")!,
            TimeoutSeconds = 180,
        });
        return new OpenAiGenerativeAiClient(
            new OpenAiResponsesTransport(options),
            options,
            Options.Create(new AiUsageOptions
            {
                AssistantOutputTokenReserve = 512,
                JsonImportRepairOutputTokenReserve = 256,
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }),
            credits,
            NullLogger<OpenAiGenerativeAiClient>.Instance);
    }

    private static AiUsageContext Usage(string operation) =>
        new("live-smoke-learner", AiUsageFeatures.Assistant, operation, Guid.NewGuid());

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
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_SECRET_KEY")))
        {
            Skip = "Set OPENAI_SECRET_KEY to run direct OpenAI smoke tests.";
        }
    }
}
