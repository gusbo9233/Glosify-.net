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
    // public DbSet<User> Users { get; set; }
    // public DbSet<FlashCard> FlashCards { get; set; }

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
    }
}
