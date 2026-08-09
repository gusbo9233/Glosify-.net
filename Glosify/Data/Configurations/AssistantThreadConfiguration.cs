using Glosify.Models.Entities;
using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Glosify.Data.Configurations;

internal sealed class AssistantThreadConfiguration : IEntityTypeConfiguration<AssistantThread>
{
    public void Configure(EntityTypeBuilder<AssistantThread> entity)
    {
        entity.HasKey(t => t.Id);
        entity.Property(t => t.UserId).HasMaxLength(450).IsRequired();
        entity.Property(t => t.Title).HasMaxLength(256);
        entity.Property(t => t.Language).HasMaxLength(64);
        entity.HasIndex(t => new { t.QuizId, t.UserId });
        entity.HasIndex(t => new { t.UserId, t.QuizId, t.Language });
        entity.HasIndex(t => t.ContextQuizId);
        entity.HasIndex(t => t.ContextTranscriptId);
        entity.HasIndex(t => t.ContextBookDocumentId);
        entity.HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(t => t.QuizId)
            .HasConstraintName("FK_AssistantThreads_Quizzes_QuizId")
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(t => t.ContextQuizId)
            .HasConstraintName("FK_AssistantThreads_Quizzes_ContextQuizId")
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .HasConstraintName("FK_AssistantThreads_AspNetUsers_UserId")
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne<RealtimeTranslationTranscript>()
            .WithMany()
            .HasForeignKey(t => t.ContextTranscriptId)
            .HasConstraintName("FK_AssistantThreads_RealtimeTranslationTranscripts_ContextTranscriptId")
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne<BookDocument>()
            .WithMany()
            .HasForeignKey(t => t.ContextBookDocumentId)
            .HasConstraintName("FK_AssistantThreads_BookDocuments_ContextBookDocumentId")
            .OnDelete(DeleteBehavior.NoAction);

    }
}
