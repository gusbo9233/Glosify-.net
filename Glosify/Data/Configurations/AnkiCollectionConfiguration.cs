using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AnkiCollectionConfiguration : IEntityTypeConfiguration<AnkiCollection>
{
    public void Configure(EntityTypeBuilder<AnkiCollection> entity)
    {
        entity.HasKey(collection => collection.Id);
        entity.Property(collection => collection.UserId).HasMaxLength(450).IsRequired();
        entity.Property(collection => collection.Name).HasMaxLength(160).IsRequired();
        entity.Property(collection => collection.SourceLanguage).HasMaxLength(64).IsRequired();
        entity.Property(collection => collection.TargetLanguage).HasMaxLength(64).IsRequired();
        entity.Property(collection => collection.DefaultDirection).HasMaxLength(32).IsRequired();
        entity.Property(collection => collection.TimeZoneId).HasMaxLength(128).IsRequired();
        entity.HasIndex(collection => new { collection.UserId, collection.SourceLanguage, collection.TargetLanguage });
        entity.HasIndex(collection => new { collection.UserId, collection.Name }).IsUnique();

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(collection => collection.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
