using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class RealtimeTranslationCaptureEventConfiguration
    : IEntityTypeConfiguration<RealtimeTranslationCaptureEvent>
{
    public void Configure(EntityTypeBuilder<RealtimeTranslationCaptureEvent> entity)
    {
        entity.HasKey(capture => capture.Id);
        entity.Property(capture => capture.Stage).HasMaxLength(16).IsRequired();
        entity.Property(capture => capture.Kind).HasMaxLength(16).IsRequired();
        entity.Property(capture => capture.Text).IsRequired();
        entity.Property(capture => capture.SourceLanguage).HasMaxLength(16);
        entity.Property(capture => capture.TargetLanguage).HasMaxLength(16);
        entity.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_RealtimeTranslationCaptureEvents_Stage",
                "[Stage] IN ('scribe', 'translator', 'bubble')");
            table.HasCheckConstraint(
                "CK_RealtimeTranslationCaptureEvents_Kind",
                "[Kind] IN ('partial', 'final')");
        });
        entity.HasIndex(capture => new { capture.SessionId, capture.Ordinal }).IsUnique();
        entity.HasIndex(capture => new { capture.SessionId, capture.Stage, capture.CapturedAt });

        entity.HasOne(capture => capture.Session)
            .WithMany(session => session.CaptureEvents)
            .HasForeignKey(capture => capture.SessionId)
            .HasConstraintName("FK_RealtimeTranslationCaptureEvents_RealtimeTranslationSessions_SessionId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
