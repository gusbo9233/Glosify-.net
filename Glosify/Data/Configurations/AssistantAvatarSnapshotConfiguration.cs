using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantAvatarSnapshotConfiguration : IEntityTypeConfiguration<AssistantAvatarSnapshot>
{
    public void Configure(EntityTypeBuilder<AssistantAvatarSnapshot> entity)
    {
        entity.HasKey(s => s.Id);
        entity.Property(s => s.Language).HasMaxLength(16).IsRequired();
        entity.Property(s => s.ContentHash).HasMaxLength(64).IsRequired();
        entity.HasIndex(s => new { s.ThreadId, s.ContentHash }).IsUnique();
        entity.HasIndex(s => new { s.ThreadId, s.ConversationId, s.Language, s.CreatedAt });
        entity.HasOne<AssistantThread>().WithMany().HasForeignKey(s => s.ThreadId).OnDelete(DeleteBehavior.Cascade);
        // Runs keep a plain snapshot id, like parts keep a run id: both are removed by
        // the thread cascade, without a second cascade path on SQL Server.
    }
}
