using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ClassroomConfiguration : IEntityTypeConfiguration<Classroom>
{
    public void Configure(EntityTypeBuilder<Classroom> entity)
    {
        entity.HasKey(c => c.Id);
        entity.Property(c => c.OwnerUserId).HasMaxLength(450).IsRequired();
        entity.Property(c => c.Name).HasMaxLength(160).IsRequired();
        entity.Property(c => c.Description).HasMaxLength(1024);
        entity.Property(c => c.Language).HasMaxLength(64);
        entity.Property(c => c.JoinCode).HasMaxLength(8).IsRequired();

        entity.HasIndex(c => c.OwnerUserId);
        entity.HasIndex(c => c.JoinCode).IsUnique();

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(c => c.OwnerUserId)
            .HasConstraintName("FK_Classrooms_AspNetUsers_OwnerUserId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
