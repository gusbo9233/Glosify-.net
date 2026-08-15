using Glosify.Models.Entities;
using Glosify.Localization;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> entity)
    {
        entity.Property(user => user.SelectedQuizLanguageCode)
            .HasMaxLength(QuizLanguageCatalog.StorageCodeMaximumLength);
        entity.Property(user => user.DisplayCulture)
            .HasMaxLength(DisplayCultureCatalog.StorageMaximumLength);
        // Names rather than codes, matching Quiz.SourceLanguage/TargetLanguage, and with no
        // check constraint: the selected-language restriction below is about what Glosify
        // teaches, not what a learner already speaks or wants to be answered in.
        entity.Property(user => user.PreferredSourceLanguage).HasMaxLength(64);
        entity.Property(user => user.PreferredAssistantLanguage).HasMaxLength(64);
        entity.Property(user => user.AssistantTelemetrySubjectId)
            .HasDefaultValueSql("NEWID()");
        entity.HasIndex(user => user.AssistantTelemetrySubjectId).IsUnique();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_AspNetUsers_SelectedQuizLanguageCode",
            QuizLanguageCatalog.SelectedLanguageCheckConstraintSql));
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_AspNetUsers_DisplayCulture",
            DisplayCultureCatalog.CheckConstraintSql));

    }
}
