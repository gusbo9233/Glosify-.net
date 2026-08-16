using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AnkiQuizLinkConfiguration : IEntityTypeConfiguration<AnkiQuizLink>
{
    public void Configure(EntityTypeBuilder<AnkiQuizLink> entity)
    {
        entity.HasKey(link => link.Id);
        entity.HasIndex(link => new { link.AnkiCollectionId, link.QuizId }).IsUnique();
        entity.HasOne(link => link.Collection)
            .WithMany(collection => collection.QuizLinks)
            .HasForeignKey(link => link.AnkiCollectionId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(link => link.Quiz)
            .WithMany()
            .HasForeignKey(link => link.QuizId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
