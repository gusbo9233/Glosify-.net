using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class RealtimeTranslationTranscriptConfiguration : IEntityTypeConfiguration<RealtimeTranslationTranscript>
{
    public void Configure(EntityTypeBuilder<RealtimeTranslationTranscript> entity)
    {
        entity.HasKey(transcript => transcript.Id);
        entity.Property(transcript => transcript.UserId).HasMaxLength(450).IsRequired();
        entity.Property(transcript => transcript.Title).HasMaxLength(160).IsRequired();
        entity.Property(transcript => transcript.TargetLanguage).HasMaxLength(16).IsRequired();
        entity.Property(transcript => transcript.Stream).HasMaxLength(16).IsRequired();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_RealtimeTranslationTranscripts_Stream",
            "[Stream] IN ('translation', 'source')"));
        entity.HasIndex(transcript => new { transcript.UserId, transcript.TargetLanguage, transcript.UpdatedAt });

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(transcript => transcript.UserId)
            .HasConstraintName("FK_RealtimeTranslationTranscripts_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
