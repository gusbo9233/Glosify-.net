using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class SavedTranslationConfiguration : IEntityTypeConfiguration<SavedTranslation>
{
    public void Configure(EntityTypeBuilder<SavedTranslation> entity)
    {
        entity.HasKey(translation => translation.Id);
        entity.Property(translation => translation.UserId).HasMaxLength(450).IsRequired();
        entity.Property(translation => translation.SourceLanguage).HasMaxLength(8).IsRequired();
        entity.Property(translation => translation.DetectedSourceLanguage).HasMaxLength(8);
        entity.Property(translation => translation.TargetLanguage).HasMaxLength(8).IsRequired();
        entity.Property(translation => translation.Preferences).HasMaxLength(500);
        entity.Property(translation => translation.SourceText).HasMaxLength(8_000).IsRequired();
        entity.Property(translation => translation.TranslatedText).HasMaxLength(16_000).IsRequired();
        entity.HasIndex(translation => new { translation.UserId, translation.RequestId }).IsUnique();
        entity.HasIndex(translation => new
        {
            translation.UserId,
            translation.TranslationOperationId,
        })
            .IsUnique()
            .HasFilter("[TranslationOperationId] IS NOT NULL");
        entity.HasIndex(translation => new { translation.UserId, translation.CreatedAt });
        entity.HasIndex(translation => new { translation.SessionId, translation.CreatedAt });

        entity.HasOne(translation => translation.Session)
            .WithMany(session => session.Translations)
            .HasForeignKey(translation => translation.SessionId)
            .HasConstraintName("FK_SavedTranslations_SavedTranslationSessions_SessionId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(translation => translation.UserId)
            .HasConstraintName("FK_SavedTranslations_AspNetUsers_UserId")
            // Account deletion cascades through SavedTranslationSession. Keeping this
            // ownership FK as NO ACTION avoids SQL Server's multiple-cascade-path rule.
            .OnDelete(DeleteBehavior.NoAction);
    }
}
