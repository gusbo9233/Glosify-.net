using System.Security.Claims;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Glosify.Tests;

public sealed class SelectedLanguageFilterTests
{
    [Fact]
    public async Task AuthenticatedRequest_DoesNotReplayGuestSelection()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = "glosify.guest-language=Spanish";
        var languages = new CookieLanguageContext(new HttpContextAccessor { HttpContext = http });
        Assert.Equal("Spanish", languages.CurrentLanguage);
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "learner")], "test"));
        var preferences = new StubChromeServices("sv");
        await languages.InitializeAsync(preferences, "learner", CancellationToken.None);
        Assert.Equal("Swedish", languages.CurrentLanguage);
        Assert.Equal(1, preferences.LanguagePreferenceCalls);
    }

    [Theory]
    [InlineData(null, "sv", "Swedish")]
    [InlineData("German", "sv", "Swedish")]
    [InlineData("Ukrainian", "sv", "Swedish")]
    [InlineData("German", null, null)]
    public async Task Action_and_view_share_one_resolved_language(string? cookie, string? savedLanguage, string? expected)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "learner")], "test"));
        if (cookie is not null) http.Request.Headers.Cookie = $"glosify.language={cookie}";
        var languages = new CookieLanguageContext(new HttpContextAccessor { HttpContext = http });
        var preferences = new StubChromeServices(savedLanguage);
        Assert.Null(languages.CurrentLanguage);
        var filter = new SelectedLanguageFilter(languages, preferences);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());
        await filter.OnActionExecutionAsync(executing, () =>
        {
            Assert.Equal(expected, languages.CurrentLanguage);
            return Task.FromResult(new ActionExecutedContext(action, [], executing.Controller));
        });
        Assert.Equal(expected, languages.CurrentLanguage);
        Assert.Equal(1, preferences.LanguagePreferenceCalls);

        Assert.True(languages.TrySetLanguage("French"));
        Assert.Equal("French", languages.CurrentLanguage);
        languages.Clear();
        Assert.Null(languages.CurrentLanguage);
    }
}
