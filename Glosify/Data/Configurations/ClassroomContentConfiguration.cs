using Glosify.Models.Entities;
using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ClassroomContentConfiguration : IEntityTypeConfiguration<ClassroomContent>
{
    public void Configure(EntityTypeBuilder<ClassroomContent> entity)
    {
        entity.HasKey(c => c.Id);
        entity.Property(c => c.SharedByUserId).HasMaxLength(450).IsRequired();
        entity.Property(c => c.Note).HasMaxLength(512);

        entity.HasIndex(c => new { c.ClassroomId, c.SharedAt });
        entity.HasIndex(c => new { c.ClassroomId, c.QuizId })
            .IsUnique()
            .HasFilter("[QuizId] IS NOT NULL");
        entity.HasIndex(c => new { c.ClassroomId, c.BookDocumentId })
            .IsUnique()
            .HasFilter("[BookDocumentId] IS NOT NULL");

        entity.HasOne<Classroom>()
            .WithMany()
            .HasForeignKey(c => c.ClassroomId)
            .HasConstraintName("FK_ClassroomContents_Classrooms_ClassroomId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(c => c.QuizId)
            .HasConstraintName("FK_ClassroomContents_Quizzes_QuizId")
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasOne<BookDocument>()
            .WithMany()
            .HasForeignKey(c => c.BookDocumentId)
            .HasConstraintName("FK_ClassroomContents_BookDocuments_BookDocumentId")
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(c => c.SharedByUserId)
            .HasConstraintName("FK_ClassroomContents_AspNetUsers_SharedByUserId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
