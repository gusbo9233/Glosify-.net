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
    public async Task EconomicalMode_IsNoLongerAccepted()
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

        await Assert.ThrowsAsync<RealtimeTranslationValidationException>(() =>
            service.CreateSessionAsync(
                "user-1",
                "es",
                translationMode: RealtimeTranslationModes.Economical));
        Assert.Empty(context.RealtimeTranslationSessions);
    }

    [Fact]
    public async Task ElevenLabsSession_UsesProviderPriceBillingAndRelayAuthorization()
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
                options.ElevenLabs.Enabled = true;
                options.ElevenLabs.CreditsPerStartedMinute = 7;
                options.SourceLanguages =
                [
                    new RealtimeTranslationSourceLanguageOptions
                    {
                        Code = "pl",
                        Name = "Polish",
                        Locale = "pl-PL",
                        TranslatorCode = "pl",
                        ScribeCode = "pl",
                        AutoDetect = true,
                    },
                ];
            });

        var catalog = await service.GetCatalogAsync("user-1");
        Assert.Equal(
            [(RealtimeTranslationModes.Scribe, 7), (RealtimeTranslationModes.Enhanced, 8)],
            catalog.Modes.Select(mode => (mode.Code, mode.CreditsPerMinute)).ToArray());

        var created = await service.CreateSessionAsync(
            "user-1",
            "es",
            translationMode: RealtimeTranslationModes.Scribe);
        var begun = await service.BeginMinuteAsync("user-1", created.SessionId, 1);

        Assert.Equal(7, created.CreditsPerMinute);
        Assert.Equal(7, begun.CreditsCharged);
        var session = await context.RealtimeTranslationSessions.SingleAsync();
        Assert.Equal(RealtimeTranslationModes.Scribe, session.TranslationMode);
        Assert.Equal(RealtimeSpeechProviders.ElevenLabs, session.SpeechProvider);
        Assert.Equal("auto", session.SourceLanguage);
        Assert.Equal("scribe_v2_realtime", session.Model);
        Assert.Equal("elevenlabs-scribe-v2-realtime+azure-translator-nmt", session.BillingModel);
        Assert.Equal(RealtimeSpeechProviders.ElevenLabs, tokens.LastSpeechProvider);
        Assert.Equal("auto", tokens.LastRequestedSourceLanguage);
        var transaction = await context.AiCreditTransactions.SingleAsync(transaction =>
            transaction.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.Equal(RealtimeTranslationConstants.ElevenLabsProvider, transaction.Provider);
    }

    [Fact]
    public async Task UnifiedPricing_OverridesCatalogSessionAndPersistedMinuteRate()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(
            context,
            new ManualTimeProvider(TestNow),
            new FakeRelayTokenStore(),
            pricingOptions: new CreditPricingOptions
            {
                Subtitles = new SubtitleCreditPricingOptions
                {
                    EnhancedCreditsPerStartedMinute = 7,
                    ScribeCreditsPerStartedMinute = 4,
                    EnhancedWithTranscriptCreditsPerStartedMinute = 12,
                },
            });

        var catalog = await service.GetCatalogAsync("user-1");
        Assert.Equal(4, catalog.Modes.Single(mode => mode.Code == RealtimeTranslationModes.Scribe).CreditsPerMinute);
        Assert.Equal(7, catalog.Modes.Single(mode => mode.Code == RealtimeTranslationModes.Enhanced).CreditsPerMinute);
        Assert.Equal(12, catalog.SavedTranscriptCreditsPerMinute);

        var created = await service.CreateSessionAsync(
            "user-1",
            "es",
            translationMode: RealtimeTranslationModes.Scribe);
        Assert.Equal(4, created.CreditsPerMinute);
        Assert.Equal(4, (await context.RealtimeTranslationSessions.SingleAsync()).CreditsPerStartedMinute);
    }

    [Fact]
    public async Task EnhancedSavedTranscript_UsesScribeWithOptionalLanguageHint()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var tokens = new FakeRelayTokenStore();
        var service = CreateService(context, new ManualTimeProvider(TestNow), tokens);

        var created = await service.CreateSessionAsync(
            "user-1",
            "es",
            saveTranscript: true,
            translationMode: RealtimeTranslationModes.Enhanced,
            sourceLanguage: "pl");

        Assert.Equal(16, created.CreditsPerMinute);
        var session = await context.RealtimeTranslationSessions.SingleAsync();
        Assert.Equal(RealtimeSpeechProviders.Foundry, session.SpeechProvider);
        Assert.Equal("pl", session.SourceLanguage);
        Assert.Equal("scribe_v2_realtime", session.SourceTranscriptionDeployment);
        Assert.Equal("gpt-realtime-translate+elevenlabs-scribe-v2-realtime", session.BillingModel);
        Assert.Equal("pl", tokens.LastRequestedSourceLanguage);
    }

    [Fact]
    public async Task ElevenLabsSession_RejectsUnknownOrDisabledMode()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var service = CreateService(
            context,
            new ManualTimeProvider(TestNow),
            new FakeRelayTokenStore(),
            options =>
            {
                options.ElevenLabs.Enabled = false;
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
                translationMode: "unknown",
                sourceLanguage: "pl"));
        await Assert.ThrowsAsync<RealtimeTranslationUnavailableException>(() =>
            service.CreateSessionAsync(
                "user-1",
                "es",
                translationMode: RealtimeTranslationModes.Scribe,
                sourceLanguage: "pl"));
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

        Assert.Equal(69, catalog.QuizLanguages.Count);
        Assert.Equal(
            QuizLanguageCatalog.LanguageLearning.Select(language => (language.Code, language.Name)),
            catalog.QuizLanguages.Select(language => (language.Code, language.Name)));
        Assert.Equal("pl", catalog.SelectedQuizLanguage?.Code);
        Assert.Equal("Polish", catalog.SelectedQuizLanguage?.Name);
        Assert.Equal("auto", catalog.SourceLanguages[0].Code);
        Assert.Contains(catalog.SourceLanguages, language => language.Code == "es");
    }

    [Fact]
    public async Task Freestyle_AllowsLiveSubtitlesWithoutChangingTheSelectedMode()
    {
        await using var context = CreateContext();
        await SeedUserAsync(context);
        var user = await context.Users.SingleAsync();
        user.SelectedQuizLanguageCode = QuizLanguageCatalog.FreestyleCode;
        await context.SaveChangesAsync();
        var service = CreateService(
            context,
            new ManualTimeProvider(TestNow),
            new FakeRelayTokenStore());

        var catalog = await service.GetCatalogAsync("user-1");
        var created = await service.CreateSessionAsync("user-1", "es");
        var begun = await service.BeginMinuteAsync("user-1", created.SessionId, 1);
        var session = await context.RealtimeTranslationSessions.SingleAsync();

        Assert.Null(catalog.SelectedQuizLanguage);
        Assert.Contains(catalog.Languages, language => language.Code == "es");
        Assert.Equal("es", session.TargetLanguage);
        Assert.Null(created.TranscriptId);
        Assert.Equal(1, begun.ChargedMinutes);
        Assert.Equal(QuizLanguageCatalog.FreestyleCode, user.SelectedQuizLanguageCode);
        Assert.Empty(context.RealtimeTranslationTranscripts);
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
        Assert.Equal(RealtimeSpeechProviders.Foundry, liveSession.SpeechProvider);
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
        Assert.All(sessions, session => Assert.Equal("auto", session.SourceLanguage));
        Assert.All(sessions, session => Assert.Equal("scribe_v2_realtime", session.SourceTranscriptionDeployment));
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
        Action<RealtimeTranslationOptions>? configure = null,
        CreditPricingOptions? pricingOptions = null)
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
        var usageOptions = new AiUsageOptions
        {
            TrialGrantCredits = 25,
            MonthlyBudget = new AiMonthlyBudgetOptions { Enabled = false },
        };
        var creditPricing = new CreditPricingResolver(
            Options.Create(new CreditPricingOptions()),
            Options.Create(usageOptions),
            Options.Create(generativeOptions),
            Options.Create(new RealtimeTranslationOptions()));
        var credits = new AiCreditService(
            context,
            new TestDbContextFactory(context),
            Options.Create(usageOptions),
            creditPricing,
            new AlwaysEligibleTrialService(),
            timeProvider);
        var realtimeOptions = new RealtimeTranslationOptions
        {
            Enabled = true,
            Model = "gpt-realtime-translate",
            Deployment = "glosify-realtime-translate",
            SavedSourceTranscriptsEnabled = true,
            SavedTranscriptBillingModel = "gpt-realtime-translate+elevenlabs-scribe-v2-realtime",
            FoundryEndpoint = "https://glosify-foundry.openai.azure.com/",
            CreditsPerStartedMinute = 8,
            SavedTranscriptCreditsPerStartedMinute = 16,
            ElevenLabs = new ElevenLabsRealtimeSpeechOptions
            {
                Enabled = true,
                ApiKey = "test-key",
                CreditsPerStartedMinute = 6,
            },
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
        var realtimePricing = new CreditPricingResolver(
            Options.Create(pricingOptions ?? new CreditPricingOptions()),
            Options.Create(usageOptions),
            Options.Create(generativeOptions),
            Options.Create(realtimeOptions));
        return new RealtimeTranslationService(
            context,
            credits,
            relayTokens,
            new QuizLanguagePreferenceService(context),
            new StaticRealtimeTranslationLanguageCatalog(realtimeOptions),
            Options.Create(realtimeOptions),
            realtimePricing,
            timeProvider,
            NullLogger<RealtimeTranslationService>.Instance,
            new ReferenceCountedKeyedAsyncLock());
    }

    private sealed class StaticRealtimeTranslationLanguageCatalog(RealtimeTranslationOptions options)
        : IRealtimeTranslationLanguageCatalog
    {
        public Task<IReadOnlyList<RealtimeTranslationLanguage>> GetLanguagesAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RealtimeTranslationLanguage>>(
                options.Languages.Where(language => language.Enabled)
                    .Select(language => new RealtimeTranslationLanguage(language.Code, language.Name))
                    .ToArray());
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
        public string? LastSpeechProvider { get; private set; }
        public string? LastRequestedSourceLanguage { get; private set; }

        public RealtimeTranslationRelayGrant Create(
            Guid sessionId,
            string userId,
            string targetLanguage,
            string translationMode,
            string speechProvider,
            string? sourceLanguage,
            bool saveTranscript,
            string? transcriptSourceLanguage)
        {
            LastSourceLanguage = transcriptSourceLanguage;
            LastTranslationMode = translationMode;
            LastSpeechProvider = speechProvider;
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
