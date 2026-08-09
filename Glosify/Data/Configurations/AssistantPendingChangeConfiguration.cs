using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantPendingChangeConfiguration : IEntityTypeConfiguration<AssistantPendingChange>
{
    public void Configure(EntityTypeBuilder<AssistantPendingChange> entity)
    {
        entity.HasKey(change => change.Id);
        entity.Property(change => change.UserId).HasMaxLength(450).IsRequired();
        entity.Property(change => change.Kind).HasMaxLength(64).IsRequired();
        entity.Property(change => change.Status).HasMaxLength(16).IsRequired();
        entity.Property(change => change.PayloadJson).IsRequired();
        // Apply reads a whole conversation in call order.
        entity.HasIndex(change => new { change.ConversationId, change.Sequence }).IsUnique();
        entity.HasIndex(change => new { change.UserId, change.Status });
        entity.HasIndex(change => change.MessageId);
        // No foreign key to assistant_messages: a tool call can land here before the
        // response that owns it has been persisted.
        entity.HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(change => change.ContextQuizId)
            .HasConstraintName("FK_AssistantPendingChanges_Quizzes_ContextQuizId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
