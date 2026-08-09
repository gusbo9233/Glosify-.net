using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ClassroomMembershipConfiguration : IEntityTypeConfiguration<ClassroomMembership>
{
    public void Configure(EntityTypeBuilder<ClassroomMembership> entity)
    {
        entity.HasKey(m => m.Id);
        entity.Property(m => m.UserId).HasMaxLength(450).IsRequired();

        entity.HasIndex(m => new { m.ClassroomId, m.UserId }).IsUnique();
        entity.HasIndex(m => m.UserId);

        entity.HasOne<Classroom>()
            .WithMany()
            .HasForeignKey(m => m.ClassroomId)
            .HasConstraintName("FK_ClassroomMemberships_Classrooms_ClassroomId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .HasConstraintName("FK_ClassroomMemberships_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
