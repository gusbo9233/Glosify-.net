using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AnkiCardConfiguration : IEntityTypeConfiguration<AnkiCard>
{
    public void Configure(EntityTypeBuilder<AnkiCard> entity)
    {
        entity.HasKey(card => card.Id);
        entity.Property(card => card.Direction).HasMaxLength(32).IsRequired();
        entity.Property(card => card.State).HasMaxLength(32).IsRequired();
        entity.Property(card => card.RowVersion).IsRowVersion();
        entity.HasIndex(card => new { card.AnkiNoteId, card.Direction }).IsUnique();
        entity.HasIndex(card => new { card.IsActive, card.State, card.DueAt });
        entity.HasOne(card => card.Note)
            .WithMany(note => note.Cards)
            .HasForeignKey(card => card.AnkiNoteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
