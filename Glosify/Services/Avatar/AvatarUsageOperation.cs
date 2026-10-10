using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Services.Avatar;

// Only accounting metadata is durable. No audio or conversation text is stored here.
public sealed class AvatarUsageOperation
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public Guid SessionId { get; set; }
    public string Kind { get; set; } = "";
    public string PeriodKey { get; set; } = "";
    public decimal CreditRate { get; set; }
    public decimal ProviderSekRate { get; set; }
    public decimal ReservedUnits { get; set; }
    public decimal SubmittedUnits { get; set; }
    public decimal ReservedCredits { get; set; }
    public long ReservedMicros { get; set; }
    public bool Settled { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class AvatarUsageOperationConfiguration : IEntityTypeConfiguration<AvatarUsageOperation>
{
    public void Configure(EntityTypeBuilder<AvatarUsageOperation> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(450);
        b.Property(x => x.Kind).HasMaxLength(20);
        b.Property(x => x.PeriodKey).HasMaxLength(7);
        b.Property(x => x.CreditRate).HasPrecision(28, 15);
        b.Property(x => x.ProviderSekRate).HasPrecision(28, 15);
        b.Property(x => x.ReservedUnits).HasPrecision(19, 6);
        b.Property(x => x.SubmittedUnits).HasPrecision(19, 6);
        b.Property(x => x.ReservedCredits).HasPrecision(19, 6);
        b.HasIndex(x => new { x.Settled, x.ExpiresAt });
        b.HasIndex(x => new { x.UserId, x.SessionId });
    }
}
