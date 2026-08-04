using Glosify.Data;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationServiceTests
{
    [Fact]
    public async Task CreateAndBegin_AreIdempotentAndChargeOneMinute()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
        var service = CreateService(context, clock, new FakeRelayTokenStore());

        var created = await service.CreateSessionAsync("user-1", "es");
        var first = await service.BeginMinuteAsync("user-1", created.SessionId, 1);
        var duplicate = await service.BeginMinuteAsync("user-1", created.SessionId, 1);

        Assert.Equal(1, first.ChargedMinutes);
        Assert.Equal(1, duplicate.ChargedMinutes);
        Assert.Equal(8, duplicate.CreditsCharged);
        Assert.Equal(17, duplicate.AvailableCredits);
        var transaction = Assert.Single(await context.AiCreditTransactions.Where(transaction =>
            transaction.Kind == AiCreditTransactionKinds.UsageDebit
            && transaction.AudioDurationSeconds == 60).ToListAsync());
        Assert.Equal("foundry", transaction.Provider);
        Assert.Equal(
            $"/api/realtime-translation/sessions/{created.SessionId:D}/stream",
            created.RelayPath);
        Assert.Equal("relay-token", created.RelayToken);
        Assert.Null(created.TranscriptId);
        Assert.Empty(context.RealtimeTranslationTranscripts);
        var liveSession = await context.RealtimeTranslationSessions.SingleAsync();
        Assert.Null(liveSession.SourceTranscriptionDeployment);
        Assert.Equal("glosify-realtime-translate", liveSession.BillingModel);
        Assert.Equal(8, liveSession.CreditsPerStartedMinute);
    }

    [Fact]
    public async Task OptIn_CreatesTranscriptAndReconnectReusesIt()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var service = CreateService(context, clock, new FakeRelayTokenStore());

        var first = await service.CreateSessionAsync("user-1", "pl", saveTranscript: true);
        await service.EndSessionAsync("user-1", first.SessionId);
        var reconnect = await service.CreateSessionAsync(
            "user-1",
            "pl",
            saveTranscript: true,
            transcriptId: first.TranscriptId);

        Assert.NotNull(first.TranscriptId);
        Assert.Equal(first.TranscriptId, reconnect.TranscriptId);
        Assert.Single(await context.RealtimeTranslationTranscripts.ToListAsync());
        var sessions = await context.RealtimeTranslationSessions.OrderBy(session => session.CreatedAt).ToListAsync();
        Assert.All(sessions, session => Assert.Equal(first.TranscriptId, session.TranscriptId));
        Assert.All(sessions, session => Assert.NotNull(session.TranscriptConsentAt));
        Assert.All(sessions, session => Assert.Equal(16, session.CreditsPerStartedMinute));
        Assert.All(sessions, session => Assert.Equal("gpt-realtime-whisper", session.SourceTranscriptionDeployment));
        Assert.Equal(16, first.CreditsPerMinute);
    }

    [Fact]
    public async Task Reconnect_RejectsTranscriptAfterQuizLanguageChanges()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(
            context,
            new ManualTimeProvider(DateTimeOffset.UtcNow),
            new FakeRelayTokenStore());
        var first = await service.CreateSessionAsync("user-1", "pl", saveTranscript: true);
        await service.EndSessionAsync("user-1", first.SessionId);
        (await context.Users.SingleAsync()).SelectedQuizLanguageCode = "de";
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<RealtimeTranslationValidationException>(() =>
            service.CreateSessionAsync(
                "user-1",
                "pl",
                saveTranscript: true,
                transcriptId: first.TranscriptId));
    }

    [Fact]
    public async Task OptIn_RequiresPersistedMatchingQuizLanguage()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var user = await context.Users.SingleAsync();
        var service = CreateService(
            context,
            new ManualTimeProvider(DateTimeOffset.UtcNow),
            new FakeRelayTokenStore());

        user.SelectedQuizLanguageCode = null;
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<RealtimeTranslationValidationException>(() =>
            service.CreateSessionAsync("user-1", "pl", saveTranscript: true));

        user.SelectedQuizLanguageCode = "pl";
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<RealtimeTranslationValidationException>(() =>
            service.CreateSessionAsync("user-1", "es", saveTranscript: true));

        Assert.Empty(await context.RealtimeTranslationSessions.ToListAsync());
        Assert.Empty(await context.RealtimeTranslationTranscripts.ToListAsync());
    }

    [Fact]
    public async Task OptIn_ChargesSixteenCreditsPerStartedMinute()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(
            context,
            new ManualTimeProvider(DateTimeOffset.UtcNow),
            new FakeRelayTokenStore());

        var created = await service.CreateSessionAsync("user-1", "pl", saveTranscript: true);
        var begun = await service.BeginMinuteAsync("user-1", created.SessionId, 1);

        Assert.Equal(16, begun.CreditsCharged);
        Assert.Equal(9, begun.AvailableCredits);
        await Assert.ThrowsAsync<InsufficientAiCreditsException>(() =>
            service.ReserveMinuteAsync("user-1", created.SessionId, 2));
    }

    [Fact]
    public async Task Reconnect_RejectsTranscriptOwnedByAnotherUser()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var transcriptId = Guid.NewGuid();
        context.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
        {
            Id = transcriptId,
            UserId = "other-user",
            Title = "Private",
            TargetLanguage = "pl",
            Stream = RealtimeTranslationTranscriptStreams.Source,
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, new ManualTimeProvider(DateTimeOffset.UtcNow), new FakeRelayTokenStore());

        await Assert.ThrowsAsync<RealtimeTranslationNotFoundException>(() => service.CreateSessionAsync(
            "user-1",
            "pl",
            saveTranscript: true,
            transcriptId: transcriptId));
    }

    [Fact]
    public async Task ReserveMinute_IsIdempotentAndStopReleasesIt()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = CreateService(context, clock, new FakeRelayTokenStore());
        var created = await service.CreateSessionAsync("user-1", "es");
        await service.BeginMinuteAsync("user-1", created.SessionId, 1);

        var first = await service.ReserveMinuteAsync("user-1", created.SessionId, 2);
        var duplicate = await service.ReserveMinuteAsync("user-1", created.SessionId, 2);
        await service.EndSessionAsync("user-1", created.SessionId);

        Assert.Equal(first.MinuteIndex, duplicate.MinuteIndex);
        Assert.Equal(9, first.AvailableCredits);
        var account = await context.AiCreditAccounts.SingleAsync();
        Assert.Equal(17, account.AvailableCredits);
        Assert.Equal(RealtimeTranslationMinuteStatuses.Released,
            (await context.RealtimeTranslationMinutes.SingleAsync(minute => minute.MinuteIndex == 2)).Status);
    }

    [Fact]
    public async Task Create_RejectsASecondActiveSession()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(context, new ManualTimeProvider(DateTimeOffset.UtcNow), new FakeRelayTokenStore());
        await service.CreateSessionAsync("user-1", "es");

        await Assert.ThrowsAsync<RealtimeTranslationConflictException>(() =>
            service.CreateSessionAsync("user-1", "es"));
    }

    [Fact]
    public async Task ReserveMinute_RejectsSkippingAhead()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(context, new ManualTimeProvider(DateTimeOffset.UtcNow), new FakeRelayTokenStore());
        var created = await service.CreateSessionAsync("user-1", "es");
        await service.BeginMinuteAsync("user-1", created.SessionId, 1);

        await Assert.ThrowsAsync<RealtimeTranslationConflictException>(() =>
            service.ReserveMinuteAsync("user-1", created.SessionId, 3));
    }

    [Fact]
    public async Task RelayGrantFailure_ReleasesTheFirstMinuteReservation()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(
            context,
            new ManualTimeProvider(DateTimeOffset.UtcNow),
            new FakeRelayTokenStore(fail: true));

        await Assert.ThrowsAsync<RealtimeTranslationUpstreamException>(() =>
            service.CreateSessionAsync("user-1", "es"));

        var account = await context.AiCreditAccounts.SingleAsync();
        Assert.Equal(25, account.AvailableCredits);
        Assert.Equal(RealtimeTranslationSessionStatuses.Failed,
            (await context.RealtimeTranslationSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Cleanup_ReleasesAnUnstartedStaleReservation()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = CreateService(context, clock, new FakeRelayTokenStore());
        await service.CreateSessionAsync("user-1", "es");
        clock.Advance(TimeSpan.FromMinutes(3));

        await service.CleanupStaleSessionsAsync();

        var account = await context.AiCreditAccounts.SingleAsync();
        Assert.Equal(25, account.AvailableCredits);
        Assert.Equal(RealtimeTranslationSessionStatuses.Interrupted,
            (await context.RealtimeTranslationSessions.SingleAsync()).Status);
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static async Task SeedUserAsync(GlosifyContext context)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = "user-1",
            Email = "user@example.test",
            UserName = "user@example.test",
            SelectedQuizLanguageCode = "pl",
        });
        await context.SaveChangesAsync();
    }

    private static RealtimeTranslationService CreateService(
        GlosifyContext context,
        TimeProvider timeProvider,
        IRealtimeTranslationRelayTokenStore relayTokens)
    {
        var generativeOptions = new GenerativeAiOptions
        {
            Foundry = new FoundryGenerativeAiOptions
            {
                AssistantDeployment = "test-model",
                AllowedAssistantDeployments = ["test-model"],
                AssistantModels =
                [
                    new AssistantModelOptions
                    {
                        Deployment = "test-model",
                        DisplayName = "Test",
                        Provider = "Test",
                        SpeedTier = "Test",
                        CostTier = "Test",
                        CreditMultiplier = 1,
                    },
                ],
            },
        };
        var resolver = new GenerativeAiModelResolver(
            Options.Create(generativeOptions),
            Options.Create(new GeminiOptions()));
        var credits = new AiCreditService(
            context,
            Options.Create(new AiUsageOptions
            {
                TrialGrantCredits = 25,
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }),
            resolver,
            timeProvider);
        return new RealtimeTranslationService(
            context,
            credits,
            relayTokens,
            new QuizLanguagePreferenceService(context),
            Options.Create(new RealtimeTranslationOptions
            {
                Enabled = true,
                Model = "gpt-realtime-translate",
                Deployment = "glosify-realtime-translate",
                SavedSourceTranscriptsEnabled = true,
                SourceTranscriptionDeployment = "gpt-realtime-whisper",
                SavedTranscriptBillingModel = "gpt-realtime-translate+gpt-realtime-whisper",
                FoundryEndpoint = "https://glosify-foundry.openai.azure.com/",
                CreditsPerStartedMinute = 8,
                SavedTranscriptCreditsPerStartedMinute = 16,
                MaxSessionMinutes = 30,
                ReservationExpirySeconds = 120,
                StaleSessionSeconds = 60,
                Languages =
                [
                    new RealtimeTranslationLanguageOptions { Code = "es", Name = "Spanish" },
                    new RealtimeTranslationLanguageOptions { Code = "pl", Name = "Polish" },
                ],
            }),
            timeProvider,
            NullLogger<RealtimeTranslationService>.Instance);
    }

    private sealed class FakeRelayTokenStore(bool fail = false) : IRealtimeTranslationRelayTokenStore
    {
        public RealtimeTranslationRelayGrant Create(
            Guid sessionId,
            string userId,
            string targetLanguage,
            bool saveTranscript)
        {
            if (fail)
            {
                throw new RealtimeTranslationUpstreamException("Microsoft Foundry unavailable.");
            }
            return new RealtimeTranslationRelayGrant(
                "relay-token",
                DateTimeOffset.UtcNow.AddMinutes(1));
        }

        public bool TryRedeem(
            Guid sessionId,
            string token,
            out RealtimeTranslationRelayAuthorization authorization)
        {
            authorization = default!;
            return false;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
