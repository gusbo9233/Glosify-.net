using System.Security.Claims;
using Glosify.Controllers.Api;
using Glosify.Models.Api;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationFreestyleGuardTests
{
    [Fact]
    public async Task Freestyle_rejects_every_session_boundary_before_calling_the_service()
    {
        var service = new RecordingRealtimeTranslationService();
        var controller = new RealtimeTranslationApiController(
            service,
            new FreestyleLanguagePreferenceService())
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
        Func<Task>[] calls =
        [
            async () => await controller.Catalog(CancellationToken.None),
            async () => await controller.CreateSession(
                new CreateRealtimeTranslationSessionRequest("pl"),
                CancellationToken.None),
            async () => await controller.ReserveMinute(sessionId, 0, CancellationToken.None),
            async () => await controller.BeginMinute(sessionId, 0, CancellationToken.None),
            async () => await controller.Heartbeat(
                sessionId,
                new RealtimeTranslationHeartbeatRequest(),
                CancellationToken.None),
        ];

        foreach (var call in calls)
        {
            var exception = await Assert.ThrowsAsync<RealtimeTranslationValidationException>(call);
            Assert.Equal("Realtime translation is not available in Freestyle mode.", exception.Message);
        }

        Assert.Equal(0, service.Calls);

        var ended = await controller.EndSession(sessionId, CancellationToken.None);

        Assert.IsType<NoContentResult>(ended);
        Assert.Equal(1, service.Calls);
        Assert.Equal(sessionId, service.EndedSessionId);
    }

    private sealed class FreestyleLanguagePreferenceService : IQuizLanguagePreferenceService
    {
        public Task<QuizLanguage?> GetSelectedAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(QuizLanguageCatalog.Find(QuizLanguageCatalog.FreestyleCode));

        public Task<QuizLanguage> SetSelectedAsync(
            string userId,
            string language,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ClearAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRealtimeTranslationService : IRealtimeTranslationService
    {
        public int Calls { get; private set; }
        public Guid? EndedSessionId { get; private set; }

        public Task<RealtimeTranslationCatalog> GetCatalogAsync(string userId, CancellationToken cancellationToken = default) =>
            Unexpected<RealtimeTranslationCatalog>();

        public Task<RealtimeTranslationSessionCreated> CreateSessionAsync(
            string userId,
            string targetLanguage,
            bool saveTranscript = false,
            Guid? transcriptId = null,
            string? translationMode = null,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            Unexpected<RealtimeTranslationSessionCreated>();

        public Task<RealtimeTranslationMinuteResult> ReserveMinuteAsync(
            string userId,
            Guid sessionId,
            int minuteIndex,
            CancellationToken cancellationToken = default) =>
            Unexpected<RealtimeTranslationMinuteResult>();

        public Task<RealtimeTranslationMinuteResult> BeginMinuteAsync(
            string userId,
            Guid sessionId,
            int minuteIndex,
            CancellationToken cancellationToken = default) =>
            Unexpected<RealtimeTranslationMinuteResult>();

        public Task<RealtimeTranslationSessionStatus> HeartbeatAsync(
            string userId,
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Unexpected<RealtimeTranslationSessionStatus>();

        public Task EndSessionAsync(string userId, Guid sessionId, CancellationToken cancellationToken = default)
        {
            Calls++;
            EndedSessionId = sessionId;
            return Task.CompletedTask;
        }

        public Task FailSessionAsync(string userId, Guid sessionId, CancellationToken cancellationToken = default) =>
            Unexpected();

        public Task CleanupStaleSessionsAsync(CancellationToken cancellationToken = default) =>
            Unexpected();

        private Task<T> Unexpected<T>()
        {
            Calls++;
            throw new InvalidOperationException("The realtime translation service must not be called.");
        }

        private Task Unexpected()
        {
            Calls++;
            throw new InvalidOperationException("The realtime translation service must not be called.");
        }
    }
}
