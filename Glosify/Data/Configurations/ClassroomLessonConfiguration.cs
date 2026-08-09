using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ClassroomLessonConfiguration : IEntityTypeConfiguration<ClassroomLesson>
{
    public void Configure(EntityTypeBuilder<ClassroomLesson> entity)
    {
        entity.HasKey(l => l.Id);
        entity.Property(l => l.Title).HasMaxLength(160).IsRequired();
        entity.Property(l => l.Description).HasMaxLength(2000);
        entity.Property(l => l.CreatedByUserId).HasMaxLength(450).IsRequired();

        entity.HasIndex(l => new { l.ClassroomId, l.ScheduledAt });

        entity.HasOne<Classroom>()
            .WithMany()
            .HasForeignKey(l => l.ClassroomId)
            .HasConstraintName("FK_ClassroomLessons_Classrooms_ClassroomId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(l => l.CreatedByUserId)
            .HasConstraintName("FK_ClassroomLessons_AspNetUsers_CreatedByUserId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
