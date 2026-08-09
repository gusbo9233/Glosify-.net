using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class QuizConfiguration : IEntityTypeConfiguration<Quiz>
{
    public void Configure(EntityTypeBuilder<Quiz> entity)
    {
        entity.HasKey(q => q.Id);

        entity.Property(q => q.Name).HasMaxLength(160).IsRequired();
        entity.Property(q => q.UserId).HasMaxLength(450).IsRequired();
        entity.Property(q => q.SourceLanguage).HasMaxLength(64).IsRequired();
        entity.Property(q => q.TargetLanguage).HasMaxLength(64).IsRequired();
        entity.Property(q => q.Language).HasMaxLength(64);
        entity.Property(q => q.ProcessingStatus).HasMaxLength(64);
        entity.Property(q => q.ProcessingMessage).HasMaxLength(512);

        entity.HasIndex(q => q.UserId);
        entity.HasIndex(q => q.CollectionId);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(q => q.UserId)
            .HasConstraintName("FK_Quizzes_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(q => q.Collection)
            .WithMany(c => c.Quizzes)
            .HasForeignKey(q => q.CollectionId)
            .HasConstraintName("FK_Quizzes_Collections_CollectionId")
            // A user owns both collections and quizzes. Cascading through both paths
            // would give SQL Server multiple cascade paths, so collection deletion is
            // handled explicitly by the application before the collection is removed.
            .OnDelete(DeleteBehavior.NoAction);

    }
}
