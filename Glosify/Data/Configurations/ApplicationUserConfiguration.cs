using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> entity)
    {
        entity.Property(user => user.SelectedQuizLanguageCode).HasMaxLength(8);
        entity.Property(user => user.AssistantTelemetrySubjectId)
            .HasDefaultValueSql("NEWID()");
        entity.HasIndex(user => user.AssistantTelemetrySubjectId).IsUnique();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_AspNetUsers_SelectedQuizLanguageCode",
            "[SelectedQuizLanguageCode] IS NULL OR [SelectedQuizLanguageCode] IN ('et', 'de', 'pl', 'uk')"));

    }
}
