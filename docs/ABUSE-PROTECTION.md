# Abuse controls and ElevenLabs rollout

Application quotas bound saved content and expensive work. They do not cap an
Azure invoice. App Service compute, SQL, blob operations/capacity, egress,
monitoring and any configured Azure services can still incur charges. Keep
single-instance hosting, Azure retention and cost alerts, and ingress traffic
protection configured independently. Provider key limits are also independent.

## Configuration and accounting

`Abuse` in `appsettings.json` contains the validated limits. Environment overrides
use `Abuse__Quizzes`, `Abuse__SitePdfBytes`, and the same convention for other
properties. Defaults are 1,000 quizzes across languages, 1,000 items per quiz and
50,000 total, 50 PDFs / 500 MiB, 200 combined regular/Anki collections, 100 saved
chats, 100 transcripts, 10,000 translations, 100 translation sessions, and 100 MiB
content per account. Site capacity is 10 GiB PDFs and 1 GiB content.

Content allowance counts UTF-16 string/JSON bytes plus 1 KiB per content record,
including Anki and practice records, extracted book text and translations,
assistant messages/pending changes/feedback, and saved transcripts. This is an
allowance, not physical SQL database size. Usage is enforced at the shared
persistence boundary used by feature services. SQL transactions update both
content and counters, with transaction-owned application locks and a five-second
lock wait. Existing above-limit content remains readable and deletable; only
positive growth is rejected. Imports and copies retain their own stricter limits.

`/account/usage` and authenticated `GET /api/account/usage` expose current,
reserved, and maximum account usage. `largest_quiz_items` shows the fullest
quiz, including any item reservations, against the per-quiz limit. The page links to content management and
explicitly deletes practice/review history without changing Anki card scheduling.
Account denials return 409 `resource_quota_exceeded`; site denials return 503
`site_capacity_exceeded`, both with the existing Problem Details `error` alias.
The `Glosify.Abuse` meter aggregates denials without user/quiz identifiers.

Reservations persist before uploads, AI generation, and transcript saving. No
SQL transaction spans provider calls or PDF extraction. Ordinary reservations
expire in five minutes. Transcript reservations last for the session and are
consumed incrementally; exhaustion stops saving with a visible warning while
captions continue. A stale/foreign reservation cannot commit content. An expired
blob reservation remains charged until its blob has been deleted successfully.

PDF uploads keep three attempts per ten minutes. They accept up to 25 MiB (`Abuse__MaxPdfBytes` may lower this limit) and
allow one extraction per account/two per process, without a waiting queue.
The application invokes itself as `--extract-pdf INPUT OUTPUT` before host
initialization. Workers reject invalid/encrypted PDFs, more than 1,000 pages,
50,000 characters per page, or five million characters total. The parent limits
execution to 30 seconds, memory to 512 MiB, and output to 64 MiB, kills failed
workers and removes temporary files. Deleted/failed blobs remain accounted for
in durable cleanup records until storage confirms deletion.

Authenticated mutations share 120 requests/minute; imports and copies also
have a ten/minute limit. Existing stricter policies apply. Auth, payment hooks,
and realtime protocols use their dedicated policies. These in-memory throttles
and process concurrency limits assume one application instance; SQL quotas and
signup admission remain durable across restarts.

## Signup

Only configured Google/Microsoft providers create new accounts. MVC/API and
scaffolded Identity password registration are closed. Existing password login,
refresh, recovery, management and provider linking remain supported. Trial
credit amount and eligibility rules are unchanged.

Admission, user creation and external-login association commit atomically.
Existing-user callbacks/linking use no admission slots. Defaults: five new
accounts per IP/hour, ten per IP/UTC day and 200 site-wide/UTC day. IPv4-mapped
addresses normalize to IPv4; IPv6 addresses share a /64. Only hashed address
buckets are persisted and expired buckets are removed. Admission returns 429
with `Retry-After`. Set `Abuse__SignupsEnabled=false` to close new registration
while preserving existing sign-ins. OAuth initiation and callbacks retain
separate throttles; mobile PKCE properties are checked before account creation.

App Service honors one forwarding hop from its front end. Ensure direct access
to a worker cannot bypass that front end. Outside App Service, only default
loopback and explicitly configured `ForwardedHeaders__KnownProxies__0`, etc.
are trusted. Do not configure arbitrary client addresses as trusted proxies.

## ElevenLabs

TTS defaults to disabled. Before enabling:

1. Configure the existing ElevenLabs secret resolver (`Speech__ApiKey` or the
   existing ElevenLabs secret settings). This does not require Scribe enabled.
