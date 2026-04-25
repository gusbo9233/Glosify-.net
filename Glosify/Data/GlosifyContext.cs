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
    // public DbSet<User> Users { get; set; }
    // public DbSet<FlashCard> FlashCards { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Word>()
            .HasKey(w => w.Id);

        modelBuilder.Entity<WordDetail>()
            .HasKey(w => w.Id);
    }
}
