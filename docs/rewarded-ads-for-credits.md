# Rewarded Ads → AI Credits: Feasibility Findings

## Context (current state)

Glosify is a **web-only** ASP.NET Core MVC app (`Glosify/Glosify.csproj`, `net10.0`). There is no mobile (MAUI/Xamarin) project — this matters a lot, because "rewarded ads" is overwhelmingly a mobile-SDK concept (AdMob rewarded video, etc.). On web, the ecosystem is thinner.

Quiz generation is already gated by a real credit system, not a flat quota:

- `Glosify/Services/Ai/IAiCreditService.cs` — `ReserveAsync` → `CommitUsageAsync`/`ReleaseAsync`, plus `GrantAsync` for manual/admin grants.
- `Glosify/Services/Ai/AiUsageOptions.cs` — `TrialGrantCredits = 25`, `CreditsPerThousandTokens = 1`. Cost is `ceil(tokens/1000) * creditsPerK`, not "N quizzes/day".
- `Glosify/Models/Entities/AiCreditTransaction.cs` — ledger with kinds `TrialGrant`, `AdminGrant`, `Reservation`, `UsageDebit`, `Release`.
- Reserve/commit calls happen in `Glosify/Services/Ai/Llm/GeminiClient.cs` (not in the controllers). `QuizController` just catches the bubbled `InsufficientAiCreditsException` and returns HTTP 402 with `{ error: message }` (e.g. lines ~109, 133, 199).
- No payment/subscription system exists today. No ad SDK exists today. The only way to get credits right now is the one-time trial grant or an admin manual grant.

So a rewarded-ads feature would slot in as **a third source of credits**, alongside trial grant and admin grant — architecturally this is the easy part, since the ledger already supports arbitrary grant reasons.

## What "rewarded ads" means on the web

Unlike mobile (AdMob rewarded video is a mature, well-documented, near-zero-integration-cost SDK), rewarded ads for a website are a much smaller, less standardized market. Realistic options:

1. **Google Ad Manager (GAM) rewarded ads for web** — Google does support rewarded ad units in Ad Manager (`googletag` rewarded slots), but it requires a GAM 360 or a Google-approved Ad Manager account, and eligibility/inventory for rewarded web ads is limited and approval-gated — this is not a self-serve AdSense feature. Fill rates for rewarded web outside of gaming/video-heavy sites are typically low.
2. **Third-party rewarded/offerwall networks** (e.g. Wowza-style offerwalls, AdGem, Ayet Studios, Adjoe web, Torox) — these mostly target gaming and offerwall use cases, integrate via a JS SDK + server-to-server postback/webhook for reward verification. More accessible than GAM but revenue per view is usually low and fill outside gaming audiences is inconsistent.
3. **Video ad networks with a "rewarded" wrapper** (IMA SDK rewarded ads, or third-party video ad exchanges) — technically works on web via Google's IMA SDK, but again requires ad inventory/approval and is mostly built for video-content sites, not a utility app.

**Bottom line: there is no "just drop in a script tag" rewarded-ad option for a general-purpose non-gaming web app.** Every option above requires an ad-network account, approval, and (for the credible ones) server-side reward verification — not just a client-side "ad finished" callback, since that's trivially spoofable from devtools.

## Anti-abuse is the hard part, not the ad SDK

Whatever network is chosen, the integration must NOT trust the browser to say "ad watched, give credits." The standard, security-correct pattern is:

- Client requests an ad from the network's SDK, gets a reward token/impression ID.
- **Server-side verification**: either (a) the ad network calls a **server-to-server postback** to a Glosify webhook endpoint with a signed reward event, or (b) Glosify calls the network's verification API with the impression ID before crediting.
- Only after server-side verification does Glosify call something like `IAiCreditService.GrantAsync(systemUserId, userId, credits, "rewarded_ad")` (this would need a new transaction kind, e.g. `AiCreditTransactionKinds.RewardedAd`, alongside a new `AiUsageOptions.RewardedAdCredits` config value).
- Rate-limit grants per user (e.g. max N rewarded-ad credits per day) the same way `Program.cs:50-91` already rate-limits `/Assistant` (60/min per user) and auth routes (20/min per IP) — reuse the existing `AddRateLimiter`/`PartitionedRateLimiter` pattern with a new partition, e.g. keyed per-user with a daily fixed window.
- Idempotency: dedupe on the ad network's impression/transaction ID so a replayed postback or a user refreshing after reward doesn't double-credit.

None of this infrastructure exists yet (no webhook endpoints, no background jobs — confirmed no `IHostedService`/Hangfire/Quartz anywhere in the repo), so this is new surface area, not a config toggle.

## Effort estimate (rough, for a single dev)

| Piece | Effort |
|---|---|
| Ledger/service changes (`RewardedAd` transaction kind, `RewardedAdCredits` option, `GrantAsync` call site) | Small — extends existing, well-tested pattern (`AiCreditServiceTests.cs`) |
| Ad network account setup + approval | **Unknown/out of your control** — GAM rewarded approval or offerwall network onboarding can take days–weeks and may be rejected for a non-gaming utility site |
| Client-side ad trigger UI (button "watch an ad for +N credits", loading state, SDK embed) | Small–Medium |
| Server-side reward verification endpoint + signature/HMAC check + idempotency + rate limit | Medium — new code, security-sensitive |
| Fraud/abuse monitoring (unusual grant volume, multi-accounting) | Medium, ongoing |

## Recommendation

Given this is a niche language-learning/quiz web app (not gaming/video content), **fill rates and eligibility for genuine rewarded web ads will likely be poor**, and the credible integration path (GAM rewarded, approval-gated) is not guaranteed to be available at all. Two more realistic alternatives to rewarded ads specifically:

- **Regular banner/interstitial ads via AdSense** (self-serve, no approval gate beyond standard AdSense) shown while a quiz is generating, funding credits indirectly rather than 1:1 per-view — much easier to actually ship.
- Since there's already a working credit ledger with `GrantAsync` but **no payment system**, a lower-effort, more reliable monetization step might be a small paid credit top-up (Stripe Checkout) before investing in rewarded-ad plumbing whose ad-network availability is uncertain.

If the user still wants to pursue rewarded ads specifically, the next concrete step is checking actual eligibility/access with a real ad network (start with applying for Google Ad Manager or contacting an offerwall network) before building the verification plumbing, since availability — not code — is the real gating factor here.
