namespace Glosify.Services.Communication;

public sealed record AcsCallToken(string Token, DateTimeOffset ExpiresOn, string AcsUserId);

public interface IAcsTokenService
{
    bool IsConfigured { get; }

    /// <summary>
    /// Returns a VoIP-scoped ACS access token for the user, creating and
    /// persisting an ACS identity for them on first use.
    /// </summary>
    Task<AcsCallToken> GetCallTokenAsync(string userId, CancellationToken cancellationToken = default);
}
