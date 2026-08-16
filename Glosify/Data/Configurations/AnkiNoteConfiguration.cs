using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AnkiNoteConfiguration : IEntityTypeConfiguration<AnkiNote>
{
    public void Configure(EntityTypeBuilder<AnkiNote> entity)
    {
        entity.HasKey(note => note.Id);
        entity.Property(note => note.ItemType).HasMaxLength(32).IsRequired();
        entity.Property(note => note.WordId).HasMaxLength(450);
        entity.Property(note => note.TargetText).HasMaxLength(4000).IsRequired();
        entity.Property(note => note.SourceText).HasMaxLength(4000).IsRequired();
        entity.HasIndex(note => new { note.AnkiCollectionId, note.WordId })
            .IsUnique()
            .HasFilter("[WordId] IS NOT NULL");
        entity.HasIndex(note => new { note.AnkiCollectionId, note.SentenceId })
            .IsUnique()
            .HasFilter("[SentenceId] IS NOT NULL");
        entity.HasIndex(note => note.QuizId);
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_AnkiNotes_OneSource",
            "([WordId] IS NOT NULL AND [SentenceId] IS NULL) OR ([WordId] IS NULL AND [SentenceId] IS NOT NULL)"));
        entity.HasOne(note => note.Collection)
            .WithMany(collection => collection.Notes)
            .HasForeignKey(note => note.AnkiCollectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
