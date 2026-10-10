using System.Text.Json;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Anki;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Glosify.BrowserTests;

public sealed partial class PortfolioJourneys
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task AssistantAnki_SelectedQuizItemsAppearInCollectionAndUndoRemovesThem()
    {
        await RegisterAndSelectPolishAsync();
        await CreateQuizWithWordAsync();
        var quizUrl = Page.Url;
        foreach (var (word, translation) in new[] { ("kot", "cat"), ("pies", "dog") })
        {
            await Page.GetByLabel("Word", new() { Exact = true }).FillAsync(word);
            await Page.GetByLabel("Translation", new() { Exact = true }).FillAsync(translation);
            await Page.GetByRole(AriaRole.Button, new() { Name = "Add word", Exact = true }).ClickAsync();
            await Expect(Page.Locator(".word-card").Filter(new() { HasText = word })).ToBeVisibleAsync();
        }
        await Page.GotoAsync("/Anki?create=true");
        var form = Page.Locator("[data-anki-create-form]");
        await form.GetByLabel("Collection name").FillAsync("Assistant Revision");
        await form.GetByLabel("Source language").SelectOptionAsync(new SelectOptionValue { Label = "English" });
        await form.GetByRole(AriaRole.Button, new() { Name = "Create collection" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Assistant Revision", Exact = true }).First).ToBeVisibleAsync();
        var collectionUrl = Page.Url;
        await Page.GotoAsync(quizUrl);

        // Script only model completion. The applier persists real cards and the production
        // Undo endpoint consumes its real journal. Runtime/tool selection is covered by
        // AssistantAnkiTests without requiring a live model in browser CI.
        await RouteRunStartsAsync(async route =>
        {
            var threadId = Guid.Parse(new Uri(route.Request.Url).Segments.Last());
            var runId = await SaveScriptedAnkiReplyAsync(threadId);
            var status = await Page.APIRequest.GetAsync($"/Assistant/Runs/{runId}");
            Assert.True(status.Ok);
            await route.FulfillAsync(new RouteFulfillOptions { Status = 202, ContentType = "application/json", Body = await status.TextAsync() });
        });
        await Page.Locator("[data-assistant-toggle]").ClickAsync();
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToBeVisibleAsync();
        await Page.Locator("[data-assistant-textarea]").FillAsync("Add the first 2 words to my Anki Assistant Revision collection");
        await Page.Locator("[data-assistant-submit]").ClickAsync();
        await Expect(Page.Locator("[data-assistant-pane='chat']")).ToContainTextAsync("Added 2 cards");
        var undo = Page.GetByRole(AriaRole.Button, new() { Name = "Undo changes", Exact = true });
        await Expect(undo).ToBeVisibleAsync();

        var collectionPage = await Page.Context.NewPageAsync();
        await collectionPage.GotoAsync(collectionUrl);
        await Expect(collectionPage.GetByText("New", new() { Exact = true }).Locator("..").Locator("strong")).ToHaveTextAsync("2");
        await Expect(collectionPage.GetByText("No quizzes linked yet.", new() { Exact = true })).ToBeVisibleAsync();
        Page.Dialog += (_, dialog) => dialog.AcceptAsync();
        await undo.ClickAsync();
        await Expect(Page.Locator(".assistant-undo")).ToContainTextAsync("Changes undone");
        await collectionPage.ReloadAsync();
        await Expect(collectionPage.GetByText("New", new() { Exact = true }).Locator("..").Locator("strong")).ToHaveTextAsync("0");
        await Expect(Page.Locator(".word-card")).ToHaveCountAsync(3);
        await collectionPage.CloseAsync();
        await AssertNoPageErrorsAsync();
    }

    private static async Task<Guid> SaveScriptedAnkiReplyAsync(Guid threadId)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException("Browser fixtures require an explicit test SQL connection.");
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(connection).Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var thread = await db.AssistantThreads.SingleAsync(t => t.Id == threadId);
        var quiz = await db.Quizzes.SingleAsync(q => q.UserId == thread.UserId && q.Name == "Portfolio Polish");
        var collection = await db.AnkiCollections.SingleAsync(c => c.UserId == thread.UserId && c.Name == "Assistant Revision");
        var ids = await db.Words.Where(w => w.QuizId == quiz.Id).OrderBy(w => w.CreatedAt).ThenBy(w => w.Id).Take(2).Select(w => w.Id).ToListAsync();
        Assert.Equal(2, ids.Count);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var anki = new AnkiCollectionService(db, TimeProvider.System);
        // These Anki changes don't use the quiz/library service dependencies.
        var applier = new ChangeApplier(db, null!, null!, NullLogger<ChangeApplier>.Instance, anki);
        var applied = await applier.ApplyAsync(quiz.Id, thread.UserId,
            [new PendingChange(PendingChangeKinds.AddAnkiItems, JsonSerializer.SerializeToElement(
                new AddAnkiItemsInput(collection.Id, quiz.Id, PracticeItemType.Words, ids, true, false), web))], CancellationToken.None);
        Assert.Equal(2, applied.AnkiCardsAdded);
        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var partId = Guid.NewGuid();
        var text = $"Added 2 cards to [Assistant Revision](/Anki/Collection/{collection.Id}).";
        var sequence = (await db.AssistantMessages.Where(m => m.ThreadId == threadId).MaxAsync(m => (int?)m.Sequence) ?? -1) + 1;
        db.AssistantMessages.Add(new AssistantMessage { Id = messageId, ThreadId = threadId, Sequence = sequence,
            Role = AssistantMessageRole.Model, ContentJson = JsonSerializer.Serialize(new { parts = new[] { new { kind = "text", text } } }), CreatedAt = now });
        db.AssistantRuns.Add(new AssistantRun { Id = runId, ThreadId = threadId, UserId = thread.UserId,
            IdempotencyKey = Guid.NewGuid().ToString("N"), Status = AssistantRunStatus.Completed, CurrentMessageId = messageId,
            CreatedAt = now, UpdatedAt = now, CompletedAt = now, Revision = 1, SavedChanges = applied.Journal.Count });
        db.AssistantParts.Add(new AssistantPart { Id = partId, MessageId = messageId, RunId = runId,
            Type = AssistantPartTypes.Text, Text = text, CreatedAt = now });
        for (var i = 0; i < applied.Journal.Count; i++)
        {
            var entry = applied.Journal[i];
            db.AssistantChanges.Add(new AssistantChange { Id = Guid.NewGuid(), RunId = runId, PartId = partId,
                UserId = thread.UserId, Sequence = i, Kind = entry.Kind, EntityType = entry.EntityType, EntityId = entry.EntityId,
                BeforeJson = entry.Before is null ? null : JsonSerializer.Serialize(entry.Before, web),
                AfterJson = JsonSerializer.Serialize(entry.After, web), CreatedAt = now });
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return runId;
    }
}
