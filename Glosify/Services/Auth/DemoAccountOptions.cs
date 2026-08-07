using System.Security.Cryptography;
using System.Text;

namespace Glosify.Services.Auth;

public sealed class DemoAccountOptions
{
    public const string SectionName = "Demo";

    public bool Enabled { get; set; }
    public string Email { get; set; } = "demo@glosify.se";

    /// <summary>
    /// Password for the seeded demo user. Only a fallback way in through the normal
    /// login form; the intended entry point is <see cref="AccessCode"/>.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Pasted into /demo?code=... to sign in as the demo user without a password.</summary>
    public string? AccessCode { get; set; }

    /// <summary>Credits the seeder tops the demo account up to on startup.</summary>
    public int Credits { get; set; } = 1000;

    /// <summary>
    /// Whether the demo is usable. The password and access code have no safe defaults,
    /// so an enabled-but-unconfigured demo stays closed rather than opening the account
    /// to anyone who guesses a blank code.
    /// </summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(AccessCode)
        && Credits > 0;

    public bool MatchesAccessCode(string? candidate)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        // Fixed-time comparison so a wrong code cannot be narrowed down one character at
        // a time; lengths are compared first because FixedTimeEquals requires equal spans.
        var expected = Encoding.UTF8.GetBytes(AccessCode!.Trim());
        var actual = Encoding.UTF8.GetBytes(candidate.Trim());
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
