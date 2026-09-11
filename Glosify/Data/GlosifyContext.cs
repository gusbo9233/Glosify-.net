using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Glosify.Services.Abuse;
using Microsoft.Extensions.Options;

namespace Glosify.Data;

/// <summary>
/// DbContext for Glosify application using Azure SQL Database
/// </summary>
public class GlosifyContext : IdentityDbContext<ApplicationUser>
{
    public GlosifyContext(DbContextOptions<GlosifyContext> options, IOptions<AbuseOptions>? abuse = null,
        RequestResourceReservations? requests = null)
        : base(options)
    {
        AbuseLimits = abuse?.Value ?? new();
        RequireAccountingReady = abuse is not null;
        EnforceAccounting = abuse is not null;
        RequestReservations = requests;
    }

    internal AbuseOptions AbuseLimits { get; }
    internal bool RequireAccountingReady { get; }
    internal bool EnforceAccounting { get; }
    internal RequestResourceReservations? RequestReservations { get; }
    internal bool KeepResourceReservation { get; set; }
    internal bool AccountingBypass { get; set; }
    internal (Guid Id, string UserId)? ClaimedResourceReservation { get; set; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try { return await ResourceAccounting.SaveAsync(this, () => base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken), cancellationToken); }
        catch (ResourceQuotaException) { ChangeTracker.Clear(); throw; }
        finally { ClaimedResourceReservation = null; KeepResourceReservation = false; }
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
    public DbSet<StripeCreditPurchase> StripeCreditPurchases { get; set; }
    public DbSet<StripePaymentEvent> StripePaymentEvents { get; set; }
    public DbSet<RealtimeTranslationSession> RealtimeTranslationSessions { get; set; }
    public DbSet<RealtimeTranslationMinute> RealtimeTranslationMinutes { get; set; }
    public DbSet<RealtimeTranslationTranscript> RealtimeTranslationTranscripts { get; set; }
    public DbSet<RealtimeTranslationTranscriptSegment> RealtimeTranslationTranscriptSegments { get; set; }
    public DbSet<RealtimeTranslationCaptureEvent> RealtimeTranslationCaptureEvents { get; set; }
    public DbSet<SavedTranslationSession> SavedTranslationSessions { get; set; }
    public DbSet<SavedTranslation> SavedTranslations { get; set; }

    public DbSet<Collection> Collections { get; set; }

    public DbSet<BookDocument> BookDocuments { get; set; }
    public DbSet<BookPage> BookPages { get; set; }
    public DbSet<BookPageTranslation> BookPageTranslations { get; set; }

    public DbSet<QuizAttempt> QuizAttempts { get; set; }
    public DbSet<QuizAttemptItem> QuizAttemptItems { get; set; }
    public DbSet<AnkiCollection> AnkiCollections { get; set; }
    public DbSet<AnkiQuizLink> AnkiQuizLinks { get; set; }
    public DbSet<AnkiNote> AnkiNotes { get; set; }
    public DbSet<AnkiCard> AnkiCards { get; set; }
    public DbSet<AnkiReview> AnkiReviews { get; set; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // One IEntityTypeConfiguration per entity, in Data/Configurations. That includes
        // the Identity types, whose configurations preserve the key lengths used by the
        // existing schema — newer Identity package defaults would otherwise scaffold
        // unrelated widening changes.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GlosifyContext).Assembly);
        // SQLite has no generated rowversion type. Keep its relational test model
        // insertable; SQL Server retains the required, database-generated token.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true)
        {
            modelBuilder.Entity<AnkiCard>().Property(card => card.RowVersion).IsRequired(false);
        }
    }
}
