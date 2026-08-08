namespace Glosify.Models.Api;

/// <param name="CodeVerifier">
/// The PKCE verifier whose S256 challenge was sent to <c>google/start</c>. Required: without
/// it an intercepted code is enough to obtain bearer tokens.
/// </param>
public sealed record ExchangeCodeRequest(string Code, string CodeVerifier);
