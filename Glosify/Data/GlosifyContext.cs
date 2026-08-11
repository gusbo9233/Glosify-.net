using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Data;

/// <summary>
/// DbContext for Glosify application using Azure SQL Database
/// </summary>
public class GlosifyContext : IdentityDbContext<ApplicationUser>
{
    public GlosifyContext(DbContextOptions<GlosifyContext> options)
        : base(options)
    {
    }

    public DbSet<Quiz> Quizzes { get; set; }
    public DbSet<Word> Words { get; set; }
    public DbSet<QuizSentence> QuizSentences { get; set; }
    public DbSet<AssistantThread> AssistantThreads { get; set; }
    public DbSet<AssistantMessage> AssistantMessages { get; set; }
    public DbSet<AssistantPendingChange> AssistantPendingChanges { get; set; }
    public DbSet<AssistantTurn> AssistantTurns { get; set; }
    public DbSet<AssistantModelInvocation> AssistantModelInvocations { get; set; }
    public DbSet<AssistantToolExecution> AssistantToolExecutions { get; set; }
    public DbSet<AssistantFeedback> AssistantFeedback { get; set; }
    public DbSet<AssistantFeedbackReason> AssistantFeedbackReasons { get; set; }
    public DbSet<AssistantTelemetryDeletionRequest> AssistantTelemetryDeletionRequests { get; set; }
    public DbSet<AiCreditAccount> AiCreditAccounts { get; set; }
    public DbSet<AiCreditTransaction> AiCreditTransactions { get; set; }
    public DbSet<AiMonthlyBudget> AiMonthlyBudgets { get; set; }
    public DbSet<RealtimeTranslationSession> RealtimeTranslationSessions { get; set; }
    public DbSet<RealtimeTranslationMinute> RealtimeTranslationMinutes { get; set; }
    public DbSet<RealtimeTranslationTranscript> RealtimeTranslationTranscripts { get; set; }
    public DbSet<RealtimeTranslationTranscriptSegment> RealtimeTranslationTranscriptSegments { get; set; }

    public DbSet<Collection> Collections { get; set; }

    public DbSet<BookDocument> BookDocuments { get; set; }
    public DbSet<BookPage> BookPages { get; set; }
    public DbSet<BookPageTranslation> BookPageTranslations { get; set; }

    public DbSet<Classroom> Classrooms { get; set; }
    public DbSet<ClassroomMembership> ClassroomMemberships { get; set; }
    public DbSet<ClassroomInvitation> ClassroomInvitations { get; set; }
    public DbSet<ClassroomContent> ClassroomContents { get; set; }
    public DbSet<ClassroomMessage> ClassroomMessages { get; set; }
    public DbSet<QuizAttempt> QuizAttempts { get; set; }
    public DbSet<QuizAttemptItem> QuizAttemptItems { get; set; }
    public DbSet<CustomQuiz> CustomQuizzes { get; set; }
    public DbSet<AcsUserIdentity> AcsUserIdentities { get; set; }
    public DbSet<ClassroomLesson> ClassroomLessons { get; set; }
    public DbSet<ClassroomAssignment> ClassroomAssignments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // One IEntityTypeConfiguration per entity, in Data/Configurations. That includes
        // the Identity types, whose configurations preserve the key lengths used by the
        // existing schema — newer Identity package defaults would otherwise scaffold
        // unrelated widening changes.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GlosifyContext).Assembly);
    }
}