2. Add a monthly-budget model price whose `Deployment` is `eleven_v3` and whose
   `TextSekPerMillionCharacters` is an explicitly chosen positive SEK price for
   your ElevenLabs subscription. Use a conservative effective price; there is
   no guessed default. The validator accepts per-character prices.
3. Configure `Speech__AllowedVoiceIds__0` and `Speech__DefaultVoiceId`. Initial
   example voice: `JBFqnCBsd6RMkjVDRZzb`. Check that each voice is available to the
   key with `GET /v1/voices/{voice_id}`, then validate one small real synthesis
   using `POST /v1/text-to-speech/{voice_id}`, model `eleven_v3`.
4. Set `Speech__Enabled=true` only after those checks succeed.

The server uses ElevenLabs' [convert endpoint](https://elevenlabs.io/docs/api-reference/text-to-speech/convert)
with a 30-second timeout and no automatic POST retries. Unsupported languages
only offer explicit browser speech. Legacy Azure preferences reset to browser
speech; legacy quality request fields are tolerated. The audio/voice-list API
shape, 200-character segments and one-credit playback price remain unchanged.

The dedicated cache is capped at 64 MiB (configurable downward through
`Speech__MemoryCacheBytes`), one hour and 2 MiB per audio entry. Keys include
model, voice, language, format/settings and a text hash. Identical concurrent
requests share synthesis; limits are two requests/account and four/process.
User credits are released when playback preparation fails and settled once on
success. Provider cost is reserved only for a cache miss. Ambiguous upstream
outcomes and abandoned provider reservations are conservatively charged.

Runtime TTS no longer accesses blob storage or Azure synthesis. Legacy classes
remain solely for compatibility tests. After rollout, preview old cache cleanup:

```sh
python3 scripts/cleanup-retired-tts-cache.py --account YOUR_STORAGE_ACCOUNT
```

Add `--delete` only after reviewing the preview. The command exclusively targets
`tts-cache` and the old `voice/sha256.mp3` Azure Neural naming scheme, uses Entra
login, and checks each ETag before deletion. It cannot delete PDF containers.
See [Azure blob CLI](https://learn.microsoft.com/en-us/cli/azure/storage/blob)
for login and data-role requirements. Storage soft-delete/version retention may
retain deleted audio according to your Azure account policy.

## Migration and rollout checks

Apply `20260911151434_DurableAbuseControls` before starting the new build. Startup
maintenance sets accounting readiness false, rebuilds counters/snapshots in
bounded batches, and resolves existing PDF lengths from blob properties. Missing
blob permissions or a failed backfill keep new content disabled. Repair access
or missing-blob discrepancies explicitly; maintenance does not delete user
content to make quotas fit. Existing reads/sign-ins remain available.

Check `ResourceAccountingState` has `Id=1, Ready=1` after completion. Confirm the
usage page on an existing account and SQL counters, including existing blob
sizes. Maintenance reconciles daily and retries cleanup/expired reservations
once per minute. Keep one active instance during reconciliation.

Diagnostic content capture is disabled with `AssistantAnalytics__CaptureContent=false`.
Local invocation/tool/caption diagnostic records are pruned after 30 days.
Financial ledgers and user-saved content are preserved. Also set the Azure Log
Analytics workspace/table retention to 30 days; application code cannot change
an Azure retention policy. Review App Insights sampling and collection limits.

Before production rollout, confirm social providers, signup kill switch and
limits, storage limits, blob access, positive v3 price and voice availability,
one real synthesis, completed quota backfill, forwarded-address boundary and
Azure diagnostic retention. These are environment checks, not implied by local
unit tests. Run .NET (with `RUN_SQLSERVER_TESTS=true` and an explicit local SQL
connection), JavaScript, published browser journeys, and EF pending-model checks.

### Local verification — 11 September 2026

- 1,048 .NET tests passed with real SQL Server tests enabled. Three opt-in live
  OpenAI tests were skipped. SQL coverage includes concurrent quota admission,
  transaction retries, signup windows and atomicity, transcript capacity, and
  per-character provider budgets.
- 38 published-app browser journeys and 101 JavaScript tests passed.
- The full migration chain applied to an isolated SQL database; EF reported no
  pending model changes. The isolated PDF worker extracted a real sample PDF.
- The configured ElevenLabs key could access the default example voice. One
  real `eleven_v3` synthesis returned MPEG audio successfully.
- NuGet's vulnerability-data lookup was unavailable (`NU1900`); this verification
  does not establish current dependency vulnerability status.

These checks did not deploy the application or backfill production. TTS remains
disabled until an explicit positive v3 character price is supplied. Production
signup/storage settings, accounting readiness, forwarding configuration, and
Azure diagnostic retention still require the rollout checks above.
