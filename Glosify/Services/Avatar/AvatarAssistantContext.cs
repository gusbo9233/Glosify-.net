using Microsoft.Extensions.Options;

namespace Glosify.Services.Avatar;

public sealed class AvatarAssistantContext(AvatarSessions sessions, IOptions<AvatarOptions> options)
{
    public void EnsureAvailable(string userId)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(userId))
            throw new AvatarException(404, "Speaking conversation not available.");
    }

    public AvatarTranscriptSnapshot Capture(Guid id, string userId, string language)
    {
        EnsureAvailable(userId);
        var snapshot = sessions.Snapshot(id, userId, language);
        if (snapshot.Turns.Length == 0) throw new AvatarException(422, "Say something to your avatar first.");
        return snapshot;
    }
}
