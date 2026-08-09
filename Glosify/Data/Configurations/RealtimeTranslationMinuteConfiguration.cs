using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class RealtimeTranslationMinuteConfiguration : IEntityTypeConfiguration<RealtimeTranslationMinute>
{
    public void Configure(EntityTypeBuilder<RealtimeTranslationMinute> entity)
    {
        entity.HasKey(minute => minute.Id);
        entity.Property(minute => minute.Status).HasMaxLength(32).IsRequired();
        entity.Property(minute => minute.RowVersion).IsRowVersion();
        entity.HasIndex(minute => new { minute.SessionId, minute.MinuteIndex }).IsUnique();
        entity.HasIndex(minute => minute.ReservationId).IsUnique();

        entity.HasOne(minute => minute.Session)
            .WithMany(session => session.Minutes)
            .HasForeignKey(minute => minute.SessionId)
            .HasConstraintName("FK_RealtimeTranslationMinutes_RealtimeTranslationSessions_SessionId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
