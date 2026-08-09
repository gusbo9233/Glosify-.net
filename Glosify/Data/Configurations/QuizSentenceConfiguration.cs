using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class QuizSentenceConfiguration : IEntityTypeConfiguration<QuizSentence>
{
    public void Configure(EntityTypeBuilder<QuizSentence> entity)
    {
        entity.HasKey(sentence => sentence.Id);
        entity.Property(sentence => sentence.Text).IsRequired();
        entity.Property(sentence => sentence.Translation).IsRequired();
        entity.HasIndex(sentence => sentence.QuizId);
        entity.HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(sentence => sentence.QuizId)
            .HasConstraintName("FK_quiz_sentences_quizzes")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
