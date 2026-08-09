using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Api;

public sealed record ExtensionExchangeCodeRequest(
    [param: Required, StringLength(512)] string Code,
    [param: Required, StringLength(2048)] string RedirectUri,
    [param: Required, StringLength(256)] string CodeVerifier);
