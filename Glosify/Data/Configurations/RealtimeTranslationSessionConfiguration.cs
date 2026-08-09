using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class RealtimeTranslationSessionConfiguration : IEntityTypeConfiguration<RealtimeTranslationSession>
{
    public void Configure(EntityTypeBuilder<RealtimeTranslationSession> entity)
    {
        entity.HasKey(session => session.Id);
        entity.Property(session => session.UserId).HasMaxLength(450).IsRequired();
        entity.Property(session => session.TargetLanguage).HasMaxLength(16).IsRequired();
        entity.Property(session => session.Model).HasMaxLength(128).IsRequired();
        entity.Property(session => session.SourceTranscriptionDeployment).HasMaxLength(128);
        entity.Property(session => session.BillingModel).HasMaxLength(256).IsRequired();
        entity.Property(session => session.Status).HasMaxLength(32).IsRequired();
        entity.Property(session => session.RowVersion).IsRowVersion();
        entity.HasIndex(session => new { session.UserId, session.CreatedAt });
        entity.HasIndex(session => session.UserId)
            .IsUnique()
            .HasFilter("[Status] IN ('pending', 'active')");

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .HasConstraintName("FK_RealtimeTranslationSessions_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(session => session.Transcript)
            .WithMany(transcript => transcript.Sessions)
            .HasForeignKey(session => session.TranscriptId)
            .HasConstraintName("FK_RealtimeTranslationSessions_RealtimeTranslationTranscripts_TranscriptId")
            .OnDelete(DeleteBehavior.Restrict);

    }
}
