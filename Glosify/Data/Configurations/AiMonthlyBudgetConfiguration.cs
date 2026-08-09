using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AiMonthlyBudgetConfiguration : IEntityTypeConfiguration<AiMonthlyBudget>
{
    public void Configure(EntityTypeBuilder<AiMonthlyBudget> entity)
    {
        entity.HasKey(budget => budget.PeriodKey);
        entity.Property(budget => budget.PeriodKey).HasMaxLength(7);
        entity.Property(budget => budget.RowVersion).IsRowVersion();
        entity.Ignore(budget => budget.AvailableMicros);

    }
}
