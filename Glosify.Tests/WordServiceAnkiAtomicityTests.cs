using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Glosify.Services.Words;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class WordServiceAnkiAtomicityTests
{
    private const string UserId = "word-owner";

    [Fact]
    public async Task AddWord_rolls_back_when_Anki_synchronization_fails()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new WordService(fixture.Context, new ThrowingAnkiCollectionService());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddWordAsync(fixture.QuizId, "dom", "house", "English", "Polish"));

        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.Words.ToListAsync());
    }

    [Fact]
    public async Task Word_and_sentence_removals_roll_back_when_Anki_synchronization_fails()
    {
        await using var fixture = await Fixture.CreateAsync();
        var word = new Word
        {
            Id = "word-1",
            QuizId = fixture.QuizId,
            Lemma = "dom",
            Translation = "house",
        };
        var sentence = new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = fixture.QuizId,
            Text = "To jest dom.",
            Translation = "This is a house.",
        };
        fixture.Context.AddRange(word, sentence);
        await fixture.Context.SaveChangesAsync();
        var service = new WordService(fixture.Context, new ThrowingAnkiCollectionService());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteWordAsync(word.Id, UserId));
        fixture.Context.ChangeTracker.Clear();
        Assert.True(await fixture.Context.Words.AnyAsync(item => item.Id == word.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteSentenceAsync(sentence.Id, UserId));
        fixture.Context.ChangeTracker.Clear();
        Assert.True(await fixture.Context.QuizSentences.AnyAsync(item => item.Id == sentence.Id));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public GlosifyContext Context { get; }
        public Guid QuizId { get; }

        private Fixture(SqliteConnection connection, GlosifyContext context, Guid quizId)
        {
            _connection = connection;
            Context = context;
            QuizId = quizId;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
                .UseSqlite(connection)
                .Options);
            await context.Database.EnsureCreatedAsync();
            var quizId = Guid.NewGuid();
            context.Users.Add(new ApplicationUser
            {
                Id = UserId,
                UserName = "word-owner@example.test",
                NormalizedUserName = "WORD-OWNER@EXAMPLE.TEST",
            });
            context.Quizzes.Add(new Quiz
            {
                Id = quizId,
                UserId = UserId,
                Name = "Atomic words",
                SourceLanguage = "English",
                TargetLanguage = "Polish",
                Language = "Polish",
                ProcessingStatus = "Ready",
            });
            await context.SaveChangesAsync();
            return new Fixture(connection, context, quizId);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ThrowingAnkiCollectionService : IAnkiCollectionService
    {
        public Task SyncQuizAsync(Guid quizId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated Anki synchronization failure.");

        public Task<IReadOnlyList<AnkiCollectionSummary>> ListAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AnkiCollectionDetails?> GetDetailsAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AnkiCollection> CreateAsync(CreateAnkiCollectionInput input, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AnkiCollection?> CreateFromQuizAsync(CreateAnkiCollectionFromQuizInput input, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RenameAsync(Guid collectionId, string name, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateSettingsAsync(Guid collectionId, double desiredRetention, int newCardsPerDay, int maximumReviewsPerDay, string timeZoneId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AddQuizAsync(AddAnkiQuizInput input, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveQuizAsync(Guid collectionId, Guid quizId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AddItemAsync(AddAnkiItemInput input, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveCardAsync(Guid cardId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SyncCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetireQuizAsync(Guid quizId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
