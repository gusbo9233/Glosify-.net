using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationCaptureService : IRealtimeTranslationCaptureService
{
    private const int MaximumStoredTextCharacters = 12_000;

    private readonly GlosifyContext _context;
    private readonly AdministratorAccess _administratorAccess;
    private readonly TimeProvider _timeProvider;
    private readonly bool _captureContent;

    public RealtimeTranslationCaptureService(
        GlosifyContext context,
        AdministratorAccess administratorAccess,
        TimeProvider timeProvider,
        Microsoft.Extensions.Options.IOptions<Glosify.Services.Ai.Assistant.AssistantAnalyticsOptions>? analytics = null)
    {
        _context = context;
        _administratorAccess = administratorAccess;
        _timeProvider = timeProvider;
        _captureContent = analytics?.Value.CaptureContent ?? false;
    }

    public async Task<bool> IsAdminUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        return _captureContent && _administratorAccess.IsAdminUser(userId)
            && await _context.Users.AnyAsync(user => user.Id == userId, cancellationToken);
    }

    public async Task AppendAsync(
        Guid sessionId,
        string userId,
        IReadOnlyList<CapturedRealtimeTranslationEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0
            || !await IsAdminUserAsync(userId, cancellationToken)
            || !await _context.RealtimeTranslationSessions.AnyAsync(
                session => session.Id == sessionId && session.UserId == userId,
                cancellationToken))
        {
            return;
        }

        var storedAt = _timeProvider.GetUtcNow();
        foreach (var captured in events)
        {
            if (string.IsNullOrWhiteSpace(captured.Text))
            {
                continue;
            }
            _context.RealtimeTranslationCaptureEvents.Add(new RealtimeTranslationCaptureEvent
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Ordinal = captured.Ordinal,
                Sequence = captured.Sequence,
                Stage = captured.Stage,
                Kind = captured.Kind,
                Text = Truncate(captured.Text),
                SourceText = captured.SourceText is null ? null : Truncate(captured.SourceText),
                SourceLanguage = captured.SourceLanguage,
                TargetLanguage = captured.TargetLanguage,
                ProviderRequest = captured.ProviderRequest,
                CapturedAt = captured.CapturedAt,
                StoredAt = storedAt,
            });
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value) => value.Length <= MaximumStoredTextCharacters
        ? value
        : value[..MaximumStoredTextCharacters];
}
