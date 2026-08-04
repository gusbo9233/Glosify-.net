# Glosify Live Subtitles Chrome extension

The unpacked Manifest V3 extension in `Glosify.LiveSubtitles.Extension` captures
the audio of the tab where the user starts it, keeps that audio audible locally,
and sends 24 kHz PCM audio over a short-lived WebSocket to Glosify. Glosify
relays those frames in memory to Microsoft Foundry over its dedicated
translation WebSocket and relays text events back to the browser. The server
coordinates authentication, credits, and one-minute billing authorization.
Audio is never persisted. Live-only sessions use translation alone for 8 credits
per started minute. When the user explicitly enables **Save original speech
transcript**, Glosify also sends the same in-memory PCM stream to
`gpt-realtime-whisper`, stores finalized source speech only, and charges 16
credits per started minute.

The provider integration uses Foundry's GA `gpt-realtime-translate` deployment
and dedicated `/openai/v1/realtime/translations` API. Opted-in sessions also use
`gpt-realtime-whisper` through `/openai/v1/realtime?intent=transcription`, with
24 kHz mono PCM, automatic language detection, and three-second manual commits.
The browser never receives an Azure credential. The relay uses the App Service
managed identity to authenticate to Foundry.

## Prerequisites

- Chrome 116 or newer.
- The `AddRealtimeTranslation`, `AddSavedRealtimeTranscripts`, and
  `AddSavedSourceTranscripts` EF Core migrations applied to the Glosify database.
- A Microsoft Foundry resource where `gpt-realtime-translate` and
  `gpt-realtime-whisper` are available, with Global Standard deployments of both.
- The Glosify server identity assigned the **Cognitive Services OpenAI User**
  role on that Foundry resource.
- A current operator-supplied SEK-per-audio-minute budget price.
- A Glosify user with at least 8 credits for live-only subtitles or 16 credits
  for a saved source transcript.

Foundry model availability changes independently of this application. Confirm
that the intended resource exposes the model before deploying it:

```bash
az cognitiveservices account list-models \
  --resource-group <resource-group> \
  --name <foundry-resource> \
  --query "[?model.name=='gpt-realtime-translate' || model.name=='gpt-realtime-whisper'].{model:model.name,version:model.version,skus:skus[].name}" \
  -o table
```

## Configure Glosify

The feature is disabled by default and uses the same `TokenCredential` as the
other Glosify Foundry integrations. Local development uses
`DefaultAzureCredential` (`az login`); production uses the App Service managed
identity selected by `AZURE_CLIENT_ID`, or the system-assigned identity when
that setting is absent.

Configure the Azure OpenAI resource root and the Foundry deployment name:

```bash
az login
dotnet user-secrets set --project Glosify \
  "RealtimeTranslation:FoundryEndpoint" \
  "https://<foundry-resource>.openai.azure.com/"
dotnet user-secrets set --project Glosify \
  "RealtimeTranslation:Deployment" \
  "<gpt-realtime-translate-deployment>"
dotnet user-secrets set --project Glosify \
  "RealtimeTranslation:SourceTranscriptionDeployment" \
  "<gpt-realtime-whisper-deployment>"
dotnet user-secrets set --project Glosify \
  "RealtimeTranslation:SavedTranscriptBillingModel" \
  "gpt-realtime-translate+gpt-realtime-whisper"
dotnet user-secrets set --project Glosify \
  "RealtimeTranslation:SavedSourceTranscriptsEnabled" "true"
dotnet user-secrets set --project Glosify \
  "RealtimeTranslation:Enabled" "true"
dotnet user-secrets set --project Glosify \
  "AiUsage:MonthlyBudget:Models:3:Deployment" \
  "<gpt-realtime-translate-deployment>"
dotnet user-secrets set --project Glosify \
  "AiUsage:MonthlyBudget:Models:3:AudioSekPerMinute" \
  "<sek-per-audio-minute>"
dotnet user-secrets set --project Glosify \
  "AiUsage:MonthlyBudget:Models:4:Deployment" \
  "gpt-realtime-translate+gpt-realtime-whisper"
dotnet user-secrets set --project Glosify \
  "AiUsage:MonthlyBudget:Models:4:AudioSekPerMinute" \
  "<combined-sek-per-audio-minute>"
```

The default budget provider list already contains `foundry`. If a deployment
overrides that list, it must still contain `foundry`.

`AudioSekPerMinute` is an operational configuration value. Foundry publishes
this model's price by audio time; convert the applicable price to SEK per minute
with the operator's exchange-rate and safety buffers instead of hard-coding a
currency conversion in the application.

Apply the migration and start the server:

```bash
dotnet ef database update --project Glosify
dotnet run --project Glosify
```

When production uses managed-identity SQL authentication, the migration can be
run inside App Service by temporarily setting
`Database__ApplyMigrationsOnStartup=true`, restarting once, confirming the
migration-complete log, and setting it back to `false`. Keep this opt-in switch
off during normal starts.

