using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AiCreditAccountConfiguration : IEntityTypeConfiguration<AiCreditAccount>
{
    public void Configure(EntityTypeBuilder<AiCreditAccount> entity)
    {
        entity.Property(x => x.BalanceCredits).HasPrecision(19, 6);
        entity.Property(x => x.ReservedCredits).HasPrecision(19, 6);
        entity.HasKey(account => account.UserId);
        entity.Property(account => account.UserId).HasMaxLength(450).IsRequired();
        entity.Property(account => account.RowVersion).IsRowVersion();
        entity.Ignore(account => account.AvailableCredits);

        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(account => account.UserId)
            .HasConstraintName("FK_AiCreditAccounts_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.Cascade);

    }
}
