using Glosify.Services.Ai;
using Glosify.Services.Speaking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class SpeakingServiceTests
{
    private static readonly SpeakingAvatarDefinition Bartender =
        SpeakingAvatarCatalog.Get(SpeakingAvatarId.Bartender);

    [Theory]
    [InlineData("bartender", SpeakingAvatarId.Bartender)]
    [InlineData("KASIA", SpeakingAvatarId.Kasia)]
    [InlineData("mietek", SpeakingAvatarId.Mietek)]
    [InlineData("maarja", SpeakingAvatarId.Maarja)]
    [InlineData("FRAU-SCHNEIDER", SpeakingAvatarId.FrauSchneider)]
    [InlineData("pan-mykola", SpeakingAvatarId.PanMykola)]
    public void Avatar_catalog_parses_supported_slugs(string slug, SpeakingAvatarId expected)
    {
        Assert.True(SpeakingAvatarCatalog.TryParse(slug, out var avatar));
        Assert.Equal(expected, avatar.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("child")]
    [InlineData("A2")]
    public void Avatar_catalog_rejects_unknown_slugs(string slug)
    {
        Assert.False(SpeakingAvatarCatalog.TryParse(slug, out _));
    }

    [Theory]
    [InlineData("Estonian", "et-EE")]
    [InlineData("German", "de-DE")]
    [InlineData("Polish", "pl-PL")]
    [InlineData("Ukrainian", "uk-UA")]
    public void Avatar_catalog_has_three_language_bound_avatars(string language, string locale)
    {
        var avatars = SpeakingAvatarCatalog.ForLanguage(language);

        Assert.Equal(3, avatars.Count);
        Assert.All(avatars, avatar =>
        {
            Assert.Equal(language, avatar.Language);
            Assert.Equal(locale, avatar.Locale);
        });
    }

    [Fact]
    public void Avatar_catalog_rejects_an_avatar_from_another_language()
    {
        Assert.False(SpeakingAvatarCatalog.TryParseForLanguage("bartender", "German", out _));
        Assert.True(SpeakingAvatarCatalog.TryParseForLanguage("hanna", "German", out var avatar));
        Assert.Equal(SpeakingAvatarId.Hanna, avatar.Id);
    }

    [Fact]
    public void Kasia_is_the_Polish_convenience_store_cashier()
    {
        var kasia = SpeakingAvatarCatalog.Get(SpeakingAvatarId.Kasia);

        Assert.Equal("Żabka checkout", kasia.Scenario);
        Assert.Equal("ŻABKA", kasia.SceneSign);
        Assert.Contains("kawę", kasia.OpeningPolish, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A1", CefrLevel.A1)]
    [InlineData("a2", CefrLevel.A2)]
    [InlineData("B1", CefrLevel.B1)]
    [InlineData("b2", CefrLevel.B2)]
    [InlineData("C1", CefrLevel.C1)]
    public void Cefr_parser_accepts_only_supported_levels(string value, CefrLevel expected)
    {
        Assert.True(SpeakingAvatarCatalog.TryParseCefr(value, out var actual));
        Assert.Equal(expected, actual);
        Assert.False(SpeakingAvatarCatalog.TryParseCefr("C2", out _));
    }

    [Fact]
    public async Task Session_store_enforces_ownership_and_deletion()
    {
        var conversation = new FakeConversation();
        var store = CreateStore(new FakeAgentClient(() => conversation));
        var session = await store.CreateAsync("learner-1", Bartender, CefrLevel.A2);

        Assert.Throws<SpeakingSessionNotFoundException>(
            () => store.Get(session.Id, "learner-2"));
        await Assert.ThrowsAsync<SpeakingSessionNotFoundException>(
            () => store.DeleteAsync(session.Id, "learner-2"));

        await store.DeleteAsync(session.Id, "learner-1");

        Assert.Equal(1, conversation.DisposeCount);
        Assert.Throws<SpeakingSessionNotFoundException>(
            () => store.Get(session.Id, "learner-1"));
    }

    [Fact]
    public async Task Session_store_invalidation_is_idempotent()
    {
        var conversation = new FakeConversation();
        var store = CreateStore(new FakeAgentClient(() => conversation));
        var session = await store.CreateAsync("learner", Bartender, CefrLevel.A2);

        await Task.WhenAll(
            store.InvalidateAsync(session),
            store.InvalidateAsync(session));

        Assert.Equal(1, conversation.DisposeCount);
        Assert.Throws<SpeakingSessionNotFoundException>(
            () => store.Get(session.Id, "learner"));
    }

    [Fact]
    public async Task Session_store_returns_gone_after_sliding_expiry()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero));
        var store = CreateStore(new FakeAgentClient(() => new FakeConversation()), clock);
        var session = await store.CreateAsync("learner", Bartender, CefrLevel.A2);

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Throws<SpeakingSessionExpiredException>(
            () => store.Get(session.Id, "learner"));
        Assert.Throws<SpeakingSessionNotFoundException>(
            () => store.Get(session.Id, "learner"));
    }

    [Fact]
    public async Task Session_store_caps_active_sessions_per_user()
    {
        var store = CreateStore(
            new FakeAgentClient(() => new FakeConversation()),
            options: new SpeakingOptions { MaxSessionsPerUser = 3 });

        for (var index = 0; index < 3; index++)
        {
            await store.CreateAsync("learner", Bartender, CefrLevel.A2);
        }

        await Assert.ThrowsAsync<SpeakingSessionLimitException>(
            () => store.CreateAsync("learner", Bartender, CefrLevel.A2));

        // The cap is per user, not global.
        await store.CreateAsync("another-learner", Bartender, CefrLevel.A2);
    }

    [Fact]
    public async Task Turn_commits_exact_foundry_usage_after_validated_output()
    {
        var expectedUsage = new AiTokenUsage(91, 27, 0, 0, 118);
        var conversation = new FakeConversation((_, _) =>
            Task.FromResult(new SpeakingAgentTurn(ValidTurn(), expectedUsage)));
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(conversation, credits);

        var turn = await service.SendTurnAsync(
            sessionId,
            "learner",
            "  Poproszę piwo.  ",
            SpeakingInputMode.Voice);

        Assert.Equal("Dobrze, już podaję.", turn.ReplyPolish);
        var reservation = Assert.Single(credits.Reservations);
        Assert.Equal(AiUsageFeatures.Speaking, reservation.Context.Feature);
        Assert.Equal("speaking_turn", reservation.Context.Operation);
        Assert.Equal("grok-4-1-fast-non-reasoning", reservation.Model);
        Assert.Equal(expectedUsage, Assert.Single(credits.Commits).Usage);
        Assert.Empty(credits.Releases);
        Assert.Contains("Poproszę piwo.", Assert.Single(conversation.Messages));
        Assert.Contains("Practice language: Polish (pl-PL)", conversation.Messages[0]);
    }

    [Fact]
    public async Task Turn_commits_reservation_estimate_when_usage_is_unavailable()
    {
        var conversation = new FakeConversation((_, _) =>
            Task.FromResult(new SpeakingAgentTurn(ValidTurn(), null)));
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(conversation, credits);

        await service.SendTurnAsync(sessionId, "learner", "Dzień dobry", SpeakingInputMode.Text);

        var reservation = Assert.Single(credits.Reservations);
        var committed = Assert.Single(credits.Commits).Usage;
        Assert.Equal(reservation.EstimatedTokens, committed.TotalTokens);
        Assert.Equal(768, committed.CandidateTokens);
    }

    [Fact]
    public async Task Turn_releases_reservation_when_agent_or_schema_fails()
    {
        var incomplete = ValidTurn();
        incomplete.ReplyPolish = " ";
        var conversation = new FakeConversation((_, _) =>
            Task.FromResult(new SpeakingAgentTurn(incomplete, null)));
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(conversation, credits);

        await Assert.ThrowsAsync<SpeakingUpstreamException>(() =>
            service.SendTurnAsync(sessionId, "learner", "Hej", SpeakingInputMode.Text));

        var reservation = Assert.Single(credits.Reservations);
        Assert.Equal(reservation.ReservationId, Assert.Single(credits.Releases));
        Assert.Empty(credits.Commits);
    }

    [Fact]
    public async Task Session_allows_only_one_in_flight_turn()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conversation = new FakeConversation(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new SpeakingAgentTurn(ValidTurn(), new AiTokenUsage(1, 1, 0, 0, 2));
        });
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(conversation, credits);

        var first = service.SendTurnAsync(sessionId, "learner", "Pierwsza", SpeakingInputMode.Text);
        await entered.Task;

        await Assert.ThrowsAsync<SpeakingSessionBusyException>(() =>
            service.SendTurnAsync(sessionId, "learner", "Druga", SpeakingInputMode.Text));

        release.TrySetResult();
        await first;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Turn_rejects_blank_text_before_reserving_credits(string text)
    {
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(new FakeConversation(), credits);

        await Assert.ThrowsAsync<SpeakingValidationException>(() =>
            service.SendTurnAsync(sessionId, "learner", text, SpeakingInputMode.Text));

        Assert.Empty(credits.Reservations);
    }

    [Fact]
    public async Task Turn_rejects_text_over_800_characters()
    {
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(new FakeConversation(), credits);

        await Assert.ThrowsAsync<SpeakingValidationException>(() =>
            service.SendTurnAsync(
                sessionId,
                "learner",
                new string('x', SpeakingService.MaxInputLength + 1),
                SpeakingInputMode.Text));
    }

    [Fact]
    public async Task Bartender_session_automatically_exposes_scene_tools_and_state()
    {
        var conversation = new FakeConversation();
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);

        var turn = await service.SendTurnAsync(
            sessionId,
            "learner",
            "Poproszę wodę gazowaną.",
            SpeakingInputMode.Text);

        Assert.NotNull(Assert.Single(conversation.InteractionStates));
        Assert.Contains("Wallet balance: 100 zł", Assert.Single(conversation.Messages));
        Assert.Contains("supplied function tools", conversation.Messages[0]);
        Assert.Contains("serve_drink", conversation.Messages[0]);
        Assert.Contains("call zero to three", conversation.Messages[0]);
        Assert.Contains("wait for each tool result", conversation.Messages[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("means drink_id lightBeer", conversation.Messages[0]);
        Assert.Contains("never require payment", conversation.Messages[0]);
        Assert.Contains(
            "Legal first scene tools now: serve_drink(drink_id=lightBeer)",
            conversation.Messages[0]);
        Assert.DoesNotContain("Return proposedActions", conversation.Messages[0]);
        Assert.NotNull(turn.Interaction);
        Assert.Equal(100, turn.Interaction.WalletBalance);
    }

    [Fact]
    public async Task Exact_piwo_turn_returns_the_pour_executed_by_the_agent_tool()
    {
        var conversation = new FakeConversation((_, state, _) =>
        {
            var commands = Assert.IsType<BartenderInteractionState>(state)
                .ApplyProposedActions(
                    [Proposal(SpeakingProposedActionType.ServeDrink, "lightBeer")]);
            return Task.FromResult(new SpeakingAgentTurn(ValidTurn(), null, commands));
        });
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);

        var turn = await service.SendTurnAsync(
            sessionId,
            "learner",
            "Piwo.",
            SpeakingInputMode.Voice);

        var command = Assert.Single(turn.SceneActions);
        Assert.Equal("pourAndServe", command.Type);
        Assert.Equal("lightBeer", command.DrinkId);
        Assert.Equal(14, command.Amount);
        Assert.Equal(3, command.FillLevel);
        Assert.Equal("lightBeer", Assert.Single(turn.Interaction!.ActiveDrinks).Id);
        Assert.Equal(14, turn.Interaction?.TabTotal);
    }

    [Fact]
    public async Task Exact_piwo_turn_without_an_agent_tool_call_does_not_use_a_phrase_fallback()
    {
        var conversation = new FakeConversation((_, _) =>
            Task.FromResult(new SpeakingAgentTurn(ValidTurn(), null, [])));
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);

        var turn = await service.SendTurnAsync(
            sessionId,
            "learner",
            "Piwo.",
            SpeakingInputMode.Voice);

        Assert.Empty(turn.SceneActions);
        Assert.Empty(turn.Interaction!.ActiveDrinks);
        Assert.Equal(0, turn.Interaction?.TabTotal);
    }

    [Fact]
    public async Task Scene_capability_is_derived_from_the_flag_and_bartender_avatar()
    {
        var timeProvider = TimeProvider.System;
        var credits = new FakeCredits();

        var disabledStore = CreateStore(
            new FakeAgentClient(() => new FakeConversation()),
            timeProvider);
        var disabled = new SpeakingService(
            disabledStore,
            credits,
            Options.Create(new AiUsageOptions()),
            Options.Create(new SpeakingOptions
            {
                InteractiveBartenderEnabled = false,
            }),
            timeProvider,
            NullLogger<SpeakingService>.Instance);
        var disabledBartender = await disabled.CreateSessionAsync(
            "learner",
            Bartender,
            CefrLevel.A2);
        Assert.Null(disabledBartender.Interaction);

        var enabledStore = CreateStore(
            new FakeAgentClient(() => new FakeConversation()),
            timeProvider);
        var enabled = new SpeakingService(
            enabledStore,
            credits,
            Options.Create(new AiUsageOptions()),
            Options.Create(new SpeakingOptions
            {
                InteractiveBartenderEnabled = true,
            }),
            timeProvider,
            NullLogger<SpeakingService>.Instance);
        var enabledBartender = await enabled.CreateSessionAsync(
            "learner",
            Bartender,
            CefrLevel.A2);
        var enabledKasia = await enabled.CreateSessionAsync(
            "learner",
            SpeakingAvatarCatalog.Get(SpeakingAvatarId.Kasia),
            CefrLevel.A2);

        Assert.NotNull(enabledBartender.Interaction);
        Assert.Null(enabledKasia.Interaction);
    }

    [Fact]
    public async Task Executed_scene_tools_commit_as_authoritative_commands_and_state()
    {
        var conversation = new FakeConversation((_, state, _) =>
        {
            var commands = Assert.IsType<BartenderInteractionState>(state)
                .ApplyProposedActions(
                    [
                        Proposal(SpeakingProposedActionType.ServeDrink, "lightBeer"),
                        Proposal(SpeakingProposedActionType.ClearGlass),
                        Proposal(SpeakingProposedActionType.PresentBill),
                    ]);
            return Task.FromResult(new SpeakingAgentTurn(
                ValidTurn(),
                new AiTokenUsage(10, 5, 0, 0, 15),
                commands));
        });
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);

        var turn = await service.SendTurnAsync(
            sessionId,
            "learner",
            "Poproszę jasne piwo i rachunek.",
            SpeakingInputMode.Text);

        Assert.Collection(
            turn.SceneActions,
            command => Assert.Equal("pourAndServe", command.Type),
            command => Assert.Equal("showBill", command.Type));
        Assert.Equal(14, turn.Interaction?.TabTotal);
        Assert.True(turn.Interaction?.BillPresented);
        Assert.Equal("lightBeer", Assert.Single(turn.Interaction!.ActiveDrinks).Id);
    }

    [Fact]
    public async Task Drinking_updates_the_scene_without_dispatching_a_bartender_reply()
    {
        var conversation = new FakeConversation((_, state, _) =>
        {
            var commands = Assert.IsType<BartenderInteractionState>(state)
                .ApplyProposedActions(
                    [Proposal(SpeakingProposedActionType.ServeDrink, "stillWater")]);
            return Task.FromResult(new SpeakingAgentTurn(
                ValidTurn(),
                null,
                commands));
        });
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);
        await service.SendTurnAsync(
            sessionId,
            "learner",
            "Poproszę wodę.",
            SpeakingInputMode.Text);

        var turn = await service.SendActionAsync(
            sessionId,
            "learner",
            SpeakingInteractionAction.Drink,
            denominations: null,
            drinkId: "stillWater");

        Assert.Equal("drink", Assert.Single(turn.SceneActions).Type);
        Assert.Equal(2, Assert.Single(turn.Interaction!.ActiveDrinks).FillLevel);
        Assert.True(turn.SuppressAvatarReaction);
        Assert.Empty(turn.ReplyPolish);
        Assert.Empty(turn.ReplyEnglish);
        Assert.Single(conversation.Messages);
        Assert.Single(credits.Reservations);
        Assert.Single(credits.Commits);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Eating_updates_the_scene_without_dispatching_a_bartender_reply()
    {
        var conversation = new FakeConversation((_, state, _) =>
        {
            var commands = Assert.IsType<BartenderInteractionState>(state)
                .ApplyProposedActions(
                    [Proposal(SpeakingProposedActionType.OfferSnack)]);
            return Task.FromResult(new SpeakingAgentTurn(
                ValidTurn(),
                null,
                commands));
        });
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);
        await service.SendTurnAsync(
            sessionId,
            "learner",
            "Macie jakieś przekąski?",
            SpeakingInputMode.Text);

        var turn = await service.SendActionAsync(
            sessionId,
            "learner",
            SpeakingInteractionAction.TakeSnack,
            denominations: null);

        Assert.Equal("takeSnack", Assert.Single(turn.SceneActions).Type);
        Assert.False(turn.Interaction?.SnackOffered);
        Assert.True(turn.SuppressAvatarReaction);
        Assert.Empty(turn.ReplyPolish);
        Assert.Empty(turn.ReplyEnglish);
        Assert.Single(conversation.Messages);
        Assert.Single(credits.Reservations);
        Assert.Single(credits.Commits);
        Assert.Empty(credits.Releases);
    }

    [Fact]
    public async Task Incomplete_reply_after_an_executed_scene_tool_invalidates_the_session()
    {
        var conversation = new FakeConversation((_, state, _) =>
        {
            var commands = Assert.IsType<BartenderInteractionState>(state)
                .ApplyProposedActions(
                    [Proposal(SpeakingProposedActionType.ServeDrink, "stillWater")]);
            var incomplete = ValidTurn();
            incomplete.ReplyPolish = " ";
            return Task.FromResult(new SpeakingAgentTurn(
                incomplete,
                null,
                commands));
        });
        var credits = new FakeCredits();
        var timeProvider = TimeProvider.System;
        var store = CreateStore(new FakeAgentClient(() => conversation), timeProvider);
        var service = CreateService(store, credits, timeProvider, interactiveMode: true);
        var created = await service.CreateSessionAsync("learner", Bartender, CefrLevel.A2);
        var session = store.Get(created.SessionId, "learner");

        var exception = await Assert.ThrowsAsync<SpeakingSessionInvalidatedException>(() =>
            service.SendTurnAsync(
                created.SessionId,
                "learner",
                "Poproszę wodę.",
                SpeakingInputMode.Text));

        Assert.IsType<SpeakingUpstreamException>(exception.InnerException);
        Assert.Single(credits.Releases);
        Assert.Empty(credits.Commits);
        Assert.Equal(1, conversation.DisposeCount);
        Assert.Equal(1, session.TurnGate.CurrentCount);
        await Assert.ThrowsAsync<SpeakingSessionNotFoundException>(() =>
            service.SendTurnAsync(
                created.SessionId,
                "learner",
                "Spróbujmy dalej.",
                SpeakingInputMode.Text));
    }

    [Fact]
    public async Task Credit_commit_failure_after_an_executed_scene_tool_invalidates_the_session()
    {
        var conversation = new FakeConversation((_, state, _) =>
        {
            var commands = Assert.IsType<BartenderInteractionState>(state)
                .ApplyProposedActions(
                    [Proposal(SpeakingProposedActionType.ServeDrink, "lightBeer")]);
            return Task.FromResult(new SpeakingAgentTurn(
                ValidTurn(),
                new AiTokenUsage(10, 5, 0, 0, 15),
                commands));
        });
        var commitError = new InvalidOperationException("database commit failed");
        var credits = new FakeCredits
        {
            CommitError = commitError,
        };
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);

        var exception = await Assert.ThrowsAsync<SpeakingSessionInvalidatedException>(() =>
            service.SendTurnAsync(
                sessionId,
                "learner",
                "Poproszę piwo.",
                SpeakingInputMode.Text));

        Assert.Same(commitError, exception.InnerException);
        Assert.Single(credits.Releases);
        Assert.Empty(credits.Commits);
        Assert.Equal(1, conversation.DisposeCount);
        await Assert.ThrowsAsync<SpeakingSessionNotFoundException>(() =>
            service.SendTurnAsync(
                sessionId,
                "learner",
                "Spróbujmy dalej.",
                SpeakingInputMode.Text));
    }

    [Fact]
    public async Task Invalid_user_action_does_not_reserve_credit()
    {
        var conversation = new FakeConversation();
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);

        await Assert.ThrowsAsync<SpeakingValidationException>(() => service.SendActionAsync(
            sessionId,
            "learner",
            SpeakingInteractionAction.Drink,
            denominations: null));

        Assert.Empty(credits.Reservations);
        Assert.Empty(conversation.Messages);

        var continued = await service.SendTurnAsync(
            sessionId,
            "learner",
            "To porozmawiajmy dalej.",
            SpeakingInputMode.Text);

        Assert.Equal("Dobrze, już podaję.", continued.ReplyPolish);
        Assert.Single(credits.Commits);
    }

    [Fact]
    public async Task Open_bill_and_scene_state_never_gate_a_later_conversation_turn()
    {
        var call = 0;
        var conversation = new FakeConversation((_, state, _) =>
        {
            call++;
            IReadOnlyList<SpeakingSceneCommand> commands = call == 1
                ? Assert.IsType<BartenderInteractionState>(state)
                    .ApplyProposedActions(
                        [
                            Proposal(SpeakingProposedActionType.ServeDrink, "lightBeer"),
                            Proposal(SpeakingProposedActionType.PresentBill),
                            Proposal(SpeakingProposedActionType.MarkUnavailable, "vodka"),
                        ])
                : [];
            return Task.FromResult(new SpeakingAgentTurn(ValidTurn(), null, commands));
        });
        var credits = new FakeCredits();
        var (service, sessionId) = await CreateServiceAsync(
            conversation,
            credits,
            interactiveMode: true);

        await service.SendTurnAsync(
            sessionId,
            "learner",
            "Piwo i rachunek, proszę.",
            SpeakingInputMode.Text);
        var insufficient = await service.SendActionAsync(
            sessionId,
            "learner",
            SpeakingInteractionAction.SubmitPayment,
            new Dictionary<int, int> { [5] = 1 });
        var continued = await service.SendTurnAsync(
            sessionId,
            "learner",
            "Nieważne, opowiedz mi coś o Krakowie.",
            SpeakingInputMode.Voice);

        Assert.Equal("paymentRejected", Assert.Single(insufficient.SceneActions).Type);
        Assert.Equal("Dobrze, już podaję.", continued.ReplyPolish);
        var interaction = Assert.IsType<SpeakingInteractionSnapshot>(continued.Interaction);
        Assert.Equal("lightBeer", Assert.Single(interaction.ActiveDrinks).Id);
        Assert.True(interaction.BillPresented);
        Assert.Equal(14, interaction.TabTotal);
        Assert.Contains("vodka", interaction.UnavailableDrinkIds);
        Assert.Contains("Bill presented: True", conversation.Messages[2]);
        Assert.Contains("Active drinks: lightBeer", conversation.Messages[2]);
        Assert.Equal(3, credits.Commits.Count);
    }

    private static SpeakingSessionStore CreateStore(
        ISpeakingAgentClient agentClient,
        TimeProvider? timeProvider = null,
        SpeakingOptions? options = null) =>
        new(
            agentClient,
            Options.Create(options ?? new SpeakingOptions
            {
                SessionTtlMinutes = 5,
                MaxSessionsPerUser = 3,
            }),
            timeProvider ?? TimeProvider.System);

    private static async Task<(SpeakingService Service, Guid SessionId)> CreateServiceAsync(
        FakeConversation conversation,
        FakeCredits credits,
        bool interactiveMode = false)
    {
        var timeProvider = TimeProvider.System;
        var store = CreateStore(new FakeAgentClient(() => conversation), timeProvider);
        var service = CreateService(store, credits, timeProvider, interactiveMode);
        var created = await service.CreateSessionAsync(
            "learner",
            Bartender,
            CefrLevel.A2);
        return (service, created.SessionId);
    }

    private static SpeakingService CreateService(
        ISpeakingSessionStore store,
        FakeCredits credits,
        TimeProvider timeProvider,
        bool interactiveMode) =>
        new(
            store,
            credits,
            Options.Create(new AiUsageOptions { SpeakingOutputTokenReserve = 768 }),
            Options.Create(new SpeakingOptions
            {
                ModelDeployment = "grok-4-1-fast-non-reasoning",
                InteractiveBartenderEnabled = interactiveMode,
            }),
            timeProvider,
            NullLogger<SpeakingService>.Instance);

    private static SpeakingProposedAction Proposal(
        SpeakingProposedActionType type,
        string? drinkId = null) =>
        new()
        {
            Type = type,
            DrinkId = drinkId,
        };

    private static SpeakingTurn ValidTurn() =>
        new()
        {
            ReplyPolish = " Dobrze, już podaję. ",
            ReplyEnglish = " All right, coming up. ",
            Coach = new SpeakingCoach
            {
                CorrectedPolish = "Poproszę piwo.",
                GrammarTipEnglish = "Use the accusative after poproszę.",
                VocabularyTipEnglish = "Lane means draft.",
                NaturalnessTipEnglish = "This sounds natural.",
            },
        };

    private sealed class FakeAgentClient(Func<FakeConversation> factory) : ISpeakingAgentClient
    {
        public bool IsConfigured => true;

        public Task<ISpeakingAgentConversation> CreateConversationAsync(
            SpeakingAvatarId avatar,
            bool interactiveMode = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ISpeakingAgentConversation>(factory());
    }

    private sealed class FakeConversation : ISpeakingAgentConversation
    {
        private readonly Func<
            string,
            BartenderInteractionState?,
            CancellationToken,
            Task<SpeakingAgentTurn>> _run;

        public FakeConversation(
            Func<string, CancellationToken, Task<SpeakingAgentTurn>>? run = null)
        {
            _run = run is null
                ? ((_, _, _) => Task.FromResult(
                    new SpeakingAgentTurn(
                        ValidTurn(),
                        new AiTokenUsage(1, 1, 0, 0, 2))))
                : ((message, _, cancellationToken) => run(message, cancellationToken));
        }

        public FakeConversation(
            Func<
                string,
                BartenderInteractionState?,
                CancellationToken,
                Task<SpeakingAgentTurn>> run)
        {
            _run = run;
        }

        public List<string> Messages { get; } = [];
        public List<BartenderInteractionState?> InteractionStates { get; } = [];
        public int DisposeCount { get; private set; }

        public Task<SpeakingAgentTurn> RunTurnAsync(
            string message,
            BartenderInteractionState? interactionState = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            InteractionStates.Add(interactionState);
            return _run(message, interactionState, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeCredits : IAiCreditService
    {
        public List<(Guid ReservationId, AiUsageContext Context, string Provider, string Model, int EstimatedTokens)>
            Reservations { get; } = [];
        public List<(Guid ReservationId, AiTokenUsage Usage)> Commits { get; } = [];
        public List<Guid> Releases { get; } = [];
        public Exception? CommitError { get; init; }

        public Task<AiCreditReservation> ReserveAsync(
            AiUsageContext context,
            string provider,
            string model,
            int estimatedTokens,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            Reservations.Add((id, context, provider, model, estimatedTokens));
            return Task.FromResult(new AiCreditReservation(id, context.UserId, 1, estimatedTokens));
        }

        public Task CommitUsageAsync(
            Guid reservationId,
            AiTokenUsage usage,
            CancellationToken cancellationToken = default)
        {
            if (CommitError is not null)
            {
                return Task.FromException(CommitError);
            }

            Commits.Add((reservationId, usage));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
        {
            Releases.Add(reservationId);
            return Task.CompletedTask;
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
}
