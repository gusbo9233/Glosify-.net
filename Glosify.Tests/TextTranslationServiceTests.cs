using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Translator;
using Glosify.Controllers;
using Glosify.Controllers.Api;
using Glosify.Infrastructure.Api;
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
        var owner = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(ApplicationUser), owner.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, owner.DeleteBehavior);
        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(SavedTranslation.UserId), nameof(SavedTranslation.RequestId)]));
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

        var first = await service.SaveAsync(
            "owner", requestId, "Hello", "Hej", "auto", "en", "sv", "Informal");
        var repeated = await service.SaveAsync(
            "owner", requestId, "Changed", "Ändrad", "auto", "en", "sv", null);

        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(1, await context.SavedTranslations.CountAsync());
        Assert.Empty((await service.GetLibraryAsync("other", 1, 24)).Items);
        var library = await service.GetLibraryAsync("owner", 1, 24);
        Assert.Equal("Hello", Assert.Single(library.Items).SourcePreview);
        Assert.Null(await service.GetDetailAsync(first.Id, "other"));
        var detail = Assert.IsType<SavedTranslationDetail>(
            await service.GetDetailAsync(first.Id, "owner"));
        Assert.Equal("Informal", detail.Preferences);

        await Assert.ThrowsAsync<SavedTranslationNotFoundException>(() =>
            service.DeleteAsync(first.Id, "other"));
        await service.DeleteAsync(first.Id, "owner");
        Assert.Empty(await context.SavedTranslations.ToListAsync());
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
