namespace Glosify.Services.Speaking;

public static class BartenderInteractionCatalog
{
    public static readonly IReadOnlyList<SpeakingDrinkSnapshot> Drinks =
    [
        new("lightBeer", "Piwo jasne", "Light beer", 14),
        new("darkBeer", "Piwo ciemne", "Dark beer", 16),
        new("vodka", "Wódka", "Vodka", 12),
        new("sparklingWater", "Woda gazowana", "Sparkling water", 8),
        new("stillWater", "Woda niegazowana", "Still water", 8),
        new("appleJuice", "Sok jabłkowy", "Apple juice", 10),
    ];

    public static readonly IReadOnlyList<int> Denominations = [50, 20, 10, 5, 2, 1];

    private static readonly IReadOnlyDictionary<string, SpeakingDrinkSnapshot> DrinksById =
        Drinks.ToDictionary(drink => drink.Id, StringComparer.Ordinal);

    public static bool TryGetDrink(string? id, out SpeakingDrinkSnapshot drink)
    {
        if (!string.IsNullOrWhiteSpace(id)
            && DrinksById.TryGetValue(id.Trim(), out var found))
        {
            drink = found;
            return true;
        }

        drink = Drinks[0];
        return false;
    }

    public static bool TryParseUserAction(
        string? value,
        out SpeakingInteractionAction action)
    {
        var normalized = value?.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        if (Enum.TryParse(normalized, true, out action))
        {
            return true;
        }

        action = default;
        return false;
    }
}

public sealed class BartenderInteractionState
{
    private readonly Dictionary<int, int> _wallet;
    private readonly HashSet<string> _unavailableDrinkIds;

    private BartenderInteractionState(
        Dictionary<int, int> wallet,
        int tabTotal,
        bool billPresented,
        string? activeDrinkId,
        int activeDrinkFillLevel,
        bool snackOffered,
        HashSet<string> unavailableDrinkIds)
    {
        _wallet = wallet;
        TabTotal = tabTotal;
        BillPresented = billPresented;
        ActiveDrinkId = activeDrinkId;
        ActiveDrinkFillLevel = activeDrinkFillLevel;
        SnackOffered = snackOffered;
        _unavailableDrinkIds = unavailableDrinkIds;
    }

    public int TabTotal { get; private set; }
    public bool BillPresented { get; private set; }
    public string? ActiveDrinkId { get; private set; }
    public int ActiveDrinkFillLevel { get; private set; }
    public bool SnackOffered { get; private set; }

    public int WalletBalance =>
        _wallet.Sum(item => item.Key * item.Value);

    public static BartenderInteractionState Create() =>
        new(
            new Dictionary<int, int>
            {
                [50] = 1,
                [20] = 1,
                [10] = 1,
                [5] = 2,
                [2] = 3,
                [1] = 4,
            },
            0,
            false,
            null,
            0,
            false,
            []);

    public BartenderInteractionState Clone() =>
        new(
            new Dictionary<int, int>(_wallet),
            TabTotal,
            BillPresented,
            ActiveDrinkId,
            ActiveDrinkFillLevel,
            SnackOffered,
            new HashSet<string>(_unavailableDrinkIds, StringComparer.Ordinal));

    public SpeakingInteractionSnapshot ToSnapshot()
    {
        SpeakingActiveDrinkSnapshot? activeDrink = null;
        if (BartenderInteractionCatalog.TryGetDrink(ActiveDrinkId, out var drink))
        {
            activeDrink = new SpeakingActiveDrinkSnapshot(
                drink.Id,
                drink.NamePolish,
                drink.NameEnglish,
                ActiveDrinkFillLevel);
        }

        var actions = new List<string>(3);
        if (activeDrink is { FillLevel: > 0 })
        {
            actions.Add("drink");
        }
        if (SnackOffered)
        {
            actions.Add("takeSnack");
        }
        if (BillPresented && TabTotal > 0)
        {
            actions.Add("submitPayment");
        }

        return new SpeakingInteractionSnapshot(
            BartenderInteractionCatalog.Drinks,
            BartenderInteractionCatalog.Denominations
                .Select(value => new SpeakingWalletDenominationSnapshot(value, _wallet[value]))
                .ToArray(),
            WalletBalance,
            TabTotal,
            BillPresented,
            activeDrink,
            SnackOffered,
            _unavailableDrinkIds.Order(StringComparer.Ordinal).ToArray(),
            actions);
    }

