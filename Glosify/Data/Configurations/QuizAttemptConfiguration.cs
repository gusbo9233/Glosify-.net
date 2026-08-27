using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class QuizAttemptConfiguration : IEntityTypeConfiguration<QuizAttempt>
{
    public void Configure(EntityTypeBuilder<QuizAttempt> entity)
    {
        entity.HasKey(a => a.Id);
        entity.Property(a => a.UserId).HasMaxLength(450).IsRequired();
        entity.Property(a => a.Mode).HasMaxLength(200).IsRequired();
        entity.Property(a => a.PracticeDirection).HasMaxLength(32);
        entity.Property(a => a.PracticeItemType).HasMaxLength(32);

        entity.HasIndex(a => new { a.UserId, a.CompletedAt });

        entity.HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(a => a.QuizId)
            .HasConstraintName("FK_QuizAttempts_Quizzes_QuizId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .HasConstraintName("FK_QuizAttempts_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
