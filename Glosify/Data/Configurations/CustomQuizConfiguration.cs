using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class CustomQuizConfiguration : IEntityTypeConfiguration<CustomQuiz>
{
    public void Configure(EntityTypeBuilder<CustomQuiz> entity)
    {
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
        entity.Property(item => item.DefinitionJson).IsRequired();
        entity.Property(item => item.RowVersion).IsRowVersion();
        entity.HasIndex(item => new { item.QuizId, item.Name }).IsUnique();
        entity.HasIndex(item => new { item.QuizId, item.IsPlayable });
        entity.HasOne(item => item.Quiz)
            .WithMany()
            .HasForeignKey(item => item.QuizId)
            .HasConstraintName("FK_CustomQuizzes_Quizzes_QuizId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
