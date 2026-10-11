using System.Net;
using AngleSharp.Html.Parser;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Avatar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarUsageTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Session = Guid.Parse("aabbeeff-1111-2222-3333-444444444444");
    private static readonly IOptions<AiUsageOptions> Prices = Options.Create(new AiUsageOptions
    {
        MonthlyBudget = new() { Models = [new() { Deployment = "reply-model", InputSekPerMillionTokens = 2, OutputSekPerMillionTokens = 10 }] }
    });
    private static GlosifyContext MemoryDb() => new(new DbContextOptionsBuilder<GlosifyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task ExistingAccountingRecordsProduceFilteredCostsWithoutDoubleCounting() { await using var db = MemoryDb(); await VerifyReport(db); }

    [SqlServerFact]
    public Task DashboardQueriesExecuteAgainstSqlServer() => SqlServerTestDatabase.RunAsync("avatar_usage_report", VerifyReport);

    private static async Task VerifyReport(GlosifyContext db)
    {
        var reports = new AvatarUsageReports(db, Prices);
        var filter = new AvatarUsageFilter(From, From.AddHours(1), Admin: "admin", SessionId: Session.ToString());
        var empty = await reports.ReadAsync(filter, default);
        Assert.Empty(empty.Entries); Assert.Empty(empty.Breakdown); Assert.Equal(0, empty.Sessions);
        db.Users.Add(new ApplicationUser { Id = "admin", UserName = "admin", Email = "admin@example.test" });
        db.Users.Add(new ApplicationUser { Id = "other", UserName = "other", Email = "other@example.test" });
        AddVoice(db, "recognition", 12.5m, .01m, .625m, true, 1);
        AddVoice(db, "speech", 100m, .002m, 2m, true, 2);
        AddReply(db, "reply-model", 100, 40, 150, .08m, 3);
        AddReply(db, "reply-model", null, null, null, 0, 4); // Pending, not its reserved token estimate.
        var released = AddReply(db, "reply-model", null, null, null, 0, 5);
        db.Add(new AiCreditTransaction { UserId = "admin", ReservationId = released, Kind = AiCreditTransactionKinds.Release, CreatedAt = From.AddMinutes(5) });
        AddReply(db, "unpriced-model", 70, 30, 100, .04m, 6);
        AddVoice(db, "recognition", 2m, .01m, 0, false, 7);
        AddVoice(db, "speech", 0m, .002m, 0, true, 8);
        // A second release must not multiply the recognition call, cost, or credit charge.
        var recognition = db.ChangeTracker.Entries<AvatarUsageOperation>().Select(x => x.Entity).Single(x => x.Kind == "recognition" && x.Settled);
        db.Add(new AiCreditTransaction { UserId = "admin", ReservationId = recognition.Id, Kind = AiCreditTransactionKinds.Release, CreatedAt = From.AddMinutes(10) });
        AddReply(db, "reply-model", 1000, 1000, 2000, 5, -1);
        AddReply(db, "reply-model", 1000, 1000, 2000, 5, 61);
        AddReply(db, "reply-model", 1000, 1000, 2000, 5, 12, user: "other");
        AddReply(db, "reply-model", 1000, 1000, 2000, 5, 13, session: Guid.NewGuid());
        var unrelated = AddReply(db, "reply-model", 1000, 1000, 2000, 5, 14);
        foreach (var entry in db.ChangeTracker.Entries<AiCreditTransaction>().Where(x => x.Entity.ReservationId == unrelated)) entry.Entity.RelatedEntityType = "AssistantChat";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var report = await reports.ReadAsync(filter, default);
        Assert.Equal(8, report.Calls); Assert.Equal(8, report.Entries.Count); Assert.Equal(1, report.Sessions);
        Assert.Equal(2, report.Pending); Assert.Equal(3, report.Unpriced);
        Assert.Equal(2.745m, report.Credits); Assert.Equal(.3457m, report.EstimatedSek);
        Assert.Equal(14.5m, report.AudioSeconds); Assert.Equal(100m, report.Characters); Assert.Equal(250, report.TotalTokens);
        Assert.All(report.Entries, entry => Assert.Equal("admin@example.test", entry.Email));
        var reply = Assert.Single(report.Entries, x => x.Model == "reply-model" && x.Status == "Settled");
        Assert.Equal(.0007m, reply.EstimatedSek); Assert.Equal(10, reply.UnclassifiedTokens);
        Assert.Null(Assert.Single(report.Entries, x => x.Status == "Released").EstimatedSek);
        Assert.Null(Assert.Single(report.Entries, x => x.Provider == "openai" && x.Status == "Pending").TotalTokens);
        Assert.Equal(8, (await reports.ReadAsync(filter with { Admin = "admin@example.test" }, default)).Calls);
        Assert.Empty((await reports.ReadAsync(filter with { Admin = "absent" }, default)).Entries);
        var voices = await reports.ReadAsync(filter with { Provider = "elevenlabs" }, default);
        Assert.Equal(4, voices.Calls); Assert.Equal(.345m, voices.EstimatedSek); Assert.Equal(2.625m, voices.Credits);
        var speech = await reports.ReadAsync(filter with { Operation = "avatar.speech", Model = AvatarOptions.SpeechModel }, default);
        Assert.Equal(2, speech.Calls); Assert.Equal(.2m, speech.EstimatedSek);
        Assert.Equal(1, (await reports.ReadAsync(filter with { To = From.AddMinutes(1) }, default)).Calls);
        // Reading the dashboard must never write to balances or the accounting ledger.
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task TotalsIncludeCallsBeyondTheLatest500AndSeparateSessions()
    {
        await using var db = MemoryDb();
        for (var i = 0; i < 503; i++) AddReply(db, "reply-model", 100, 100, 200, 1, i, session: i % 2 == 0 ? Session : Guid.Empty);
        await db.SaveChangesAsync();
        var report = await new AvatarUsageReports(db, Prices).ReadAsync(new(From, From.AddDays(1)), default);
        Assert.Equal(500, report.Entries.Count); Assert.Equal(503, report.Calls); Assert.Equal(2, report.Sessions);
        Assert.Equal(503m, report.Credits); Assert.Equal(503 * .0012m, report.EstimatedSek); Assert.Equal(100600, report.TotalTokens);
        Assert.Equal(From.AddMinutes(502), report.Entries[0].StartedAt);
    }

    [Fact]
    public void SqlServerTranslatesTheDetailsAndWholeRangeAggregation()
    {
        using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer("Server=localhost;Database=translation_only;Integrated Security=true;TrustServerCertificate=true").Options);
        var query = new AvatarUsageReports(db, Prices).Query(new(From, From.AddHours(1), "admin@example.test"));
        Assert.Contains("LEFT JOIN", query.OrderByDescending(x => x.StartedAt).Take(500).ToQueryString());
        Assert.Contains("GROUP BY", AvatarUsageReports.Group(query).ToQueryString());
    }

    [Fact]
    public async Task OnlyAdminsCanReadTheDashboardAndItsSettingsLink()
    {
        using var app = new AvatarFixture();
        using var anonymous = await app.Client(null);
        using var learner = await app.Client("learner");
        using var admin = await app.Client("admin");
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/AvatarUsage")).StatusCode);
        var denied = await learner.GetAsync("/Admin/AvatarUsage");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Equal("/Home/Error", denied.Headers.Location!.AbsolutePath);
        var response = await admin.GetAsync("/Admin/AvatarUsage"); response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var page = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        Assert.Contains("No Avatar usage", page.Body!.TextContent);
        Assert.NotNull(page.QuerySelector("#settings-admin a[href='/Admin/AvatarUsage']"));
        var learnerPage = new HtmlParser().ParseDocument(await learner.GetStringAsync("/Quiz"));
        Assert.Null(learnerPage.QuerySelector("a[href='/Admin/AvatarUsage']"));
    }

    [Theory]
    [InlineData("from=bad")]
    [InlineData("to=bad")]
    [InlineData("from=2026-10-11&to=2026-10-10")]
    [InlineData("from=2024-01-01&to=2026-01-01")]
    [InlineData("sessionId=invalid")]
    public async Task InvalidFiltersReturnProblemDetails(string query)
    {
        using var app = new AvatarFixture(); using var client = await app.Client("admin");
        var response = await client.GetAsync("/Admin/AvatarUsage?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DateFormUsesUtcAndSessionLinksKeepFilters()
    {
        using var app = new AvatarFixture(); using var client = await app.Client("admin");
        await app.Seed(async db => { AddReply(db, "reply-model", 100, 40, 140, .1m, 1); await db.SaveChangesAsync(); });
        var url = "/Admin/AvatarUsage?from=2026-10-10T12:00:00&to=2026-10-10T13:00:00&admin=admin&provider=openai&model=reply-model&operation=avatar.reply&sessionId=" + Session.ToString("N");
        var response = await client.GetAsync(url); response.EnsureSuccessStatusCode();
        var page = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(page.QuerySelectorAll("[data-avatar-usage-call]"));
        Assert.Contains("2026-10-10 12:01:00", row.TextContent);
        var link = row.QuerySelector("a")!.GetAttribute("href")!;
        Assert.Contains("sessionId=" + Session, link); Assert.Contains("admin=admin", link);
        Assert.Contains("provider=openai", link); Assert.Contains("model=reply-model", link); Assert.Contains("operation=avatar.reply", link);
        Assert.Equal("2026-10-10T12:00:00", page.QuerySelector("input[name=from]")!.GetAttribute("value"));
    }

    private static Guid AddReply(GlosifyContext db, string model, int? input, int? output, int? total, decimal credits, int minute, string user = "admin", Guid? session = null)
    {
        var id = Guid.NewGuid();
        db.Add(new AiCreditTransaction { UserId = user, ReservationId = id, Kind = AiCreditTransactionKinds.Reservation,
            Provider = "openai", Model = model, Operation = "avatar.reply", RelatedEntityType = "AvatarSession", RelatedEntityId = (session ?? Session).ToString(),
            CreditAmount = 99, TotalTokens = 99999, CreatedAt = From.AddMinutes(minute) });
        if (total.HasValue) db.Add(new AiCreditTransaction { UserId = user, ReservationId = id, Kind = AiCreditTransactionKinds.UsageDebit,
            Provider = "openai", Model = model, Operation = "avatar.reply", RelatedEntityType = "AvatarSession", RelatedEntityId = (session ?? Session).ToString(),
            CreditAmount = -credits, PromptTokens = input, CandidateTokens = output, TotalTokens = total, BudgetAmountMicros = 1, CreatedAt = From.AddMinutes(minute).AddSeconds(10) });
        return id;
    }
    private static void AddVoice(GlosifyContext db, string kind, decimal units, decimal rate, decimal credits, bool settled, int minute)
    {
        var id = Guid.NewGuid();
        db.Add(new AvatarUsageOperation { Id = id, UserId = "admin", SessionId = Session, Kind = kind, SubmittedUnits = units, ProviderSekRate = rate, Settled = settled, ExpiresAt = From.AddMinutes(minute + 5) });
        db.Add(new AiCreditTransaction { UserId = "admin", ReservationId = id, Kind = AiCreditTransactionKinds.Reservation,
            Provider = "elevenlabs", Model = kind == "speech" ? AvatarOptions.SpeechModel : AvatarOptions.RecognitionModel, Operation = "avatar." + kind,
            RelatedEntityType = "AvatarSession", RelatedEntityId = Session.ToString(), CreditAmount = 99, CreatedAt = From.AddMinutes(minute) });
        if (credits > 0) db.Add(new AiCreditTransaction { UserId = "admin", ReservationId = id, Kind = AiCreditTransactionKinds.UsageDebit, CreditAmount = -credits, CreatedAt = From.AddMinutes(minute).AddSeconds(20) });
        if (settled) db.Add(new AiCreditTransaction { UserId = "admin", ReservationId = id, Kind = AiCreditTransactionKinds.Release, CreditAmount = 99 - credits, CreatedAt = From.AddMinutes(minute).AddSeconds(20) });
    }
}
