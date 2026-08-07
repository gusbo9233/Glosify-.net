using Glosify.Services.Auth;
using Xunit;

namespace Glosify.Tests;

public sealed class DemoAccountOptionsTests
{
    [Fact]
    public void Demo_StaysClosedUntilFullyConfigured()
    {
        var options = new DemoAccountOptions { Enabled = true };
        Assert.False(options.IsConfigured);

        options.Password = "demo-password";
        Assert.False(options.IsConfigured);

        options.AccessCode = "let-me-in";
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void AccessCode_MatchesOnlyTheConfiguredValue()
    {
        var options = CreateConfigured();

        Assert.True(options.MatchesAccessCode("let-me-in"));
        Assert.True(options.MatchesAccessCode("  let-me-in  "));
        Assert.False(options.MatchesAccessCode("Let-Me-In"));
        Assert.False(options.MatchesAccessCode("let-me-i"));
        Assert.False(options.MatchesAccessCode("let-me-inn"));
    }

    [Fact]
    public void BlankCandidate_NeverMatches()
    {
        var options = CreateConfigured();

        Assert.False(options.MatchesAccessCode(null));
        Assert.False(options.MatchesAccessCode(""));
        Assert.False(options.MatchesAccessCode("   "));
    }

    [Fact]
    public void DisabledDemo_RejectsTheRightCode()
    {
        var options = CreateConfigured();
        options.Enabled = false;

        Assert.False(options.MatchesAccessCode("let-me-in"));
    }

    private static DemoAccountOptions CreateConfigured() => new()
    {
        Enabled = true,
        Email = "demo@glosify.se",
        Password = "demo-password",
        AccessCode = "let-me-in",
        Credits = 1000,
    };
}
