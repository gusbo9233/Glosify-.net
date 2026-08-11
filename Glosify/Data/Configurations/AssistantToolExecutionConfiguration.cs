using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantToolExecutionConfiguration : IEntityTypeConfiguration<AssistantToolExecution>
{
    public void Configure(EntityTypeBuilder<AssistantToolExecution> entity)
    {
        entity.HasKey(execution => execution.Id);
        entity.Property(execution => execution.ToolName).HasMaxLength(128).IsRequired();
        entity.Property(execution => execution.ArgumentsJson).IsRequired();
        entity.Property(execution => execution.Status).HasMaxLength(16).IsRequired();
        entity.Property(execution => execution.ErrorCategory).HasMaxLength(64);
        entity.HasIndex(execution => new { execution.TurnId, execution.Sequence }).IsUnique();
        entity.HasIndex(execution => execution.InvocationId);

        entity.HasOne<AssistantModelInvocation>()
            .WithMany()
            .HasForeignKey(execution => execution.InvocationId)
            .HasConstraintName("FK_AssistantToolExecutions_AssistantModelInvocations_InvocationId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
