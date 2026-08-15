using Azure.AI.Projects;
using Azure.Core;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Mcp;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Glosify.Services.Auth;
using Glosify.Services.Books;
using Glosify.Services.Classrooms;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Flashcards;
using Glosify.Services.Language;
using Glosify.Services.Legal;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Speaking;
using Glosify.Services.Speech;
using Glosify.Services.Storage;
using Glosify.Services.Typing;
using Glosify.Services.Words;
using Glosify.Services.Payments;
using Glosify.Infrastructure.Concurrency;
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

        services.AddOptions<GeminiOptions>()
            .Bind(configuration.GetSection("Gemini"));
        services.AddOptions<GenerativeAiOptions>()
            .Bind(configuration.GetSection(GenerativeAiOptions.SectionName))
            .ValidateOnStart();
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
        // Keep the legacy Gemini credential alias only while the explicit rollback
        // provider is available. All model and timeout settings use standard ASP.NET
        // double-underscore configuration binding.
        services.Configure<GeminiOptions>(options =>
        {
            var apiKey = configuration["GEMINI_API_KEY"];
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                options.ApiKey = apiKey;
            }
        });

        // Register application services
        services.Configure<BlobStorageOptions>(configuration.GetSection("BlobStorage"));
        services.AddScoped<IQuizService, QuizService>();
        services.AddScoped<ICollectionService, CollectionService>();
        services.AddScoped<IQuizRepairService, QuizRepairService>();
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
        // One classroom service per capability, all sharing the role checks in
        // IClassroomAccess and the unchecked reads in ClassroomQueries.
        services.AddScoped<ClassroomQueries>();
        services.AddScoped<IClassroomAccess, ClassroomAccess>();
        services.AddScoped<IClassroomDirectory, ClassroomDirectory>();
        services.AddScoped<IClassroomRoster, ClassroomRoster>();
        services.AddScoped<IClassroomLibrary, ClassroomLibrary>();
        services.AddScoped<IClassroomConversation, ClassroomConversation>();
        services.AddScoped<IClassroomPlanner, ClassroomPlanner>();
        services.AddScoped<IClassroomResults, ClassroomResults>();
        services.AddScoped<IClassroomCall, ClassroomCall>();
        services.AddSingleton<IClassroomCallPresence, Glosify.Hubs.HubClassroomCallPresence>();
        services.AddScoped<IQuizAttemptService, QuizAttemptService>();
        services.AddScoped<ICustomQuizService, CustomQuizService>();
        services.AddSingleton<ICustomQuizTemplateCatalog, CustomQuizTemplateCatalog>();
        services.Configure<Glosify.Services.Communication.AcsOptions>(
            configuration.GetSection(Glosify.Services.Communication.AcsOptions.SectionName));
        services.AddScoped<Glosify.Services.Communication.IAcsTokenService, Glosify.Services.Communication.AcsTokenService>();
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
        services.AddSingleton<IRealtimeTranslationLanguageCatalog, AzureTranslatorLanguageCatalog>();
        services.AddSingleton<IRealtimeTextTranslator, AzureRealtimeTextTranslator>();
        services.AddSingleton<IEconomicalSubtitleTranslator, EconomicalSubtitleTranslator>();
        services.AddSingleton<RealtimeTranslationRelayAuthorizationMonitor>();
        services.AddSingleton<IEnhancedTranslationRelay, FoundryTranslationRelay>();
        services.AddSingleton<IScribeTranslationRelay, ScribeTranslationRelay>();
        services.AddSingleton<IFoundryTranslationRelay, RealtimeTranslationRelayRouter>();
        services.AddScoped<IRealtimeTranslationService, RealtimeTranslationService>();
        services.AddScoped<IRealtimeTranslationTranscriptService, RealtimeTranslationTranscriptService>();
        services.AddHostedService<RealtimeTranslationCleanupService>();
        // The Gemini model factory remains only for the explicit deployment-level
        // rollback path during the Foundry soak.
        services.AddSingleton<IGeminiModelFactory, GeminiModelFactory>();
        services.AddSingleton<IGenerativeAiModelResolver, GenerativeAiModelResolver>();
        services.AddSingleton<IFoundryAgentInvoker, FoundryAgentInvoker>();
        services.AddScoped<FoundryGenerativeAiClient>();
        services.AddScoped<GeminiGenerativeAiClient>();
        services.AddScoped<IGenerativeAiClient>(services =>
        {
            var provider = services.GetRequiredService<IOptions<GenerativeAiOptions>>()
                .Value.Provider.Trim();
            return string.Equals(
                provider,
                GenerativeAiOptions.GeminiProvider,
                StringComparison.OrdinalIgnoreCase)
                ? services.GetRequiredService<GeminiGenerativeAiClient>()
                : services.GetRequiredService<FoundryGenerativeAiClient>();
        });
        services.AddScoped<IVocabularyGenerationService, LlmVocabularyGenerationService>();
        services.AddScoped<IImageTextExtractionService, LlmImageTextExtractionService>();
        services.AddAssistantTools();
        services.AddScoped<IChangeApplier, ChangeApplier>();
        services.AddScoped<IAssistantPendingChangeStore, AssistantPendingChangeStore>();
        services.AddScoped<AssistantContextResolver>();
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
        services.AddAssistantMcp(configuration);

        services.Configure<SpeechOptions>(configuration.GetSection(SpeechOptions.SectionName));
        services.AddOptions<SpeakingOptions>()
            .Bind(configuration.GetSection(SpeakingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SpeakingOptions>, SpeakingOptionsValidator>();
        services.AddSingleton<TokenCredential>(_ =>
            FoundryCredentialFactory.Create(environment, configuration));
        services.AddSingleton<GlosifyBlobServiceClient>();
        services.AddSingleton(services =>
        {
            var endpoint = services.GetRequiredService<IOptions<GenerativeAiOptions>>()
                .Value.Foundry.ProjectEndpoint;
            return new AIProjectClient(
                new Uri(endpoint, UriKind.Absolute),
                services.GetRequiredService<TokenCredential>());
        });
        // Without a resilience handler this client falls back to HttpClient's 100-second
        // default with no retry and no circuit breaker, so a Speech regional brownout would
        // hold a request thread for the full 100 seconds per call.
        services.AddHttpClient(nameof(AzureTextToSpeechService))
            .AddStandardResilienceHandler();
        services.AddScoped<ITextToSpeechService, AzureTextToSpeechService>();
        services.AddScoped<ISpeechAuthorizationTokenService, SpeechAuthorizationTokenService>();
        services.AddSingleton<ISpeakingAgentClient, FoundrySpeakingAgentClient>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISpeakingSessionStore, SpeakingSessionStore>();
        services.AddHostedService<SpeakingSessionCleanupService>();
        services.AddScoped<ISpeakingService, SpeakingService>();
        services.AddScoped<ISpeakingQuizReader, SpeakingQuizReader>();

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
}
