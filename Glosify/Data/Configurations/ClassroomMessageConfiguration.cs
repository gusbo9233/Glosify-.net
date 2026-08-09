using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ClassroomMessageConfiguration : IEntityTypeConfiguration<ClassroomMessage>
{
    public void Configure(EntityTypeBuilder<ClassroomMessage> entity)
    {
        entity.HasKey(m => m.Id);
        entity.Property(m => m.UserId).HasMaxLength(450).IsRequired();
        entity.Property(m => m.Body).HasMaxLength(4000).IsRequired();

        entity.HasIndex(m => new { m.ClassroomId, m.Kind, m.CreatedAt });

        entity.HasOne<Classroom>()
            .WithMany()
            .HasForeignKey(m => m.ClassroomId)
            .HasConstraintName("FK_ClassroomMessages_Classrooms_ClassroomId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .HasConstraintName("FK_ClassroomMessages_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
