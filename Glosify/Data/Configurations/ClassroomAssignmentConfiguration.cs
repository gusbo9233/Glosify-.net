using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ClassroomAssignmentConfiguration : IEntityTypeConfiguration<ClassroomAssignment>
{
    public void Configure(EntityTypeBuilder<ClassroomAssignment> entity)
    {
        entity.HasKey(a => a.Id);
        entity.Property(a => a.Title).HasMaxLength(160).IsRequired();
        entity.Property(a => a.Instructions).HasMaxLength(2000);
        entity.Property(a => a.CreatedByUserId).HasMaxLength(450).IsRequired();

        entity.HasIndex(a => new { a.ClassroomId, a.DueAt });
        entity.HasIndex(a => a.LessonId);

        entity.HasOne<Classroom>()
            .WithMany()
            .HasForeignKey(a => a.ClassroomId)
            .HasConstraintName("FK_ClassroomAssignments_Classrooms_ClassroomId")
            .OnDelete(DeleteBehavior.Cascade);

        // NoAction because lessons already cascade from the classroom (a second
        // cascade path would be rejected); the service nulls LessonId on lesson delete.
        entity.HasOne<ClassroomLesson>()
            .WithMany()
            .HasForeignKey(a => a.LessonId)
            .HasConstraintName("FK_ClassroomAssignments_ClassroomLessons_LessonId")
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(a => a.QuizId)
            .HasConstraintName("FK_ClassroomAssignments_Quizzes_QuizId")
            .OnDelete(DeleteBehavior.NoAction);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(a => a.CreatedByUserId)
            .HasConstraintName("FK_ClassroomAssignments_AspNetUsers_CreatedByUserId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
