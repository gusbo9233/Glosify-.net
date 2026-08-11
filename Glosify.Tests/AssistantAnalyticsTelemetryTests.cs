using System.Diagnostics;
using System.Collections.Concurrent;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantAnalyticsTelemetryTests
{
    [Fact]
    public void Correlation_and_genai_fields_are_emitted_without_feedback_comments()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GenerativeAiTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var turnId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();

        using (var turn = AssistantAnalyticsTelemetry.StartTurn(
            turnId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Librarian",
            "gpt-5.6-luna"))
        {
            using var invocation = AssistantAnalyticsTelemetry.StartInvocation(
                turnId,
                invocationId,
                0,
                "Librarian",
                "gpt-5.6-luna");
            AssistantAnalyticsTelemetry.CompleteInvocation(
                invocation,
                new AgentTurnResult("Done", [])
                {
                    Metadata = new AgentInvocationMetadata(
                        "foundry",
                        "gpt-5.6-luna",
                        "resp-1",
                        new AiTokenUsage(10, 5, 0, 0, 15),
                        "glosify-librarian",
                        "3"),
                });
        }

        var invocationSpan = Assert.Single(
            stopped,
            activity => activity.DisplayName == "assistant.model.invoke"
                && (string?)activity.GetTagItem("assistant.turn.id") == turnId.ToString()
                && (string?)activity.GetTagItem("assistant.invocation.id") == invocationId.ToString());
        Assert.Equal(turnId.ToString(), invocationSpan.GetTagItem("assistant.turn.id"));
        Assert.Equal(invocationId.ToString(), invocationSpan.GetTagItem("assistant.invocation.id"));
        Assert.Equal("glosify-librarian", invocationSpan.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("resp-1", invocationSpan.GetTagItem("gen_ai.response.id"));

        AssistantAnalyticsTelemetry.RecordFeedback(
            turnId,
            "0123456789abcdef0123456789abcdef",
            "down",
            ["incorrect"]);
        var feedback = Assert.Single(
            stopped,
            activity => activity.DisplayName == "gen_ai.evaluation.result"
                && (string?)activity.GetTagItem("assistant.turn.id") == turnId.ToString());
        var evaluation = Assert.Single(feedback.Events, item => item.Name == "gen_ai.evaluation.result");
        var evaluationTags = evaluation.Tags.ToDictionary(tag => tag.Key, tag => tag.Value);
        Assert.Equal(0, evaluationTags["gen_ai.evaluation.score.value"]);
        Assert.Equal("incorrect", evaluationTags["assistant.feedback.reasons"]);
        Assert.DoesNotContain(feedback.TagObjects, tag => tag.Key.Contains("comment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analytics_json_redacts_secret_fields_but_keeps_normal_user_text()
    {
        var json = AssistantAnalyticsJson.Serialize(new
        {
            prompt = "My password was forgotten.",
            tool = new
            {
                apiKey = "private-key",
                connection_string = "Server=private",
                pageToken = "not-a-secret-pagination-token",
            },
        });

        Assert.Contains("My password was forgotten.", json);
        Assert.DoesNotContain("private-key", json);
        Assert.DoesNotContain("Server=private", json);
        Assert.Contains("not-a-secret-pagination-token", json);
        Assert.Equal(2, json.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Analytics_json_redacts_secret_fields_inside_json_encoded_strings()
    {
        var encodedContent = """{"parts":[{"metadata":{"apiKey":"nested-secret"},"text":"Keep this text."}]}""";

        var json = AssistantAnalyticsJson.Serialize(new
        {
            contentJson = encodedContent,
            malformed = "{not-json",
        });

        Assert.DoesNotContain("nested-secret", json);
        Assert.Contains("[REDACTED]", json);
        Assert.Contains("Keep this text.", json);
        Assert.Contains("{not-json", json);
    }
}
