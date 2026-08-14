using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class StripePaymentEventConfiguration : IEntityTypeConfiguration<StripePaymentEvent>
{
    public void Configure(EntityTypeBuilder<StripePaymentEvent> entity)
    {
        entity.HasKey(item => item.IdempotencyKey);
        entity.Property(item => item.IdempotencyKey).HasMaxLength(255);
        entity.Property(item => item.StripeEventId).HasMaxLength(255).IsRequired();
        entity.Property(item => item.Type).HasMaxLength(64).IsRequired();
        entity.HasIndex(item => item.StripeEventId);

        entity.HasOne<StripeCreditPurchase>()
            .WithMany()
            .HasForeignKey(item => item.PurchaseId)
            .HasConstraintName("FK_StripePaymentEvents_StripeCreditPurchases_PurchaseId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
