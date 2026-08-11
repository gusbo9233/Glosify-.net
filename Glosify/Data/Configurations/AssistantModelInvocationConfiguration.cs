using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantModelInvocationConfiguration : IEntityTypeConfiguration<AssistantModelInvocation>
{
    public void Configure(EntityTypeBuilder<AssistantModelInvocation> entity)
    {
        entity.HasKey(invocation => invocation.Id);
        entity.Property(invocation => invocation.AgentName).HasMaxLength(128);
        entity.Property(invocation => invocation.AgentVersion).HasMaxLength(64);
        entity.Property(invocation => invocation.Profile).HasMaxLength(32).IsRequired();
        entity.Property(invocation => invocation.Provider).HasMaxLength(64).IsRequired();
        entity.Property(invocation => invocation.RequestedModel).HasMaxLength(128);
        entity.Property(invocation => invocation.ActualModel).HasMaxLength(128);
        entity.Property(invocation => invocation.RequestJson).IsRequired();
        entity.Property(invocation => invocation.ProviderResponseId).HasMaxLength(256);
        entity.Property(invocation => invocation.Status).HasMaxLength(16).IsRequired();
        entity.Property(invocation => invocation.ErrorCategory).HasMaxLength(64);
        entity.Property(invocation => invocation.TraceId).HasMaxLength(32);
        entity.Property(invocation => invocation.SpanId).HasMaxLength(16);
        entity.HasIndex(invocation => new { invocation.TurnId, invocation.Sequence }).IsUnique();
        entity.HasIndex(invocation => invocation.ProviderResponseId);
        entity.HasIndex(invocation => invocation.TraceId);

        entity.HasOne<AssistantTurn>()
            .WithMany()
            .HasForeignKey(invocation => invocation.TurnId)
            .HasConstraintName("FK_AssistantModelInvocations_AssistantTurns_TurnId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
