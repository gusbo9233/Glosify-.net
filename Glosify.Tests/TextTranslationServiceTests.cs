using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Translator;
using Glosify.Controllers;
using Glosify.Controllers.Api;
using Glosify.Infrastructure.Api;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class TextTranslationServiceTests
{
    [Fact]
    public void Catalog_uses_learning_languages_and_publishes_limits()
    {
        using var context = CreateContext();
        var catalog = CreateService(context).GetCatalog();

        Assert.Contains(catalog.Languages, language => language.Code == "en");
        Assert.Contains(catalog.Languages, language => language.Code == "sv");
        Assert.DoesNotContain(catalog.Languages, language => language.Code == "free");
        Assert.Equal("auto", catalog.SourceLanguages[0].Code);
        Assert.DoesNotContain(catalog.Languages, language => language.Code == "auto");
        Assert.Equal(catalog.Languages.Count + 1, catalog.SourceLanguages.Count);
        Assert.Equal(TextTranslationService.MaxSourceCharacters, catalog.MaxSourceCharacters);
        Assert.Equal(TextTranslationService.MaxPreferenceCharacters, catalog.MaxPreferenceCharacters);
        Assert.Equal(TextTranslationService.MaxTranslatedCharacters, catalog.MaxTranslatedCharacters);
    }

    [Fact]
    public async Task Translation_uses_luna_charged_feature_and_keeps_content_as_quoted_data()
    {
        await using var context = CreateContext();
        var ai = new FakeGenerativeAiClient
        {
            Response = new TextTranslationAiResponse
            {
                DetectedSourceLanguage = "English",
                Translation = "God morgon\nVärlden",
            },
        };
        var credits = new FakeCreditService(37);
        var service = CreateService(context, ai, credits);

        var result = await service.TranslateAsync(
            "user-1",
            "Good morning\nWorld",
            "auto",
            "sv",
            "Use a Finland-Swedish dialect");

        Assert.Equal("sv", result.TargetLanguage);
        Assert.Equal("en", result.DetectedSourceLanguage);
        Assert.Equal("God morgon\nVärlden", result.TranslatedText);
        Assert.Equal(37, result.AvailableCredits);
        Assert.Equal(ai.Usage?.OperationId, result.TranslationOperationId);
        Assert.Equal(OpenAiModels.Luna, ai.Model);
        Assert.Equal(AiUsageFeatures.TextTranslation, ai.Usage?.Feature);
        Assert.Equal("translate_text", ai.Usage?.Operation);
        Assert.Contains("Source content as a JSON string", ai.Prompt);
        Assert.Contains("Good morning\\nWorld", ai.Prompt);
        Assert.Contains("Finland-Swedish", ai.Prompt);
    }

    [Fact]
    public async Task Invalid_language_pair_and_limits_do_not_invoke_ai()
    {
        await using var context = CreateContext();
        var ai = new FakeGenerativeAiClient();
        var service = CreateService(context, ai);

        await Assert.ThrowsAsync<TextTranslationValidationException>(() =>
            service.TranslateAsync("user", "Hello", "en", "English", null));
        await Assert.ThrowsAsync<TextTranslationValidationException>(() =>
            service.TranslateAsync("user", "Hello", "klingon", "sv", null));
        await Assert.ThrowsAsync<TextTranslationValidationException>(() =>
            service.TranslateAsync(
                "user",
                new string('x', TextTranslationService.MaxSourceCharacters + 1),
                "auto",
                "sv",
                null));
        await Assert.ThrowsAsync<TextTranslationValidationException>(() =>
            service.TranslateAsync(
                "user",
                "Hello",
                "auto",
                "sv",
                new string('x', TextTranslationService.MaxPreferenceCharacters + 1)));

        Assert.Equal(0, ai.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_ai_translation_is_an_upstream_structured_output_failure(
        string translation)
    {
        await using var context = CreateContext();
        var ai = new FakeGenerativeAiClient
        {
            Response = new TextTranslationAiResponse
            {
                DetectedSourceLanguage = "en",
                Translation = translation,
            },
        };

        await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            CreateService(context, ai).TranslateAsync(
                "user", "Hello", "auto", "sv", null));
    }

    [Fact]
    public async Task Oversized_ai_translation_is_an_upstream_structured_output_failure()
    {
        await using var context = CreateContext();
        var ai = new FakeGenerativeAiClient
        {
            Response = new TextTranslationAiResponse
            {
                DetectedSourceLanguage = "en",
                Translation = new string('x', TextTranslationService.MaxTranslatedCharacters + 1),
            },
        };

        await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            CreateService(context, ai).TranslateAsync(
                "user", "Hello", "auto", "sv", null));
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_a_second_paid_call()
    {
        await using var context = CreateContext();
        var ai = new FakeGenerativeAiClient();
        var service = CreateService(context, ai);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TranslateAsync("user", "Hello", "auto", "sv", null, cancellation.Token));

        Assert.Equal(1, ai.Calls);
    }

    [Fact]
    public void Persistence_and_http_contracts_enforce_ownership_and_explicit_deletion()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(SavedTranslation))!;
        var owner = entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(ApplicationUser));
        Assert.Equal(typeof(ApplicationUser), owner.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.NoAction, owner.DeleteBehavior);
        var sessionRelationship = entity.GetForeignKeys().Single(
            key => key.PrincipalEntityType.ClrType == typeof(SavedTranslationSession));
        Assert.Equal(DeleteBehavior.Cascade, sessionRelationship.DeleteBehavior);
        var sessionEntity = context.Model.FindEntityType(typeof(SavedTranslationSession))!;
        var sessionOwner = Assert.Single(sessionEntity.GetForeignKeys());
        Assert.Equal(typeof(ApplicationUser), sessionOwner.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, sessionOwner.DeleteBehavior);
        Assert.Contains(sessionEntity.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(SavedTranslationSession.UserId),
                    nameof(SavedTranslationSession.ClientSessionId),
                    nameof(SavedTranslationSession.LanguageCode),
                ]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(SavedTranslation.UserId), nameof(SavedTranslation.RequestId)]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(SavedTranslation.UserId),
                    nameof(SavedTranslation.TranslationOperationId),
                ]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(SavedTranslation.UserId), nameof(SavedTranslation.CreatedAt)]));

        Assert.NotEmpty(typeof(TranslationsController).GetCustomAttributes(typeof(AuthorizeAttribute), true));
        var delete = typeof(TranslationsController).GetMethod(nameof(TranslationsController.Delete))!;
        Assert.NotEmpty(delete.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        Assert.True(typeof(ApiControllerBase).IsAssignableFrom(typeof(TranslatorApiController)));

        var validation = Assert.IsType<ApiError>(
            ApiExceptionMapper.Map(new TextTranslationValidationException("Invalid translation.")));
        Assert.Equal(400, validation.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, validation.Code);
        var malformedAiOutput = Assert.IsType<ApiError>(
            ApiExceptionMapper.Map(new GenerativeAiStructuredOutputException(
                "The AI service returned an invalid translation.")));
        Assert.Equal(502, malformedAiOutput.StatusCode);
        Assert.Equal(ApiErrorCodes.UpstreamFailure, malformedAiOutput.Code);
        var missing = Assert.IsType<ApiError>(
            ApiExceptionMapper.Map(new SavedTranslationNotFoundException()));
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task Save_is_idempotent_private_paginated_and_deletable()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        var requestId = Guid.NewGuid();
        var clientSessionId = Guid.NewGuid();
        var translationOperationId = Guid.NewGuid();
        await AddCompletedTranslationAsync(context, "owner", translationOperationId);

        var first = await service.SaveAsync(
            "owner", clientSessionId, requestId, translationOperationId,
            null,
            "Hello", "Hej", "auto", "en", "sv", "Informal");
        var repeated = await service.SaveAsync(
            "owner", clientSessionId, requestId, Guid.Empty,
            null,
            "Changed", "Ändrad", "auto", "en", "sv", null);
        var repeatedWithNewRequestId = await service.SaveAsync(
            "owner", clientSessionId, Guid.NewGuid(), translationOperationId,
            "en",
            "Changed again", "Ändrad igen", "auto", "en", "sv", null);

        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.Id, repeatedWithNewRequestId.Id);
        Assert.Equal(first.SessionId, repeated.SessionId);
        Assert.Equal(1, await context.SavedTranslations.CountAsync());
        Assert.Equal(1, await context.SavedTranslationSessions.CountAsync());
        Assert.Empty((await service.GetLibraryAsync("other", "sv", 1, 24)).Items);
        var library = await service.GetLibraryAsync("owner", "sv", 1, 24);
        var librarySession = Assert.Single(library.Items);
        Assert.Equal("sv", library.LanguageCode);
        Assert.Equal("sv", librarySession.LanguageCode);
        Assert.Equal("Hello", librarySession.SourcePreview);
        Assert.Equal(1, librarySession.TranslationCount);
        Assert.Equal(1, library.TotalTranslations);
        var clampedLibrary = await service.GetLibraryAsync("owner", "sv", 100_000_000, 24);
        Assert.Equal(1, clampedLibrary.Page);
        Assert.Equal("Hello", Assert.Single(clampedLibrary.Items).SourcePreview);
        Assert.Null(await service.GetSessionAsync(first.SessionId, "other", "sv", 1, 24));
        Assert.Null(await service.GetSessionAsync(first.SessionId, "owner", "en", 1, 24));
        var session = Assert.IsType<SavedTranslationSessionDetailPage>(
            await service.GetSessionAsync(first.SessionId, "owner", "sv", 1, 24));
        Assert.Equal("sv", session.LanguageCode);
        var detail = Assert.Single(session.Translations);
        Assert.Equal("Informal", detail.Preferences);

        await Assert.ThrowsAsync<SavedTranslationNotFoundException>(() =>
            service.DeleteSessionAsync(first.SessionId, "other", "sv"));
        await Assert.ThrowsAsync<SavedTranslationNotFoundException>(() =>
            service.DeleteSessionAsync(first.SessionId, "owner", "en"));
        await service.DeleteSessionAsync(first.SessionId, "owner", "sv");
        Assert.Empty(await context.SavedTranslations.ToListAsync());
        Assert.Empty(await context.SavedTranslationSessions.ToListAsync());
    }

    [Fact]
    public async Task Saves_from_one_client_session_are_grouped_in_chronological_order()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        var clientSessionId = Guid.NewGuid();
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();
        await AddCompletedTranslationAsync(context, "owner", firstOperation);
        await AddCompletedTranslationAsync(context, "owner", secondOperation);

        var first = await service.SaveAsync(
            "owner", clientSessionId, Guid.NewGuid(), firstOperation,
            "sv",
            "First source", "First result", "en", "en", "sv", null);
        var second = await service.SaveAsync(
            "owner", clientSessionId, Guid.NewGuid(), secondOperation,
            "sv",
            "Second source", "Second result", "en", "en", "sv", null);

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Single(await context.SavedTranslationSessions.ToListAsync());
        var library = await service.GetLibraryAsync("owner", "sv", 1, 24);
        Assert.Equal(2, Assert.Single(library.Items).TranslationCount);
        var session = Assert.IsType<SavedTranslationSessionDetailPage>(
            await service.GetSessionAsync(first.SessionId, "owner", "sv", 1, 24));
        Assert.Collection(
            session.Translations,
            item => Assert.Equal("First source", item.SourceText),
            item => Assert.Equal("Second source", item.SourceText));
    }

    [Fact]
    public async Task Save_defaults_to_target_language_and_can_bind_to_the_source_language()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        var clientSessionId = Guid.NewGuid();
        var targetOperation = Guid.NewGuid();
        var sourceOperation = Guid.NewGuid();
        await AddCompletedTranslationAsync(context, "owner", targetOperation);
        await AddCompletedTranslationAsync(context, "owner", sourceOperation);

        var targetSave = await service.SaveAsync(
            "owner", clientSessionId, Guid.NewGuid(), targetOperation,
            null,
            "Hello", "Hej", "auto", "en", "sv", null);
        var sourceSave = await service.SaveAsync(
            "owner", clientSessionId, Guid.NewGuid(), sourceOperation,
            "en",
            "Goodbye", "Hej då", "auto", "en", "sv", null);

        Assert.Equal("sv", targetSave.LanguageCode);
        Assert.Equal("en", sourceSave.LanguageCode);
        Assert.NotEqual(targetSave.SessionId, sourceSave.SessionId);
        Assert.Equal(2, await context.SavedTranslationSessions.CountAsync());
        Assert.Equal(targetSave.SessionId, Assert.Single(
            (await service.GetLibraryAsync("owner", "sv", 1, 24)).Items).Id);
        Assert.Equal(sourceSave.SessionId, Assert.Single(
            (await service.GetLibraryAsync("owner", "en", 1, 24)).Items).Id);

        var unsupportedBinding = Guid.NewGuid();
        await AddCompletedTranslationAsync(context, "owner", unsupportedBinding);
        await Assert.ThrowsAsync<TextTranslationValidationException>(() =>
            service.SaveAsync(
                "owner", clientSessionId, Guid.NewGuid(), unsupportedBinding,
                "fr",
                "Again", "Igen", "auto", "en", "sv", null));
    }

    [Fact]
    public async Task Account_deletion_cascades_through_sessions_on_a_relational_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new GlosifyContext(options);
        await context.Database.EnsureCreatedAsync();
        context.Users.Add(new ApplicationUser
        {
            Id = "owner",
            UserName = "owner@example.test",
            NormalizedUserName = "OWNER@EXAMPLE.TEST",
            AssistantTelemetrySubjectId = Guid.NewGuid(),
        });
        var session = new SavedTranslationSession
        {
            UserId = "owner",
            ClientSessionId = Guid.NewGuid(),
            LanguageCode = "sv",
            Title = "Relational session",
        };
        context.SavedTranslationSessions.Add(session);
        context.SavedTranslations.Add(new SavedTranslation
        {
            Session = session,
            UserId = "owner",
            RequestId = Guid.NewGuid(),
            SourceLanguage = "en",
            TargetLanguage = "sv",
            SourceText = "Hello",
            TranslatedText = "Hej",
        });
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        context.Users.Remove(await context.Users.SingleAsync(user => user.Id == "owner"));
        await context.SaveChangesAsync();
        Assert.Empty(await context.SavedTranslationSessions.ToListAsync());
        Assert.Empty(await context.SavedTranslations.ToListAsync());
    }

    [Fact]
    public async Task Save_requires_a_completed_paid_translation_owned_by_the_caller()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        var ownerOperationId = Guid.NewGuid();
        await AddCompletedTranslationAsync(context, "owner", ownerOperationId);

        await Assert.ThrowsAsync<TextTranslationValidationException>(() =>
            service.SaveAsync(
                "other", Guid.NewGuid(), Guid.NewGuid(), ownerOperationId,
                "sv",
                "Hello", "Hej", "auto", "en", "sv", null));
        await Assert.ThrowsAsync<TextTranslationValidationException>(() =>
            service.SaveAsync(
                "owner", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "sv",
                "Hello", "Hej", "auto", "en", "sv", null));

        Assert.Empty(await context.SavedTranslations.ToListAsync());
    }

    private static async Task AddCompletedTranslationAsync(
        GlosifyContext context,
        string userId,
        Guid operationId)
    {
        context.AiCreditTransactions.Add(new AiCreditTransaction
        {
            UserId = userId,
            OperationId = operationId,
            Kind = AiCreditTransactionKinds.UsageDebit,
            Feature = AiUsageFeatures.TextTranslation,
            Operation = "translate_text",
        });
        await context.SaveChangesAsync();
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static TextTranslationService CreateService(
        GlosifyContext context,
        FakeGenerativeAiClient? ai = null,
        FakeCreditService? credits = null) =>
        new(
            context,
            ai ?? new FakeGenerativeAiClient(),
            credits ?? new FakeCreditService(50),
            TimeProvider.System);

    private sealed class FakeGenerativeAiClient : IGenerativeAiClient
    {
        public int Calls { get; private set; }
        public string? Prompt { get; private set; }
        public string? Model { get; private set; }
        public AiUsageContext? Usage { get; private set; }
        public TextTranslationAiResponse Response { get; set; } = new()
        {
            DetectedSourceLanguage = "English",
            Translation = "Translation",
        };

        public Task<T> GenerateStructuredAsync<T>(
            string prompt,
            AiUsageContext usageContext,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            Prompt = prompt;
            Model = model;
            Usage = usageContext;
            return Task.FromResult((T)(object)Response);
        }

        public Task<string> ExtractTextFromImageAsync(
            byte[] imageBytes,
            string contentType,
            string prompt,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AgentTurnResult> RunAgentTurnAsync(
            AgentRequest request,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeCreditService(int availableCredits) : IAiCreditService
    {
        public Task<AiCreditAccountView> GetOrCreateAccountAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiCreditAccountView(userId, availableCredits, 0, availableCredits, null));

        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(
            string userId,
            int count = 25,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiCreditReservation> ReserveAsync(
            AiUsageContext context,
            string provider,
            string model,
            int estimatedTokens,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CommitUsageAsync(
            Guid reservationId,
            AiTokenUsage usage,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task GrantAsync(
            string adminUserId,
            string targetUserId,
            int credits,
            string note,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
