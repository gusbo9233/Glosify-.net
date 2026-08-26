using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationCaptureService : IRealtimeTranslationCaptureService
{
    private const int MaximumStoredTextCharacters = 12_000;

    private readonly GlosifyContext _context;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public RealtimeTranslationCaptureService(
        GlosifyContext context,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _context = context;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    public async Task<bool> IsAdminUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var email = await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Email ?? user.UserName)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        return _configuration.GetSection("Admin:Emails").Get<string[]>()
            ?.Any(adminEmail => string.Equals(
                adminEmail?.Trim(),
                email.Trim(),
                StringComparison.OrdinalIgnoreCase)) == true;
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
