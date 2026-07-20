using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Glosify.Services.Speaking;

/// <summary>
/// Owns the executable scene tools for one Foundry bartender conversation.
/// A turn binds these tools to a cloned interaction state; only the caller can
/// commit that clone after the final structured reply and credit usage succeed.
/// </summary>
internal sealed class BartenderSceneToolRuntime
{
    public const int MaxSceneActionsPerTurn = 3;

    private static readonly JsonSerializerOptions ToolJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

    private readonly List<SpeakingSceneCommand> _commands = [];
    private BartenderInteractionState? _turnState;
    private int _toolCalls;

    public BartenderSceneToolRuntime()
    {
        Tools =
        [
            AIFunctionFactory.Create(
                (Func<string, BartenderSceneToolResult>)ServeDrink,
                "serve_drink",
                "Pour and serve one menu drink only after the learner clearly orders or accepts it. Use a drink_id from the trusted scene state.",
                ToolJsonOptions),
            AIFunctionFactory.Create(
                (Func<BartenderSceneToolResult>)PresentBill,
                "present_bill",
                "Place the current non-empty bill on the counter when the learner asks to pay.",
                ToolJsonOptions),
            AIFunctionFactory.Create(
                (Func<BartenderSceneToolResult>)OfferSnack,
                "offer_snack",
                "Offer the learner paluszki when that fits the conversation naturally.",
                ToolJsonOptions),
            AIFunctionFactory.Create(
                (Func<BartenderSceneToolResult>)ClearEmptyGlass,
                "clear_empty_glass",
                "Clear the learner's active glass only after it is empty.",
                ToolJsonOptions),
            AIFunctionFactory.Create(
                (Func<BartenderSceneToolResult>)PolishGlass,
                "polish_glass",
                "Perform an occasional ambient glass-polishing animation.",
                ToolJsonOptions),
            AIFunctionFactory.Create(
                (Func<BartenderSceneToolResult>)WipeCounter,
                "wipe_counter",
                "Perform an occasional ambient counter-wiping animation.",
                ToolJsonOptions),
            AIFunctionFactory.Create(
                (Func<BartenderSceneToolResult>)AnnounceLastCall,
                "announce_last_call",
                "Perform a last-call gesture only when last call is contextually appropriate.",
                ToolJsonOptions),
            AIFunctionFactory.Create(
                (Func<string, BartenderSceneToolResult>)MarkDrinkUnavailable,
                "mark_drink_unavailable",
                "Mark a known menu drink unavailable when Marek tells the learner it cannot be served. Use a drink_id from the trusted scene state.",
                ToolJsonOptions),
        ];
    }

    public IReadOnlyList<AITool> Tools { get; }

    public void BeginTurn(BartenderInteractionState turnState)
    {
        ArgumentNullException.ThrowIfNull(turnState);
        if (_turnState is not null)
        {
            throw new InvalidOperationException("A bartender scene-tool turn is already active.");
        }

        _turnState = turnState;
        _commands.Clear();
        _toolCalls = 0;
    }

    public IReadOnlyList<SpeakingSceneCommand> CompleteTurn()
    {
        EnsureActiveTurn();
        var commands = _commands.ToArray();
        ResetTurn();
        return commands;
    }

    public void AbortTurn() => ResetTurn();

    private BartenderSceneToolResult ServeDrink(string drink_id) =>
        Execute(SpeakingProposedActionType.ServeDrink, drink_id);

    private BartenderSceneToolResult PresentBill() =>
        Execute(SpeakingProposedActionType.PresentBill);

    private BartenderSceneToolResult OfferSnack() =>
        Execute(SpeakingProposedActionType.OfferSnack);

    private BartenderSceneToolResult ClearEmptyGlass() =>
        Execute(SpeakingProposedActionType.ClearGlass);

    private BartenderSceneToolResult PolishGlass() =>
        Execute(SpeakingProposedActionType.PolishGlass);

    private BartenderSceneToolResult WipeCounter() =>
        Execute(SpeakingProposedActionType.WipeCounter);

    private BartenderSceneToolResult AnnounceLastCall() =>
        Execute(SpeakingProposedActionType.LastCall);

    private BartenderSceneToolResult MarkDrinkUnavailable(string drink_id) =>
        Execute(SpeakingProposedActionType.MarkUnavailable, drink_id);

    private BartenderSceneToolResult Execute(
        SpeakingProposedActionType actionType,
        string? drinkId = null)
    {
        var state = EnsureActiveTurn();
        var startedAt = Stopwatch.GetTimestamp();
        var accepted = false;
        var reason = string.Empty;
        try
        {
            _toolCalls++;
            if (_toolCalls > MaxSceneActionsPerTurn)
            {
                reason = "Marek has already used the maximum of three scene tools this turn.";
                return Result(state, accepted: false, reason);
            }

            var proposal = new SpeakingProposedAction
            {
                Type = actionType,
                DrinkId = drinkId,
            };
            var commands = state.ApplyProposedActions(
                [proposal],
                (_, rejectedReason) => reason = rejectedReason);
            if (commands.Count == 0)
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? "That scene action is not legal in the current bar state."
                    : reason;
                return Result(state, accepted: false, reason);
            }

            var command = commands[0];
            _commands.Add(command);
            accepted = true;
            return Result(
                state,
                accepted: true,
                $"Accepted. The application executed {command.Type}.");
        }
        finally
        {
            SpeakingTelemetry.SceneToolCalls.Add(
                1,
                new KeyValuePair<string, object?>(
                    "speaking.scene_tool",
                    ToToolName(actionType)),
                new KeyValuePair<string, object?>(
                    "speaking.tool_outcome",
                    accepted ? "accepted" : "rejected"));
            SpeakingTelemetry.SceneToolDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>(
                    "speaking.scene_tool",
                    ToToolName(actionType)));
        }
    }

    private static BartenderSceneToolResult Result(
        BartenderInteractionState state,
        bool accepted,
        string message) =>
        new(
            accepted,
            message,
            new BartenderSceneToolState(
                state.ActiveDrinkId,
                state.ActiveDrinkFillLevel,
                state.TabTotal,
                state.BillPresented,
                state.SnackOffered,
                state.GetPermittedFirstToolCalls()));

    private BartenderInteractionState EnsureActiveTurn() =>
        _turnState
        ?? throw new InvalidOperationException(
            "Bartender scene tools were invoked outside an active speaking turn.");

    private void ResetTurn()
    {
        _turnState = null;
        _commands.Clear();
        _toolCalls = 0;
    }

    private static string ToToolName(SpeakingProposedActionType actionType) =>
        actionType switch
        {
            SpeakingProposedActionType.ServeDrink => "serve_drink",
            SpeakingProposedActionType.PresentBill => "present_bill",
            SpeakingProposedActionType.OfferSnack => "offer_snack",
            SpeakingProposedActionType.ClearGlass => "clear_empty_glass",
            SpeakingProposedActionType.PolishGlass => "polish_glass",
            SpeakingProposedActionType.WipeCounter => "wipe_counter",
            SpeakingProposedActionType.LastCall => "announce_last_call",
            SpeakingProposedActionType.MarkUnavailable => "mark_drink_unavailable",
            _ => "unknown",
        };
}

internal sealed record BartenderSceneToolResult(
    bool Accepted,
    string Message,
    BartenderSceneToolState State);

internal sealed record BartenderSceneToolState(
    string? ActiveDrinkId,
    int ActiveDrinkFillLevel,
    int TabTotal,
    bool BillPresented,
    bool SnackOffered,
    IReadOnlyList<string> LegalNextActions);
