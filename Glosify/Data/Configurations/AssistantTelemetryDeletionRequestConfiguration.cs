using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantTelemetryDeletionRequestConfiguration : IEntityTypeConfiguration<AssistantTelemetryDeletionRequest>
{
    public void Configure(EntityTypeBuilder<AssistantTelemetryDeletionRequest> entity)
    {
        entity.HasKey(request => request.Id);
        entity.Property(request => request.TableName).HasMaxLength(128).IsRequired();
        entity.Property(request => request.DimensionName).HasMaxLength(128).IsRequired();
        entity.Property(request => request.DimensionValue).HasMaxLength(256).IsRequired();
        entity.Property(request => request.Status).HasMaxLength(16).IsRequired();
        entity.Property(request => request.AzureOperationId).HasMaxLength(512);
        entity.Property(request => request.LastError).HasMaxLength(2000);
        entity.HasIndex(request => new { request.Status, request.NextAttemptAt });
        entity.HasIndex(request => new
        {
            request.TableName,
            request.DimensionName,
            request.DimensionValue,
            request.Status,
        });
    }
}
