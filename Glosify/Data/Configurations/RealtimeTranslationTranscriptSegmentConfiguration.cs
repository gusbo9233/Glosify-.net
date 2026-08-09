using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class RealtimeTranslationTranscriptSegmentConfiguration : IEntityTypeConfiguration<RealtimeTranslationTranscriptSegment>
{
    public void Configure(EntityTypeBuilder<RealtimeTranslationTranscriptSegment> entity)
    {
        entity.HasKey(segment => segment.Id);
        entity.Property(segment => segment.ProviderEventKey).HasMaxLength(256).IsRequired();
        entity.Property(segment => segment.Text).IsRequired();
        entity.Property(segment => segment.Stream).HasMaxLength(16).IsRequired();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_RealtimeTranslationTranscriptSegments_Stream",
            "[Stream] IN ('translation', 'source')"));
        // Both streams of one session accumulate sequences and provider keys
        // independently, so uniqueness only holds within a stream.
        entity.HasIndex(segment => new { segment.SessionId, segment.Stream, segment.Sequence }).IsUnique();
        entity.HasIndex(segment => new { segment.SessionId, segment.Stream, segment.ProviderEventKey }).IsUnique();
        entity.HasIndex(segment => new { segment.TranscriptId, segment.CapturedAt });
        entity.HasIndex(segment => new { segment.TranscriptId, segment.Stream, segment.CapturedAt });

        entity.HasOne(segment => segment.Transcript)
            .WithMany(transcript => transcript.Segments)
            .HasForeignKey(segment => segment.TranscriptId)
            .HasConstraintName("FK_RealtimeTranslationTranscriptSegments_RealtimeTranslationTranscripts_TranscriptId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
