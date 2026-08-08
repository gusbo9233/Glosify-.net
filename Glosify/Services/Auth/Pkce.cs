using System.Security.Cryptography;
using System.Text;

namespace Glosify.Services.Auth;

/// <summary>
/// RFC 7636 S256 proof key for code exchange, shared by the browser-extension and mobile
/// sign-in flows so both enforce it identically.
/// </summary>
internal static class Pkce
{
    /// <summary>The S256 challenge for a verifier: BASE64URL(SHA256(ASCII(verifier))).</summary>
    public static string CreateChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    /// <summary>RFC 7636 section 4.1: 43-128 characters from the unreserved set.</summary>
    public static bool IsValidVerifier(string? value) =>
        value is { Length: >= 43 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');

    /// <summary>A base64url-encoded SHA-256 digest is always 43 characters with padding trimmed.</summary>
    public static bool IsValidChallenge(string? value) =>
        value is { Length: 43 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// Whether <paramref name="codeVerifier"/> proves possession of <paramref name="expectedChallenge"/>.
    /// Compared in fixed time, since a timing signal here would leak the challenge byte by byte.
    /// </summary>
    public static bool Matches(string expectedChallenge, string? codeVerifier)
    {
        if (!IsValidVerifier(codeVerifier))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(expectedChallenge);
        var actual = Encoding.ASCII.GetBytes(CreateChallenge(codeVerifier!));
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>A 256-bit authorization code, base64url encoded.</summary>
    public static string CreateAuthorizationCode() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
