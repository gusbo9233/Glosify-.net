using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class StripeCreditPurchaseConfiguration : IEntityTypeConfiguration<StripeCreditPurchase>
{
    public void Configure(EntityTypeBuilder<StripeCreditPurchase> entity)
    {
        entity.HasKey(purchase => purchase.Id);
        entity.Property(purchase => purchase.UserId).HasMaxLength(450).IsRequired();
        entity.Property(purchase => purchase.PackageKey).HasMaxLength(64).IsRequired();
        entity.Property(purchase => purchase.PriceId).HasMaxLength(128).IsRequired();
        entity.Property(purchase => purchase.Currency).HasMaxLength(3).IsRequired();
        entity.Property(purchase => purchase.DisplayName).HasMaxLength(128).IsRequired();
        entity.Property(purchase => purchase.Status).HasMaxLength(32).IsRequired();
        entity.Property(purchase => purchase.StripeCheckoutSessionId).HasMaxLength(255);
        entity.Property(purchase => purchase.StripePaymentIntentId).HasMaxLength(255);
        entity.Property(purchase => purchase.LastWebhookEventId).HasMaxLength(255);
        entity.Property(purchase => purchase.RowVersion).IsRowVersion();
        entity.HasIndex(purchase => purchase.StripeCheckoutSessionId)
            .IsUnique()
            .HasFilter("[StripeCheckoutSessionId] IS NOT NULL");
        entity.HasIndex(purchase => new { purchase.UserId, purchase.CreatedAt });
        entity.HasIndex(purchase => purchase.StripePaymentIntentId)
            .IsUnique()
            .HasFilter("[StripePaymentIntentId] IS NOT NULL");

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(purchase => purchase.UserId)
            .HasConstraintName("FK_StripeCreditPurchases_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
