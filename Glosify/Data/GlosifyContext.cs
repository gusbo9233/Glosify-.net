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

        modelBuilder.Entity<Word>()
            .HasKey(w => w.Id);

        modelBuilder.Entity<WordDetail>()
            .HasKey(w => w.Id);

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
