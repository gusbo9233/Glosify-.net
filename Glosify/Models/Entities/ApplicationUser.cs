using Microsoft.AspNetCore.Identity;

namespace Glosify.Models.Entities;

public class ApplicationUser : IdentityUser
{
    public string? SelectedQuizLanguageCode { get; set; }
}
