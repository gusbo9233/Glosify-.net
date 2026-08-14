using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AiCreditTransactionConfiguration : IEntityTypeConfiguration<AiCreditTransaction>
{
    public void Configure(EntityTypeBuilder<AiCreditTransaction> entity)
    {
        entity.HasKey(transaction => transaction.Id);
        entity.Property(transaction => transaction.UserId).HasMaxLength(450).IsRequired();
        entity.Property(transaction => transaction.Kind).HasMaxLength(32).IsRequired();
        entity.Property(transaction => transaction.Provider).HasMaxLength(64);
        entity.Property(transaction => transaction.Model).HasMaxLength(128);
        entity.Property(transaction => transaction.Feature).HasMaxLength(64);
        entity.Property(transaction => transaction.Operation).HasMaxLength(128);
        entity.Property(transaction => transaction.ActorUserId).HasMaxLength(450);
        entity.Property(transaction => transaction.Note).HasMaxLength(512);
        entity.Property(transaction => transaction.RelatedEntityType).HasMaxLength(64);
        entity.Property(transaction => transaction.RelatedEntityId).HasMaxLength(255);
        entity.Property(transaction => transaction.BudgetPeriodKey).HasMaxLength(7);
        entity.HasIndex(transaction => new { transaction.UserId, transaction.CreatedAt });
        entity.HasIndex(transaction => transaction.ReservationId);
        entity.HasIndex(transaction => transaction.OperationId);
        entity.HasIndex(transaction => transaction.AssistantTurnId);
        entity.HasIndex(transaction => transaction.BudgetPeriodKey);
        entity.HasIndex(transaction => new
        {
            transaction.RelatedEntityType,
            transaction.RelatedEntityId,
        })
            .IsUnique()
            .HasFilter("[Kind] = 'stripe_purchase' AND [RelatedEntityType] = 'StripeCreditPurchase' AND [RelatedEntityId] IS NOT NULL");
        entity.HasIndex(transaction => new
        {
            transaction.RelatedEntityType,
            transaction.RelatedEntityId,
            transaction.Kind,
        })
            .IsUnique()
            .HasFilter("[Kind] = 'stripe_adjustment' AND [RelatedEntityType] = 'StripePaymentEvent' AND [RelatedEntityId] IS NOT NULL");

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(transaction => transaction.UserId)
            .HasConstraintName("FK_AiCreditTransactions_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
