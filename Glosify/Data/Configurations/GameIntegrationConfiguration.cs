using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

public sealed class GamePlayerProfileConfiguration : IEntityTypeConfiguration<GamePlayerProfile>
{
    public void Configure(EntityTypeBuilder<GamePlayerProfile> b)
    {
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).HasMaxLength(450);
        b.Property(x => x.CharacterJson).HasMaxLength(20000);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class GameUsageEventConfiguration : IEntityTypeConfiguration<GameUsageEvent>
{
    public void Configure(EntityTypeBuilder<GameUsageEvent> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(450);
        b.Property(x => x.SessionId).HasMaxLength(64);
        foreach (var name in new[] { "Provider", "Model", "Endpoint", "Operation", "ServiceTier", "Outcome", "Measurement" })
            b.Property<string>(name).HasMaxLength(100);
        b.Property(x => x.AudioSeconds).HasPrecision(18, 3);
        b.Property(x => x.EstimatedUsd).HasPrecision(20, 10);
        b.Property(x => x.RateJson).HasMaxLength(4000);
        b.HasIndex(x => new { x.StartedAt, x.UserId });
        b.HasIndex(x => x.SessionId);
    }
}
public sealed class GamePlaySessionConfiguration : IEntityTypeConfiguration<GamePlaySession>
{
    public void Configure(EntityTypeBuilder<GamePlaySession> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64);
        b.Property(x => x.UserId).HasMaxLength(450);
        b.HasIndex(x => new { x.StartedAt, x.UserId });
    }
}
