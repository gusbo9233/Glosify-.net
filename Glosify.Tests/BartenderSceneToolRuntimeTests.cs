using System.Text.Json;
using Glosify.Services.Speaking;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glosify.Tests;

public sealed class BartenderSceneToolRuntimeTests
{
    private static readonly JsonSerializerOptions ToolJsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void Tools_are_executable_functions_with_the_expected_names_and_schemas()
    {
        var runtime = new BartenderSceneToolRuntime();
        var functions = runtime.Tools
            .Select(tool => Assert.IsAssignableFrom<AIFunction>(tool))
            .ToArray();

        Assert.Equal(
            [
                "serve_drink",
                "present_bill",
                "offer_snack",
                "clear_empty_glass",
                "polish_glass",
                "wipe_counter",
                "announce_last_call",
                "mark_drink_unavailable",
            ],
            functions.Select(function => function.Name));

        Assert.All(functions, function =>
        {
            Assert.False(string.IsNullOrWhiteSpace(function.Description));
            Assert.Equal("object", function.JsonSchema.GetProperty("type").GetString());
        });

        foreach (var function in functions)
        {
            var properties = function.JsonSchema.GetProperty("properties");
            if (function.Name is "serve_drink" or "mark_drink_unavailable")
            {
                Assert.Equal(
                    "string",
                    properties.GetProperty("drink_id").GetProperty("type").GetString());
                Assert.Contains(
                    function.JsonSchema.GetProperty("required").EnumerateArray(),
                    property => property.GetString() == "drink_id");
            }
            else
            {
                Assert.Empty(properties.EnumerateObject());
            }
        }
    }

    [Fact]
    public async Task Serve_drink_mutates_the_turn_clone_and_emits_a_canonical_command()
    {
        var authoritativeState = BartenderInteractionState.Create();
        var turnState = authoritativeState.Clone();
        var runtime = new BartenderSceneToolRuntime();
        runtime.BeginTurn(turnState);

        var result = await InvokeAsync(
            runtime,
            "serve_drink",
            ("drink_id", " lightBeer "));
        var command = Assert.Single(runtime.CompleteTurn());

        Assert.True(result.Accepted);
        Assert.Equal("lightBeer", result.State.ActiveDrinkId);
        Assert.Equal(3, result.State.ActiveDrinkFillLevel);
        Assert.Equal(14, result.State.TabTotal);
        Assert.Equal("lightBeer", turnState.ActiveDrinkId);
        Assert.Equal(3, turnState.ActiveDrinkFillLevel);
        Assert.Equal(14, turnState.TabTotal);

        Assert.Equal("pourAndServe", command.Type);
        Assert.Equal("lightBeer", command.DrinkId);
        Assert.Equal(14, command.Amount);
        Assert.Equal(3, command.FillLevel);

        Assert.Null(authoritativeState.ActiveDrinkId);
        Assert.Equal(0, authoritativeState.TabTotal);
    }

    [Fact]
    public async Task Invalid_serve_is_rejected_without_mutating_state_or_emitting_a_command()
    {
        var turnState = BartenderInteractionState.Create();
        var runtime = new BartenderSceneToolRuntime();
        runtime.BeginTurn(turnState);

        var result = await InvokeAsync(
            runtime,
            "serve_drink",
            ("drink_id", "not-on-the-menu"));
        var commands = runtime.CompleteTurn();

        Assert.False(result.Accepted);
        Assert.Null(result.State.ActiveDrinkId);
        Assert.Equal(0, result.State.ActiveDrinkFillLevel);
        Assert.Equal(0, result.State.TabTotal);
        Assert.Null(turnState.ActiveDrinkId);
        Assert.Equal(0, turnState.TabTotal);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task Second_serve_is_rejected_without_overwriting_the_active_drink()
    {
        var turnState = BartenderInteractionState.Create();
        var runtime = new BartenderSceneToolRuntime();
        runtime.BeginTurn(turnState);

        var first = await InvokeAsync(
            runtime,
            "serve_drink",
            ("drink_id", "darkBeer"));
        var second = await InvokeAsync(
            runtime,
            "serve_drink",
            ("drink_id", "vodka"));
        var command = Assert.Single(runtime.CompleteTurn());

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal("darkBeer", second.State.ActiveDrinkId);
        Assert.Equal(3, second.State.ActiveDrinkFillLevel);
        Assert.Equal(16, second.State.TabTotal);
        Assert.Equal("darkBeer", turnState.ActiveDrinkId);
        Assert.Equal(16, turnState.TabTotal);
        Assert.Equal("pourAndServe", command.Type);
        Assert.Equal("darkBeer", command.DrinkId);
    }

    [Fact]
    public async Task Fourth_scene_tool_call_is_rejected_without_mutation()
    {
        var turnState = BartenderInteractionState.Create();
        var runtime = new BartenderSceneToolRuntime();
        runtime.BeginTurn(turnState);

        Assert.True((await InvokeAsync(runtime, "polish_glass")).Accepted);
        Assert.True((await InvokeAsync(runtime, "wipe_counter")).Accepted);
        Assert.True((await InvokeAsync(runtime, "announce_last_call")).Accepted);

        var fourth = await InvokeAsync(runtime, "offer_snack");
        var commands = runtime.CompleteTurn();

        Assert.False(fourth.Accepted);
        Assert.Contains(
            "maximum of three",
            fourth.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(fourth.State.SnackOffered);
        Assert.False(turnState.SnackOffered);
        Assert.Equal(
            ["polishGlass", "wipeCounter", "lastCall"],
            commands.Select(command => command.Type));
    }

    [Fact]
    public async Task Abort_turn_discards_staged_commands_and_leaves_authoritative_state_unchanged()
    {
        var authoritativeState = BartenderInteractionState.Create();
        var discardedTurnState = authoritativeState.Clone();
        var runtime = new BartenderSceneToolRuntime();
        runtime.BeginTurn(discardedTurnState);

        Assert.True((await InvokeAsync(
            runtime,
            "serve_drink",
            ("drink_id", "appleJuice"))).Accepted);
        runtime.AbortTurn();

        Assert.Equal("appleJuice", discardedTurnState.ActiveDrinkId);
        Assert.Equal(10, discardedTurnState.TabTotal);
        Assert.Null(authoritativeState.ActiveDrinkId);
        Assert.Equal(0, authoritativeState.TabTotal);

        var retryTurnState = authoritativeState.Clone();
        runtime.BeginTurn(retryTurnState);
        Assert.True((await InvokeAsync(runtime, "wipe_counter")).Accepted);

        var command = Assert.Single(runtime.CompleteTurn());
        Assert.Equal("wipeCounter", command.Type);
        Assert.Null(command.DrinkId);
        Assert.Equal(0, retryTurnState.TabTotal);
    }

    private static async Task<BartenderSceneToolResult> InvokeAsync(
        BartenderSceneToolRuntime runtime,
        string toolName,
        params (string Name, object? Value)[] arguments)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(runtime.Tools, candidate => candidate.Name == toolName));
        var functionArguments = new AIFunctionArguments();
        foreach (var (name, value) in arguments)
        {
            functionArguments[name] = value;
        }

        var rawResult = await tool.InvokeAsync(functionArguments);
        var jsonResult = Assert.IsType<JsonElement>(rawResult);
        var result = jsonResult.Deserialize<BartenderSceneToolResult>(ToolJsonOptions);
        Assert.NotNull(result);
        return result;
    }
}
