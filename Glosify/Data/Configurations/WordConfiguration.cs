using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class WordConfiguration : IEntityTypeConfiguration<Word>
{
    public void Configure(EntityTypeBuilder<Word> entity)
    {
        entity.HasKey(w => w.Id);
        entity.HasIndex(w => w.QuizId);
        entity.HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(w => w.QuizId)
            .HasConstraintName("FK_words_quizzes")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
