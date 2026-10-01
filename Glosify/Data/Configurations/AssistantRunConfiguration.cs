using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantRunConfiguration : IEntityTypeConfiguration<AssistantRun>
{
    public void Configure(EntityTypeBuilder<AssistantRun> entity)
    {
        entity.HasKey(run => run.Id);
        entity.Property(run => run.UserId).HasMaxLength(450).IsRequired();
        entity.Property(run => run.ActiveUserId).HasMaxLength(450);
        entity.Property(run => run.IdempotencyKey).HasMaxLength(100).IsRequired();
        entity.Property(run => run.RequestHash).HasMaxLength(64).IsRequired();
        entity.Property(run => run.Mode).HasMaxLength(16).IsRequired();
        entity.Property(run => run.Status).HasMaxLength(32).IsRequired();
        entity.Property(run => run.Reason).HasMaxLength(1000);
        entity.Property(run => run.Revision).IsConcurrencyToken();
        entity.HasIndex(run => new { run.UserId, run.IdempotencyKey }).IsUnique();
        entity.HasIndex(run => run.ActiveUserId).IsUnique().HasFilter("[active_user_id] IS NOT NULL");
        entity.HasIndex(run => new { run.Status, run.RetryAt, run.LeaseUntil });
        entity.HasIndex(run => new { run.ThreadId, run.CreatedAt });
        entity.HasOne<AssistantThread>()
            .WithMany()
            .HasForeignKey(run => run.ThreadId)
            .HasConstraintName("FK_AssistantRuns_AssistantThreads_ThreadId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssistantPartConfiguration : IEntityTypeConfiguration<AssistantPart>
{
    public void Configure(EntityTypeBuilder<AssistantPart> entity)
    {
        entity.HasKey(part => part.Id);
        entity.Property(part => part.Type).HasMaxLength(16).IsRequired();
        entity.Property(part => part.ToolName).HasMaxLength(64);
        entity.Property(part => part.CallId).HasMaxLength(128);
        entity.Property(part => part.State).HasMaxLength(32);
        entity.Property(part => part.Title).HasMaxLength(500);
        entity.HasIndex(part => new { part.MessageId, part.Sequence }).IsUnique();
        entity.HasIndex(part => new { part.RunId, part.Step });
        // Parts leave with their message, which leaves with its thread. The run id is a plain
        // indexed column: a foreign key would be a second cascade path from the thread, which
        // SQL Server rejects, and the thread's cascade already removes runs and parts together.
        entity.HasOne<AssistantMessage>()
            .WithMany()
            .HasForeignKey(part => part.MessageId)
            .HasConstraintName("FK_AssistantParts_AssistantMessages_MessageId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssistantChangeConfiguration : IEntityTypeConfiguration<AssistantChange>
{
    public void Configure(EntityTypeBuilder<AssistantChange> entity)
    {
        entity.HasKey(change => change.Id);
        entity.Property(change => change.UserId).HasMaxLength(450).IsRequired();
        entity.Property(change => change.Kind).HasMaxLength(32).IsRequired();
        entity.Property(change => change.EntityType).HasMaxLength(32).IsRequired();
        entity.Property(change => change.EntityId).HasMaxLength(64).IsRequired();
        entity.Property(change => change.Status).HasMaxLength(16).IsRequired();
        entity.HasIndex(change => new { change.RunId, change.Sequence }).IsUnique();
        entity.HasOne<AssistantRun>()
            .WithMany()
            .HasForeignKey(change => change.RunId)
            .HasConstraintName("FK_AssistantChanges_AssistantRuns_RunId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssistantToolEvaluationConfiguration : IEntityTypeConfiguration<AssistantToolEvaluation>
{
    public void Configure(EntityTypeBuilder<AssistantToolEvaluation> entity)
    {
        entity.HasKey(evaluation => evaluation.Id);
        entity.Property(evaluation => evaluation.ToolName).HasMaxLength(64).IsRequired();
        entity.Property(evaluation => evaluation.Status).HasMaxLength(32).IsRequired();
        entity.Property(evaluation => evaluation.SnapshotHash).HasMaxLength(64).IsRequired();
        entity.HasIndex(evaluation => new { evaluation.Status, evaluation.CreatedAt });
        entity.HasIndex(evaluation => evaluation.PartId).IsUnique();
        entity.HasOne<AssistantRun>()
            .WithMany()
            .HasForeignKey(evaluation => evaluation.RunId)
            .HasConstraintName("FK_AssistantToolEvaluations_AssistantRuns_RunId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
