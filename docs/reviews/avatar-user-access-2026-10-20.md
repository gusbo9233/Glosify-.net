# Avatar access for signed-in users

Request: make avatars accessible to all users. The implementation opens Speaking
to all signed-in Identity application users. Anonymous visitors still need to sign
in because conversations use account credits and owned sessions.

Changes:
- Remove the administrator policy from AvatarController while retaining cookie
  authentication, feature gating, security stamp/account validation, antiforgery,
  WebSocket origin checks, session ownership and credit checks.
- Show Speaking in the shared sidebar for every signed-in user when enabled.
- Remove the administrator allowlist from assistant avatar context. Retain feature
  availability, nonempty user identity, snapshot ownership and language checks.
- Keep /Admin/AvatarUsage and its settings link administrator-only.
- Update the unavailable-provider message and avatar documentation.

Verification:
- `dotnet test Glosify.Tests/Glosify.Tests.csproj --no-restore --filter 'FullyQualifiedName~Avatar'`
  built successfully: 167 passed, 8 skipped, 1 failed. The failure was
  ShippedConfigurationTests.AvatarShipsCompleteCharacterCastingWithYoungerRain,
  whose host startup encountered SQL SSPI authentication failure before assertions.
  All avatar controller and assistant conversation tests passed, including normal
  learner navigation/config, all four partners, credit denial, administrator report
  denial, disabled-feature behavior, antiforgery and ownership checks.
- `node --test Glosify.ClientTests/avatar-*.test.js`: 209 passed.
- Scoped `git diff --check` passed.
- No full browser or live paid-provider run for this access-only change.

Qodo rules search returned no applicable rules. Final auto-depth review was
submitted from an isolated checkout containing only this access change, excluding
unrelated guest-browsing work and the earlier avatar model change. Operations
0def12a1-e638-416d-bb4b-70f828df2eb1 and retry
2cba4c0b-dbd3-492d-b417-2f41769ebb51 both failed with:
`managed agent 'pre-pr-review' terminated with state 'failed'`.
No completed Qodo findings are available; this is not a clean review. DeepSource
was not run because no PR/push was requested. No commit or deployment performed.
