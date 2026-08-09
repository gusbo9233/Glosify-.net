using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class BookPageConfiguration : IEntityTypeConfiguration<BookPage>
{
    public void Configure(EntityTypeBuilder<BookPage> entity)
    {
        entity.HasKey(p => p.Id);
        entity.Property(p => p.Text).IsRequired();
        entity.Property(p => p.ExtractionWarning).HasMaxLength(512);

        entity.HasIndex(p => new { p.BookDocumentId, p.PageNumber }).IsUnique();

        entity.HasOne(p => p.BookDocument)
            .WithMany(b => b.Pages)
            .HasForeignKey(p => p.BookDocumentId)
            .HasConstraintName("FK_BookPages_BookDocuments_BookDocumentId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
