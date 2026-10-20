using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantAvatarSnapshotAccountingTests
{
    [SqlServerFact]
    public Task SnapshotsChargeOnceEnforceQuotaAndReleaseThroughChatCleanupBeforeAccountDeletion() =>
        SqlServerTestDatabase.RunAsync("avatar_snapshot_quota", async seed =>
        {
            var connection = seed.Database.GetConnectionString()!;
            await using var h = await AssistantHarness.CreateSqlServerAsync(connection);
            var limits = new AbuseOptions();
            var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlServer(connection).Options;
            GlosifyContext AccountedDb() => new(options, Options.Create(limits));
            await using (var db = h.Db())
            {
                db.Add(new ResourceAccountingState { Id = 1, Ready = true });
                await db.SaveChangesAsync();
            }
            var sessions = new AvatarSessions(h.Clock);
            var session = sessions.Create(AssistantHarness.UserId, QuizLanguageCatalog.Find("pl")!, "free");
            h.Configure = services =>
            {
                services.RemoveAll<GlosifyContext>();
                services.AddScoped(_ => AccountedDb());
                services.AddSingleton(new AvatarAssistantContext(sessions,
                    Options.Create(new AvatarOptions { Enabled = true })));
            };
            h.Restart();
            for (var i = 0; i < 60; i++) sessions.RecordTurn(session, i.ToString(), "user", new string('x', 1000));
            var input = new AssistantRunInput("Help", AvatarConversationId: session.MemoryId);
            for (var i = 0; i < 2; i++)
            {
                var run = await h.StartAsync("Help", input: input);
                await h.CommandAsync(run.Id, "cancel");
            }
            long used;
            await using (var db = h.Db())
            {
                var snapshot = await db.AssistantAvatarSnapshots.SingleAsync();
                Assert.All(await db.AssistantRuns.ToListAsync(), run => Assert.Equal(snapshot.Id, run.AvatarSnapshotId));
                var entries = await db.Set<ResourceEntry>().Where(e => e.UserId == AssistantHarness.UserId).ToListAsync();
                var source = Assert.Single(entries, e => e.EntityType == nameof(AssistantAvatarSnapshot));
                Assert.True(ResourceAccounting.Charges(source.ChargesJson)["content_bytes"] > 120_000);
                used = (await db.Set<ResourceUsage>().SingleAsync(u => u.Scope == AssistantHarness.UserId && u.Resource == "content_bytes")).Used;
                Assert.Equal(entries.Sum(e => ResourceAccounting.Charges(e.ChargesJson).GetValueOrDefault("content_bytes")), used);
            }
            limits.ContentBytes = used + 20_000; // Room for a normal request, not another copy of the source.
            sessions.RecordTurn(session, "new", "user", new string('y', 12_000));
            var error = await Assert.ThrowsAsync<ResourceQuotaException>(() => h.StartAsync("Updated help", input: input with { Message = "Updated help" }));
            Assert.Equal("content_bytes", error.Resource);
            await using (var db = h.Db())
            {
                Assert.Equal(1, await db.AssistantAvatarSnapshots.CountAsync());
                Assert.Equal(2, await db.AssistantRuns.CountAsync());
                Assert.Equal(used, (await db.Set<ResourceUsage>().SingleAsync(u => u.Scope == AssistantHarness.UserId && u.Resource == "content_bytes")).Used);
            }
            limits.ContentBytes = new AbuseOptions().ContentBytes;
            await using (var scope = h.Scope())
                await scope.ServiceProvider.GetRequiredService<AssistantThreadStore>().DeleteAsync(h.ThreadId, AssistantHarness.UserId, default);
            await using (var db = AccountedDb())
            {
                Assert.Empty(await db.AssistantAvatarSnapshots.ToListAsync());
                Assert.Empty(await db.Set<ResourceEntry>().Where(e => e.EntityType == nameof(AssistantAvatarSnapshot)).ToListAsync());
                Assert.Equal(0, (await db.Set<ResourceUsage>().SingleAsync(u => u.Scope == AssistantHarness.UserId && u.Resource == "content_bytes")).Used);
            }
            var nextThread = Guid.NewGuid();
            await using (var db = AccountedDb())
            {
                db.AssistantThreads.Add(new AssistantThread
                { Id = nextThread, UserId = AssistantHarness.UserId, Language = "Polish", Title = "Another chat" });
                await db.SaveChangesAsync();
            }
            var last = await h.StartAsync("Help", threadId: nextThread, input: input);
            await h.CommandAsync(last.Id, "cancel");
            await using (var db = h.Db()) Assert.Equal(1, await db.AssistantAvatarSnapshots.CountAsync());
            // Existing user->chat foreign keys use NoAction. Remove owned chats via
            // the application's cleanup service before removing the identity row.
            await using (var scope = h.Scope())
                await scope.ServiceProvider.GetRequiredService<AssistantThreadStore>().DeleteAsync(nextThread, AssistantHarness.UserId, default);
            await using (var db = AccountedDb())
            {
                db.Users.Remove(await db.Users.SingleAsync(u => u.Id == AssistantHarness.UserId));
                await db.SaveChangesAsync();
                Assert.Empty(await db.AssistantAvatarSnapshots.ToListAsync());
                Assert.Empty(await db.Set<ResourceEntry>().Where(e => e.UserId == AssistantHarness.UserId).ToListAsync());
                Assert.Equal(0, (await db.Set<ResourceUsage>().SingleAsync(u => u.Scope == ResourceAccounting.Site && u.Resource == "content_bytes")).Used);
            }
        });
}
