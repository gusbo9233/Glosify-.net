using Glosify.Services.Abuse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ResourceAccountingConfiguration : IEntityTypeConfiguration<ResourceUsage>,
    IEntityTypeConfiguration<ResourceEntry>, IEntityTypeConfiguration<ResourceReservation>,
    IEntityTypeConfiguration<BlobCleanupRequest>, IEntityTypeConfiguration<SignupBucket>,
    IEntityTypeConfiguration<ResourceAccountingState>
{
    public void Configure(EntityTypeBuilder<ResourceUsage> b)
    {
        b.HasKey(x => new { x.Scope, x.Resource });
        b.Property(x => x.Scope).HasMaxLength(450);
        b.Property(x => x.Resource).HasMaxLength(128);
    }
    public void Configure(EntityTypeBuilder<ResourceEntry> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.Id).HasMaxLength(64);
        b.Property(x => x.UserId).HasMaxLength(450);
        b.Property(x => x.EntityType).HasMaxLength(128);
        b.HasIndex(x => x.UserId);
    }
    public void Configure(EntityTypeBuilder<ResourceReservation> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.UserId).HasMaxLength(450);
        b.Property(x => x.BlobName).HasMaxLength(1024);
        b.HasIndex(x => x.UserId); b.HasIndex(x => x.ExpiresAt);
    }
    public void Configure(EntityTypeBuilder<BlobCleanupRequest> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.UserId).HasMaxLength(450);
        b.Property(x => x.BlobName).HasMaxLength(1024);
        b.HasIndex(x => x.BlobName).IsUnique();
    }
    public void Configure(EntityTypeBuilder<SignupBucket> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.Id).HasMaxLength(128);
        b.HasIndex(x => x.ExpiresAt);
    }
    public void Configure(EntityTypeBuilder<ResourceAccountingState> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
    }
}
