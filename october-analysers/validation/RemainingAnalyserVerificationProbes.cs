using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Glosify.Services.Words;
using Glosify.Services.Typing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Glosify.Tests;

// Investigation probes: archived under october-analysers after execution.
public sealed class RemainingAnalyserVerificationProbes(ITestOutputHelper output)
{
    [Fact]
    public async Task Empty_avatar_preference_body_disables_autosave_for_the_authenticated_owner()
    {
        using var app = new AvatarFixture();
        using var client = await app.Client("admin");
        await app.Antiforgery(client);
        var session = app.Services.GetRequiredService<AvatarSessions>().Create("admin", QuizLanguageCatalog.Find("sv")!, "free");
        var statusUrl = $"/api/avatar/conversations/{session.MemoryId}/transcript";
        (await client.PostAsJsonAsync("/api/avatar/preferences", new { autoSaveConversations = true })).EnsureSuccessStatusCode();
        Assert.True((await client.GetFromJsonAsync<AvatarTranscriptStatus>(statusUrl))!.AutoSave);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/avatar/preferences", new { })).StatusCode);
        Assert.False((await client.GetFromJsonAsync<AvatarTranscriptStatus>(statusUrl))!.AutoSave);
    }

    [Theory]
    [InlineData("manage_quiz_library")]
    [InlineData("manage_anki")]
    public async Task Unknown_learning_actions_do_not_create_changes(string tool)
    {
        await using var harness = await AssistantHarness.CreateAsync();
        harness.Model.ThenCall(tool, JsonSerializer.Serialize(new { action = "unknown_action", id = harness.QuizId })).ThenText("Cannot apply.");
        var run = await harness.RunAsync("Do this action");
        Assert.Empty(run.ResourceChanges);
        Assert.Contains(run.Parts, p => p.Type == "tool" && p.State == "error");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Word_matching_treats_regex_syntax_as_literal_and_scales_on_long_input(bool typing)
    {
        var factory = typeof(WordService).GetMethod("BuildWordRegex", BindingFlags.NonPublic | BindingFlags.Static)!;
        var contains = typeof(TypingQuizService).GetMethod("ContainsWord", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Match(string sentence, string word) => typing
            ? (bool)contains.Invoke(null, [sentence, word])!
            : ((Regex)factory.Invoke(null, [word])!).IsMatch(sentence);
        Assert.True(Match("literal (a+)+$ here", "(a+)+$"));
        Assert.False(Match("aaaaaaaaaaaa!", "(a+)+$"));
        Assert.False(Match("scatter", "cat"));
        Assert.True(Match("a cat!", "cat"));
        foreach (var length in new[] { 10000, 100000, 1000000 })
        {
            var value = new string('a', length) + "!";
            var timer = Stopwatch.StartNew();
            Assert.False(Match(value, "(a+)+$"));
            output.WriteLine($"{length} characters: {timer.Elapsed.TotalMilliseconds:F3} ms");
        }
    }
}
