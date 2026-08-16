using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class FoundryGenerativeAiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Budget disabled: these tests exercise deployment shape, not pricing.</summary>
    private static IOptions<AiUsageOptions> UnbudgetedUsage() =>
        Options.Create(new AiUsageOptions
        {
            MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
        });

    [Fact]
    public void History_mapper_preserves_roles_text_calls_results_and_legacy_call_ids()
    {
        var history = new[]
        {
            new AgentTurn("user", Content(new { kind = "text", text = "Hello" })),
            new AgentTurn("model", Content(
                new
                {
                    kind = "function_call",
                    name = "lookup",
                    argsJson = """{"word":"hej"}""",
                    thoughtSignature = "ignored-legacy-provider-data",
                },
                new
                {
                    kind = "function_call",
                    name = "lookup",
                    argsJson = """{"word":"tack"}""",
                    callId = "call-2",
                })),
            new AgentTurn("user", Content(
                new
                {
                    kind = "function_response",
                    name = "lookup",
                    responseJson = """{"translation":"hello"}""",
                },
                new
                {
                    kind = "function_response",
                    name = "lookup",
                    responseJson = """{"translation":"thanks"}""",
                    callId = "call-2",
                })),
        };

        var messages = FoundryMessageMapper.MapHistory(history);

        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("Hello", Assert.IsType<TextContent>(Assert.Single(messages[0].Contents)).Text);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        var calls = messages[1].Contents.OfType<FunctionCallContent>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("legacy-call-1-0", calls[0].CallId);
        Assert.Equal("call-2", calls[1].CallId);
        Assert.Equal(ChatRole.Tool, messages[2].Role);
        var results = messages[2].Contents.OfType<FunctionResultContent>().ToArray();
        Assert.Equal("legacy-call-1-0", results[0].CallId);
        Assert.Equal("call-2", results[1].CallId);
    }

    [Fact]
    public void Tool_mapper_preserves_name_description_and_parameter_schema()
    {
        var tools = FoundryMessageMapper.MapTools(
        [
            new AgentToolDeclaration(
                "lookup_word",
                "Looks up a word.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        word = new { type = "string" },
                    },
                    required = new[] { "word" },
                }),
        ]);

        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(tools));
        Assert.Equal("lookup_word", declaration.Name);
        Assert.Equal("Looks up a word.", declaration.Description);
        Assert.Equal("object", declaration.JsonSchema.GetProperty("type").GetString());
        Assert.True(declaration.JsonSchema.GetProperty("properties").TryGetProperty("word", out _));
    }

    [Fact]
    public async Task Assistant_turn_returns_multiple_calls_and_commits_exact_foundry_usage()
    {
        var invoker = new FakeInvoker
        {
            Response = Response(
                [
                    new TextContent("Checking both."),
                    new FunctionCallContent(
                        "call-a",
                        "lookup",
                        new Dictionary<string, object?> { ["word"] = "hej" }),
                    new FunctionCallContent(
                        "call-b",
                        "lookup",
                        new Dictionary<string, object?> { ["word"] = "tack" }),
                ],
                new UsageDetails
                {
                    InputTokenCount = 91,
                    OutputTokenCount = 27,
                    ReasoningTokenCount = 3,
                    TotalTokenCount = 118,
                }),
        };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        var result = await client.RunAgentTurnAsync(
            new AgentRequest(
                "Help.",
                [new AgentTurn("user", Content(new { kind = "text", text = "Translate two words." }))],
                [],
                "gpt-5.4-mini"),
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal("Checking both.", result.Text);
        Assert.Collection(
            result.FunctionCalls,
            call =>
            {
                Assert.Equal("call-a", call.CallId);
                Assert.Equal("lookup", call.Name);
                Assert.Contains("hej", call.ArgsJson);
            },
            call => Assert.Equal("call-b", call.CallId));
        var reservation = Assert.Single(credits.Reservations);
        Assert.Equal("foundry", reservation.Provider);
        Assert.False(invoker.EnableSensitiveData);
        Assert.Equal("gpt-5.4-mini", reservation.Model);
        Assert.Equal(
            new AiTokenUsage(91, 27, 3, 0, 118),
            Assert.Single(credits.Commits).Usage);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Assistant_turn_rejects_length_truncated_tool_call_before_commit()
    {
        var response = Response(
            [
                new FunctionCallContent(
                    "call-a",
                    "create_vocabulary_quiz",
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Polish lyrics",
                        ["source_language"] = "English",
                    }),
            ],
            new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 2048,
                TotalTokenCount = 2148,
            });
        response.FinishReason = ChatFinishReason.Length;
        var credits = new FakeCredits();
        var client = CreateClient(new FakeInvoker { Response = response }, credits);

        var exception = await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            client.RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant)));

        Assert.Contains("too large to finish", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(credits.Reservations);
        Assert.Empty(credits.Commits);
        Assert.Equal(2148, Assert.Single(credits.IndependentCommits).Usage.TotalTokens);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Assistant_turn_rejects_tool_call_without_arguments_before_commit()
    {
        var call = new FunctionCallContent(
            "call-a",
            "create_vocabulary_quiz",
            new Dictionary<string, object?> { ["name"] = "Polish lyrics" })
        {
            Arguments = null!,
        };
        var credits = new FakeCredits();
        var client = CreateClient(
            new FakeInvoker { Response = Response([call]) },
            credits);

        var exception = await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            client.RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant)));

        Assert.Contains("finish preparing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(credits.Reservations);
        Assert.Empty(credits.Commits);
        Assert.Single(credits.IndependentCommits);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Assistant_turn_rejects_models_outside_the_allowlist_before_charging()
    {
        var credits = new FakeCredits();
        var client = CreateClient(new FakeInvoker(), credits);

        await Assert.ThrowsAsync<GenerativeAiValidationException>(() =>
            client.RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "unapproved-model"),
                Usage(AiUsageFeatures.Assistant)));

        Assert.Empty(credits.Reservations);
    }

    [Fact]
    public async Task Structured_output_success_commits_usage_and_returns_the_typed_result()
    {
        var invoker = new FakeInvoker
        {
            StructuredJson = """{"value":"generated"}""",
            StructuredUsage = new UsageDetails
            {
                InputTokenCount = 20,
                OutputTokenCount = 5,
                TotalTokenCount = 25,
            },
        };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        var result = await client.GenerateStructuredAsync<StructuredFixture>(
            "Generate this.",
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal("generated", result.Value);
        Assert.Equal(
            new AiTokenUsage(20, 5, 0, 0, 25),
            Assert.Single(credits.Commits).Usage);
    }

    [Fact]
    public async Task Assistant_turn_uses_the_same_estimated_usage_for_credits_and_analytics_when_provider_usage_is_missing()
    {
        var invoker = new FakeInvoker
        {
            Response = Response([new TextContent("Done.")]),
        };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        var result = await client.RunAgentTurnAsync(
            new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
            Usage(AiUsageFeatures.Assistant));

        var committed = Assert.Single(credits.Commits).Usage;
        Assert.True(committed.TotalTokens > 0);
        Assert.Equal(committed, result.Metadata!.Usage);
    }

    [Fact]
    public async Task Json_output_uses_plain_response_mode_and_returns_the_typed_result()
    {
        var invoker = new FakeInvoker
        {
            Response = Response(
                [new TextContent("```json\n{\"value\":\"translated\"}\n```")],
                new UsageDetails
                {
                    InputTokenCount = 18,
                    OutputTokenCount = 4,
                    TotalTokenCount = 22,
                }),
        };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        var result = await client.GenerateJsonAsync<StructuredFixture>(
            "Translate this.",
            Usage(AiUsageFeatures.PageTranslation));

        Assert.Equal("translated", result.Value);
        Assert.Equal(
            new AiTokenUsage(18, 4, 0, 0, 22),
            Assert.Single(credits.Commits).Usage);
        Assert.Empty(credits.Releases);
        Assert.False(invoker.EnableSensitiveData);
    }

    [Fact]
    public async Task Invalid_json_output_charges_the_confirmed_usage()
    {
        var invoker = new FakeInvoker
        {
            Response = Response([new TextContent("not json")]),
        };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            client.GenerateJsonAsync<StructuredFixture>(
                "Translate this.",
                Usage(AiUsageFeatures.PageTranslation)));

        Assert.Empty(credits.Commits);
        Assert.Single(credits.IndependentCommits);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Structured_refusal_is_typed_and_charges_the_confirmed_usage()
    {
        var invoker = new FakeInvoker
        {
            StructuredJson = """{"value":"ignored"}""",
            StructuredFinishReason = ChatFinishReason.ContentFilter,
            StructuredUsage = new UsageDetails
            {
                InputTokenCount = 12,
                OutputTokenCount = 1,
                TotalTokenCount = 13,
            },
        };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            client.GenerateStructuredAsync<StructuredFixture>(
                "Generate this.",
                Usage(AiUsageFeatures.Assistant)));

        Assert.Empty(credits.Commits);
        Assert.Equal(13, Assert.Single(credits.IndependentCommits).Usage.TotalTokens);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Invalid_structured_schema_charges_confirmed_usage_and_is_sanitized()
    {
        var invoker = new FakeInvoker { StructuredJson = "{" };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        var exception = await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            client.GenerateStructuredAsync<StructuredFixture>(
                "Generate this.",
                Usage(AiUsageFeatures.Assistant)));

        Assert.Equal("The AI service could not produce a valid structured response.", exception.Message);
        Assert.Single(credits.IndependentCommits);
        Assert.Empty(credits.Releases);
        Assert.Empty(credits.Commits);
    }

    [Fact]
    public async Task Empty_structured_response_is_typed_and_charges_confirmed_usage()
    {
        var invoker = new FakeInvoker
        {
            StructuredJson = "null",
            StructuredUsage = new UsageDetails { TotalTokenCount = 7 },
        };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            client.GenerateStructuredAsync<StructuredFixture>(
                "Generate this.",
                Usage(AiUsageFeatures.Assistant)));

        Assert.Empty(credits.Commits);
        Assert.Equal(7, Assert.Single(credits.IndependentCommits).Usage.TotalTokens);
        Assert.Empty(credits.Releases);
    }

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("image/jpg", "image/jpeg")]
    public async Task Image_extraction_maps_supported_media_and_returns_normalized_text(
        string suppliedType,
        string expectedType)
    {
        var invoker = new FakeInvoker
        {
            Response = Response([new TextContent("  Hej världen  ")], new UsageDetails
            {
                InputTokenCount = 30,
                OutputTokenCount = 4,
                TotalTokenCount = 34,
            }),
        };
        var credits = new FakeCredits();
        var client = CreateClient(invoker, credits);

        var text = await client.ExtractTextFromImageAsync(
            [1, 2, 3],
            suppliedType,
            "Extract.",
            Usage(AiUsageFeatures.ImageExtraction));

        Assert.Equal("Hej världen", text);
        var data = Assert.Single(
            Assert.Single(invoker.Messages).Contents.OfType<DataContent>());
        Assert.Equal(expectedType, data.MediaType);
        Assert.Single(credits.Commits);
        Assert.False(invoker.EnableSensitiveData);
    }

    [Fact]
    public async Task Unsupported_image_media_is_rejected_before_charging()
    {
        var credits = new FakeCredits();
        var client = CreateClient(new FakeInvoker(), credits);

        await Assert.ThrowsAsync<GenerativeAiValidationException>(() =>
            client.ExtractTextFromImageAsync(
                [1],
                "image/gif",
                "Extract.",
                Usage(AiUsageFeatures.ImageExtraction)));

        Assert.Empty(credits.Reservations);
    }

    [Fact]
    public async Task Empty_image_extraction_is_a_valid_committed_no_text_result()
    {
        var invoker = new FakeInvoker
        {
            Response = Response([new TextContent("   ")], new UsageDetails
            {
                InputTokenCount = 8,
                OutputTokenCount = 0,
                TotalTokenCount = 8,
            }),
        };
        var credits = new FakeCredits();

        var result = await CreateClient(invoker, credits).ExtractTextFromImageAsync(
            [1, 2, 3],
            "image/png",
            "Extract.",
            Usage(AiUsageFeatures.ImageExtraction));

        Assert.Equal(string.Empty, result);
        Assert.Single(credits.Commits);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Cancellation_and_upstream_failure_release_reservations()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledCredits = new FakeCredits();
        var cancelledClient = CreateClient(
            new FakeInvoker { Error = new OperationCanceledException(cancellation.Token) },
            cancelledCredits);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelledClient.RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant),
                cancellation.Token));
        Assert.Single(cancelledCredits.Releases);

        var failedCredits = new FakeCredits();
        var failedClient = CreateClient(
            new FakeInvoker { Error = new HttpRequestException("sensitive transport detail") },
            failedCredits);
        var exception = await Assert.ThrowsAsync<GenerativeAiDependencyUnavailableException>(() =>
            failedClient.RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant)));
        Assert.Equal("The AI service is temporarily unavailable. Please try again.", exception.Message);
        Assert.Single(failedCredits.Releases);
    }

    [Fact]
    public async Task Usage_commit_failure_charges_returned_usage_from_an_independent_scope()
    {
        var invoker = new FakeInvoker
        {
            Response = Response(
                [new TextContent("Done.")],
                new UsageDetails
                {
                    InputTokenCount = 19,
                    OutputTokenCount = 6,
                    TotalTokenCount = 25,
                }),
        };
        var credits = new FakeCredits
        {
            CommitError = new InvalidOperationException("database commit failed"),
        };

        await Assert.ThrowsAsync<GenerativeAiUpstreamException>(() =>
            CreateClient(invoker, credits).RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant)));

        Assert.Empty(credits.Commits);
        Assert.Equal(
            new AiTokenUsage(19, 6, 0, 0, 25),
            Assert.Single(credits.IndependentCommits).Usage);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Cancellation_during_usage_commit_still_charges_returned_usage()
    {
        using var cancellation = new CancellationTokenSource();
        var credits = new FakeCredits
        {
            BeforeCommit = cancellation.Cancel,
            CommitError = new OperationCanceledException(cancellation.Token),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(new FakeInvoker(), credits).RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant),
                cancellation.Token));

        Assert.Single(credits.IndependentCommits);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Emits_sanitized_provider_feature_deployment_usage_and_credit_metrics()
    {
        var measurements = new List<(string Instrument, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Glosify.GenerativeAi")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        listener.Start();
        var invoker = new FakeInvoker
        {
            Response = Response([new TextContent("Done.")], new UsageDetails
            {
                InputTokenCount = 11,
                OutputTokenCount = 4,
                TotalTokenCount = 15,
            }),
        };

        await CreateClient(invoker, new FakeCredits()).RunAgentTurnAsync(
            new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
            Usage(AiUsageFeatures.Assistant));

        Assert.Contains(measurements, item =>
            item.Instrument == "glosify.ai.requests"
            && (string?)item.Tags["ai.feature"] == AiUsageFeatures.Assistant
            && (string?)item.Tags["ai.provider"] == "foundry"
            && (string?)item.Tags["ai.deployment"] == "gpt-5.4-mini");
        Assert.Contains(measurements, item =>
            item.Instrument == "glosify.ai.input_tokens"
            && (string?)item.Tags["ai.outcome"] == "success");
        Assert.Contains(measurements, item =>
            item.Instrument == "glosify.ai.credit_commits");
        Assert.DoesNotContain(measurements.SelectMany(item => item.Tags.Keys), key =>
            key.Contains("prompt", StringComparison.OrdinalIgnoreCase)
            || key.Contains("argument", StringComparison.OrdinalIgnoreCase)
            || key.Contains("image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Quiz_builder_profile_runs_on_the_instructions_authored_in_foundry()
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent("You build custom quiz elements.", null, []),
        };
        var client = CreateClient(invoker, new FakeCredits(), ConfigureQuizBuilderAgent);

        var result = await client.RunAgentTurnAsync(
            new AgentRequest(
                "In-code fallback instruction.",
                [],
                [],
                Model: null,
                Profile: AssistantAgentProfile.CustomQuizBuilder,
                ContextInstruction: "The open custom quiz is \"Verb drills\"."),
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal("glosify-quiz-builder@3", Assert.Single(invoker.AuthoredLookups));
        Assert.Contains("You build custom quiz elements.", invoker.Instructions);
        Assert.Contains("The open custom quiz is \"Verb drills\".", invoker.Instructions);
        Assert.DoesNotContain("In-code fallback instruction.", invoker.Instructions);
        Assert.Equal("glosify-quiz-builder", result.Metadata!.AgentName);
        Assert.Equal("3", result.Metadata.AgentVersion);
    }

    [Theory]
    [InlineData(AssistantAgentProfile.FreestyleQuizAssistant, "glosify-freestyle-quiz-assistant")]
    [InlineData(AssistantAgentProfile.FreestyleLibrarian, "glosify-freestyle-librarian")]
    [InlineData(AssistantAgentProfile.FreestyleCustomQuizBuilder, "glosify-freestyle-quiz-builder")]
    public async Task Freestyle_profiles_use_their_dedicated_authored_agents(
        AssistantAgentProfile profile,
        string expectedAgentName)
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent("You are a general study assistant.", "gpt-5.6-luna", []),
        };
        var client = CreateClient(invoker, new FakeCredits(), foundry =>
        {
            foundry.AllowedAssistantDeployments = ["gpt-5.4-mini", "gpt-5.6-luna"];
            var configured = new FoundryAgentVersionOptions
            {
                Name = expectedAgentName,
                Version = "1",
            };
            switch (profile)
            {
                case AssistantAgentProfile.FreestyleQuizAssistant:
                    foundry.Agents.FreestyleQuizAssistant = configured;
                    break;
                case AssistantAgentProfile.FreestyleLibrarian:
                    foundry.Agents.FreestyleLibrarian = configured;
                    break;
                default:
                    foundry.Agents.FreestyleCustomQuizBuilder = configured;
                    break;
            }
        });

        var result = await client.RunAgentTurnAsync(
            new AgentRequest(
                "Generic fallback.",
                [],
                [],
                Model: null,
                Profile: profile,
                ContextInstruction: "The Cardiology quiz is open."),
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal($"{expectedAgentName}@1", Assert.Single(invoker.AuthoredLookups));
        Assert.Contains("general study assistant", invoker.Instructions);
        Assert.Contains("The Cardiology quiz is open.", invoker.Instructions);
        Assert.Equal("gpt-5.6-luna", invoker.Deployment);
        Assert.Equal(expectedAgentName, result.Metadata!.AgentName);
    }

    // Tools are defined in Foundry, but which of them a given turn may use is the
    // application's call: the per-turn allowlist subtracts from the authored surface.
    [Fact]
    public async Task Authored_tools_are_narrowed_by_the_per_turn_allowlist()
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent(
                "You build custom quiz elements.",
                null,
                [Declaration("add_word"), Declaration("add_sentence")]),
        };
        var client = CreateClient(invoker, new FakeCredits(), ConfigureQuizBuilderAgent);

        await client.RunAgentTurnAsync(
            new AgentRequest(
                "In-code fallback instruction.",
                [],
                [],
                Model: null,
                Profile: AssistantAgentProfile.CustomQuizBuilder,
                ContextInstruction: null,
                AllowedToolNames: new HashSet<string> { "add_sentence" }),
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal(["add_sentence"], invoker.Tools.Select(tool => tool.Name));
    }

    // The allowlist is a filter, never a source. A name the published agent does not declare
    // cannot appear just because the application would have permitted it.
    [Fact]
    public async Task The_allowlist_cannot_broaden_the_authored_surface()
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent(
                "You build custom quiz elements.",
                null,
                [Declaration("add_sentence")]),
        };
        var client = CreateClient(invoker, new FakeCredits(), ConfigureQuizBuilderAgent);

        await client.RunAgentTurnAsync(
            new AgentRequest(
                "In-code fallback instruction.",
                [],
                [Declaration("delete_word")],
                Model: null,
                Profile: AssistantAgentProfile.CustomQuizBuilder,
                ContextInstruction: null,
                AllowedToolNames: new HashSet<string> { "add_sentence", "delete_word" }),
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal(["add_sentence"], invoker.Tools.Select(tool => tool.Name));
    }

    private static AgentToolDeclaration Declaration(string name) =>
        new(name, name, new { type = "object", properties = new { } });

    // The assistant has to keep working before the agent is authored in Foundry, and
    // has to survive the project being unreachable.
    [Fact]
    public async Task Quiz_builder_profile_falls_back_to_in_code_instructions_when_the_agent_is_unavailable()
    {
        var invoker = new FakeInvoker { AuthoredAgent = null };
        var client = CreateClient(invoker, new FakeCredits(), ConfigureQuizBuilderAgent);

        var result = await client.RunAgentTurnAsync(
            new AgentRequest(
                "In-code fallback instruction.",
                [],
                [],
                Model: null,
                Profile: AssistantAgentProfile.CustomQuizBuilder,
                ContextInstruction: "The open custom quiz is \"Verb drills\"."),
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal("In-code fallback instruction.", invoker.Instructions);
        Assert.Null(result.Metadata!.AgentName);
        Assert.Null(result.Metadata.AgentVersion);
    }

    [Fact]
    public async Task Unconfigured_profiles_never_look_up_an_agent()
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent("You build custom quiz elements.", null, []),
        };
        var client = CreateClient(invoker, new FakeCredits(), ConfigureQuizBuilderAgent);

        await client.RunAgentTurnAsync(
            new AgentRequest("General assistant instruction.", [], []),
            Usage(AiUsageFeatures.Assistant));

        Assert.Empty(invoker.AuthoredLookups);
        Assert.Equal("General assistant instruction.", invoker.Instructions);
    }

    [Fact]
    public async Task Authored_model_is_a_default_that_the_user_selection_overrides()
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent("Build elements.", "grok-4-1-fast-non-reasoning", []),
        };
        var client = CreateClient(invoker, new FakeCredits(), foundry =>
        {
            ConfigureQuizBuilderAgent(foundry);
            foundry.AllowedAssistantDeployments = ["gpt-5.4-mini", "grok-4-1-fast-non-reasoning"];
        });

        await client.RunAgentTurnAsync(
            new AgentRequest("Fallback.", [], [], "gpt-5.4-mini", AssistantAgentProfile.CustomQuizBuilder),
            Usage(AiUsageFeatures.Assistant));
        Assert.Equal("gpt-5.4-mini", invoker.Deployment);

        await client.RunAgentTurnAsync(
            new AgentRequest("Fallback.", [], [], Model: null, Profile: AssistantAgentProfile.CustomQuizBuilder),
            Usage(AiUsageFeatures.Assistant));
        Assert.Equal("grok-4-1-fast-non-reasoning", invoker.Deployment);
    }

    // Editing an agent in the Foundry portal must not be able to route traffic to a
    // deployment the application has not approved.
    [Fact]
    public async Task Authored_model_outside_the_allowlist_is_ignored()
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent("Build elements.", "some-unapproved-deployment", []),
        };
        var client = CreateClient(invoker, new FakeCredits(), ConfigureQuizBuilderAgent);

        await client.RunAgentTurnAsync(
            new AgentRequest("Fallback.", [], [], Model: null, Profile: AssistantAgentProfile.CustomQuizBuilder),
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal("gpt-5.4-mini", invoker.Deployment);
    }

    // The agent owns which tools exist and how they are described; Glosify still runs
    // them, matching handlers by name.
    [Fact]
    public async Task Tools_declared_on_the_agent_replace_the_in_code_declarations()
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent(
                "Build elements.",
                null,
                [new AgentToolDeclaration("add_text_input", "Add one graded text answer.", new { type = "object" })]),
        };
        var client = CreateClient(invoker, new FakeCredits(), ConfigureQuizBuilderAgent);

        await client.RunAgentTurnAsync(
            new AgentRequest(
                "Fallback.",
                [],
                [new AgentToolDeclaration("in_code_only", "Should not be offered.", new { type = "object" })],
                Model: null,
                Profile: AssistantAgentProfile.CustomQuizBuilder),
            Usage(AiUsageFeatures.Assistant));

        var offered = invoker.Tools.Select(tool => tool.Name).ToList();
        Assert.Equal(["add_text_input"], offered);
    }

    [Fact]
    public async Task In_code_declarations_are_used_when_the_agent_declares_no_tools()
    {
        var invoker = new FakeInvoker
        {
            AuthoredAgent = new FoundryAuthoredAgent("Build elements.", null, []),
        };
        var client = CreateClient(invoker, new FakeCredits(), ConfigureQuizBuilderAgent);

        await client.RunAgentTurnAsync(
            new AgentRequest(
                "Fallback.",
                [],
                [new AgentToolDeclaration("in_code_only", "Offered because the agent declares none.", new { type = "object" })],
                Model: null,
                Profile: AssistantAgentProfile.CustomQuizBuilder),
            Usage(AiUsageFeatures.Assistant));

        Assert.Equal(["in_code_only"], invoker.Tools.Select(tool => tool.Name).ToList());
    }

    private static void ConfigureQuizBuilderAgent(FoundryGenerativeAiOptions foundry) =>
        foundry.Agents.CustomQuizBuilder = new FoundryAgentVersionOptions
        {
            Name = "glosify-quiz-builder",
            Version = "3",
        };

    [Fact]
    public async Task Credit_step_failures_emit_outcomes_and_do_not_mask_the_original_ai_failure()
    {
        var measurements = new List<(string Instrument, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Glosify.GenerativeAi")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        listener.Start();

        var reservationFailure = new FakeCredits
        {
            ReserveError = new InsufficientAiCreditsException(0, 1),
        };
        await Assert.ThrowsAsync<InsufficientAiCreditsException>(() =>
            CreateClient(new FakeInvoker(), reservationFailure).RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant)));

        var commitFailure = new FakeCredits
        {
            CommitError = new InvalidOperationException("sensitive database detail"),
        };
        await Assert.ThrowsAsync<GenerativeAiUpstreamException>(() =>
            CreateClient(new FakeInvoker(), commitFailure).RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant)));
        Assert.Single(commitFailure.IndependentCommits);
        Assert.Empty(commitFailure.Releases);

        var independentCommitFailure = new FakeCredits
        {
            CommitError = new InvalidOperationException("sensitive database detail"),
            IndependentCommitError = new InvalidOperationException("sensitive recovery detail"),
        };
        await Assert.ThrowsAsync<GenerativeAiUpstreamException>(() =>
            CreateClient(new FakeInvoker(), independentCommitFailure).RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant)));
        Assert.Empty(independentCommitFailure.Commits);
        Assert.Empty(independentCommitFailure.IndependentCommits);
        Assert.Empty(independentCommitFailure.Releases);

        var releaseFailure = new FakeCredits
        {
            ReleaseError = new InvalidOperationException("sensitive release detail"),
        };
        var originalFailure = await Assert.ThrowsAsync<GenerativeAiDependencyUnavailableException>(() =>
            CreateClient(
                new FakeInvoker { Error = new HttpRequestException("sensitive transport detail") },
                releaseFailure).RunAgentTurnAsync(
                new AgentRequest("Help.", [], [], "gpt-5.4-mini"),
                Usage(AiUsageFeatures.Assistant)));
        Assert.Equal(
            "The AI service is temporarily unavailable. Please try again.",
            originalFailure.Message);

        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "glosify.ai.credit_reservations"
            && (string?)measurement.Tags["ai.outcome"] == "reservation_failed");
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "glosify.ai.credit_commits"
            && (string?)measurement.Tags["ai.outcome"] == "commit_failed");
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "glosify.ai.credit_commits"
            && (string?)measurement.Tags["ai.outcome"] == "failure_usage_committed");
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "glosify.ai.credit_commits"
            && (string?)measurement.Tags["ai.outcome"] == "failure_usage_commit_failed");
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "glosify.ai.input_tokens"
            && (string?)measurement.Tags["ai.outcome"] == "usage_commit_failed");
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "glosify.ai.credit_releases"
            && (string?)measurement.Tags["ai.outcome"] == "release_failed");
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "glosify.ai.duration"
            && (string?)measurement.Tags["ai.outcome"] == "reservation_failed");
    }

    [Fact]
    public void Options_validator_accepts_foundry_and_requires_gemini_credentials_only_for_rollback()
    {
        var foundry = ValidOptions();
        var noGeminiKey = new GenerativeAiOptionsValidator(Options.Create(new GeminiOptions()), UnbudgetedUsage());
        Assert.True(noGeminiKey.Validate(null, foundry).Succeeded);

        foundry.Provider = GenerativeAiOptions.GeminiProvider;
        Assert.True(noGeminiKey.Validate(null, foundry).Failed);

        var withGeminiKey = new GenerativeAiOptionsValidator(
            Options.Create(new GeminiOptions { ApiKey = "configured-secret" }),
            UnbudgetedUsage());
        Assert.True(withGeminiKey.Validate(null, foundry).Succeeded);
    }

    [Fact]
    public void Options_validator_rejects_gemini_rollback_when_the_enabled_budget_cannot_price_it()
    {
        var options = ValidOptions();
        options.Provider = GenerativeAiOptions.GeminiProvider;
        var usage = new AiUsageOptions
        {
            MonthlyBudget = new AiMonthlyBudgetOptions
            {
                Enabled = true,
                Providers = [AiUsageProviders.Foundry],
            },
        };
        var validator = new GenerativeAiOptionsValidator(
            Options.Create(new GeminiOptions { ApiKey = "configured-secret" }),
            Options.Create(usage));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("includes 'gemini'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://example.test/project", "gpt-5.4-mini", "gpt-5.4-mini", "gpt-5.4-mini", 180)]
    [InlineData("https://example.test/project", "", "gpt-5.4-mini", "gpt-5.4-mini", 180)]
    [InlineData("https://example.test/project", "gpt-5.4-mini", "", "gpt-5.4-mini", 180)]
    [InlineData("https://example.test/project", "gpt-5.4-mini", "gpt-5.4-mini", "", 180)]
    [InlineData("https://example.test/project", "gpt-5.4-mini", "gpt-5.4-mini", "gpt-5.4-mini", 0)]
    public void Options_validator_rejects_invalid_foundry_configuration(
        string endpoint,
        string assistant,
        string structured,
        string vision,
        int timeout)
    {
        var options = ValidOptions();
        options.Foundry.ProjectEndpoint = endpoint;
        options.Foundry.AssistantDeployment = assistant;
        options.Foundry.StructuredDeployment = structured;
        options.Foundry.VisionDeployment = vision;
        options.Foundry.TimeoutSeconds = timeout;

        var result = new GenerativeAiOptionsValidator(Options.Create(new GeminiOptions()), UnbudgetedUsage())
            .Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Model_resolver_enforces_allowlist_and_preserves_configured_casing()
    {
        var options = ValidOptions();
        options.Foundry.AllowedAssistantDeployments.Add("grok-4-1-fast-reasoning");
        options.Foundry.AssistantModels.Add(new AssistantModelOptions
        {
            Deployment = "grok-4-1-fast-reasoning",
            DisplayName = "Grok 4.1 Fast Reasoning",
            Provider = "xAI",
            SpeedTier = "Thoughtful",
            CostTier = "Premium",
            CreditMultiplier = 2m,
        });
        var resolver = new GenerativeAiModelResolver(
            Options.Create(options),
            Options.Create(new GeminiOptions()));

        Assert.Equal("gpt-5.4-mini", resolver.ResolveAssistantModel(null));
        Assert.Equal("gpt-5.4-mini", resolver.ResolveAssistantModel("GPT-5.4-MINI"));
        Assert.Equal(
            "Grok 4.1 Fast Reasoning",
            resolver.AssistantModels.Single(model =>
                model.Deployment == "grok-4-1-fast-reasoning").DisplayName);
        Assert.Equal(2m, resolver.GetCreditMultiplier("GROK-4-1-FAST-REASONING"));
        Assert.Equal(1m, resolver.GetCreditMultiplier("unknown-model"));
        Assert.Throws<GenerativeAiValidationException>(() =>
            resolver.ResolveAssistantModel("gpt-5.6"));
    }

    [Fact]
    public void Options_validator_rejects_unknown_provider_and_assistant_missing_from_allowlist()
    {
        var validator = new GenerativeAiOptionsValidator(Options.Create(new GeminiOptions()), UnbudgetedUsage());
        var options = ValidOptions();
        options.Provider = "Automatic";
        Assert.True(validator.Validate(null, options).Failed);

        options.Provider = GenerativeAiOptions.FoundryProvider;
        options.Foundry.AllowedAssistantDeployments = ["another-deployment"];
        Assert.True(validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Options_validator_requires_complete_unique_model_metadata_and_positive_credit_multipliers()
    {
        var validator = new GenerativeAiOptionsValidator(Options.Create(new GeminiOptions()), UnbudgetedUsage());
        var options = ValidOptions();
        options.Foundry.AssistantModels[0].CreditMultiplier = 0m;
        Assert.True(validator.Validate(null, options).Failed);

        options = ValidOptions();
        options.Foundry.AssistantModels.Add(new AssistantModelOptions
        {
            Deployment = "gpt-5.4-mini",
            DisplayName = "Duplicate",
            Provider = "OpenAI",
            SpeedTier = "Fast",
            CostTier = "Standard",
            CreditMultiplier = 1m,
        });
        Assert.True(validator.Validate(null, options).Failed);

        options = ValidOptions();
        options.Foundry.AssistantModels = [];
        Assert.True(validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Credential_factory_uses_default_credential_in_development_and_managed_identity_elsewhere()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZURE_CLIENT_ID"] = Guid.NewGuid().ToString(),
            })
            .Build();

        TokenCredential development = FoundryCredentialFactory.Create(
            new TestEnvironment { EnvironmentName = Environments.Development },
            configuration);
        TokenCredential production = FoundryCredentialFactory.Create(
            new TestEnvironment { EnvironmentName = Environments.Production },
            configuration);

        Assert.IsType<DefaultAzureCredential>(development);
        Assert.IsType<ManagedIdentityCredential>(production);
    }

    private static FoundryGenerativeAiClient CreateClient(
        IFoundryAgentInvoker invoker,
        FakeCredits credits,
        Action<FoundryGenerativeAiOptions>? configureFoundry = null)
    {
        var options = ValidOptions();
        configureFoundry?.Invoke(options.Foundry);
        return CreateClient(invoker, credits, options);
    }

    private static FoundryGenerativeAiClient CreateClient(
        IFoundryAgentInvoker invoker,
        FakeCredits credits,
        GenerativeAiOptions options) =>
        new(
            invoker,
            Options.Create(options),
            Options.Create(new AiUsageOptions
            {
                AssistantOutputTokenReserve = 50,
                ImageExtractionOutputTokenReserve = 30,
                PageTranslationOutputTokenReserve = 40,
            }),
            credits,
            NullLogger<FoundryGenerativeAiClient>.Instance);

    private static GenerativeAiOptions ValidOptions() => new()
    {
        Provider = GenerativeAiOptions.FoundryProvider,
        Foundry = new FoundryGenerativeAiOptions
        {
            ProjectEndpoint = "https://example.services.ai.azure.com/api/projects/glosify",
            AssistantDeployment = "gpt-5.4-mini",
            StructuredDeployment = "gpt-5.4-mini",
            VisionDeployment = "gpt-5.4-mini",
            AllowedAssistantDeployments = ["gpt-5.4-mini"],
            AssistantModels =
            [
                new AssistantModelOptions
                {
                    Deployment = "gpt-5.4-mini",
                    DisplayName = "GPT-5.4 Mini",
                    Provider = "OpenAI",
                    SpeedTier = "Balanced",
                    CostTier = "Standard",
                    CreditMultiplier = 1m,
                },
            ],
            TimeoutSeconds = 10,
        },
    };

    private static AgentResponse Response(
        IList<AIContent> contents,
        UsageDetails? usage = null)
    {
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            Usage = usage,
        };
    }

    private static string Content(params object[] parts) =>
        JsonSerializer.Serialize(new { parts }, JsonOptions);

    private static AiUsageContext Usage(string feature) =>
        new("user-1", feature, "test", Guid.NewGuid());

    private static Dictionary<string, object?> CopyTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }
        return copy;
    }

    private sealed class StructuredFixture
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class FakeInvoker : IFoundryAgentInvoker
    {
        public AgentResponse Response { get; set; } =
            FoundryGenerativeAiTests.Response([new TextContent("Done.")]);
        public Exception? Error { get; set; }
        public string StructuredJson { get; set; } = """{"value":"ok"}""";
        public UsageDetails? StructuredUsage { get; set; }
        public ChatFinishReason? StructuredFinishReason { get; set; }
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];
        public IReadOnlyList<AITool> Tools { get; private set; } = [];
        public string? Deployment { get; private set; }
        public string? Instructions { get; private set; }
        public bool EnableSensitiveData { get; private set; }
        public FoundryAuthoredAgent? AuthoredAgent { get; set; }
        public List<string> AuthoredLookups { get; } = [];

        public Task<AgentResponse> RunAsync(
            string deployment,
            string instructions,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<AITool> tools,
            int maxOutputTokens,
            bool enableSensitiveData,
            CancellationToken cancellationToken)
        {
            Deployment = deployment;
            Instructions = instructions;
            Messages = messages;
            Tools = tools;
            EnableSensitiveData = enableSensitiveData;
            return Error is null
                ? Task.FromResult(Response)
                : Task.FromException<AgentResponse>(Error);
        }

        public Task<FoundryAuthoredAgent?> TryGetAuthoredAgentAsync(
            string name,
            string version,
            CancellationToken cancellationToken)
        {
            AuthoredLookups.Add($"{name}@{version}");
            return Task.FromResult(AuthoredAgent);
        }

        public Task<AgentResponse<T>> RunStructuredAsync<T>(
            string deployment,
            string instructions,
            string prompt,
            int maxOutputTokens,
            CancellationToken cancellationToken)
        {
            if (Error is not null)
            {
                return Task.FromException<AgentResponse<T>>(Error);
            }

            var response = FoundryGenerativeAiTests.Response(
                [new TextContent(StructuredJson)],
                StructuredUsage);
            response.FinishReason = StructuredFinishReason;
            return Task.FromResult(new AgentResponse<T>(response, JsonOptions));
        }
    }

    private sealed class FakeCredits : IAiCreditService
    {
        public List<(Guid Id, AiUsageContext Context, string Provider, string Model, int Tokens)>
            Reservations
        { get; } = [];
        public List<(Guid Id, AiTokenUsage Usage)> Commits { get; } = [];
        public List<(Guid Id, AiTokenUsage Usage)> IndependentCommits { get; } = [];
        public List<Guid> Releases { get; } = [];
        public Exception? ReserveError { get; init; }
        public Exception? CommitError { get; init; }
        public Exception? IndependentCommitError { get; init; }
        public Exception? ReleaseError { get; init; }
        public Action? BeforeCommit { get; init; }

        public Task<AiCreditReservation> ReserveAsync(
            AiUsageContext context,
            string provider,
            string model,
            int estimatedTokens,
            CancellationToken cancellationToken = default)
        {
            if (ReserveError is not null)
            {
                return Task.FromException<AiCreditReservation>(ReserveError);
            }

            var id = Guid.NewGuid();
            Reservations.Add((id, context, provider, model, estimatedTokens));
            return Task.FromResult(new AiCreditReservation(id, context.UserId, 1, estimatedTokens));
        }

        public Task CommitUsageAsync(
            Guid reservationId,
            AiTokenUsage usage,
            CancellationToken cancellationToken = default)
        {
            BeforeCommit?.Invoke();
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
            if (IndependentCommitError is not null)
            {
                return Task.FromException(IndependentCommitError);
            }

            IndependentCommits.Add((reservationId, usage));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default)
        {
            Releases.Add(reservationId);
            return ReleaseError is null
                ? Task.CompletedTask
                : Task.FromException(ReleaseError);
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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Glosify.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
