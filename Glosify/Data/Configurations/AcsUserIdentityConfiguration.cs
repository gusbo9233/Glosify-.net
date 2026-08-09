using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AcsUserIdentityConfiguration : IEntityTypeConfiguration<AcsUserIdentity>
{
    public void Configure(EntityTypeBuilder<AcsUserIdentity> entity)
    {
        entity.HasKey(a => a.UserId);
        entity.Property(a => a.UserId).HasMaxLength(450).IsRequired();
        entity.Property(a => a.AcsUserId).HasMaxLength(256).IsRequired();

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .HasConstraintName("FK_AcsUserIdentities_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
