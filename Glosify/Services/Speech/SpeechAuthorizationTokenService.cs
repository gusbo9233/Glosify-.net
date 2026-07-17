using Azure.Core;
using Glosify.Services.Speaking;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Speech;

public sealed class SpeechAuthorizationTokenService : ISpeechAuthorizationTokenService
{
    private static readonly TokenRequestContext SpeechTokenContext =
        new(["https://cognitiveservices.azure.com/.default"]);

    private readonly SpeechOptions _options;
    private readonly TokenCredential _credential;

    public SpeechAuthorizationTokenService(
        IOptions<SpeechOptions> options,
        TokenCredential credential)
    {
        _options = options.Value;
        _credential = credential;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ResourceId)
        && !string.IsNullOrWhiteSpace(_options.Region);

    public async Task<SpeechAuthorizationToken> GetTokenAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new SpeakingDependencyUnavailableException(
                "Azure Speech managed-identity authentication is not configured.");
        }

        try
        {
            var token = await _credential.GetTokenAsync(SpeechTokenContext, cancellationToken);
            return new SpeechAuthorizationToken(
                $"aad#{_options.ResourceId.Trim()}#{token.Token}",
                _options.Region.Trim(),
                token.ExpiresOn);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SpeakingTelemetry.SpeechFailures.Add(1);
            throw new SpeakingDependencyUnavailableException(
                "Azure Speech is temporarily unavailable.",
                ex);
        }
    }
}
