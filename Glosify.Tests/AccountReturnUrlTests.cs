using System.Security.Claims;
using System.Net;
using Glosify.Controllers;
using Glosify.Extensions;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Glosify.Tests;

public sealed class AccountReturnUrlTests
{
    [Fact]
    public void RegisterPost_RequiresExplicitAntiforgeryValidation()
    {
        var action = typeof(AccountController)
            .GetMethods()
            .Single(method =>
                method.Name == nameof(AccountController.Register)
                && method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Length > 0);

        Assert.NotEmpty(action.GetCustomAttributes(
            typeof(ValidateAntiForgeryTokenAttribute),
            inherit: false));
    }

    [Fact]
    public async Task RegisterPost_WithoutAntiforgeryToken_IsRejectedByMvcPipeline()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.PostAsync(
            "/Account/Register",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = "csrf-probe@example.test",
                ["Password"] = "NeverSubmitted1!",
                ["ConfirmPassword"] = "NeverSubmitted1!",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void ExternalOauthFailure_PreservesTheFullLocalPkceReturnUrl()
    {
        const string returnUrl = "/extension/connect?redirect_uri=https%3A%2F%2Fakepdpjieiokffdapibipomhbplikock.chromiumapp.org%2Fglosify&state=a-b_c&code_challenge=challenge&code_challenge_method=S256";
        var callback = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            "/Account/ExternalLoginCallback",
            "returnUrl",
            returnUrl);

        var failurePath = AuthenticationExtensions.BuildExternalLoginFailurePath("Google", callback);
        var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            new Uri($"https://glosify.se{failurePath}").Query);

        Assert.Equal("Google", parsed["externalLoginError"]);
        Assert.Equal(returnUrl, parsed["returnUrl"]);
    }

    [Fact]
    public void Register_PreservesAValidatedExtensionReturnUrl()
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ControllerActionDescriptor());
        var controller = new AccountController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext(actionContext),
            Url = new UrlHelper(actionContext),
        };
        const string returnUrl = "/extension/connect?redirect_uri=https%3A%2F%2Fakepdpjieiokffdapibipomhbplikock.chromiumapp.org%2Fglosify&state=state&code_challenge=challenge";

        var result = controller.Register(returnUrl);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(returnUrl, controller.ViewData["ReturnUrl"]);
    }

    [Theory]
    [InlineData("https://attacker.example/register")]
    [InlineData("//attacker.example/register")]
    public void Register_DropsAnExternalReturnUrl(string returnUrl)
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ControllerActionDescriptor());
        var controller = new AccountController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext(actionContext),
            Url = new UrlHelper(actionContext),
        };

        controller.Register(returnUrl);

        Assert.Null(controller.ViewData["ReturnUrl"]);
    }

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
