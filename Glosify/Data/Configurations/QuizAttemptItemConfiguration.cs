using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class QuizAttemptItemConfiguration : IEntityTypeConfiguration<QuizAttemptItem>
{
    public void Configure(EntityTypeBuilder<QuizAttemptItem> entity)
    {
        entity.HasKey(i => i.Id);
        entity.Property(i => i.Prompt).HasMaxLength(512).IsRequired();
        entity.Property(i => i.ExpectedAnswer).HasMaxLength(512).IsRequired();
        entity.Property(i => i.GivenAnswer).HasMaxLength(512);

        entity.HasIndex(i => new { i.QuizAttemptId, i.Sequence });

        entity.HasOne<QuizAttempt>()
            .WithMany(a => a.Items)
            .HasForeignKey(i => i.QuizAttemptId)
            .HasConstraintName("FK_QuizAttemptItems_QuizAttempts_QuizAttemptId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
