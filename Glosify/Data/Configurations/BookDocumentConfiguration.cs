using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class BookDocumentConfiguration : IEntityTypeConfiguration<BookDocument>
{
    public void Configure(EntityTypeBuilder<BookDocument> entity)
    {
        entity.HasKey(b => b.Id);
        entity.Property(b => b.UserId).HasMaxLength(450).IsRequired();
        entity.Property(b => b.Title).HasMaxLength(256).IsRequired();
        entity.Property(b => b.OriginalFileName).HasMaxLength(512).IsRequired();
        entity.Property(b => b.BlobName).HasMaxLength(1024).IsRequired();
        entity.Property(b => b.ProcessingStatus).HasMaxLength(64).IsRequired();
        entity.Property(b => b.ProcessingMessage).HasMaxLength(512);
        entity.Property(b => b.PreferredTranslationLanguage).HasMaxLength(64);
        entity.Property(b => b.Language).HasMaxLength(64);

        entity.HasIndex(b => b.UserId);
        entity.HasIndex(b => new { b.UserId, b.CreatedAt });
        entity.HasIndex(b => new { b.UserId, b.Language });

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .HasConstraintName("FK_BookDocuments_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
