using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantMessageConfiguration : IEntityTypeConfiguration<AssistantMessage>
{
    public void Configure(EntityTypeBuilder<AssistantMessage> entity)
    {
        entity.HasKey(m => m.Id);
        entity.Property(m => m.Role).HasMaxLength(16).IsRequired();
        entity.Property(m => m.Status).HasMaxLength(16).IsRequired().IsConcurrencyToken();
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

    }
}
