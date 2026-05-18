using System.Security.Claims;

namespace Glosify.Extensions;

public static class ClaimsPrincipalExtensions
{
    // [Authorize] guarantees a principal; the NameIdentifier claim is set by Identity for password
    // and external sign-ins alike. Throwing here means a caller who forgot [Authorize] gets a loud
    // error instead of a silent redirect that never fires.
    public static string GetUserId(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(id))
        {
            throw new InvalidOperationException("No authenticated user id; action must be marked [Authorize].");
        }
        return id;
    }
}
