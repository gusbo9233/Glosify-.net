using Glosify.Services.Speaking;
using Xunit;

namespace Glosify.Tests;

public sealed class BartenderInteractionStateTests
{
    [Theory]
    [InlineData("lightBeer", 14)]
    [InlineData("darkBeer", 16)]
    [InlineData("vodka", 12)]
    [InlineData("sparklingWater", 8)]
    [InlineData("stillWater", 8)]
    [InlineData("appleJuice", 10)]
    public void Catalog_contains_the_authoritative_drinks(string id, int price)
    {
        Assert.True(BartenderInteractionCatalog.TryGetDrink(id, out var drink));
        Assert.Equal(price, drink.Price);
    }

    [Fact]
    public void New_session_has_the_expected_wallet_and_balance()
    {
        var snapshot = BartenderInteractionState.Create().ToSnapshot();

        Assert.Equal(100, snapshot.WalletBalance);
        Assert.Equal(
            [(50, 1), (20, 1), (10, 1), (5, 2), (2, 3), (1, 4)],
            snapshot.Wallet.Select(item => (item.Value, item.Count)));
        Assert.Equal(6, snapshot.Menu.Count);
        Assert.Empty(snapshot.AvailableActions);
    }

    [Fact]
    public void Serving_accumulates_the_tab_and_ignores_a_second_drink_over_the_active_glass()
    {
        var state = BartenderInteractionState.Create();
        var rejected = new List<(SpeakingProposedActionType Type, string Reason)>();

        var command = Assert.Single(state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.ServeDrink, "darkBeer")]));

        Assert.Equal("pourAndServe", command.Type);
        Assert.Equal(16, state.TabTotal);
        Assert.Equal(3, state.ActiveDrinkFillLevel);
        Assert.Empty(state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.ServeDrink, "vodka")],
            (proposal, reason) => rejected.Add((proposal.Type, reason))));
        Assert.Contains(
            rejected,
            item => item.Type == SpeakingProposedActionType.ServeDrink
                && item.Reason.Contains("clearing", StringComparison.Ordinal));
        Assert.Equal("darkBeer", state.ActiveDrinkId);
        Assert.Equal(16, state.TabTotal);
        Assert.DoesNotContain(
            state.GetPermittedFirstToolCalls(),
            action => action.StartsWith("serve_drink", StringComparison.Ordinal));
        Assert.DoesNotContain("clear_empty_glass", state.GetPermittedFirstToolCalls());
    }

    [Fact]
    public void Drinking_has_three_stages_and_empty_glass_must_be_cleared()
    {
        var state = BartenderInteractionState.Create();
        state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.ServeDrink, "stillWater")]);

        Assert.Equal(2, Assert.Single(
            state.ApplyUserAction(SpeakingInteractionAction.Drink, null).SceneCommands).FillLevel);
        Assert.Equal(1, Assert.Single(
            state.ApplyUserAction(SpeakingInteractionAction.Drink, null).SceneCommands).FillLevel);
        Assert.Equal(0, Assert.Single(
            state.ApplyUserAction(SpeakingInteractionAction.Drink, null).SceneCommands).FillLevel);
        Assert.DoesNotContain("drink", state.ToSnapshot().AvailableActions);
        Assert.Contains("clear_empty_glass", state.GetPermittedFirstToolCalls());
        Assert.Throws<SpeakingValidationException>(
            () => state.ApplyUserAction(SpeakingInteractionAction.Drink, null));

        var clear = Assert.Single(state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.ClearGlass)]));
        Assert.Equal("clearGlass", clear.Type);
        Assert.Null(state.ActiveDrinkId);
    }

    [Fact]
    public void Clear_glass_is_ignored_until_the_drink_is_empty()
    {
        var state = BartenderInteractionState.Create();
        state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.ServeDrink, "appleJuice")]);

        Assert.Empty(state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.ClearGlass)]));
        Assert.Equal("appleJuice", state.ActiveDrinkId);
        Assert.Equal(3, state.ActiveDrinkFillLevel);
    }

    [Fact]
    public void Tab_accumulates_across_finished_and_cleared_drinks()
    {
        var state = BartenderInteractionState.Create();
        state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.ServeDrink, "lightBeer")]);
        for (var sip = 0; sip < 3; sip++)
        {
            state.ApplyUserAction(SpeakingInteractionAction.Drink, null);
        }
        state.ApplyProposedActions(
            [
                Proposal(SpeakingProposedActionType.ClearGlass),
                Proposal(SpeakingProposedActionType.ServeDrink, "vodka"),
            ]);

        Assert.Equal(26, state.TabTotal);
        Assert.Equal("vodka", state.ActiveDrinkId);
    }

    [Fact]
    public void Snack_can_only_be_taken_after_an_offer_and_only_once()
    {
        var state = BartenderInteractionState.Create();

        Assert.Throws<SpeakingValidationException>(
            () => state.ApplyUserAction(SpeakingInteractionAction.TakeSnack, null));
        state.ApplyProposedActions([Proposal(SpeakingProposedActionType.OfferSnack)]);
        Assert.Contains("takeSnack", state.ToSnapshot().AvailableActions);

        Assert.Equal(
            "takeSnack",
            Assert.Single(
                state.ApplyUserAction(SpeakingInteractionAction.TakeSnack, null).SceneCommands).Type);
        Assert.False(state.SnackOffered);
        Assert.Throws<SpeakingValidationException>(
            () => state.ApplyUserAction(SpeakingInteractionAction.TakeSnack, null));
    }

    [Fact]
    public void Exact_payment_removes_the_selected_money_and_closes_the_bill()
    {
        var state = StateWithBill("appleJuice");

        var payment = state.ApplyUserAction(
            SpeakingInteractionAction.SubmitPayment,
            new Dictionary<int, int> { [10] = 1 });

        Assert.Equal("paymentAccepted", Assert.Single(payment.SceneCommands).Type);
        Assert.Equal(90, state.WalletBalance);
        Assert.Equal(0, state.TabTotal);
        Assert.False(state.BillPresented);
    }

    [Fact]
    public void Overpayment_returns_deterministic_greedy_change()
    {
        var state = StateWithBill("lightBeer");

        var payment = state.ApplyUserAction(
            SpeakingInteractionAction.SubmitPayment,
            new Dictionary<int, int> { [20] = 1 });
        var snapshot = state.ToSnapshot();

        Assert.Collection(
            payment.SceneCommands,
            accepted =>
            {
                Assert.Equal("paymentAccepted", accepted.Type);
                Assert.Equal(20, accepted.Amount);
            },
            change =>
            {
                Assert.Equal("returnChange", change.Type);
                Assert.Equal(6, change.Amount);
            });
        Assert.Equal(86, snapshot.WalletBalance);
        Assert.Equal(0, snapshot.Wallet.Single(item => item.Value == 20).Count);
        Assert.Equal(3, snapshot.Wallet.Single(item => item.Value == 5).Count);
        Assert.Equal(5, snapshot.Wallet.Single(item => item.Value == 1).Count);
    }

    [Fact]
    public void Insufficient_payment_keeps_the_wallet_and_bill_unchanged()
    {
        var state = StateWithBill("lightBeer");

        var payment = state.ApplyUserAction(
            SpeakingInteractionAction.SubmitPayment,
            new Dictionary<int, int> { [5] = 1 });

        Assert.Equal("paymentRejected", Assert.Single(payment.SceneCommands).Type);
        Assert.Equal(100, state.WalletBalance);
        Assert.Equal(14, state.TabTotal);
        Assert.True(state.BillPresented);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(10, 2)]
    [InlineData(5, 0)]
    [InlineData(2, -1)]
    public void Payment_rejects_unavailable_or_malformed_denominations(int value, int count)
    {
        var state = StateWithBill("stillWater");

        Assert.Throws<SpeakingValidationException>(() => state.ApplyUserAction(
            SpeakingInteractionAction.SubmitPayment,
            new Dictionary<int, int> { [value] = count }));
        Assert.Equal(100, state.WalletBalance);
    }

    [Fact]
    public void Unavailable_items_are_not_served_when_the_model_proposes_one()
    {
        var state = BartenderInteractionState.Create();

        state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.MarkUnavailable, "vodka")]);

        Assert.Contains("vodka", state.ToSnapshot().UnavailableDrinkIds);
        Assert.Empty(state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.ServeDrink, "vodka")]));
        Assert.Null(state.ActiveDrinkId);
        Assert.Equal(0, state.TabTotal);
    }

    [Fact]
    public void Invalid_and_excess_model_transitions_are_ignored()
    {
        var state = BartenderInteractionState.Create();
        var rejected = new List<SpeakingProposedActionType>();

        Assert.Empty(state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.PresentBill)]));
        Assert.Throws<SpeakingValidationException>(() => state.ApplyUserAction(
            SpeakingInteractionAction.SubmitPayment,
            new Dictionary<int, int> { [1] = 1 }));
        Assert.Empty(state.ApplyProposedActions(
            [Proposal(SpeakingProposedActionType.MarkUnavailable, "invented")]));
        var accepted = state.ApplyProposedActions(
            [
                Proposal(SpeakingProposedActionType.PolishGlass),
                Proposal(SpeakingProposedActionType.WipeCounter),
                Proposal(SpeakingProposedActionType.LastCall),
                Proposal(SpeakingProposedActionType.OfferSnack),
            ],
            (proposal, _) => rejected.Add(proposal.Type));

        Assert.Equal(
            ["polishGlass", "wipeCounter", "lastCall"],
            accepted.Select(command => command.Type));
        Assert.Equal([SpeakingProposedActionType.OfferSnack], rejected);
        Assert.False(state.SnackOffered);
    }

    [Fact]
    public void Ambient_actions_serialize_only_their_allowlisted_command_names()
    {
        var state = BartenderInteractionState.Create();

        var commands = state.ApplyProposedActions(
            [
                Proposal(SpeakingProposedActionType.PolishGlass),
                Proposal(SpeakingProposedActionType.WipeCounter),
                Proposal(SpeakingProposedActionType.LastCall),
            ]);

        Assert.Equal(
            ["polishGlass", "wipeCounter", "lastCall"],
            commands.Select(command => command.Type));
        Assert.All(commands, command =>
        {
            Assert.Null(command.DrinkId);
            Assert.Null(command.Amount);
            Assert.Null(command.FillLevel);
        });
    }

    private static BartenderInteractionState StateWithBill(string drinkId)
    {
        var state = BartenderInteractionState.Create();
        state.ApplyProposedActions(
            [
                Proposal(SpeakingProposedActionType.ServeDrink, drinkId),
                Proposal(SpeakingProposedActionType.PresentBill),
            ]);
        return state;
    }

    private static SpeakingProposedAction Proposal(
        SpeakingProposedActionType type,
        string? drinkId = null) =>
        new()
        {
            Type = type,
            DrinkId = drinkId,
        };
}
