using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class RealtimeTranslationTranscriptTests
{
    [Fact]
    public async Task Append_IsOptInOnlyAndIdempotent()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        var optedInSession = Guid.NewGuid();
        var privateSession = Guid.NewGuid();
        context.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
        {
            Id = transcriptId,
            UserId = "user-1",
            Title = "Polish source transcript",
            TargetLanguage = "pl",
            Stream = RealtimeTranslationTranscriptStreams.Source,
        });
        context.RealtimeTranslationSessions.AddRange(
            Session(optedInSession, "user-1", transcriptId, DateTimeOffset.UtcNow),
            Session(privateSession, "user-1", null, null));
        await context.SaveChangesAsync();
        var service = new RealtimeTranslationTranscriptService(context, TimeProvider.System);
        var segment = new CapturedTranslationSegment(1, "response:item:0", "Hola", DateTimeOffset.UtcNow);

        await service.AppendAsync(optedInSession, [segment]);
        await service.AppendAsync(optedInSession, [segment]);
        await service.AppendAsync(privateSession, [segment with { ProviderEventKey = "private" }]);

        var saved = Assert.Single(await context.RealtimeTranslationTranscriptSegments.ToListAsync());
        Assert.Equal("Hola", saved.Text);
        Assert.Equal(transcriptId, saved.TranscriptId);
    }

    [Fact]
    public async Task AssistantTools_PageOwnedTranscriptAndRejectForeignTranscript()
    {
        await using var context = CreateContext();
        var ownId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        AddTranscript(context, ownId, "user-1", "Mine", "Hola");
        AddTranscript(context, foreignId, "user-2", "Private", "Secret");
        await context.SaveChangesAsync();
        var tools = new AssistantTools(context);
        var toolContext = new AgentToolContext
        {
            UserId = "user-1",
            TranscriptId = ownId,
            CurrentLanguageCode = "pl",
        };

        var own = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_saved_transcript",
            "{}",
            toolContext,
            CancellationToken.None));
        var foreign = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_saved_transcript",
            JsonSerializer.Serialize(new { transcript_id = foreignId }),
            toolContext,
            CancellationToken.None));

        Assert.Equal("Hola", own.GetProperty("captions")[0].GetProperty("text").GetString());
        Assert.Equal("Saved transcript not found.", foreign.GetProperty("error").GetString());
    }

    private static GlosifyContext CreateContext() => new(
        new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static RealtimeTranslationSession Session(
        Guid id,
        string userId,
        Guid? transcriptId,
        DateTimeOffset? consent) => new()
    {
        Id = id,
        UserId = userId,
        TargetLanguage = "pl",
        Model = "translate",
        BillingModel = "gpt-realtime-translate+gpt-realtime-whisper",
        CreditsPerStartedMinute = 16,
        Status = RealtimeTranslationSessionStatuses.Completed,
        TranscriptId = transcriptId,
        TranscriptConsentAt = consent,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
    };

    private static void AddTranscript(
        GlosifyContext context,
        Guid id,
        string userId,
        string title,
        string text)
    {
        context.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
        {
            Id = id,
            UserId = userId,
            Title = title,
            TargetLanguage = "pl",
            Stream = RealtimeTranslationTranscriptStreams.Source,
            Segments =
            [
                new RealtimeTranslationTranscriptSegment
                {
                    Id = Guid.NewGuid(),
                    TranscriptId = id,
                    SessionId = Guid.NewGuid(),
                    Sequence = 1,
                    ProviderEventKey = "event:" + id,
                    Text = text,
                },
            ],
        });
    }
}
