using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[AllowAnonymous]
public class LoginController : Controller
{
    public IActionResult Index() => View();
}
