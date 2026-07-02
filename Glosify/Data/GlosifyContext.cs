using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Glosify.Models;
using Glosify.Models.Library;

namespace Glosify.Data;

/// <summary>
/// DbContext for Glosify application using Azure SQL Database
/// </summary>
public class GlosifyContext : IdentityDbContext<ApplicationUser>
{
    public GlosifyContext(DbContextOptions<GlosifyContext> options)
        : base(options)
    {
    }

    // Add your DbSets here as you define your models
    // Example:
    public DbSet<Quiz> Quizzes { get; set; }
    public DbSet<Word> Words { get; set; }
    public DbSet<QuizSentence> QuizSentences { get; set; }
    public DbSet<AssistantThread> AssistantThreads { get; set; }
    public DbSet<AssistantMessage> AssistantMessages { get; set; }
    public DbSet<AiCreditAccount> AiCreditAccounts { get; set; }
    public DbSet<AiCreditTransaction> AiCreditTransactions { get; set; }

    public DbSet<Collection> Collections { get; set; }

    public DbSet<BookDocument> BookDocuments { get; set; }
    public DbSet<BookPage> BookPages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Quiz>(entity =>
        {
            entity.HasKey(q => q.Id);

            entity.Property(q => q.Name).HasMaxLength(160).IsRequired();
            entity.Property(q => q.UserId).HasMaxLength(450).IsRequired();
            entity.Property(q => q.SourceLanguage).HasMaxLength(64).IsRequired();
            entity.Property(q => q.TargetLanguage).HasMaxLength(64).IsRequired();
            entity.Property(q => q.Language).HasMaxLength(64);
            entity.Property(q => q.ProcessingStatus).HasMaxLength(64);
            entity.Property(q => q.ProcessingMessage).HasMaxLength(512);

            entity.HasIndex(q => q.UserId);
            entity.HasIndex(q => q.CollectionId);

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(q => q.UserId)
                .HasConstraintName("FK_Quizzes_AspNetUsers_UserId")
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(q => q.Collection)
                .WithMany(c => c.Quizzes)
                .HasForeignKey(q => q.CollectionId)
                .HasConstraintName("FK_Quizzes_Collections_CollectionId")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Word>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.HasIndex(w => w.QuizId);
            entity.HasOne<Quiz>()
                .WithMany()
                .HasForeignKey(w => w.QuizId)
                .HasConstraintName("FK_words_quizzes")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuizSentence>(entity =>
        {
            entity.HasKey(sentence => sentence.Id);
            entity.Property(sentence => sentence.Text).IsRequired();
            entity.Property(sentence => sentence.Translation).IsRequired();
            entity.HasIndex(sentence => sentence.QuizId);
            entity.HasOne<Quiz>()
                .WithMany()
                .HasForeignKey(sentence => sentence.QuizId)
                .HasConstraintName("FK_quiz_sentences_quizzes")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssistantThread>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.UserId).HasMaxLength(450).IsRequired();
            entity.Property(t => t.Title).HasMaxLength(256);
            entity.HasIndex(t => new { t.QuizId, t.UserId });
            entity.HasIndex(t => new { t.UserId, t.QuizId });
            entity.HasIndex(t => t.ContextQuizId);
            entity.HasOne<Quiz>()
                .WithMany()
                .HasForeignKey(t => t.QuizId)
                .HasConstraintName("FK_AssistantThreads_Quizzes_QuizId")
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Quiz>()
                .WithMany()
                .HasForeignKey(t => t.ContextQuizId)
                .HasConstraintName("FK_AssistantThreads_Quizzes_ContextQuizId")
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .HasConstraintName("FK_AssistantThreads_AspNetUsers_UserId")
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<AssistantMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Role).HasMaxLength(16).IsRequired();
            entity.Property(m => m.Status).HasMaxLength(16).IsRequired();
            entity.HasIndex(m => new { m.ThreadId, m.Sequence }).IsUnique();
            entity.HasIndex(m => m.ContextQuizId);
            entity.HasOne<AssistantThread>()
                .WithMany()
                .HasForeignKey(m => m.ThreadId)
                .HasConstraintName("FK_AssistantMessages_AssistantThreads_ThreadId")
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Quiz>()
                .WithMany()
                .HasForeignKey(m => m.ContextQuizId)
                .HasConstraintName("FK_AssistantMessages_Quizzes_ContextQuizId")
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<AiCreditAccount>(entity =>
        {
            entity.HasKey(account => account.UserId);
            entity.Property(account => account.UserId).HasMaxLength(450).IsRequired();
            entity.Property(account => account.RowVersion).IsRowVersion();
            entity.Ignore(account => account.AvailableCredits);

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(account => account.UserId)
                .HasConstraintName("FK_AiCreditAccounts_AspNetUsers_UserId")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiCreditTransaction>(entity =>
        {
            entity.HasKey(transaction => transaction.Id);
            entity.Property(transaction => transaction.UserId).HasMaxLength(450).IsRequired();
            entity.Property(transaction => transaction.Kind).HasMaxLength(32).IsRequired();
            entity.Property(transaction => transaction.Provider).HasMaxLength(64);
            entity.Property(transaction => transaction.Model).HasMaxLength(128);
            entity.Property(transaction => transaction.Feature).HasMaxLength(64);
            entity.Property(transaction => transaction.Operation).HasMaxLength(128);
            entity.Property(transaction => transaction.ActorUserId).HasMaxLength(450);
            entity.Property(transaction => transaction.Note).HasMaxLength(512);
            entity.Property(transaction => transaction.RelatedEntityType).HasMaxLength(64);
            entity.Property(transaction => transaction.RelatedEntityId).HasMaxLength(128);
            entity.HasIndex(transaction => new { transaction.UserId, transaction.CreatedAt });
            entity.HasIndex(transaction => transaction.ReservationId);

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(transaction => transaction.UserId)
                .HasConstraintName("FK_AiCreditTransactions_AspNetUsers_UserId")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Collection>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.UserId).HasMaxLength(450).IsRequired();
            entity.Property(c => c.Name).HasMaxLength(160).IsRequired();
            entity.Property(c => c.Language).HasMaxLength(64).IsRequired();

            entity.HasIndex(c => new { c.UserId, c.Language, c.ParentCollectionId, c.Name });
            entity.HasIndex(c => new { c.IsPublic, c.Language });
            entity.HasIndex(c => c.OriginalCollectionId);

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.ParentCollection)
                .WithMany(c => c.ChildCollections)
                .HasForeignKey(c => c.ParentCollectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BookDocument>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.UserId).HasMaxLength(450).IsRequired();
            entity.Property(b => b.Title).HasMaxLength(256).IsRequired();
            entity.Property(b => b.OriginalFileName).HasMaxLength(512).IsRequired();
            entity.Property(b => b.BlobName).HasMaxLength(1024).IsRequired();
            entity.Property(b => b.ProcessingStatus).HasMaxLength(64).IsRequired();
            entity.Property(b => b.ProcessingMessage).HasMaxLength(512);

            entity.HasIndex(b => b.UserId);
            entity.HasIndex(b => new { b.UserId, b.CreatedAt });

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(b => b.UserId)
                .HasConstraintName("FK_BookDocuments_AspNetUsers_UserId")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BookPage>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Text).IsRequired();
            entity.Property(p => p.ExtractionWarning).HasMaxLength(512);

            entity.HasIndex(p => new { p.BookDocumentId, p.PageNumber }).IsUnique();

            entity.HasOne(p => p.BookDocument)
                .WithMany(b => b.Pages)
                .HasForeignKey(p => p.BookDocumentId)
                .HasConstraintName("FK_BookPages_BookDocuments_BookDocumentId")
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
