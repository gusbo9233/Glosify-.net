using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantTurnConfiguration : IEntityTypeConfiguration<AssistantTurn>
{
    public void Configure(EntityTypeBuilder<AssistantTurn> entity)
    {
        entity.HasKey(turn => turn.Id);
        entity.Property(turn => turn.Profile).HasMaxLength(32).IsRequired();
        entity.Property(turn => turn.RequestedModel).HasMaxLength(128);
        entity.Property(turn => turn.Provider).HasMaxLength(64);
        entity.Property(turn => turn.ActualModel).HasMaxLength(128);
        entity.Property(turn => turn.Status).HasMaxLength(16).IsRequired();
        entity.Property(turn => turn.ErrorCategory).HasMaxLength(64);
        entity.Property(turn => turn.ChangeOutcome).HasMaxLength(16);
        entity.Property(turn => turn.TraceId).HasMaxLength(32);
        entity.Property(turn => turn.ProviderResponseId).HasMaxLength(256);
        entity.Property(turn => turn.PromptVersion).HasMaxLength(32);
        entity.Property(turn => turn.IntentArtifact).HasMaxLength(32);
        entity.Property(turn => turn.IntentContent).HasMaxLength(32);
        entity.Property(turn => turn.IntentOperation).HasMaxLength(32);
        entity.Property(turn => turn.TargetLanguage).HasMaxLength(64);
        entity.Property(turn => turn.SourceLanguage).HasMaxLength(64);
        entity.Property(turn => turn.ReplyLanguage).HasMaxLength(64);
        // Language balance is validated per language, so the dataset queries group on these.
        entity.HasIndex(turn => new { turn.TargetLanguage, turn.StartedAt });
        // Long enough for the widest profile surface with room to grow. Truncated rather
        // than rejected on write, so a future tool list can never fail a turn.
        entity.Property(turn => turn.AllowedTools).HasMaxLength(2048);
        entity.HasIndex(turn => new { turn.ThreadId, turn.StartedAt });
        entity.HasIndex(turn => new { turn.Status, turn.StartedAt });
        entity.HasIndex(turn => new { turn.Provider, turn.ActualModel, turn.StartedAt });
        entity.HasIndex(turn => turn.TraceId);

        entity.HasOne<AssistantThread>()
            .WithMany()
            .HasForeignKey(turn => turn.ThreadId)
            .HasConstraintName("FK_AssistantTurns_AssistantThreads_ThreadId")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
