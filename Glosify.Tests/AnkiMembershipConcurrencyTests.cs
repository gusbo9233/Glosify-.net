using System.Data.Common;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Anki;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiMembershipConcurrencyTests
{
    [SqlServerFact]
    public Task Concurrent_adds_to_an_existing_collection_preserve_one_note_and_its_charges() => VerifyAsync(false, false);

    [SqlServerFact]
    public Task Concurrent_sentence_adds_preserve_one_note_and_its_charges() => VerifyAsync(true, false);

    [SqlServerFact]
    public Task Caller_owned_transactions_serialize_membership() => VerifyAsync(false, true);

    [SqlServerFact]
    public Task Concurrent_adds_of_different_ids_with_identical_text_preserve_one_note() => VerifyAsync(false, false, true);

    private static Task VerifyAsync(bool sentence, bool callerTransaction, bool duplicateText = false) =>
        SqlServerTestDatabase.RunAsync("anki-membership-race", async database =>
        {
            var options = new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(database.Database.GetConnectionString()).Options;
            await using var setup = new GlosifyContext(options, Options.Create(new AbuseOptions()));
            const string userId = "membership-user";
            setup.Add(new ResourceAccountingState { Id = 1, Ready = true });
            await setup.SaveChangesAsync();
            setup.Users.Add(new ApplicationUser { Id = userId, UserName = "membership@example.test" });
            var quiz = new Quiz { Id = Guid.NewGuid(), UserId = userId, Name = "Quiz", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
            var word = new Word { Id = "concurrent-word", QuizId = quiz.Id, Lemma = "dom", Translation = "house" };
            var sentenceId = Guid.NewGuid();
            setup.AddRange(quiz, word, new QuizSentence { Id = sentenceId, QuizId = quiz.Id, Text = "sentence", Translation = "meaning" });
            await setup.SaveChangesAsync();
            if (duplicateText)
            {
                setup.Words.Add(new Word { Id = "duplicate-word", QuizId = quiz.Id, Lemma = word.Lemma, Translation = word.Translation });
                await setup.SaveChangesAsync();
            }
            var collection = await new AnkiCollectionService(setup, TimeProvider.System).GetOrCreateAsync(userId, "Polish", "UTC");
            setup.ChangeTracker.Clear();

            var race = new MembershipReadBarrier();
            var competingOptions = new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlServer(database.Database.GetConnectionString()).AddInterceptors(race).Options;
            async Task<bool> AddAsync(bool single)
            {
                await using var db = new GlosifyContext(competingOptions, Options.Create(new AbuseOptions()));
                var service = new AnkiCollectionService(db, TimeProvider.System);
                var key = new AnkiItemKey(quiz.Id, sentence ? PracticeItemType.Sentences : PracticeItemType.Words,
                    sentence ? sentenceId.ToString() : duplicateText && !single ? "duplicate-word" : word.Id);
                await using var transaction = callerTransaction ? await db.Database.BeginTransactionAsync() : null;
                var result = single
                    ? await service.AddItemForLanguageAsync("Polish", "UTC", key, userId)
                    : sentence ? await service.SetMembershipAsync(collection.Id, [key], [], userId)
                    : await service.SetMembershipForLanguageAsync("Polish", "UTC", [key], [], userId);
                if (transaction is not null) await transaction.CommitAsync();
                return result;
            }
            Assert.All(await Task.WhenAll(AddAsync(true), AddAsync(false)), Assert.True);
            Assert.True(race.Reads >= 2, "Both requests must attempt the serialized collection read.");
            var note = Assert.Single(await setup.AnkiNotes.AsNoTracking().ToListAsync());
            Assert.Equal(collection.Id, note.AnkiCollectionId);
            if (sentence) Assert.Equal(sentenceId, note.SentenceId);
            else Assert.Contains(note.WordId, duplicateText ? new[] { word.Id, "duplicate-word" } : new[] { word.Id });
            var cards = await setup.AnkiCards.AsNoTracking().ToListAsync();
            Assert.Equal(2, cards.Count);
            Assert.All(cards, card => Assert.True(card.IsActive && card.DirectlyIncluded));
            Assert.Equal(2, cards.Select(card => card.Direction).Distinct().Count());
            Assert.Single(await setup.Set<ResourceEntry>().Where(entry => entry.EntityType == "AnkiNote").ToListAsync());
            Assert.Equal(2, await setup.Set<ResourceEntry>().CountAsync(entry => entry.EntityType == "AnkiCard"));
            var before = await setup.Set<ResourceUsage>().AsNoTracking().OrderBy(row => row.Scope).ThenBy(row => row.Resource)
                .Select(row => new { row.Scope, row.Resource, row.Used }).ToListAsync();
            await ResourceMaintenanceService.ReconcileCountersAsync(setup, default);
            var after = await setup.Set<ResourceUsage>().AsNoTracking().OrderBy(row => row.Scope).ThenBy(row => row.Resource)
                .Select(row => new { row.Scope, row.Resource, row.Used }).ToListAsync();
            Assert.Equal(before, after);
        });

    private sealed class MembershipReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public int Reads => _reads;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // Start both requests before either acquires the collection membership lock.
            if (command.CommandText.Contains("UPDLOCK, HOLDLOCK", StringComparison.Ordinal))
            {
                var read = Interlocked.Increment(ref _reads);
                if (read == 2) _bothRead.TrySetResult();
                if (read <= 2) await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
}
