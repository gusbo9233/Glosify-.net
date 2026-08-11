using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantFeedbackReasonConfiguration : IEntityTypeConfiguration<AssistantFeedbackReason>
{
    public void Configure(EntityTypeBuilder<AssistantFeedbackReason> entity)
    {
        entity.HasKey(reason => new { reason.FeedbackId, reason.ReasonCode });
        entity.Property(reason => reason.ReasonCode).HasMaxLength(32);
        entity.HasOne(reason => reason.Feedback)
            .WithMany(feedback => feedback.Reasons)
            .HasForeignKey(reason => reason.FeedbackId)
            .HasConstraintName("FK_AssistantFeedbackReasons_AssistantFeedback_FeedbackId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
