using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> entity)
    {
        entity.HasKey(c => c.Id);
        entity.Property(c => c.UserId).HasMaxLength(450).IsRequired();
        entity.Property(c => c.Name).HasMaxLength(160).IsRequired();
        entity.Property(c => c.Language).HasMaxLength(64).IsRequired();

        entity.HasIndex(c => new { c.UserId, c.Language, c.ParentCollectionId, c.Name });
        entity.HasIndex(c => new { c.IsPublic, c.Language });
        entity.HasIndex(c => c.OriginalCollectionId);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(c => c.ParentCollection)
            .WithMany(c => c.ChildCollections)
            .HasForeignKey(c => c.ParentCollectionId)
            .OnDelete(DeleteBehavior.Restrict);

    }
}
