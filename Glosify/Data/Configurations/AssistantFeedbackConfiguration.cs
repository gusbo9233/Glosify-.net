using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantFeedbackConfiguration : IEntityTypeConfiguration<AssistantFeedback>
{
    public void Configure(EntityTypeBuilder<AssistantFeedback> entity)
    {
        entity.HasKey(feedback => feedback.Id);
        entity.Property(feedback => feedback.Rating).HasMaxLength(8).IsRequired();
        entity.Property(feedback => feedback.Comment).HasMaxLength(1000);
        entity.HasIndex(feedback => feedback.TurnId).IsUnique();

        entity.HasOne<AssistantTurn>()
            .WithMany()
            .HasForeignKey(feedback => feedback.TurnId)
            .HasConstraintName("FK_AssistantFeedback_AssistantTurns_TurnId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
