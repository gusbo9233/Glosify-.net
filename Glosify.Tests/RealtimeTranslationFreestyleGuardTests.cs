using System.Security.Claims;
using Glosify.Controllers.Api;
using Glosify.Models.Api;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationFreestyleTests
{
    [Fact]
    public async Task Freestyle_allows_live_subtitle_boundaries_without_changing_selected_mode()
    {
        var service = new RecordingRealtimeTranslationService();
        var preferences = new FreestyleLanguagePreferenceService();
        var controller = new RealtimeTranslationApiController(
            service,
            preferences)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                        "test")),
                },
            },
        };
        var sessionId = Guid.NewGuid();
        await controller.Catalog(CancellationToken.None);
        await controller.CreateSession(
            new CreateRealtimeTranslationSessionRequest("pl"),
            CancellationToken.None);
        await controller.ReserveMinute(sessionId, 2, CancellationToken.None);
        await controller.BeginMinute(sessionId, 1, CancellationToken.None);
        await controller.Heartbeat(
            sessionId,
            new RealtimeTranslationHeartbeatRequest(),
            CancellationToken.None);
        var ended = await controller.EndSession(sessionId, CancellationToken.None);

        Assert.IsType<NoContentResult>(ended);
        Assert.Equal(
            ["catalog", "create", "reserve", "begin", "heartbeat", "end"],
            service.Calls);
        Assert.Equal(sessionId, service.EndedSessionId);
        Assert.Equal(0, preferences.GetCalls);
        Assert.Equal(0, preferences.SetCalls);
    }

    private sealed class FreestyleLanguagePreferenceService : IQuizLanguagePreferenceService
    {
        public int GetCalls { get; private set; }
        public int SetCalls { get; private set; }

        public Task<QuizLanguage?> GetSelectedAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(QuizLanguageCatalog.Find(QuizLanguageCatalog.FreestyleCode));
        }

        public Task<QuizLanguage> SetSelectedAsync(
            string userId,
            string language,
            CancellationToken cancellationToken = default)
        {
            SetCalls++;
            return Task.FromResult(QuizLanguageCatalog.Find(language)!);
        }

        public Task ClearAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRealtimeTranslationService : IRealtimeTranslationService
    {
        public List<string> Calls { get; } = [];
        public Guid? EndedSessionId { get; private set; }

        public Task<RealtimeTranslationCatalog> GetCatalogAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("catalog");
            return Task.FromResult(new RealtimeTranslationCatalog(
                [new RealtimeTranslationLanguage("pl", "Polish")],
                [new RealtimeTranslationLanguage("pl", "Polish")],
                8,
                16,
                true,
                null,
                30,
                5,
                15,
                "model",
                25,
                [new RealtimeTranslationMode("enhanced", "Enhanced", "Best quality", 8)],
                [new RealtimeTranslationSourceLanguage("auto", "Auto detect", null)]));
        }

        public Task<RealtimeTranslationSessionCreated> CreateSessionAsync(
            string userId,
            string targetLanguage,
            bool saveTranscript = false,
            Guid? transcriptId = null,
            string? translationMode = null,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("create");
            return Task.FromResult(new RealtimeTranslationSessionCreated(
                Guid.NewGuid(),
                "relay-token",
                DateTimeOffset.UtcNow.AddMinutes(1),
                "/relay",
                1,
                25,
                8,
                null));
        }

        public Task<RealtimeTranslationMinuteResult> ReserveMinuteAsync(
            string userId,
            Guid sessionId,
            int minuteIndex,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("reserve");
            return Task.FromResult(new RealtimeTranslationMinuteResult(
                sessionId,
                minuteIndex,
                "reserved",
                25,
                0,
                0));
        }

        public Task<RealtimeTranslationMinuteResult> BeginMinuteAsync(
            string userId,
            Guid sessionId,
            int minuteIndex,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("begin");
            return Task.FromResult(new RealtimeTranslationMinuteResult(
                sessionId,
                minuteIndex,
                "started",
                17,
                1,
                8));
        }

        public Task<RealtimeTranslationSessionStatus> HeartbeatAsync(
            string userId,
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("heartbeat");
            return Task.FromResult(new RealtimeTranslationSessionStatus(
                sessionId,
                "active",
                DateTimeOffset.UtcNow.AddMinutes(30),
                1,
                8));
        }

        public Task EndSessionAsync(string userId, Guid sessionId, CancellationToken cancellationToken = default)
        {
            Calls.Add("end");
            EndedSessionId = sessionId;
            return Task.CompletedTask;
        }

        public Task FailSessionAsync(string userId, Guid sessionId, CancellationToken cancellationToken = default) =>
            Unexpected();

        public Task CleanupStaleSessionsAsync(CancellationToken cancellationToken = default) =>
            Unexpected();

        private Task Unexpected()
        {
            throw new InvalidOperationException("The realtime translation service must not be called.");
        }
    }
}
