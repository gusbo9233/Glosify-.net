using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ClassroomInvitationConfiguration : IEntityTypeConfiguration<ClassroomInvitation>
{
    public void Configure(EntityTypeBuilder<ClassroomInvitation> entity)
    {
        entity.HasKey(i => i.Id);
        entity.Property(i => i.Email).HasMaxLength(256).IsRequired();
        entity.Property(i => i.InvitedByUserId).HasMaxLength(450).IsRequired();
        entity.Property(i => i.AcceptedByUserId).HasMaxLength(450);

        entity.HasIndex(i => i.Email);
        entity.HasIndex(i => new { i.ClassroomId, i.Email })
            .IsUnique()
            .HasFilter("[AcceptedAt] IS NULL");

        entity.HasOne<Classroom>()
            .WithMany()
            .HasForeignKey(i => i.ClassroomId)
            .HasConstraintName("FK_ClassroomInvitations_Classrooms_ClassroomId")
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(i => i.InvitedByUserId)
            .HasConstraintName("FK_ClassroomInvitations_AspNetUsers_InvitedByUserId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
