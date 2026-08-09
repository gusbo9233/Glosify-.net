using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class BookPageTranslationConfiguration : IEntityTypeConfiguration<BookPageTranslation>
{
    public void Configure(EntityTypeBuilder<BookPageTranslation> entity)
    {
        entity.HasKey(translation => translation.Id);
        entity.Property(translation => translation.TargetLanguage).HasMaxLength(64).IsRequired();
        entity.Property(translation => translation.SourceTextHash).HasMaxLength(64).IsRequired();
        entity.Property(translation => translation.DetectedSourceLanguage).HasMaxLength(64);
        entity.Property(translation => translation.Model).HasMaxLength(128).IsRequired();
        entity.Property(translation => translation.SegmentsJson).IsRequired();

        entity.HasIndex(translation => new
        {
            translation.BookPageId,
            translation.TargetLanguage,
            translation.SourceTextHash,
            translation.SchemaVersion,
        }).IsUnique();

        entity.HasOne(translation => translation.BookPage)
            .WithMany(page => page.Translations)
            .HasForeignKey(translation => translation.BookPageId)
            .HasConstraintName("FK_BookPageTranslations_BookPages_BookPageId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