    public IReadOnlyList<string> GetPermittedFirstToolCalls()
    {
        var actions = new List<string>();
        if (ActiveDrinkId is null)
        {
            actions.AddRange(BartenderInteractionCatalog.Drinks
                .Where(drink =>
                    !_unavailableDrinkIds.Contains(drink.Id)
                    && TabTotal + drink.Price <= WalletBalance)
                .Select(drink => $"serve_drink(drink_id={drink.Id})"));
        }
        if (TabTotal > 0 && !BillPresented)
        {
            actions.Add("present_bill");
        }
        if (!SnackOffered)
        {
            actions.Add("offer_snack");
        }
        if (ActiveDrinkId is not null && ActiveDrinkFillLevel == 0)
        {
            actions.Add("clear_empty_glass");
        }

        actions.Add("polish_glass");
        actions.Add("wipe_counter");
        actions.Add("announce_last_call");
        actions.AddRange(BartenderInteractionCatalog.Drinks
            .Where(drink =>
                drink.Id != ActiveDrinkId
                && !_unavailableDrinkIds.Contains(drink.Id))
            .Select(drink => $"mark_drink_unavailable(drink_id={drink.Id})"));
        return actions;
    }

    public SpeakingInteractionEvent ApplyUserAction(
        SpeakingInteractionAction action,
        IReadOnlyDictionary<int, int>? tender)
    {
        return action switch
        {
            SpeakingInteractionAction.Drink => Drink(),
            SpeakingInteractionAction.TakeSnack => TakeSnack(),
            SpeakingInteractionAction.SubmitPayment => SubmitPayment(tender),
            _ => throw new SpeakingValidationException("That interaction is not supported."),
        };
    }

    public IReadOnlyList<SpeakingSceneCommand> ApplyProposedActions(
        IReadOnlyList<SpeakingProposedAction>? actions,
        Action<SpeakingProposedAction, string>? onRejected = null)
    {
        if (actions is null || actions.Count == 0)
        {
            return [];
        }

        var working = Clone();
        var commands = new List<SpeakingSceneCommand>(Math.Min(actions.Count, 3));
        foreach (var action in actions.Take(3))
        {
            var trial = working.Clone();
            try
            {
                commands.Add(trial.ApplyProposedAction(action));
                working = trial;
            }
            catch (InvalidBartenderProposalException ex)
            {
                onRejected?.Invoke(action, ex.Message);
            }
        }

        foreach (var action in actions.Skip(3))
        {
            onRejected?.Invoke(action, "The avatar proposed more than three scene actions.");
        }

        CopyFrom(working);
        return commands;
    }

    private SpeakingSceneCommand ApplyProposedAction(SpeakingProposedAction action)
    {
        return action.Type switch
        {
            SpeakingProposedActionType.ServeDrink => ServeDrink(action.DrinkId),
            SpeakingProposedActionType.PresentBill => PresentBill(),
            SpeakingProposedActionType.OfferSnack => OfferSnack(),
            SpeakingProposedActionType.ClearGlass => ClearGlass(),
            SpeakingProposedActionType.PolishGlass => new("polishGlass"),
            SpeakingProposedActionType.WipeCounter => new("wipeCounter"),
            SpeakingProposedActionType.LastCall => new("lastCall"),
            SpeakingProposedActionType.MarkUnavailable => MarkUnavailable(action.DrinkId),
            _ => throw InvalidAgentAction("The avatar proposed an unknown scene action."),
        };
    }

    private SpeakingInteractionEvent Drink()
    {
        if (ActiveDrinkId is null || ActiveDrinkFillLevel <= 0)
        {
            throw new SpeakingValidationException("There is no drink to sip.");
        }

        ActiveDrinkFillLevel--;
        return new SpeakingInteractionEvent(
            ActiveDrinkFillLevel == 0
                ? "The learner finished the served drink."
                : "The learner took a sip of the served drink.",
            [new SpeakingSceneCommand("drink", ActiveDrinkId, FillLevel: ActiveDrinkFillLevel)]);
    }

    private SpeakingInteractionEvent TakeSnack()
    {
        if (!SnackOffered)
        {
            throw new SpeakingValidationException("Marek has not offered a snack.");
        }

        SnackOffered = false;
        return new SpeakingInteractionEvent(
            "The learner accepted and took some paluszki.",
            [new SpeakingSceneCommand("takeSnack")]);
    }