Production uses equivalent App Service settings with double underscores, such
as `RealtimeTranslation__FoundryEndpoint`,
`RealtimeTranslation__Deployment`, `RealtimeTranslation__Enabled`, and (only
after provisioning and smoke testing) `RealtimeTranslation__SavedSourceTranscriptsEnabled`.
No
standard OpenAI API key is used by this feature. Enable WebSockets on the App
Service before enabling live subtitles:

```bash
az webapp config set \
  --resource-group <resource-group> \
  --name <app-name> \
  --web-sockets-enabled true
```

## Load and authorize the unpacked extension

1. If necessary, change `config.js` to the Glosify origin being tested. The
   origin must also appear in `manifest.json` under `host_permissions`.
2. Open `chrome://extensions`, enable Developer mode, choose **Load unpacked**,
   and select `Glosify.LiveSubtitles.Extension`.
3. The pilot manifest pins extension ID `akepdpjieiokffdapibipomhbplikock`.
   Its callback is
   `https://akepdpjieiokffdapibipomhbplikock.chromiumapp.org/glosify`.
4. Add that exact callback to Glosify configuration and restart the app:

   ```bash
   dotnet user-secrets set --project Glosify \
     "ExtensionAuth:AllowedRedirectUris:0" \
     "https://<extension-id>.chromiumapp.org/glosify"
   ```

5. Open Twitch, YouTube, or another ordinary HTTP(S) page. Open the extension,
   connect Glosify, select a target language, optionally enable **Save original
   speech transcript**, and choose **Start subtitles**. Saving is available only
   when the target matches the persisted Glosify quiz language (`et`, `de`, `pl`,
   or `uk`). Storage consent resets after Stop.

The manifest key keeps the unpacked pilot ID stable across directories and
Chrome profiles. Add only known exact callback URLs; never allow a wildcard
`chromiumapp.org` callback.

## Runtime behavior

- Starting reserves the first 8 or 16 credits according to the selected mode.
  Credits are debited only after all required Foundry WebSockets accept their
  configurations and the first minute begins.
- The next minute's applicable credits are reserved five seconds before its boundary
  and debited at the boundary. If they cannot be reserved, capture stops before
  the unpaid minute.
- The relay validates PCM messages and paces upstream audio against elapsed time
  and committed paid minutes. A modified extension cannot continue the Foundry
  stream after server-side billing authorization ends.
- A new Foundry and Glosify session is created automatically after 30 minutes.
  If storage was enabled, reconnect sessions append to the same saved transcript.
- Live-only sessions open only the translation connection. Saved sessions require
  both translation and source-transcription connections. Source deltas may enable
  the optional bilingual overlay, which remains off by default.
- The overlay is an adjustable subtitle chat: drag its header to reposition it,
  resize it from the lower-right corner, minimize it from the header, or clear
  the visible chat without stopping translation. It retains at most 30 bounded
  translation bubbles in memory and clears when capture stops.
- The content script receives subtitle/status events only. Glosify bearer tokens
  stay in the service worker. The offscreen document receives only a single-use,
  two-minute Glosify relay grant, passed in a WebSocket subprotocol instead of a
  log-prone query string.
- Foundry audio-output events are discarded by the server. Application logs and
  telemetry contain session identifiers and aggregate timing/counts only—not
  audio, captions, authorization codes, bearer tokens, or relay grants.
- New saved transcripts contain finalized source speech only. Legacy translated
  transcripts remain labeled and readable. They are private to the owning account,
  visible only while their learning language matches the user's currently selected
  quiz language, remain until deleted, and are available from
  `/Transcripts`. The assistant can retrieve their text through ownership-checked,
  bounded read tools; quiz changes still require the normal Apply action.

## Validation

Run the backend and extension tests:

```bash
dotnet test Glosify.slnx
npm test --prefix Glosify.LiveSubtitles.Extension
```

Before advertising a target language, run a short live sample for that language
and disable it in `RealtimeTranslation:Languages` if the deployed model does not
meet the pilot's accuracy and latency requirements.

Relevant provider documentation:

- [GPT Realtime Translate in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/openai/concepts/gpt-realtime-translate)
- [Foundry Realtime translation example](https://learn.microsoft.com/azure/foundry/openai/how-to/realtime-audio-websockets#translate-audio-in-real-time)

## Pilot security boundary

Audio travels through a memory-only Glosify relay to the configured Microsoft
Foundry resource. Saved sessions fan audio to translation and transcription;
neither connection stores audio. The relay closes the upstream provider connection if
heartbeats, session lifetime, or committed-minute checks fail and never stores
media. Finalized source speech is stored only for an explicitly opted-in,
language-matched session; translated captions, partial events, and audio are not
stored for new sessions. Chrome Web Store
packaging and submission remain outside the V1 pilot boundary.
