namespace Glosify.Services.Speech;

public interface ISpeechAuthorizationTokenService
{
    bool IsConfigured { get; }

    Task<SpeechAuthorizationToken> GetTokenAsync(
        CancellationToken cancellationToken = default);
}

public sealed record SpeechAuthorizationToken(
    string AuthorizationToken,
    string Region,
    DateTimeOffset ExpiresAtUtc);
