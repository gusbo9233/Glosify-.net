using Glosify.Services.Ai.Assistant.Mcp;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public class AssistantMcpSessionTests
{
    private const string Key = "a-signing-key-that-is-long-enough-32";

    [Fact]
    public void An_empty_signing_key_disables_the_endpoint_without_failing_startup()
    {
        var result = new AssistantMcpOptionsValidator()
            .Validate(null, new AssistantMcpOptions { SigningKey = string.Empty });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void A_truncated_signing_key_fails_startup_rather_than_silently_serving_404s()
    {
        var result = new AssistantMcpOptionsValidator()
            .Validate(null, new AssistantMcpOptions { SigningKey = "too-short" });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("Assistant:Mcp:SigningKey", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_enough_signing_key_is_accepted()
    {
        var result = new AssistantMcpOptionsValidator()
            .Validate(null, new AssistantMcpOptions { SigningKey = Key });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Round_trip_preserves_the_acting_user_and_page_context()
    {
        var codec = CreateCodec();
        var quizId = Guid.NewGuid();
        var customQuizId = Guid.NewGuid();

        var token = codec.Protect(new AssistantMcpSession
        {
            UserId = "user-1",
            QuizId = quizId,
            CustomQuizId = customQuizId,
            CurrentLanguage = "Polish",
        });
        var session = codec.Unprotect(token);

        Assert.NotNull(session);
        Assert.Equal("user-1", session.UserId);
        Assert.Equal(quizId, session.QuizId);
        Assert.Equal(customQuizId, session.CustomQuizId);
        Assert.Equal("Polish", session.CurrentLanguage);
    }

    // The signature is all that stops a guessed or edited URL from acting as another
    // user, because Foundry authenticates as one shared identity for everyone.
    [Fact]
    public void Tampering_with_the_payload_is_rejected()
    {
        var codec = CreateCodec();
        var token = codec.Protect(new AssistantMcpSession { UserId = "user-1" });
        var forged = codec.Protect(new AssistantMcpSession { UserId = "victim" });

        // Swap in another user's payload while keeping a signature that was valid for
        // a different one.
        var tampered = $"{forged.Split('.')[0]}.{token.Split('.')[1]}";

        Assert.Null(codec.Unprotect(tampered));
    }

    [Fact]
    public void A_token_signed_with_another_key_is_rejected()
    {
        var token = CreateCodec().Protect(new AssistantMcpSession { UserId = "user-1" });
        var otherCodec = CreateCodec(key: "a-different-signing-key-long-enough-1");

        Assert.Null(otherCodec.Unprotect(token));
    }

    [Fact]
    public void An_expired_session_is_rejected()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var codec = CreateCodec(time: time);
        var token = codec.Protect(new AssistantMcpSession { UserId = "user-1" });

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.NotNull(codec.Unprotect(token));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(codec.Unprotect(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only-one-part.")]
    [InlineData(".only-a-signature")]
    [InlineData("!!!not-base64!!!.!!!also-not!!!")]
    public void Malformed_tokens_are_rejected_without_throwing(string token)
    {
        Assert.Null(CreateCodec().Unprotect(token));
    }

    [Fact]
    public void A_session_without_a_user_is_rejected()
    {
        var codec = CreateCodec();
        var token = codec.Protect(new AssistantMcpSession { UserId = string.Empty });

        Assert.Null(codec.Unprotect(token));
    }

    // Without a configured key the endpoint is off, so nothing may be minted or accepted.
    [Fact]
    public void An_unconfigured_signing_key_disables_the_surface()
    {
        var codec = CreateCodec(key: "too-short");

        Assert.Throws<InvalidOperationException>(() =>
            codec.Protect(new AssistantMcpSession { UserId = "user-1" }));
        Assert.Null(codec.Unprotect("anything.atall"));
    }

    private static AssistantMcpSessionCodec CreateCodec(
        string key = Key,
        TimeProvider? time = null) =>
        new(
            Options.Create(new AssistantMcpOptions { SigningKey = key }),
            time);
}
