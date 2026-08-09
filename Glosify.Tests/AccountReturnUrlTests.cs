using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Glosify.Tests;

public sealed class AccountReturnUrlTests
{
    [Theory]
    [InlineData("https://attacker.example/steal")]
    [InlineData("//attacker.example/steal")]
    public async Task AuthenticatedLogin_RejectsExternalReturnUrl(string returnUrl)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                "Test")),
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor());
        var controller = new AccountController(
            null!,
            null!,
            null!,
            null!)
        {
            ControllerContext = new ControllerContext(actionContext),
            Url = new UrlHelper(actionContext),
        };

        var result = await controller.Login(returnUrl);

        Assert.Equal("/", Assert.IsType<LocalRedirectResult>(result).Url);
    }

    [Fact]
    public async Task AuthenticatedLogin_AllowsLocalReturnUrl()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                "Test")),
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor());
        var controller = new AccountController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext(actionContext),
            Url = new UrlHelper(actionContext),
        };

        var result = await controller.Login("/Quizzes");

        Assert.Equal("/Quizzes", Assert.IsType<LocalRedirectResult>(result).Url);
    }
}
