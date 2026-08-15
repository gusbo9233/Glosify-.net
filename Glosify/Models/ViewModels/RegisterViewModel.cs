using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.ViewModels;

public class RegisterViewModel
{
    [Required(ErrorMessage = "Validation.Required")]
    [EmailAddress(ErrorMessage = "Validation.Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.Required")]
    [MinLength(6, ErrorMessage = "Validation.MinLength")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation.Required")]
    [DataType(DataType.Password)]
    [Compare("Password", ErrorMessage = "Validation.PasswordsMismatch")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
