using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationCaptureServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 26, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Capture_IsEnabledOnlyForConfiguredAdminEmail()
    {
        await using var context = CreateContext();
        AddUserAndSession(context, "admin", "ADMIN@example.test", Guid.NewGuid());
        AddUserAndSession(context, "learner", "learner@example.test", Guid.NewGuid());
        await context.SaveChangesAsync();
        var service = CreateService(context);

        Assert.True(await service.IsAdminUserAsync("admin"));
        Assert.False(await service.IsAdminUserAsync("learner"));
        Assert.False(await service.IsAdminUserAsync("missing"));
    }

    [Fact]
    public async Task Append_StoresAllAdminStagesAndRejectsNonAdminSession()
    {
        await using var context = CreateContext();
        var adminSession = Guid.NewGuid();
        var learnerSession = Guid.NewGuid();
        AddUserAndSession(context, "admin", "admin@example.test", adminSession);
        AddUserAndSession(context, "learner", "learner@example.test", learnerSession);
        await context.SaveChangesAsync();
        var service = CreateService(context);
        CapturedRealtimeTranslationEvent[] events =
        [
            new(1, 4, RealtimeTranslationCaptureStages.Scribe,
                RealtimeTranslationCaptureKinds.Partial, "Good morn", null, "en", null, false, Now),
            new(2, 4, RealtimeTranslationCaptureStages.Translator,
                RealtimeTranslationCaptureKinds.Partial, "God morg", "Good morn", "en", "sv", true, Now.AddMilliseconds(10)),
            new(3, 4, RealtimeTranslationCaptureStages.Bubble,
                RealtimeTranslationCaptureKinds.Final, "God morgon.", "Good morning.", "en", "sv", false, Now.AddMilliseconds(20)),
        ];

        await service.AppendAsync(adminSession, "admin", events);
        await service.AppendAsync(learnerSession, "learner", events);
        await service.AppendAsync(adminSession, "learner", events);

        var stored = await context.RealtimeTranslationCaptureEvents
            .OrderBy(capture => capture.Ordinal)
            .ToListAsync();
        Assert.Equal(3, stored.Count);
        Assert.Equal(
            [RealtimeTranslationCaptureStages.Scribe,
                RealtimeTranslationCaptureStages.Translator,
                RealtimeTranslationCaptureStages.Bubble],
            stored.Select(capture => capture.Stage));
        Assert.False(stored[0].ProviderRequest);
        Assert.True(stored[1].ProviderRequest);
        Assert.False(stored[2].ProviderRequest);
        Assert.Equal("Good morn", stored[1].SourceText);
        Assert.All(stored, capture => Assert.Equal(adminSession, capture.SessionId));
        Assert.All(stored, capture => Assert.Equal(Now, capture.StoredAt));
    }

    private static RealtimeTranslationCaptureService CreateService(GlosifyContext context)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Emails:0"] = "admin@example.test",
            })
            .Build();
        return new RealtimeTranslationCaptureService(
            context,
            configuration,
            new FakeTimeProvider(Now));
    }

    private static GlosifyContext CreateContext() => new(
        new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static void AddUserAndSession(
        GlosifyContext context,
        string userId,
        string email,
        Guid sessionId)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = userId,
            Email = email,
            UserName = email,
        });
        context.RealtimeTranslationSessions.Add(new RealtimeTranslationSession
        {
            Id = sessionId,
            UserId = userId,
            TargetLanguage = "sv",
            TranslationMode = RealtimeTranslationModes.Scribe,
            SpeechProvider = RealtimeSpeechProviders.ElevenLabs,
            Model = "scribe_v2_realtime",
            BillingModel = "scribe+translator",
            CreditsPerStartedMinute = 1,
            Status = RealtimeTranslationSessionStatuses.Active,
            ExpiresAt = Now.AddMinutes(30),
        });
    }
}
