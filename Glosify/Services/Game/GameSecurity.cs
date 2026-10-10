using System.Security.Cryptography;
using System.Text;
using Glosify.Infrastructure.Api;
using Glosify.Models.Entities;
using Glosify.Services.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Glosify.Services.Game;

public static class GameServiceAuthentication
{
    public static bool IsTrusted(HttpContext context)
    {
        var expected = context.RequestServices.GetRequiredService<IConfiguration>()["GameIntegration:ServiceKey"];
        var supplied = context.Request.Headers["X-Game-Service-Key"].ToString();
        return expected is { Length: >= 32 } && supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class GameAdminAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        http.Response.Headers.CacheControl = "no-store";
        var signIn = services.GetRequiredService<SignInManager<ApplicationUser>>();
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = http.User.Identity?.IsAuthenticated == true ? await signIn.ValidateSecurityStampAsync(http.User) : null;
        var status = !services.GetRequiredService<IConfiguration>().GetValue<bool>("SharedAuth:Enabled") ? 404
            : user is null || !await signIn.CanSignInAsync(user) || users.SupportsUserLockout && await users.IsLockedOutAsync(user) ? 401
            : !services.GetRequiredService<AdministratorAccess>().IsAdminUser(user.Id) ? 403 : 200;
        if (status != 200) context.Result = GlosifyProblemDetails.Result(http, status, GlosifyProblemDetails.CodeForStatus(status));
    }
}
