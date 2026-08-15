using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AnkiReviewConfiguration : IEntityTypeConfiguration<AnkiReview>
{
    public void Configure(EntityTypeBuilder<AnkiReview> entity)
    {
        entity.HasKey(review => review.Id);
        entity.Property(review => review.Rating).HasMaxLength(16).IsRequired();
        entity.Property(review => review.PreviousState).HasMaxLength(32).IsRequired();
        entity.Property(review => review.NewState).HasMaxLength(32).IsRequired();
        entity.Property(review => review.Prompt).HasMaxLength(4000).IsRequired();
        entity.Property(review => review.Answer).HasMaxLength(4000).IsRequired();
        entity.Property(review => review.SchedulerVersion).HasMaxLength(32).IsRequired();
        entity.HasIndex(review => review.ClientToken).IsUnique();
        entity.HasIndex(review => new { review.AnkiCollectionId, review.ReviewedAt });
        entity.HasOne(review => review.Collection)
            .WithMany(collection => collection.Reviews)
            .HasForeignKey(review => review.AnkiCollectionId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(review => review.Card)
            .WithMany(card => card.Reviews)
            .HasForeignKey(review => review.AnkiCardId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
