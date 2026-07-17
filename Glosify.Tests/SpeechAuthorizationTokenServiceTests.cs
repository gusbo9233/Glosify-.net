using Azure.Core;
using Glosify.Services.Speaking;
using Glosify.Services.Speech;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class SpeechAuthorizationTokenServiceTests
{
    [Fact]
    public async Task Constructs_browser_token_without_exposing_a_speech_key()
    {
        const string resourceId =
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/speech";
        var expires = new DateTimeOffset(2026, 7, 17, 13, 0, 0, TimeSpan.Zero);
        var credential = new StubTokenCredential(new AccessToken("managed-identity-token", expires));
        var service = new SpeechAuthorizationTokenService(
            Options.Create(new SpeechOptions
            {
                ResourceId = resourceId,
                Region = "swedencentral",
                Key = "must-not-be-used",
            }),
            credential);

        var result = await service.GetTokenAsync();

        Assert.Equal($"aad#{resourceId}#managed-identity-token", result.AuthorizationToken);
        Assert.Equal("swedencentral", result.Region);
        Assert.Equal(expires, result.ExpiresAtUtc);
        Assert.Equal(
            ["https://cognitiveservices.azure.com/.default"],
            credential.RequestedScopes);
        Assert.DoesNotContain("must-not-be-used", result.AuthorizationToken);
    }

    [Fact]
    public async Task Missing_managed_identity_configuration_is_reported_as_unavailable()
    {
        var service = new SpeechAuthorizationTokenService(
            Options.Create(new SpeechOptions { Key = "legacy-key" }),
            new StubTokenCredential(new AccessToken(string.Empty, DateTimeOffset.MinValue)));

        await Assert.ThrowsAsync<SpeakingDependencyUnavailableException>(
            () => service.GetTokenAsync());
    }

    [Fact]
    public async Task Credential_failures_are_reported_as_unavailable()
    {
        var service = new SpeechAuthorizationTokenService(
            Options.Create(new SpeechOptions
            {
                ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/speech",
                Region = "swedencentral",
            }),
            new StubTokenCredential(new InvalidOperationException("identity unavailable")));

        var exception = await Assert.ThrowsAsync<SpeakingDependencyUnavailableException>(
            () => service.GetTokenAsync());

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private readonly AccessToken _token;
        private readonly Exception? _exception;

        public StubTokenCredential(AccessToken token) => _token = token;
        public StubTokenCredential(Exception exception) => _exception = exception;

        public IReadOnlyList<string> RequestedScopes { get; private set; } = [];

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            RequestedScopes = requestContext.Scopes;
            return _exception is null ? _token : throw _exception;
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            RequestedScopes = requestContext.Scopes;
            return _exception is null
                ? ValueTask.FromResult(_token)
                : ValueTask.FromException<AccessToken>(_exception);
        }
    }
}
