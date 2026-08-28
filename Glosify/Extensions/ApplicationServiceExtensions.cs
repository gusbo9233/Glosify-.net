using Azure.Core;
using Glosify.Infrastructure.Concurrency;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Anki;
using Glosify.Services.Auth;
using Glosify.Services.Books;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Flashcards;
using Glosify.Services.Language;
using Glosify.Services.Legal;
using Glosify.Services.Payments;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Speech;
using Glosify.Services.Storage;
using Glosify.Services.Typing;
using Glosify.Services.Words;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Glosify.Extensions;

public static class ApplicationServiceExtensions
{
    /// <summary>
    /// Options binding and the application service graph.
    /// </summary>
    public static IServiceCollection AddGlosifyServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.AddHttpContextAccessor();
        var legalOptions = services.AddOptions<LegalOptions>()
            .Bind(configuration.GetSection(LegalOptions.SectionName));
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            legalOptions
                .Validate(
                    options => !string.IsNullOrWhiteSpace(options.ControllerName),
                    "Legal:ControllerName is required outside development.")
                .Validate(
                    options => !string.IsNullOrWhiteSpace(options.ContactEmail)
                        && new System.ComponentModel.DataAnnotations.EmailAddressAttribute()
                            .IsValid(options.ContactEmail),
                    "Legal:ContactEmail must be a valid public email outside development.")
                .ValidateOnStart();
        }
        services.AddScoped<ILanguageContext, CookieLanguageContext>();
        services.AddScoped<IQuizLanguagePreferenceService, QuizLanguagePreferenceService>();

        services.AddOptions<GenerativeAiOptions>()
            .Bind(configuration.GetSection(GenerativeAiOptions.SectionName))
            .ValidateOnStart();
        services.Configure<GenerativeAiOptions>(options =>
        {
            // Always overwrite the bound property so GenerativeAi:ApiKey and old
            // provider settings can never become credential fallbacks.
            options.ApiKey = ResolveOpenAiApiKey(configuration);
        });
        services.AddSingleton<IValidateOptions<GenerativeAiOptions>, GenerativeAiOptionsValidator>();
        services.AddOptions<AiUsageOptions>()
            .Bind(configuration.GetSection("AiUsage"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiUsageOptions>, AiUsageOptionsValidator>();
        services.AddOptions<CreditPricingOptions>()
            .Bind(configuration.GetSection(CreditPricingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CreditPricingOptions>, CreditPricingOptionsValidator>();
        services.AddSingleton<ICreditPricingResolver, CreditPricingResolver>();
        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StripeOptions>, StripeOptionsValidator>();
        services.AddOptions<ExtensionAuthOptions>()
            .Bind(configuration.GetSection(ExtensionAuthOptions.SectionName));
        services.AddOptions<DemoAccountOptions>()
            .Bind(configuration.GetSection(DemoAccountOptions.SectionName));
        services.AddScoped<DemoAccountSeeder>();
        services.AddOptions<RealtimeTranslationOptions>()
            .Bind(configuration.GetSection(RealtimeTranslationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RealtimeTranslationOptions>, RealtimeTranslationOptionsValidator>();
        services.Configure<RealtimeTranslationOptions>(options =>
        {
            var elevenLabsApiKey = ResolveElevenLabsApiKey(configuration);
            if (!string.IsNullOrWhiteSpace(elevenLabsApiKey))
            {
                options.ElevenLabs.ApiKey = elevenLabsApiKey;
            }
        });
        // Register application services
        services.Configure<BlobStorageOptions>(configuration.GetSection("BlobStorage"));
        services.AddScoped<IQuizService, QuizService>();
        services.AddScoped<ICollectionService, CollectionService>();
        services.AddSingleton<IAnkiScheduler, Fsrs6AnkiScheduler>();
        services.AddScoped<IAnkiCollectionService, AnkiCollectionService>();
        services.AddScoped<IAnkiStudyService, AnkiStudyService>();
        services.AddScoped<IAnkiStatisticsService, AnkiStatisticsService>();
        services.AddScoped<IQuizJsonImportService, QuizJsonImportService>();
        services.AddScoped<IWordService, WordService>();
        services.AddSingleton<IQuizSessionRegistry, QuizSessionRegistry>();
        services.AddScoped<IFlashcardSessionService, FlashcardSessionService>();
        services.AddScoped<ITypingQuizService, TypingQuizService>();
        services.AddScoped<ITypingSessionService, TypingSessionService>();
        services.AddScoped<IBookFileStorage, AzureBlobBookFileStorage>();
        services.AddScoped<IPdfTextExtractionService, PdfPigTextExtractionService>();
        services.AddScoped<IBookDocumentService, BookDocumentService>();
        services.AddSingleton<IBookPageTranslationCoordinator, BookPageTranslationCoordinator>();
        services.AddScoped<IBookPageTranslationService, BookPageTranslationService>();
        services.AddScoped<IQuizAttemptService, QuizAttemptService>();
        services.AddScoped<ICustomQuizService, CustomQuizService>();
        services.AddSingleton<ICustomQuizTemplateCatalog, CustomQuizTemplateCatalog>();
        services.AddScoped<IAiCreditService, AiCreditService>();
        services.AddScoped<IStripePaymentService, StripePaymentService>();
        services.AddScoped<IPaidServiceGate, PaidServiceGate>();
        services.AddScoped<ITrialEligibilityService, TrialEligibilityService>();
        services.AddScoped<IExternalAccountUserStore, IdentityExternalAccountUserStore>();
        services.AddScoped<IExternalAccountService, ExternalAccountService>();
        services.AddSingleton<IExtensionAuthorizationCodeStore, ExtensionAuthorizationCodeStore>();
        services.AddSingleton<IMobileAuthorizationCodeStore, MobileAuthorizationCodeStore>();
        services.AddSingleton<IRealtimeTranslationRelayTokenStore, RealtimeTranslationRelayTokenStore>();
        services.AddSingleton<IKeyedAsyncLock, ReferenceCountedKeyedAsyncLock>();
        services.AddSingleton<AzureRealtimeSpeechTranscriber>();
        services.AddSingleton<IElevenLabsRealtimeWebSocketFactory, ElevenLabsRealtimeWebSocketFactory>();
        services.AddSingleton<ElevenLabsRealtimeSpeechTranscriber>();
        services.AddSingleton<IRealtimeSpeechTranscriber, RealtimeSpeechTranscriberRouter>();
        var translatorTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>(
                $"{RealtimeTranslationOptions.SectionName}:TranslatorTimeoutSeconds") ?? 5,
            1,
            30));
        services.AddHttpClient(AzureRealtimeTextTranslator.HttpClientName)
        .AddStandardResilienceHandler(options =>
        {
            // The standard handler owns timeouts and deliberately sets
            // HttpClient.Timeout to infinite.
            options.TotalRequestTimeout.Timeout = translatorTimeout;
            options.AttemptTimeout.Timeout = translatorTimeout;
            // Translation is billed per POST. Retrying an ambiguous failure can
            // submit and charge the same phrase more than once.
            options.Retry.DisableForUnsafeHttpMethods();
        });
        services.AddHttpClient(AzureTranslatorLanguageCatalog.HttpClientName, client =>
            client.BaseAddress = new Uri("https://api.cognitive.microsofttranslator.com/"))
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = translatorTimeout;
            options.AttemptTimeout.Timeout = translatorTimeout;
        });
        var cloudflareTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>(
                $"{RealtimeTranslationOptions.SectionName}:Cloudflare:TimeoutSeconds") ?? 20,
            1,
            60));
        services.AddHttpClient(CloudflareSubtitleTranslator.HttpClientName)
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = cloudflareTimeout;
            options.AttemptTimeout.Timeout = cloudflareTimeout;
            options.CircuitBreaker.SamplingDuration = cloudflareTimeout * 2;
            // Worker AI requests consume quota. Never resubmit an ambiguous POST.
            options.Retry.DisableForUnsafeHttpMethods();
        });
        services.AddSingleton<IRealtimeTranslationLanguageCatalog, AzureTranslatorLanguageCatalog>();
        services.AddSingleton<IRealtimeTextTranslator, AzureRealtimeTextTranslator>();
        services.AddSingleton<IEconomicalSubtitleTranslator, EconomicalSubtitleTranslator>();
        services.AddSingleton<ICloudflareSubtitleTranslator, CloudflareSubtitleTranslator>();
        services.AddSingleton<RealtimeTranslationRelayAuthorizationMonitor>();
        services.AddSingleton<IEnhancedTranslationRelay, OpenAiTranslationRelay>();
        services.AddSingleton<IScribeTranslationRelay, ScribeTranslationRelay>();
        services.AddSingleton<IRealtimeTranslationRelay, RealtimeTranslationRelayRouter>();
        services.AddScoped<IRealtimeTranslationService, RealtimeTranslationService>();
        services.AddScoped<IRealtimeTranslationTranscriptService, RealtimeTranslationTranscriptService>();
        services.AddScoped<IRealtimeTranslationCaptureService, RealtimeTranslationCaptureService>();
        services.AddHostedService<RealtimeTranslationCleanupService>();
        services.AddSingleton<IOpenAiResponsesTransport, OpenAiResponsesTransport>();
        services.AddScoped<OpenAiGenerativeAiClient>();
        services.AddScoped<IGenerativeAiClient>(services =>
            services.GetRequiredService<OpenAiGenerativeAiClient>());
        services.AddScoped<IQuizJsonImportRepairService, QuizJsonImportRepairService>();
        services.AddScoped<IImageTextExtractionService, LlmImageTextExtractionService>();
        services.AddAssistantTools();
        services.AddScoped<IChangeApplier, ChangeApplier>();
        services.AddScoped<AssistantContextResolver>();
        services.AddScoped<AssistantContextOptionsProvider>();
        services.AddScoped<AssistantMessagePresenter>();
        services.AddScoped<AssistantPromptBuilder>();
        services.AddSingleton<AssistantIntentResolver>();
        services.AddScoped<AssistantTelemetryDeletionQueue>();
        services.AddScoped<AssistantThreadStore>();
        services.AddSingleton<AssistantAnalyticsBackgroundWriter>();
        services.AddSingleton<IAssistantAnalyticsBatchWriter>(services =>
            services.GetRequiredService<AssistantAnalyticsBackgroundWriter>());
        services.AddHostedService<AssistantAnalyticsBackgroundWriter>(services =>
            services.GetRequiredService<AssistantAnalyticsBackgroundWriter>());
        services.AddScoped<AssistantAnalyticsStore>();
        services.AddScoped<AssistantFeedbackService>();
        services.AddOptions<AssistantAnalyticsOptions>()
            .Bind(configuration.GetSection(AssistantAnalyticsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AssistantAnalyticsOptions>, AssistantAnalyticsOptionsValidator>();
        services.AddHttpClient(
                AssistantTelemetryDeletionService.HttpClientName,
                client => client.BaseAddress = new Uri("https://management.azure.com"))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.DisableForUnsafeHttpMethods();
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            });
        services.AddHostedService<AssistantTelemetryDeletionService>();
        services.AddScoped<IAssistantTurnLeaseService, AssistantTurnLeaseService>();
        services.AddScoped<AssistantTurnRunner>();
        services.AddScoped<AssistantChangeWorkflow>();
        services.AddScoped<IAssistantOrchestrator, AssistantOrchestrator>();

        services.Configure<SpeechOptions>(configuration.GetSection(SpeechOptions.SectionName));
        services.AddSingleton<TokenCredential>(_ =>
            AzureCredentialFactory.Create(environment, configuration));
        services.AddSingleton<GlosifyBlobServiceClient>();
        // Without a resilience handler this client falls back to HttpClient's 100-second
        // default with no retry and no circuit breaker, so a Speech regional brownout would
        // hold a request thread for the full 100 seconds per call.
        services.AddHttpClient(nameof(AzureTextToSpeechService))
            .AddStandardResilienceHandler();
        services.AddScoped<ITextToSpeechService, AzureTextToSpeechService>();
        services.AddSingleton(TimeProvider.System);

        return services;
    }

    internal static string? ResolveElevenLabsApiKey(IConfiguration configuration)
    {
        var canonical = configuration[$"{RealtimeTranslationOptions.SectionName}:ElevenLabs:ApiKey"];
        if (!string.IsNullOrWhiteSpace(canonical))
        {
            return canonical;
        }

        var legacy = configuration["Elevenlabs_key"];
        return !string.IsNullOrWhiteSpace(legacy)
            ? legacy
            : configuration["ELEVENLABS_API_KEY"];
    }

    internal static string ResolveOpenAiApiKey(IConfiguration configuration)
    {
        var apiKey = configuration["OPENAI_SECRET_KEY"];
        return string.IsNullOrWhiteSpace(apiKey)
            ? string.Empty
            : apiKey.Trim();
    }
}
