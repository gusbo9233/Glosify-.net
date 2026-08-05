namespace Glosify.Models.Api;

public sealed record ExtensionExchangeCodeRequest(
    string Code,
    string RedirectUri,
    string CodeVerifier);
