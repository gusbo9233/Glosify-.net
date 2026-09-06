using System.Security.Claims;

namespace Glosify.Services.Auth;

/// <summary>Administrator grants bind to operator-approved Identity IDs, never user-editable emails.</summary>
public sealed class AdministratorAccess
{
    private readonly HashSet<string> _userIds;

    public AdministratorAccess(IConfiguration configuration)
    {
        _userIds = (configuration.GetSection("Admin:UserIds").Get<string[]>() ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    public bool IsAdmin(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && IsAdminUser(user.FindFirstValue(ClaimTypes.NameIdentifier));

    public bool IsAdminUser(string? userId) =>
        !string.IsNullOrEmpty(userId) && _userIds.Contains(userId);
}
