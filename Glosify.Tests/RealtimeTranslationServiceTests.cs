using Glosify.Data;
using Glosify.Infrastructure.Concurrency;
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
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EconomicalSession_UsesFourCreditsAndExistingSpeechForSavedTranscript()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var tokens = new FakeRelayTokenStore();
        var service = CreateService(
            context,
            new ManualTimeProvider(TestNow),
            tokens,
            options =>
            {
                options.EconomicalEnabled = true;
                options.EconomicalCreditsPerStartedMinute = 4;
                options.EconomicalBillingModel = "azure-speech-standard+azure-translator-nmt";
                options.SourceLanguages =
                [
                    new RealtimeTranslationSourceLanguageOptions
                    {
                        Code = "pl",
                        Name = "Polish",
                        Locale = "pl-PL",
                        TranslatorCode = "pl",
                        AutoDetect = true,
                    },
                ];
            });

        var created = await service.CreateSessionAsync(
            "user-1",
            "es",
            saveTranscript: true,
            translationMode: RealtimeTranslationModes.Economical,
            sourceLanguage: "pl");

        Assert.Equal(4, created.CreditsPerMinute);
        var session = await context.RealtimeTranslationSessions.SingleAsync();
        Assert.Equal(RealtimeTranslationModes.Economical, session.TranslationMode);
        Assert.Equal("pl", session.SourceLanguage);
        Assert.Equal("azure-speech-standard+azure-translator-nmt", session.BillingModel);
        Assert.Null(session.SourceTranscriptionDeployment);
        Assert.Equal(RealtimeTranslationModes.Economical, tokens.LastTranslationMode);
        Assert.Equal("pl", tokens.LastRequestedSourceLanguage);
    }

    [Fact]
    public async Task EconomicalSavedTranscript_RejectsAutoDetection()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(
            context,
            new ManualTimeProvider(TestNow),
            new FakeRelayTokenStore(),
            options =>
            {
                options.EconomicalEnabled = true;
                options.SourceLanguages =
                [
                    new RealtimeTranslationSourceLanguageOptions
                    {
                        Code = "pl",
                        Name = "Polish",
                        Locale = "pl-PL",
                        TranslatorCode = "pl",
                        AutoDetect = true,
                    },
                ];
            });

        await Assert.ThrowsAsync<RealtimeTranslationValidationException>(() =>
            service.CreateSessionAsync(
                "user-1",
                "es",
                saveTranscript: true,
                translationMode: RealtimeTranslationModes.Economical,
                sourceLanguage: "auto"));
    }

    [Fact]
    public async Task Catalog_ReturnsAllQuizLanguagesAndThePersistedSelection()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(
            context,
            new ManualTimeProvider(TestNow),
            new FakeRelayTokenStore());

        var catalog = await service.GetCatalogAsync("user-1");

        Assert.Equal(
            [("et", "Estonian"), ("de", "German"), ("pl", "Polish"), ("uk", "Ukrainian")],
            catalog.QuizLanguages.Select(language => (language.Code, language.Name)).ToArray());
        Assert.Equal("pl", catalog.SelectedQuizLanguage?.Code);
        Assert.Equal("Polish", catalog.SelectedQuizLanguage?.Name);
    }

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
        Assert.Equal(RealtimeTranslationModes.Enhanced, liveSession.TranslationMode);
        Assert.Null(liveSession.SourceLanguage);
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
            new ManualTimeProvider(TestNow),
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
    public async Task OptIn_RequiresAQuizLanguageButAllowsAnySubtitleTarget()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var user = await context.Users.SingleAsync();
        var relayTokens = new FakeRelayTokenStore();
        var service = CreateService(
            context,
            new ManualTimeProvider(TestNow),
            relayTokens);

        user.SelectedQuizLanguageCode = null;
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<RealtimeTranslationValidationException>(() =>
            service.CreateSessionAsync("user-1", "pl", saveTranscript: true));

        user.SelectedQuizLanguageCode = "pl";
        await context.SaveChangesAsync();
        var created = await service.CreateSessionAsync("user-1", "es", saveTranscript: true);

        Assert.NotNull(created.TranscriptId);
        Assert.Equal("es", (await context.RealtimeTranslationSessions.SingleAsync()).TargetLanguage);
        var transcript = await context.RealtimeTranslationTranscripts.SingleAsync();
        Assert.Equal("pl", transcript.TargetLanguage);
        Assert.StartsWith("Polish source transcript", transcript.Title);
        Assert.Equal("pl", relayTokens.LastSourceLanguage);
    }

    [Fact]
    public async Task OptIn_ChargesSixteenCreditsPerStartedMinute()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(
            context,
            new ManualTimeProvider(TestNow),
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
        var service = CreateService(context, new ManualTimeProvider(TestNow), new FakeRelayTokenStore());

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
        var clock = new ManualTimeProvider(TestNow);
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
        var service = CreateService(context, new ManualTimeProvider(TestNow), new FakeRelayTokenStore());
        await service.CreateSessionAsync("user-1", "es");

        await Assert.ThrowsAsync<RealtimeTranslationConflictException>(() =>
            service.CreateSessionAsync("user-1", "es"));
    }

    [Fact]
    public async Task ReserveMinute_RejectsSkippingAhead()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(context, new ManualTimeProvider(TestNow), new FakeRelayTokenStore());
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
            new ManualTimeProvider(TestNow),
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
        var clock = new ManualTimeProvider(TestNow);
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
        return new FactoryBackedGlosifyContext(options);
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
        IRealtimeTranslationRelayTokenStore relayTokens,
        Action<RealtimeTranslationOptions>? configure = null)
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
            new TestDbContextFactory(context),
            Options.Create(new AiUsageOptions
            {
                TrialGrantCredits = 25,
                MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
            }),
            resolver,
            new AlwaysEligibleTrialService(),
            timeProvider);
        var realtimeOptions = new RealtimeTranslationOptions
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
        };
        configure?.Invoke(realtimeOptions);
        return new RealtimeTranslationService(
            context,
            credits,
            relayTokens,
            new QuizLanguagePreferenceService(context),
            Options.Create(realtimeOptions),
            timeProvider,
            NullLogger<RealtimeTranslationService>.Instance,
            new ReferenceCountedKeyedAsyncLock());
    }

    private sealed class AlwaysEligibleTrialService : Glosify.Services.Auth.ITrialEligibilityService
    {
        public Task<bool> IsEligibleAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeRelayTokenStore(bool fail = false) : IRealtimeTranslationRelayTokenStore
    {
        public string? LastSourceLanguage { get; private set; }
        public string? LastTranslationMode { get; private set; }
        public string? LastRequestedSourceLanguage { get; private set; }

        public RealtimeTranslationRelayGrant Create(
            Guid sessionId,
            string userId,
            string targetLanguage,
            string translationMode,
            string? sourceLanguage,
            bool saveTranscript,
            string? transcriptSourceLanguage)
        {
            LastSourceLanguage = transcriptSourceLanguage;
            LastTranslationMode = translationMode;
            LastRequestedSourceLanguage = sourceLanguage;
            if (fail)
            {
                throw new RealtimeTranslationUpstreamException("Microsoft Foundry unavailable.");
            }
            return new RealtimeTranslationRelayGrant(
                "relay-token",
                TestNow.AddMinutes(1));
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
