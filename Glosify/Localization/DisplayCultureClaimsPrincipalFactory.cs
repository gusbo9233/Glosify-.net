using System.Security.Claims;
using Glosify.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Glosify.Localization;

public sealed class DisplayCultureClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser>(userManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (DisplayCultureCatalog.TryCanonicalize(user.DisplayCulture, out var culture))
        {
            identity.AddClaim(new Claim(DisplayCultureCatalog.ClaimType, culture));
        }

        return identity;
    }
}
