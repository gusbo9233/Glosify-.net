using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantTaskConfiguration : IEntityTypeConfiguration<AssistantTask>
{
    public void Configure(EntityTypeBuilder<AssistantTask> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.UserId).HasMaxLength(450);
        e.Property(x => x.ActiveUserId).HasMaxLength(450);
        e.Property(x => x.IdempotencyKey).HasMaxLength(100);
        e.Property(x => x.RequestHash).HasMaxLength(64);
        e.Property(x => x.Status).HasMaxLength(32);
        e.Property(x => x.Reason).HasMaxLength(1000);
        e.Property(x => x.Revision).IsConcurrencyToken();
        e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
        e.HasIndex(x => x.ActiveUserId).IsUnique().HasFilter("[ActiveUserId] IS NOT NULL");
        e.HasIndex(x => new { x.Status, x.RetryAt, x.LeaseUntil });
        e.HasOne<AssistantThread>().WithMany().HasForeignKey(x => x.ThreadId).OnDelete(DeleteBehavior.Cascade);
    }
}
internal sealed class AssistantTaskCallConfiguration : IEntityTypeConfiguration<AssistantTaskCall>
{
    public void Configure(EntityTypeBuilder<AssistantTaskCall> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.ToolName).HasMaxLength(100);
        e.Property(x => x.Status).HasMaxLength(32);
        e.Property(x => x.EvaluationStatus).HasMaxLength(32);
        e.Property(x => x.SnapshotHash).HasMaxLength(64);
        e.HasIndex(x => new { x.TaskId, x.Sequence }).IsUnique();
        e.HasOne<AssistantTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssistantTaskAttemptConfiguration : IEntityTypeConfiguration<AssistantTaskAttempt>
{
    public void Configure(EntityTypeBuilder<AssistantTaskAttempt> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.Status).HasMaxLength(32);
        e.Property(x => x.ErrorCategory).HasMaxLength(100);
        e.HasIndex(x => new { x.TaskId, x.StartedAt });
        e.HasOne<AssistantTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}
