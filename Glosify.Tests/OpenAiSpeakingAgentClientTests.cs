#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Text.Json;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Speaking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Responses;
using Xunit;

namespace Glosify.Tests;

public sealed class OpenAiSpeakingAgentClientTests
{
    [Fact]
    public void Code_catalog_covers_every_avatar_and_strict_contract()
    {
        foreach (var avatar in Enum.GetValues<SpeakingAvatarId>())
        {
            var profile = SpeakingPromptCatalog.Get(avatar, interactiveMode: false);
            Assert.False(string.IsNullOrWhiteSpace(profile.Instructions));
            using var schema = JsonDocument.Parse(profile.JsonSchema);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.Contains("replyPolish", schema.RootElement.GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()));
        }

        var interactive = SpeakingPromptCatalog.Get(SpeakingAvatarId.Bartender, true);
        Assert.True(interactive.UsesSceneTools);
        Assert.Contains("zero to three tools", interactive.Instructions, StringComparison.Ordinal);
        Assert.Contains("Legal first scene tools now", interactive.Instructions, StringComparison.Ordinal);

        var tutor = SpeakingPromptCatalog.Get(SpeakingAvatarId.TutorGerman, false);
        Assert.True(tutor.UsesQuizTools);
        Assert.Contains("practice", tutor.JsonSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conversation_uses_luna_store_false_safety_and_complete_local_history()
    {
        var transport = new QueueTransport(
            Envelope("Cześć!", "Hi!", new AiTokenUsage(8, 4, 1, 0, 12)),
            Envelope("Jak leci?", "How is it going?", new AiTokenUsage(14, 5, 1, 0, 19)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var client = new OpenAiSpeakingAgentClient(
            Options.Create(new GenerativeAiOptions
            {
                ApiKey = "test-key",
                TimeoutSeconds = 180,
            }),
            Options.Create(new AiUsageOptions { SpeakingOutputTokenReserve = 321 }),
            transport,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OpenAiSpeakingAgentClient>.Instance);
        await using var conversation = await client.CreateConversationAsync(
            "learner-7",
            SpeakingAvatarId.Kasia);

        var first = await conversation.RunTurnAsync("Cześć");
        var second = await conversation.RunTurnAsync("Co słychać?");

        Assert.Equal(12, first.Usage?.TotalTokens);
        Assert.Equal(19, second.Usage?.TotalTokens);
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, request =>
        {
            Assert.Equal(OpenAiModels.Luna, request.Model);
            Assert.False(request.StoredOutputEnabled ?? true);
            Assert.Equal(321, request.MaxOutputTokenCount);
            Assert.Equal(
                OpenAiRequestFactory.CreateSafetyIdentifier("learner-7"),
                request.SafetyIdentifier);
        });

        var firstJson = Serialize(transport.Requests[0]);
        var secondJson = Serialize(transport.Requests[1]);
        Assert.Contains("Cześć", firstJson, StringComparison.Ordinal);
        Assert.DoesNotContain("previous_response_id", secondJson, StringComparison.Ordinal);
        Assert.Contains("Cześć", secondJson, StringComparison.Ordinal);
        Assert.Contains("Co słychać?", secondJson, StringComparison.Ordinal);
        Assert.Contains("json_schema", secondJson, StringComparison.Ordinal);
        Assert.Contains("\"strict\":true", secondJson, StringComparison.Ordinal);
        var messages = transport.Requests[1].InputItems
            .OfType<MessageResponseItem>()
            .ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal("Cześć", Assert.Single(messages[0].Content).Text);
        using var assistantPayload = JsonDocument.Parse(Assert.Single(messages[1].Content).Text);
        Assert.Equal("Cześć!", assistantPayload.RootElement.GetProperty("replyPolish").GetString());
        Assert.Equal("Co słychać?", Assert.Single(messages[2].Content).Text);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Incomplete_or_refused_reply_aborts_without_retaining_the_turn(
        bool incomplete,
        bool refusal)
    {
        var transport = new QueueTransport(
            Envelope("", "", new AiTokenUsage(2, 0, 0, 0, 2)) with
            {
                IsIncomplete = incomplete,
                IsRefusal = refusal,
            },
            Envelope("Dobrze.", "Good.", new AiTokenUsage(3, 2, 0, 0, 5)));
        using var services = new ServiceCollection().BuildServiceProvider();
        var client = new OpenAiSpeakingAgentClient(
            Options.Create(new GenerativeAiOptions { ApiKey = "test-key" }),
            Options.Create(new AiUsageOptions()),
            transport,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OpenAiSpeakingAgentClient>.Instance);
        await using var conversation = await client.CreateConversationAsync(
            "learner-7",
            SpeakingAvatarId.Kasia);

        await Assert.ThrowsAsync<SpeakingUpstreamException>(() =>
            conversation.RunTurnAsync("failed turn"));
        await conversation.RunTurnAsync("next turn");

        var secondJson = Serialize(transport.Requests[1]);
        Assert.DoesNotContain("failed turn", secondJson, StringComparison.Ordinal);
        Assert.Contains("next turn", secondJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_continuation_replays_encrypted_reasoning_before_function_result()
    {
        const string reasoningJson =
            """{"type":"reasoning","id":"rs_speaking","encrypted_content":"speaking-state","summary":[]}""";
        var functionCall = new OpenAiFunctionCall("call-speaking", "unknown_tool", "{}");
        var functionCallJson = ModelReaderWriter.Write(
            ResponseItem.CreateFunctionCallItem(
                functionCall.CallId,
                functionCall.Name,
                BinaryData.FromString(functionCall.ArgumentsJson)))
            .ToString();
        var toolResponse = Envelope("", "", new AiTokenUsage(4, 2, 1, 0, 6)) with
        {
            FunctionCalls = [functionCall],
            OutputItemsJson = [reasoningJson, functionCallJson],
        };
        var finalResponse = Envelope("Dobrze.", "Good.", new AiTokenUsage(5, 3, 1, 0, 8));
        using var services = new ServiceCollection().BuildServiceProvider();
        var transport = new QueueTransport(toolResponse, finalResponse);
        var client = new OpenAiSpeakingAgentClient(
            Options.Create(new GenerativeAiOptions { ApiKey = "test-key" }),
            Options.Create(new AiUsageOptions { SpeakingOutputTokenReserve = 4096 }),
            transport,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OpenAiSpeakingAgentClient>.Instance);
        await using var conversation = await client.CreateConversationAsync(
            "learner-7",
            SpeakingAvatarId.Kasia);

        var turn = await conversation.RunTurnAsync("Pomóż mi.");

        Assert.Equal(14, turn.Usage?.TotalTokens);
        var continuation = transport.Requests[1].InputItems;
        Assert.Collection(
            continuation,
            item => Assert.IsAssignableFrom<MessageResponseItem>(item),
            item => Assert.Equal("speaking-state", Assert.IsType<ReasoningResponseItem>(item).EncryptedContent),
            item => Assert.Equal("call-speaking", Assert.IsType<FunctionCallResponseItem>(item).CallId),
            item => Assert.Equal("call-speaking", Assert.IsType<FunctionCallOutputResponseItem>(item).CallId));
    }

    private static OpenAiResponseEnvelope Envelope(
        string replyPolish,
        string replyEnglish,
        AiTokenUsage usage) =>
        new(
            JsonSerializer.Serialize(new
            {
                replyPolish,
                replyEnglish,
                coach = new
                {
                    correctedPolish = replyPolish,
                    grammarTipEnglish = "",
                    vocabularyTipEnglish = "",
                    naturalnessTipEnglish = "",
                },
            }),
            [],
            Guid.NewGuid().ToString("N"),
            OpenAiModels.Luna,
            usage,
            IsIncomplete: false,
            IsRefusal: false);

    private static string Serialize(CreateResponseOptions request) =>
        ModelReaderWriter.Write(request).ToString();

    private sealed class QueueTransport(params OpenAiResponseEnvelope[] responses)
        : IOpenAiResponsesTransport
    {
        private readonly Queue<OpenAiResponseEnvelope> _responses = new(responses);
        public List<CreateResponseOptions> Requests { get; } = [];

        public Task<OpenAiResponseEnvelope> CreateResponseAsync(
            CreateResponseOptions request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}

#pragma warning restore OPENAI001
