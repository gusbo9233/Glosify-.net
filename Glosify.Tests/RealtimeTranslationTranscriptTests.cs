using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
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
        var service = new RealtimeTranslationTranscriptService(context, Clock());
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
        var tools = AssistantToolFactory.Create(context);
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

    [Fact]
    public async Task Append_StoresBothStreamsAndDeduplicatesWithinEachStream()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        context.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
        {
            Id = transcriptId,
            UserId = "user-1",
            Title = "Polish source transcript",
            TargetLanguage = "pl",
            Stream = RealtimeTranslationTranscriptStreams.Source,
        });
        context.RealtimeTranslationSessions.Add(
            Session(sessionId, "user-1", transcriptId, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        var service = new RealtimeTranslationTranscriptService(context, Clock());
        // Both accumulators number their own segments, so the streams collide on key.
        var source = new CapturedTranslationSegment(1, "item:0", "Dzień dobry", DateTimeOffset.UtcNow);
        var translation = source with
        {
            Text = "Good morning",
            Stream = RealtimeTranslationTranscriptStreams.Translation,
        };

        await service.AppendAsync(sessionId, [source, translation]);
        await service.AppendAsync(sessionId, [source, translation]);

        var saved = await context.RealtimeTranslationTranscriptSegments.ToListAsync();
        Assert.Equal(2, saved.Count);
        Assert.Equal(
            "Dzień dobry",
            Assert.Single(saved, segment =>
                segment.Stream == RealtimeTranslationTranscriptStreams.Source).Text);
        Assert.Equal(
            "Good morning",
            Assert.Single(saved, segment =>
                segment.Stream == RealtimeTranslationTranscriptStreams.Translation).Text);
    }

    [Fact]
    public async Task AssistantTools_ReadSourceByDefaultAndTranslationOnRequest()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        AddTranscript(context, transcriptId, "user-1", "Mine", "Dzień dobry");
        context.RealtimeTranslationTranscriptSegments.Add(new RealtimeTranslationTranscriptSegment
        {
            Id = Guid.NewGuid(),
            TranscriptId = transcriptId,
            SessionId = Guid.NewGuid(),
            Sequence = 1,
            Stream = RealtimeTranslationTranscriptStreams.Translation,
            ProviderEventKey = "translation:" + transcriptId,
            Text = "Good morning",
        });
        await context.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(context);
        var toolContext = new AgentToolContext
        {
            UserId = "user-1",
            TranscriptId = transcriptId,
            CurrentLanguageCode = "pl",
        };

        var fallback = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_saved_transcript",
            "{}",
            toolContext,
            CancellationToken.None));
        var translated = JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_saved_transcript",
            JsonSerializer.Serialize(new { stream = "translation" }),
            toolContext,
            CancellationToken.None));

        Assert.Equal("source", fallback.GetProperty("stream").GetString());
        Assert.Equal("Dzień dobry", fallback.GetProperty("captions")[0].GetProperty("text").GetString());
        Assert.Equal(1, fallback.GetProperty("total_segments").GetInt32());
        Assert.Equal("translation", translated.GetProperty("stream").GetString());
        Assert.Equal("Good morning", translated.GetProperty("captions")[0].GetProperty("text").GetString());
        Assert.Equal(
            ["source", "translation"],
            translated.GetProperty("available_streams").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Detail_SelectsRequestedStreamAndCountsEachSeparately()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        AddTranscript(context, transcriptId, "user-1", "Mine", "Dzień dobry");
        context.RealtimeTranslationTranscriptSegments.Add(new RealtimeTranslationTranscriptSegment
        {
            Id = Guid.NewGuid(),
            TranscriptId = transcriptId,
            SessionId = Guid.NewGuid(),
            Sequence = 1,
            Stream = RealtimeTranslationTranscriptStreams.Translation,
            ProviderEventKey = "translation:" + transcriptId,
            Text = "Good morning",
        });
        await context.SaveChangesAsync();
        var service = new RealtimeTranslationTranscriptService(context, Clock());

        var fallback = await service.GetDetailAsync(transcriptId, "user-1", "pl", 1, 50);
        var translated = await service.GetDetailAsync(transcriptId, "user-1", "pl", 1, 50, "translation");
        var library = await service.GetLibraryAsync("user-1", "pl", 1, 50);

        Assert.Equal("source", fallback!.SelectedStream);
        Assert.Equal("Dzień dobry", Assert.Single(fallback.Segments).Text);
        Assert.Equal("Good morning", Assert.Single(translated!.Segments).Text);
        Assert.Equal(1, translated.SourceSegmentCount);
        Assert.Equal(1, translated.TranslationSegmentCount);
        Assert.Equal(1, translated.TotalSegments);
        // The library counts the transcript's own stream, not both.
        Assert.Equal(1, Assert.Single(library.Items).SegmentCount);
    }

    [Fact]
    public async Task AssistantTools_PageMatchesTheEquivalentOffsetInEachStream()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        AddPagedTranscript(context, transcriptId, sourceSegments: 250, translationSegments: 130);
        await context.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(context);
        var toolContext = Context(transcriptId);

        foreach (var stream in new[] { "source", "translation" })
        {
            var byPage = await ReadAsync(tools, toolContext, new { page = 2, stream });
            var byOffset = await ReadAsync(tools, toolContext, new { offset = 100, stream });

            Assert.Equal(2, byPage.GetProperty("page_number").GetInt32());
            Assert.Equal(
                byOffset.GetProperty("captions").ToString(),
                byPage.GetProperty("captions").ToString());
        }
    }

    [Fact]
    public async Task AssistantTools_ReadTheSamePageOneAsTheReader()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        AddPagedTranscript(context, transcriptId, sourceSegments: 250, translationSegments: 0);
        await context.SaveChangesAsync();
        var service = new RealtimeTranslationTranscriptService(context, Clock());
        var tools = AssistantToolFactory.Create(context);

        var reader = await service.GetDetailAsync(
            transcriptId,
            "user-1",
            "pl",
            1,
            RealtimeTranslationTranscriptService.DetailPageSize);
        var assistant = await ReadAsync(tools, Context(transcriptId), new { page = 1 });

        Assert.Equal(
            reader!.Segments.Select(segment => segment.Text),
            assistant.GetProperty("captions").EnumerateArray()
                .Select(caption => caption.GetProperty("text").GetString()));
        Assert.Equal(3, assistant.GetProperty("total_pages").GetInt32());
    }

    [Fact]
    public async Task AssistantTools_AtTimeLandsOnADifferentPagePerStream()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        // The streams transcribe the same audio at different granularities, so the same
        // instant is caption 150 of one and caption 60 of the other.
        AddPagedTranscript(context, transcriptId, sourceSegments: 250, translationSegments: 130);
        await context.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(context);
        var toolContext = Context(transcriptId);
        var moment = Origin.AddSeconds(150 * 2);

        var source = await ReadAsync(tools, toolContext, new { at_time = moment, stream = "source" });
        var translation = await ReadAsync(
            tools,
            toolContext,
            new { at_time = moment, stream = "translation" });

        Assert.Equal(2, source.GetProperty("page_number").GetInt32());
        Assert.Equal(1, translation.GetProperty("page_number").GetInt32());
        // Each page still covers the requested instant.
        Assert.True(source.GetProperty("starts_at").GetDateTimeOffset() <= moment);
        Assert.True(source.GetProperty("ends_at").GetDateTimeOffset() >= moment);
        Assert.True(translation.GetProperty("ends_at").GetDateTimeOffset() >= moment);
    }

    [Fact]
    public async Task AssistantTools_ResumeATruncatedPageWithoutLosingCaptions()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        // 100 captions of 500 characters is 50,000 — four times the per-call budget.
        AddPagedTranscript(context, transcriptId, sourceSegments: 100, translationSegments: 0, captionLength: 500);
        await context.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(context);
        var toolContext = Context(transcriptId);

        var first = await ReadAsync(tools, toolContext, new { page = 1 });
        Assert.False(first.GetProperty("page_complete").GetBoolean());
        Assert.Equal(1, first.GetProperty("page_number").GetInt32());

        var read = first.GetProperty("captions").EnumerateArray().Count();
        var offset = first.GetProperty("next_offset").GetInt32();
        var hasMore = first.GetProperty("has_more").GetBoolean();
        while (hasMore)
        {
            var next = await ReadAsync(tools, toolContext, new { offset });
            Assert.Equal(offset, next.GetProperty("offset").GetInt32());
            read += next.GetProperty("captions").EnumerateArray().Count();
            offset = next.GetProperty("next_offset").GetInt32();
            hasMore = next.GetProperty("has_more").GetBoolean();
        }

        Assert.Equal(100, read);
    }

    [Fact]
    public async Task AssistantTools_ResumingATruncatedPageNeverCrossesIntoTheNextPage()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        // Two pages of captions too long to return in one call, so resuming page 1 by
        // offset would otherwise read on into page 2 while still reporting page 1.
        AddPagedTranscript(context, transcriptId, sourceSegments: 200, translationSegments: 0, captionLength: 500);
        await context.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(context);
        var toolContext = Context(transcriptId);

        var resumed = await ReadAsync(tools, toolContext, new { page = 1 });
        Assert.False(resumed.GetProperty("page_complete").GetBoolean());

        // The boundary is only reachable from the last read that still starts inside page
        // 1, so follow next_offset all the way there. Asserting on an earlier read proves
        // nothing: the character budget stops it well short of caption 99 either way.
        var offset = resumed.GetProperty("next_offset").GetInt32();
        while (offset < RealtimeTranslationTranscriptService.DetailPageSize)
        {
            resumed = await ReadAsync(tools, toolContext, new { offset });
            offset = resumed.GetProperty("next_offset").GetInt32();
        }

        Assert.Equal(1, resumed.GetProperty("page_number").GetInt32());
        Assert.True(resumed.GetProperty("page_complete").GetBoolean());
        // Page 1 is captions 0..99, so nothing it returns may be captioned later than 99.
        Assert.Equal(Origin.AddSeconds(99 * 2), resumed.GetProperty("ends_at").GetDateTimeOffset());
        // Caption 100 opens page 2; the padding means this substring can only appear if
        // the read genuinely crossed the boundary.
        Assert.DoesNotContain(
            "source caption 100",
            resumed.GetProperty("captions").ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssistantTools_ClampAPageBeyondTheEnd()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        AddPagedTranscript(context, transcriptId, sourceSegments: 150, translationSegments: 0);
        await context.SaveChangesAsync();
        var tools = AssistantToolFactory.Create(context);

        var beyond = await ReadAsync(tools, Context(transcriptId), new { page = 999 });

        Assert.Equal(2, beyond.GetProperty("page_number").GetInt32());
        Assert.Equal(2, beyond.GetProperty("total_pages").GetInt32());
        Assert.Equal(50, beyond.GetProperty("captions").EnumerateArray().Count());
        Assert.False(beyond.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task Detail_ClampsAPageBeyondTheEndToTheLastPage()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        AddPagedTranscript(context, transcriptId, sourceSegments: 150, translationSegments: 0);
        await context.SaveChangesAsync();
        var service = new RealtimeTranslationTranscriptService(context, Clock());

        var page = await service.GetDetailAsync(
            transcriptId,
            "user-1",
            "pl",
            999,
            RealtimeTranslationTranscriptService.DetailPageSize);

        Assert.Equal(2, page!.Page);
        Assert.Equal(50, page.Segments.Count);
    }

    [Fact]
    public async Task PageSpans_DescribeEveryPageOfTheSelectedStream()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        AddPagedTranscript(context, transcriptId, sourceSegments: 250, translationSegments: 130);
        await context.SaveChangesAsync();
        var service = new RealtimeTranslationTranscriptService(context, Clock());

        var source = await service.GetPageSpansAsync(
            transcriptId,
            "user-1",
            "pl",
            null,
            RealtimeTranslationTranscriptService.DetailPageSize);
        var translation = await service.GetPageSpansAsync(
            transcriptId,
            "user-1",
            "pl",
            "translation",
            RealtimeTranslationTranscriptService.DetailPageSize);

        Assert.Equal([1, 2, 3], source.Select(span => span.Page));
        Assert.Equal([1, 2], translation.Select(span => span.Page));
        Assert.Equal(Origin, source[0].StartsAt);
        Assert.Equal(Origin.AddSeconds(99 * 2), source[0].EndsAt);
        Assert.Equal(Origin.AddSeconds(100 * 2), source[1].StartsAt);
    }

    private static readonly DateTimeOffset Origin = new(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A fixed clock, so nothing here depends on when the suite happens to run. The read
    /// paths do not consult it today, but AppendAsync and RenameAsync stamp UpdatedAt with
    /// it, and a paging test should not start failing because a read path later does too.
    /// </summary>
    private static TimeProvider Clock() => new FakeTimeProvider(Origin);

    private static AgentToolContext Context(Guid transcriptId) => new()
    {
        UserId = "user-1",
        TranscriptId = transcriptId,
        CurrentLanguageCode = "pl",
    };

    private static async Task<JsonElement> ReadAsync(
        IAssistantTools tools,
        AgentToolContext context,
        object args) =>
        JsonSerializer.SerializeToElement(await tools.ExecuteAsync(
            "get_saved_transcript",
            JsonSerializer.Serialize(args),
            context,
            CancellationToken.None));

    /// <summary>
    /// A transcript long enough to page, whose two streams deliberately hold different
    /// numbers of captions over the same two-second-per-caption timeline: that mismatch is
    /// what makes page numbers stream-specific and timestamps the only shared coordinate.
    /// </summary>
    private static void AddPagedTranscript(
        GlosifyContext context,
        Guid id,
        int sourceSegments,
        int translationSegments,
        int captionLength = 0)
    {
        context.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
        {
            Id = id,
            UserId = "user-1",
            Title = "Long session",
            TargetLanguage = "pl",
            Stream = RealtimeTranslationTranscriptStreams.Source,
        });
        var sessionId = Guid.NewGuid();
        var spanSeconds = sourceSegments * 2.0;
        AddStream(RealtimeTranslationTranscriptStreams.Source, sourceSegments);
        AddStream(RealtimeTranslationTranscriptStreams.Translation, translationSegments);

        void AddStream(string stream, int count)
        {
            for (var index = 0; index < count; index++)
            {
                var text = $"{stream} caption {index}";
                context.RealtimeTranslationTranscriptSegments.Add(new RealtimeTranslationTranscriptSegment
                {
                    Id = Guid.NewGuid(),
                    TranscriptId = id,
                    SessionId = sessionId,
                    Sequence = index,
                    Stream = stream,
                    ProviderEventKey = $"{stream}:{index}",
                    Text = captionLength > 0 ? text.PadRight(captionLength, '.') : text,
                    CapturedAt = Origin.AddSeconds(index * (spanSeconds / Math.Max(1, count))),
                });
            }
        }
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
