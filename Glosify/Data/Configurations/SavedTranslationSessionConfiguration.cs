using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class SavedTranslationSessionConfiguration
    : IEntityTypeConfiguration<SavedTranslationSession>
{
    public void Configure(EntityTypeBuilder<SavedTranslationSession> entity)
    {
        entity.HasKey(session => session.Id);
        entity.Property(session => session.UserId).HasMaxLength(450).IsRequired();
        entity.Property(session => session.LanguageCode).HasMaxLength(8).IsRequired();
        entity.Property(session => session.Title).HasMaxLength(160).IsRequired();
        entity.HasIndex(session => new
        {
            session.UserId,
            session.ClientSessionId,
            session.LanguageCode,
        }).IsUnique();
        entity.HasIndex(session => new { session.UserId, session.LanguageCode, session.UpdatedAt });

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .HasConstraintName("FK_SavedTranslationSessions_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
