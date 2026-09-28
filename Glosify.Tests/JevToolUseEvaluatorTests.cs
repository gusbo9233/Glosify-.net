using System.Net;
using System.Text.Json;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class JevToolUseEvaluatorTests
{
    [Fact]
    public async Task Translation_changes_do_not_change_evaluator_state_or_questions()
    {
        var handler = new RecordingHandler();
        var evaluator = Create(handler);
        var first = Snapshot("Muszę wyjść na ląd", "I must go ashore");
        var second = Snapshot("Muszę wyjść na ląd", "completely wrong translation");
        await evaluator.EvaluateAsync(first, default);
        await evaluator.EvaluateAsync(second, default);
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        Assert.DoesNotContain("completely wrong translation", handler.Requests[1]);
        Assert.Contains("wrong_content_destination", handler.Requests[0]);
        Assert.Contains("never grade wording", handler.Requests[0]);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", handler.Uri);
    }

    [Theory]
    [InlineData("add_word", "Muszę wyjść na ląd", "wrong_content_destination")]
    [InlineData("delete_word", "dom", "outside_scope")]
    [InlineData("add_word", "Stare Bielany", "duplicate_mutation")]
    public async Task Structured_tool_findings_are_advisory(string tool, string text, string code)
    {
        var handler = new RecordingHandler { Violation = code };
        var result = await Create(handler).EvaluateAsync(Snapshot(text, "translation") with { Tool = tool }, default);
        Assert.Equal("evaluated", result.Status);
        Assert.Equal("violation", result.Findings.Single(x => x.Code == code).Verdict);
        Assert.All(result.Findings, x => Assert.NotEmpty(x.Evidence));
        Assert.Equal(JevToolUseEvaluator.RubricVersion, result.RubricVersion);
    }

    [Fact]
    public async Task Low_confidence_is_uncertain_not_a_confirmed_mistake()
    {
        var handler = new RecordingHandler { Violation = "wrong_content_destination", Confidence = .2 };
        var result = await Create(handler).EvaluateAsync(Snapshot("Idź!", "Go!"), default);
        Assert.All(result.Findings, x => Assert.Equal("uncertain", x.Verdict));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Outage_returns_unavailable_without_retrying_or_fallback(int status)
    {
        var handler = new RecordingHandler { Status = status };
        var result = await Create(handler).EvaluateAsync(Snapshot("dom", "house"), default);
        Assert.Equal("unavailable", result.Status);
        Assert.Empty(result.Findings);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_answers_do_not_count_as_passed_checks()
    {
        var handler = new RecordingHandler { MissingAnswer = true };
        var result = await Create(handler).EvaluateAsync(Snapshot("dom", "house"), default);
        Assert.Equal("unavailable", result.Status);
    }

    [Fact]
    public async Task Freestyle_context_and_phrase_exception_are_visible_to_judge()
    {
        var handler = new RecordingHandler();
        await Create(handler).EvaluateAsync(Snapshot("Stare Bielany", "Old Bielany") with { Context = new { isFreestyle = true } }, default);
        Assert.Contains("\"isFreestyle\":true", handler.Requests.Single());
        Assert.Contains("Multiword vocabulary and proper names are allowed", handler.Requests.Single());
    }

    private static ToolDecisionSnapshot Snapshot(string word, string translation) => new("Add all words and sentences", [],
        new { quizId = "quiz", isFreestyle = false }, new[] { new { name = "add_word" }, new { name = "add_sentence" } },
        "add_word", JsonSerializer.SerializeToElement(new { word, translation }), []);
    private static JevToolUseEvaluator Create(HttpMessageHandler handler) => new(new HttpClient(handler),
        Options.Create(new JevOptions { Enabled = true, ApiKey = "test-only" }));
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? Uri { get; private set; }
        public string? Violation { get; init; }
        public double Confidence { get; init; } = .95;
        public int Status { get; init; } = 200;
        public bool MissingAnswer { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri!.ToString();
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            var answers = JevToolUseEvaluator.Rubric.Keys.Where(x => !MissingAnswer || x != "wrong_target").ToDictionary(x => x, x => new
            {
                type = "choice", choice = x == Violation ? "violation" : "pass", confidence = Confidence,
                probabilities = new { violation = x == Violation ? .99 : .01, pass = x == Violation ? .01 : .99, uncertain = 0 },
            });
            return new((HttpStatusCode)Status) { Content = new StringContent(JsonSerializer.Serialize(new { model = "jev-1.13.0", answers, usage = new { input_tokens = 100 } })) };
        }
    }
}
