using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Glosify.Models;

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
    public DbSet<WordDetail> WordDetails { get; set; }
    public DbSet<DictionaryEntry> DictionaryEntries { get; set; }
    public DbSet<AssistantThread> AssistantThreads { get; set; }
    public DbSet<AssistantMessage> AssistantMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Quiz>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.Property(q => q.UserId).HasMaxLength(450).IsRequired();
            entity.HasIndex(q => q.UserId);
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(q => q.UserId)
                .HasConstraintName("FK_Quizzes_AspNetUsers_UserId")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Word>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.WordDetailId).HasMaxLength(450);
            entity.HasIndex(w => w.WordDetailId);
            entity.HasOne<WordDetail>()
                .WithMany()
                .HasForeignKey(w => w.WordDetailId)
                .HasConstraintName("FK_words_word_details")
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WordDetail>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new
            {
                e.SourceLanguage,
                e.TargetLanguage,
                e.NormalizedWordHash,
                e.NormalizedTranslationHash
            }).IsUnique();

            entity.Property(e => e.SourceLanguage).HasMaxLength(64);
            entity.Property(e => e.TargetLanguage).HasMaxLength(64);
            entity.Property(e => e.Word).HasMaxLength(256);
            entity.Property(e => e.Translation).HasMaxLength(512);
            entity.Property(e => e.NormalizedWord).HasMaxLength(1024);
            entity.Property(e => e.NormalizedTranslation).HasMaxLength(1024);
            entity.Property(e => e.NormalizedWordHash).HasMaxLength(64);
            entity.Property(e => e.NormalizedTranslationHash).HasMaxLength(64);
        });

        modelBuilder.Entity<DictionaryEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SourceHash).IsUnique();
            entity.HasIndex(e => new { e.LangCode, e.Word });

            entity.Property(e => e.SourceHash).HasMaxLength(64);
            entity.Property(e => e.Word).HasMaxLength(256);
            entity.Property(e => e.Language).HasMaxLength(64);
            entity.Property(e => e.LangCode).HasMaxLength(16);
            entity.Property(e => e.PartOfSpeech).HasMaxLength(32);
            entity.Property(e => e.Source).HasMaxLength(64);
        });

        modelBuilder.Entity<AssistantThread>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.UserId).HasMaxLength(450).IsRequired();
            entity.Property(t => t.Title).HasMaxLength(256);
            entity.HasIndex(t => new { t.QuizId, t.UserId });
            entity.HasOne<Quiz>()
                .WithMany()
                .HasForeignKey(t => t.QuizId)
                .HasConstraintName("FK_AssistantThreads_Quizzes_QuizId")
                .OnDelete(DeleteBehavior.Cascade);
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
            entity.HasOne<AssistantThread>()
                .WithMany()
                .HasForeignKey(m => m.ThreadId)
                .HasConstraintName("FK_AssistantMessages_AssistantThreads_ThreadId")
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
