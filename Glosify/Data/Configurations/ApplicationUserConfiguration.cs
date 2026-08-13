using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> entity)
    {
        entity.Property(user => user.SelectedQuizLanguageCode).HasMaxLength(8);
        // Names rather than codes, matching Quiz.SourceLanguage/TargetLanguage, and with no
        // check constraint: the four-language restriction below is about what Glosify teaches,
        // not about what a learner already speaks or wants to be answered in.
        entity.Property(user => user.PreferredSourceLanguage).HasMaxLength(64);
        entity.Property(user => user.PreferredAssistantLanguage).HasMaxLength(64);
        entity.Property(user => user.AssistantTelemetrySubjectId)
            .HasDefaultValueSql("NEWID()");
        entity.HasIndex(user => user.AssistantTelemetrySubjectId).IsUnique();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_AspNetUsers_SelectedQuizLanguageCode",
            "[SelectedQuizLanguageCode] IS NULL OR [SelectedQuizLanguageCode] IN ('et', 'de', 'pl', 'uk')"));

    }
}
