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
        Assert.Equal("gpt-5.4-mini", reservation.Model);
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
        FakeCredits credits)
    {
        var timeProvider = TimeProvider.System;
        var store = CreateStore(new FakeAgentClient(() => conversation), timeProvider);
        var service = new SpeakingService(
            store,
            credits,
            Options.Create(new AiUsageOptions { SpeakingOutputTokenReserve = 768 }),
            Options.Create(new SpeakingOptions { ModelDeployment = "gpt-5.4-mini" }),
            timeProvider,
            NullLogger<SpeakingService>.Instance);
        var created = await service.CreateSessionAsync("learner", Bartender, CefrLevel.A2);
        return (service, created.SessionId);
    }

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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ISpeakingAgentConversation>(factory());
    }

    private sealed class FakeConversation : ISpeakingAgentConversation
    {
        private readonly Func<string, CancellationToken, Task<SpeakingAgentTurn>> _run;

        public FakeConversation(
            Func<string, CancellationToken, Task<SpeakingAgentTurn>>? run = null)
        {
            _run = run ?? ((_, _) => Task.FromResult(
                new SpeakingAgentTurn(ValidTurn(), new AiTokenUsage(1, 1, 0, 0, 2))));
        }

        public List<string> Messages { get; } = [];
        public int DisposeCount { get; private set; }

        public Task<SpeakingAgentTurn> RunTurnAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return _run(message, cancellationToken);
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
