# GlobeGlotter migration and admin-only game SSO

Code is staged behind `GlobeGlotter:CanonicalEnabled` and `SharedAuth:Enabled` (both default false).
Do not enable shared authentication on production before completing the canonical-domain cutover.
The game is the current langgame rewrite, with the existing baked scene and no NPC/voice endpoints.

## Cutover checklist

- Deploy these code changes through the existing protected PR/build/review workflow.
- Verify HTTPS on globeglotter.app, www.globeglotter.app and game.globeglotter.app.
- Retain old-domain bindings and callbacks. Add the new Google `/signin-google` and Microsoft
  `/signin-microsoft` redirect URIs in their existing provider registrations.
- Set `SharedAuth__BlobUri=https://ggkeys462fqtu46qbcq.blob.core.windows.net/auth-keys/keyring.xml`.
- Set `SharedAuth__KeyIdentifier=https://gg-auth-462fqtu46qbcq.vault.azure.net/keys/data-protection`.
- In the coordinated cutover set `GlobeGlotter__CanonicalEnabled=true`, `SharedAuth__Enabled=true`,
  and `Stripe__PublicBaseUrl=https://globeglotter.app`. Azure values override appsettings.json.
- Update Stripe's webhook destination to the same route on the new hostname and test signed delivery.
- Verify password/social/2FA sign-in, extension bootstrap, bearer refresh, sitemap/canonical URLs,
  old-origin API compatibility and logout. The key-ring/application-name change can invalidate
  existing bearer, refresh, reset and cookie tokens: expect reauthentication.
- Release updated extension store packages only after production checks pass.

## Authority and endpoints

`GET /sso/game` authenticates with the existing Identity application cookie, revalidates the user,
and requires the existing `Admin:UserIds` allowlist. It redirects only to the fixed game homepage.
Arbitrary return URLs are ignored; login keeps its local-return-url protections.

`GET /api/game/access` uses the same cookie scheme and validates the current security stamp,
sign-in eligibility, lockout and admin status. It returns `{ "userId": "<immutable Identity ID>" }`,
401 or 403, never login HTML. Its responses are not cacheable. The game calls it server-side,
forwards only the authentication cookie and caches successful validation for at most 60 seconds.
There is no additional admin list and no game SQL access. Both backends are equally trusted.

Glosify alone creates/renews shared cookies and rotates keys. Azure managed identities access the
private Blob key ring and Key Vault encryption key. Development requires an explicit
`SharedAuth:LocalKeyPath`; production keys must never be copied to development.

## Compatibility and rollback

The canonical switch removes the previous duplicate redirect middleware. Browser GET/HEAD pages
on the old host redirect to the new apex. API, extension and webhook routes are retained. Old-origin browser sign-in POSTs and OAuth
callbacks restart with a 303 to the new login page before processing credentials/codes;
validated local return URLs survive the restart. Old-page logout continues at the new
origin’s existing logout confirmation form because only that origin can delete its cookie.
Logout still requires an antiforgery-protected POST. Unrelated account-management and legacy
POSTs are not blanket-redirected. New-origin redirects include a stable
`__gg` query marker to bypass previously cached permanent redirects back to the old domain. Responses from the old hostname never issue the new
parent-domain application cookie. Account management and POST/antiforgery logout remain here.

Stop the game first if necessary. Restore coordinated artifact/settings for an auth rollback;
DNS alone cannot restore cookies. Retain all old Data Protection/Key Vault key versions and domain
certificates. No database migrations are introduced by this change.

## Review record

Greptile reviewed the initial revision on October 6, 2026. Its three findings were confirmed
and addressed: cached redirect recovery, explicit old-origin sign-in restart, and standard
Problem Details for game API errors. Regression tests cover each, including a subsequent review’s logout-continuation and local-return-URL findings. The real Identity logout form is tested for antiforgery enforcement and shared-cookie deletion. No Copilot review was
available for that revision; absence is not treated as a clean review.
