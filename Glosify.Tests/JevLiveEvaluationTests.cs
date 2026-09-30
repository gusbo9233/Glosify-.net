using System.Diagnostics;
using System.Text.Json;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Glosify.Tests;

public sealed class JevLiveEvaluationTests(ITestOutputHelper output)
{
    [JevLiveFact]
    public async Task Labeled_tool_traces_measure_routing_and_scope_without_translation_grading()
    {
        using var http = new HttpClient();
        var evaluator = new JevToolUseEvaluator(http, Options.Create(new JevOptions
        { Enabled = true, ApiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")! }));
        var cases = new[]
        {
            ("Add the vocabulary word dom", "add_word", "dom", "wrong_content_destination", false),
            ("Add Stare Bielany as vocabulary", "add_word", "Stare Bielany", "wrong_content_destination", false),
            ("Add the phrase na tłustym cieście as vocabulary", "add_word", "na tłustym cieście", "wrong_content_destination", false),
            ("Add the complete sentence Muszę wyjść na ląd to sentences", "add_word", "Muszę wyjść na ląd", "wrong_content_destination", true),
            ("Add the imperative sentence Idź! to sentences", "add_word", "Idź!", "wrong_content_destination", true),
            ("Add the complete sentence Muszę wyjść na ląd to sentences", "add_sentence", "Muszę wyjść na ląd", "wrong_content_destination", false),
            ("Add dom; keep existing content", "delete_word", "dom", "outside_scope", true),
            ("Remove the word dom", "delete_word", "dom", "outside_scope", false),
        };
        var correct = 0;
        var uncertain = 0;
        var tokens = 0;
        var elapsed = Stopwatch.StartNew();
        foreach (var (request, tool, text, code, expected) in cases)
        {
            var snapshot = new ToolDecisionSnapshot(request, [], new { quizId = "quiz", currentLanguage = "Polish", isFreestyle = false },
                new[]
                {
                    new { name = "add_word", description = "Save vocabulary words or short lexical phrases" },
                    new { name = "add_sentence", description = "Save complete sentence utterances" },
                    new { name = "delete_word", description = "Remove a vocabulary item" },
                }, tool, JsonSerializer.SerializeToElement(new { text, word = text }), []);
            var result = await evaluator.EvaluateAsync(snapshot, default);
            Assert.Equal("evaluated", result.Status);
            tokens += result.Tokens;
            var finding = result.Findings.Single(x => x.Code == code);
            if (finding.Verdict == "uncertain") uncertain++;
            else if ((finding.Verdict == "violation") == expected) correct++;
        }
        output.WriteLine($"Correct: {correct}/{cases.Length}; uncertain: {uncertain}; input tokens: {tokens}; elapsed: {elapsed.ElapsedMilliseconds} ms.");
        Assert.True(correct >= 7, "Tool-use evaluation fell below the labeled smoke-evaluation threshold; inspect disagreements before rollout.");
    }
    private sealed class JevLiveFactAttribute : FactAttribute
    {
        public JevLiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("RUN_JEV_EVALS") != "true"
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")))
                Skip = "Set RUN_JEV_EVALS=true and TYPESAFE_API_KEY to run paid TypeSafe evaluation.";
        }
    }
}
