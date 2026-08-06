using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant.Mcp;

/// <summary>
/// The request context a Foundry-run tool call acts within. Foundry authenticates as a
/// single shared identity, so the acting user cannot come from the caller: it is minted
/// here, signed, and carried in the per-response MCP server URL.
/// </summary>
public sealed class AssistantMcpSession
{
    [JsonPropertyName("uid")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("qid")]
    public Guid? QuizId { get; set; }

    [JsonPropertyName("cqid")]
    public Guid? CustomQuizId { get; set; }

    [JsonPropertyName("tid")]
    public Guid? TranscriptId { get; set; }

    [JsonPropertyName("fwid")]
    public string? FocusedWordId { get; set; }

    [JsonPropertyName("lang")]
    public string? CurrentLanguage { get; set; }

    [JsonPropertyName("langc")]
    public string? CurrentLanguageCode { get; set; }

    /// <summary>Groups the pending changes a single assistant response produces.</summary>
    [JsonPropertyName("cid")]
    public Guid ConversationId { get; set; }

    [JsonPropertyName("exp")]
    public long ExpiresAtUnixSeconds { get; set; }
}

public interface IAssistantMcpSessionCodec
{
    string Protect(AssistantMcpSession session);
    AssistantMcpSession? Unprotect(string token);
}

public sealed class AssistantMcpSessionCodec : IAssistantMcpSessionCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AssistantMcpOptions _options;
    private readonly TimeProvider _time;

    public AssistantMcpSessionCodec(IOptions<AssistantMcpOptions> options, TimeProvider? time = null)
    {
        _options = options.Value;
        _time = time ?? TimeProvider.System;
    }

    public string Protect(AssistantMcpSession session)
    {
        if (!_options.IsEnabled)
        {
            throw new InvalidOperationException("Assistant MCP signing key is not configured.");
        }

        session.ExpiresAtUnixSeconds = _time.GetUtcNow()
            .AddMinutes(Math.Max(1, _options.SessionLifetimeMinutes))
            .ToUnixTimeSeconds();

        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var encoded = Base64Url(payload);
        return $"{encoded}.{Base64Url(Sign(encoded))}";
    }

    public AssistantMcpSession? Unprotect(string token)
    {
        if (!_options.IsEnabled || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var separator = token.IndexOf('.');
        if (separator <= 0 || separator == token.Length - 1)
        {
            return null;
        }

        var encoded = token[..separator];
        byte[] suppliedSignature;
        byte[] payload;
        try
        {
            suppliedSignature = FromBase64Url(token[(separator + 1)..]);
            payload = FromBase64Url(encoded);
        }
        catch (FormatException)
        {
            return null;
        }

        // Fixed-time comparison: the signature is the only thing standing between a
        // guessed URL and another user's quiz data.
        if (!CryptographicOperations.FixedTimeEquals(suppliedSignature, Sign(encoded)))
        {
            return null;
        }

        AssistantMcpSession? session;
        try
        {
            session = JsonSerializer.Deserialize<AssistantMcpSession>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (session is null
            || string.IsNullOrWhiteSpace(session.UserId)
            || session.ExpiresAtUnixSeconds <= _time.GetUtcNow().ToUnixTimeSeconds())
        {
            return null;
        }

        return session;
    }

    private byte[] Sign(string encodedPayload) =>
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.SigningKey),
            Encoding.UTF8.GetBytes(encodedPayload));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length."),
        };
        return Convert.FromBase64String(padded);
    }
}
