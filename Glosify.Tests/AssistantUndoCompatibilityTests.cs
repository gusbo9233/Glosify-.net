using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantUndoCompatibilityTests
{
    [Theory]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "rename")]
    [InlineData(true, "rename")]
    [InlineData(false, "move")]
    [InlineData(true, "move")]
    [InlineData(false, "visibility")]
    [InlineData(true, "visibility")]
    [InlineData(false, "content")]
    [InlineData(true, "content")]
    [InlineData(false, "owner")]
    [InlineData(true, "owner")]
    [InlineData(false, "missing-field")]
    [InlineData(true, "missing-field")]
    [InlineData(false, "extra-field")]
    [InlineData(true, "extra-field")]
    public async Task Legacy_creation_snapshots_undo_only_unchanged_owned_empty_containers(bool folder, string edit)
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall(folder ? "create_collection" : "create_quiz", "{\"name\":\"Created\"}").ThenText("Created.");
        var run = await h.RunAsync("Create an empty resource", quizId: Guid.Empty);
        Guid id;
        await using (var db = h.Db())
        {
            var change = await db.AssistantChanges.SingleAsync(c => c.RunId == run.Id);
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.AfterJson!)!;
            var retained = folder ? "parent_collection_id" : "collection_id";
            fields = fields.Where(p => p.Key == "name" || p.Key == retained || p.Key == "is_public").ToDictionary();
            Assert.Equal(3, fields.Count);
            if (edit == "missing-field") fields.Remove("is_public");
            if (edit == "extra-field") fields["unexpected"] = JsonSerializer.SerializeToElement(true);
            change.AfterJson = JsonSerializer.Serialize(fields);
            var parentId = Guid.NewGuid();
            if (edit == "move") db.Collections.Add(new Collection { Id = parentId, UserId = "user", Name = "Destination", Language = "Polish" });
            if (folder)
            {
                var item = await db.Collections.SingleAsync(c => c.Name == "Created"); id = item.Id;
                if (edit == "rename") item.Name = "User rename";
                if (edit == "move") item.ParentCollectionId = parentId;
                if (edit == "visibility") item.IsPublic = true;
                if (edit == "owner") item.UserId = "other";
                if (edit == "content") db.Collections.Add(new Collection { Id = Guid.NewGuid(), UserId = "user", Name = "User child", Language = "Polish", ParentCollectionId = id });
            }
            else
            {
                var item = await db.Quizzes.SingleAsync(q => q.Name == "Created"); id = item.Id;
                if (edit == "rename") item.Name = "User rename";
                if (edit == "move") item.CollectionId = parentId;
                if (edit == "visibility") item.IsPublic = true;
                if (edit == "owner") item.UserId = "other";
                if (edit == "content") db.Words.Add(new Word { Id = Guid.NewGuid().ToString(), QuizId = id, Lemma = "User word", Translation = "Translation" });
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(edit == "unchanged" ? new AssistantUndoResult(1, 0) : new AssistantUndoResult(0, 1), await h.UndoAsync(run.Id));
        await using var after = h.Db();
        Assert.Equal(edit != "unchanged", folder
            ? await after.Collections.AnyAsync(c => c.Id == id)
            : await after.Quizzes.AnyAsync(q => q.Id == id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modern_creation_snapshots_still_protect_fields_absent_from_legacy_records(bool folder)
    {
        await using var h = await AssistantHarness.CreateAsync();
        h.Model.ThenCall(folder ? "create_collection" : "create_quiz", "{\"name\":\"Created\"}").ThenText("Created.");
        var run = await h.RunAsync("Create an empty resource", quizId: Guid.Empty);
        await using (var db = h.Db())
        {
            if (folder) (await db.Collections.SingleAsync(c => c.Name == "Created")).Language = "Swedish";
            else (await db.Quizzes.SingleAsync(q => q.Name == "Created")).SourceLanguage = "Swedish";
            await db.SaveChangesAsync();
        }
        Assert.Equal(new AssistantUndoResult(0, 1), await h.UndoAsync(run.Id));
        await using var after = h.Db();
        Assert.Equal("Swedish", folder
            ? (await after.Collections.SingleAsync(c => c.Name == "Created")).Language
            : (await after.Quizzes.SingleAsync(q => q.Name == "Created")).SourceLanguage);
    }

}