    private SpeakingInteractionEvent SubmitPayment(IReadOnlyDictionary<int, int>? tender)
    {
        if (!BillPresented || TabTotal <= 0)
        {
            throw new SpeakingValidationException("Marek has not presented a bill.");
        }
        if (tender is null || tender.Count == 0)
        {
            throw new SpeakingValidationException("Choose at least one note or coin.");
        }

        var selected = new Dictionary<int, int>();
        foreach (var item in tender)
        {
            if (!BartenderInteractionCatalog.Denominations.Contains(item.Key)
                || item.Value <= 0
                || !_wallet.TryGetValue(item.Key, out var owned)
                || item.Value > owned)
            {
                throw new SpeakingValidationException("The selected payment is not available in the wallet.");
            }
            selected[item.Key] = item.Value;
        }

        var amount = selected.Sum(item => item.Key * item.Value);
        if (amount < TabTotal)
        {
            return new SpeakingInteractionEvent(
                $"The learner offered {amount} zł for a {TabTotal} zł bill, which is insufficient. No money changed hands.",
                [new SpeakingSceneCommand("paymentRejected", Amount: amount)]);
        }

        foreach (var item in selected)
        {
            _wallet[item.Key] -= item.Value;
        }

        var bill = TabTotal;
        var change = amount - bill;
        AddChange(change);
        TabTotal = 0;
        BillPresented = false;

        var commands = new List<SpeakingSceneCommand>
        {
            new("paymentAccepted", Amount: amount),
        };
        if (change > 0)
        {
            commands.Add(new SpeakingSceneCommand("returnChange", Amount: change));
        }

        return new SpeakingInteractionEvent(
            change == 0
                ? $"The learner paid the exact {bill} zł bill."
                : $"The learner paid {amount} zł for a {bill} zł bill and received {change} zł change.",
            commands);
    }

    private SpeakingSceneCommand ServeDrink(string? drinkId)
    {
        if (!BartenderInteractionCatalog.TryGetDrink(drinkId, out var drink))
        {
            throw InvalidAgentAction("The avatar proposed an unknown drink.");
        }
        if (_unavailableDrinkIds.Contains(drink.Id))
        {
            throw InvalidAgentAction("The avatar tried to serve an unavailable drink.");
        }
        if (ActiveDrinkId is not null)
        {
            throw InvalidAgentAction("The avatar tried to serve a drink before clearing the current glass.");
        }
        if (TabTotal + drink.Price > WalletBalance)
        {
            throw InvalidAgentAction("The avatar tried to serve more than the learner can pay for.");
        }

        ActiveDrinkId = drink.Id;
        ActiveDrinkFillLevel = 3;
        TabTotal += drink.Price;
        return new SpeakingSceneCommand("pourAndServe", drink.Id, drink.Price, 3);
    }

    private SpeakingSceneCommand PresentBill()
    {
        if (TabTotal <= 0)
        {
            throw InvalidAgentAction("The avatar tried to present an empty bill.");
        }
        if (BillPresented)
        {
            throw InvalidAgentAction("The bill is already on the counter.");
        }

        BillPresented = true;
        return new SpeakingSceneCommand("showBill", Amount: TabTotal);
    }

    private SpeakingSceneCommand OfferSnack()
    {
        if (SnackOffered)
        {
            throw InvalidAgentAction("The snack is already being offered.");
        }

        SnackOffered = true;
        return new SpeakingSceneCommand("offerSnack");
    }

    private SpeakingSceneCommand ClearGlass()
    {
        if (ActiveDrinkId is null || ActiveDrinkFillLevel != 0)
        {
            throw InvalidAgentAction("The avatar can only clear an empty glass.");
        }

        var drinkId = ActiveDrinkId;
        ActiveDrinkId = null;
        ActiveDrinkFillLevel = 0;
        return new SpeakingSceneCommand("clearGlass", drinkId);
    }

    private SpeakingSceneCommand MarkUnavailable(string? drinkId)
    {
        if (!BartenderInteractionCatalog.TryGetDrink(drinkId, out var drink))
        {
            throw InvalidAgentAction("The avatar marked an unknown drink unavailable.");
        }
        if (string.Equals(ActiveDrinkId, drink.Id, StringComparison.Ordinal))
        {
            throw InvalidAgentAction("A served drink cannot become unavailable.");
        }
        if (!_unavailableDrinkIds.Add(drink.Id))
        {
            throw InvalidAgentAction("That drink is already unavailable.");
        }

        return new SpeakingSceneCommand("markUnavailable", drink.Id);
    }

    private void AddChange(int amount)
    {
        foreach (var denomination in BartenderInteractionCatalog.Denominations)
        {
            var count = amount / denomination;
            if (count == 0)
            {
                continue;
            }

            _wallet[denomination] += count;
            amount -= count * denomination;
        }
    }

    private void CopyFrom(BartenderInteractionState source)
    {
        _wallet.Clear();
        foreach (var item in source._wallet)
        {
            _wallet[item.Key] = item.Value;
        }

        TabTotal = source.TabTotal;
        BillPresented = source.BillPresented;
        ActiveDrinkId = source.ActiveDrinkId;
        ActiveDrinkFillLevel = source.ActiveDrinkFillLevel;
        SnackOffered = source.SnackOffered;
        _unavailableDrinkIds.Clear();
        _unavailableDrinkIds.UnionWith(source._unavailableDrinkIds);
    }

    private static InvalidBartenderProposalException InvalidAgentAction(string message) =>
        new(message);

    private sealed class InvalidBartenderProposalException(string message)
        : InvalidOperationException(message);
}

public sealed record SpeakingInteractionEvent(
    string Description,
    IReadOnlyList<SpeakingSceneCommand> SceneCommands);
