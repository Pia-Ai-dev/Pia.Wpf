# External endpoints Pia.Wpf dials at runtime

**Status:** Current as of 2026-08-29; one endpoint open (§5), and §3 gained a mirror hop that is
unverified against the live host · **Owner:** Marco Altmann ·
**Written:** 2026-08-29 · **Origin:** an audit asked for the full runtime egress list; nothing in the
repo recorded it, and the sweep turned up a 404 that had been shipping unnoticed

## 1. What this is for

Three questions this answers that previously needed a grep: what a customer's firewall team has to
allow, what leaves the machine and to whom, and whether any of it has rotted. That last one matters
more than it sounds — every call below is lazy (first use) and wrapped in a catch-and-log, so a dead
URL presents as "nothing happened", not as an error.

**Scope.** Runtime egress from the shipped client. Build-time feeds (`api.nuget.org`) and the
live-provider endpoints under `tests/` are excluded; a user's machine never dials those.

**How it was built.** A grep over `src/` finds only the URLs we wrote; three of the hosts below live
inside NuGet packages instead (Velopack → `api.github.com`, Playwright → `cdn.playwright.dev`,
PiperSharp → Hugging Face and rhasspy). So the shipped assemblies were also string-swept — every
non-`Pia`/non-BCL `.dll` in the Release output, scanned for UTF-16 and UTF-8 URL literals. That sweep
turns up nothing beyond what is listed here: the rest is repository and documentation metadata that
is never fetched, certificate-chain URLs, and XML namespaces.

**The list is exhaustive for what the repo hard-codes, not for what the process can dial.** Three
categories are unbounded by design and cannot be enumerated from source: a user-configured provider
endpoint, an MCP server a user adds, and every asset host the Teams web client pulls in once the
meeting attendee drives a real browser. They are listed as categories in §4.

`scripts/Test-ExternalEndpoints.ps1` re-runs the whole sweep. It is deliberately not part of
`dotnet test` — the gate must stay offline.

## 2. Pia-operated

| Endpoint | Trigger | Override | What leaves | If down |
|---|---|---|---|---|
| `storage.pia-ai.de/f/wpf/` | Update check on startup, then every 4–6 h | `Update:FeedUrl` (`appsettings.json`) | Nothing but a GET | Silent — no updates, nothing surfaced. Serving since 2026-08-30, see §5.1 |
| `storage.pia-ai.de/f/assets/` | Before every §3 model download that has a mirror key | `Assets:MirrorBaseUrl` (`appsettings.json`); blank goes straight upstream | Nothing but a GET | Silent — falls back to the upstream host in §3, latched per process |
| `github.com/Pia-Ai-dev/Pia.Wpf` → `api.github.com` | Update check when `FeedUrl` is blank | `Update:GitHubRepoUrl` (`Models/UpdateOptions.cs`) | Nothing but a GET | Same |
| `cloud.pia-ai.de` | Login, sync, cloud chat, E2EE device management, policy, capabilities, assignments, plugin CABs + icons + trusted certs, AI feedback | `PIA_CLOUD_SERVER_URL`, else `AppSettings.ServerUrl`; default in `Bootstrapper.cs` | Bearer token; chat and prompt content (E2EE-wrapped on the sync path); device keys; assignment payloads | Hard fail for the cloud persona, sync and assignments |
| `cloud.pia-ai.de/auth/{register,forgot-password}.html` | Button in account settings / first-run wizard | same | Opens the default browser | Cosmetic |
| `127.0.0.1:{ephemeral}` | OAuth loopback redirect (`Services/AuthService.cs`) | none | Nothing — loopback | Login blocked |
| `pia-ai.de`, `/impressum.html`, `/datenschutz.html`, `docs.pia-ai.de` | About-view links (`Models/PiaLinks.cs`) | none | Opens the default browser | Cosmetic. The two legal pages are the AI Act Art. 50 transparency documents |

## 3. Model and browser downloads

Fetched once on first use and cached under `%LOCALAPPDATA%\Pia`. All GET-only, no credentials.

**`storage.pia-ai.de/f/assets/` is tried first for every row below that carries a mirror key.** One
service — `Services/Assets/AssetDownloader.cs` — owns the order: our mirror, then the upstream host
named here if that fails for any reason other than the caller cancelling. `Assets:MirrorBaseUrl` in
`appsettings.json` moves it, and blank goes straight upstream, which is the switch for a deployment
that runs no mirror of its own. The keys live in `Services/Assets/RuntimeAsset.cs` and are uploaded by
`scripts/Publish-RuntimeAssets.ps1`; `RuntimeAssetCatalogTests` pins those two lists against each other.

Because the fallback is silent, **every row here stays a live dependency** — the mirror is a control
and latency path, not a replacement. Verified against the real host on 2026-08-30: all 11 mirror keys
answer `200` and every `Content-Length` matches its upstream byte for byte, so the mirror is now the
path that actually serves (§5.1 closed the TLS failure that had made it unreachable).

| Endpoint | Trigger | Override | Cached in |
|---|---|---|---|
| `github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/…` | First local transcription, per model | none | `Models\sherpa-whisper-*`, `Models\sherpa-parakeet-tdt-v3` |
| `github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/…` | First speaker attribution | none | `Models\` |
| `github.com/snakers4/silero-vad/raw/v6.2.1/…` | First VAD use | none | `Models\` |
| `huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/…` ×3 | First embedding — vault recall | none | `Models\` |
| `github.com/rhasspy/piper` releases | First use of text-to-speech — the Piper engine | none — **no mirror key**, PiperSharp holds the URL | `Piper\piper\` |
| `huggingface.co/rhasspy/piper-voices` | Downloading a TTS voice | none — **no mirror key**, same reason | `Piper\models\<voice-key>\` |
| `cdn.playwright.dev` | First meeting-attendee join, then again whenever the pinned `Microsoft.Playwright` version changes; not reached at all when the release bundles the browser (`docs/meeting_browser_lifecycle/2026-08-29-chromium-lifecycle.md`) | `ChromiumProvisioner.DownloadHostOverride` → `PLAYWRIGHT_DOWNLOAD_HOST`; currently null, i.e. Playwright's own version-matched default | `Browsers\` |

**Every one of these redirects off the source host,** which is what an egress allowlist actually
needs:

| Source host | Terminus |
|---|---|
| `github.com/…/releases/download/…` | `release-assets.githubusercontent.com` |
| `github.com/…/raw/<tag>/…` | `raw.githubusercontent.com` |
| `huggingface.co/…/resolve/main/…` | `us.aws.cdn.hf.co` (xet bridge) |

The two Piper rows are the one place the repo does **not** own the URL. `TtsService` calls into the
PiperSharp package, which holds `huggingface.co/rhasspy/piper-voices/raw/main/voices.json` and
`github.com/rhasspy/piper` as its own constants — both hosts read out of the shipped assembly, so
they are certain, but the exact release asset PiperSharp composes is not visible from our source.
Treat those two rows as host-level, not URL-level. `cdn.playwright.dev` is the same shape for a
different reason: it is Playwright's own default, deliberately not hard-coded here so the browser
revision stays matched to the pinned package. Measured during a real install it serves
`/builds/cft/<version>/win64/chrome-win64.zip` plus a headless shell, ffmpeg and winldd.

Two spellings here are load-bearing and must not be tidied. The release tag
`speaker-recongition-models` is misspelled upstream; the corrected spelling 404s. And sherpa
publishes large-v3-turbo as the bare asset `sherpa-onnx-whisper-turbo.tar.bz2` — see §5.2.
`ModelDownloadUrlTests` pins both offline.

## 4. User-configured — unbounded

| Endpoint | Trigger | Default / preset | What leaves |
|---|---|---|---|
| `AiProvider.Endpoint` — chat and `/models` (`Services/ProviderService.cs`) | Every assistant turn; the model dropdown | Presets in `ViewModels/Models/ProviderEditModel.cs` and the first-run wizard: `api.openai.com/v1`, `openrouter.ai/api/v1`, `api.mistral.ai/v1`, `localhost:11434/v1` (Ollama), `localhost:8000/v1` (vLLM). An Azure endpoint is free-form | **The full prompt, tool results, and the API key** |
| `openrouter.ai/api/v1/models` | Saving an OpenRouter provider; the context-window snapshot | fixed, keyless on purpose | Nothing but a GET |
| `teams.microsoft.com` / `teams.live.com`, **plus every asset host the Teams web client loads** | Meeting-attendee join | user pastes the link; host allowlist in `Services/MeetingAttendee/TeamsMeetingUrl.cs` | A redirect-follow, then a real Chromium session. The asset hosts are not enumerable from this repo |
| An MCP plugin's SSE `url` | Plugin activation (HEAD ping), then tool calls | plugin config JSON | Tool arguments and results |
| An MCP stdio plugin's command | Plugin activation | plugin config JSON | Nothing directly, but an `npx`-based server fetches its package from `registry.npmjs.org` on first launch |
| Any URL in a chat message or source chip | The user clicks it | — | Handed to the default browser |

OpenRouter chat requests carry two fixed headers — `X-Title: Pia` and
`HTTP-Referer: https://github.com/Pia-Ai-dev/Pia.Wpf`. That is attribution metadata on a request the
user already initiated, not a separate call.

## 5. Open

Both entries below are now **closed** — 5.1 on 2026-08-30, 5.2 earlier. What remains genuinely open is
one design question 5.1 raised and its fix did not answer: a broken update feed and "you are up to date"
are still indistinguishable in the UI, so `UpdateService` falling back to `GithubSource` on an
*unreachable* feed is undecided.

### 5.1 Fixed: the update feed served no TLS certificate

**Resolved 2026-08-30.** ACME succeeded for the `storage` hostname — Let's Encrypt issued
`CN=storage.pia-ai.de` at 07:58 UTC that day (valid to 2026-11-28), the storage service is deployed, and
the mirror is filled. `https://storage.pia-ai.de/f/wpf/releases.win.json` answers `200`, and all 11 asset
keys were verified against their upstream `Content-Length`. Everything below is the 2026-08-29 diagnosis,
kept because it records what the failure looked like and how it was probed.

The one thing it leaves open is the design question at the end of this section: a broken feed and "you
are up to date" are still indistinguishable in the UI.

`appsettings.json` points `Update:FeedUrl` at `https://storage.pia-ai.de/f/wpf/`, and a non-blank
`FeedUrl` wins over GitHub. That is the production update path, and it does not answer:

- Port 80 responds — `Caddy`, `308 → https://…`. Port 443 aborts the handshake with
  `tlsv1 alert internal error` (alert 80) and **presents no certificate at all**. Reproduced with
  OpenSSL, with schannel/curl, and with .NET's own stack (which is what `HttpClient` uses), with and
  without SNI, on TLS 1.2 and 1.3, on `/` as well as `/f/wpf/`.
- **One site on a healthy host, not a host outage.** `pia-ai.de`, `cloud.pia-ai.de`, `docs.pia-ai.de`
  and `storage.pia-ai.de` all resolve to the same address; the first three serve valid Let's Encrypt
  certificates from that same Caddy. So this is per-site ACME issuance failing for the `storage`
  hostname — the retry loop the Caddyfile warning in the handoff doc predicts once a DNS record
  exists ahead of a working site block.

This is the expected state, not a regression: the storage service is still unmerged, and §3.1 of
`Pia/docs/storage_service/2026-08-29-storage-feed-server-handoff.md` lists deploying it as prerequisite
work. That handoff lives in the private Pia repo, beside the workflow and the service it describes, and
is the single copy — the duplicate that used to sit in this repo under `docs/update_feed/` was removed
2026-08-29 after drifting. This audit is what supplied its prerequisite 2: the DNS record is already
live and pointing at the shared Caddy host, so ACME is failing there *now*, ahead of the rollout, and
what is missing is the certificate rather than the record.

Reproduce:

```bash
openssl s_client -connect storage.pia-ai.de:443 -servername storage.pia-ai.de   # alert 80, no cert
curl -sSI http://storage.pia-ai.de/f/wpf/                                       # 308, Server: Caddy
```

Probed from one network on 2026-08-29 — worth a second vantage point before anyone touches the server.

**Worth deciding separately:** whether `UpdateService` should fall back to `GithubSource` when the
feed itself is unreachable. Today a broken feed and "you are up to date" are indistinguishable from
the UI, which is what let this sit. Note the constraint in the handoff §5: installed clients read the
`appsettings.json` in their own `current\`, so GitHub must keep receiving the full feed either way.

### 5.2 Fixed: Whisper Large 404'd on download

`LiveTranscriptionModels` mapped `WhisperModelSize.Large` to the slug `large-v3-turbo`, building
`…/asr-models/sherpa-onnx-whisper-large-v3-turbo.tar.bz2`. No such asset exists — sherpa publishes
that model as `sherpa-onnx-whisper-turbo.tar.bz2` (563 MB). Large is offered in the settings dropdown
and labelled "Whisper Large v3 Turbo", so picking it downloaded nothing and failed quietly.

Fixed 2026-08-29 by changing the slug to `turbo`. The slug reaches only the asset name and the cache
directory: `ExtractTarBz2` strips the archive's wrapping folder, and `WhisperSherpaEngine` locates
the pieces by globbing `*encoder*.onnx` / `*decoder*.onnx` / `*tokens*.txt` rather than composing a
prefix, so nothing else had to move. Confirmed against the archive listing rather than assumed: the
bundle holds `turbo-encoder.int8.onnx`, `turbo-decoder.int8.onnx` and `turbo-tokens.txt`, one match
per glob. Not yet run end-to-end through the recognizer.

## 6. No telemetry

A sweep of `src/` — the `.csproj` files included — for `sentry`, `applicationinsights`, `telemetry`,
`analytics`, `crashreport`, `appcenter`, `posthog`, `mixpanel` and `datadog` returns one hit: an
in-process observer list in `AgentTimelineService`, which never leaves the machine. There is no
crash-reporting, analytics or usage-telemetry egress of any kind.

## 7. Audited and excluded

These look like endpoints in a grep but are not. Listed so a future audit does not re-investigate them.

| String | Where | What it is |
|---|---|---|
| `pia-sync.example.com` | `Resources/Strings/ViewStrings` | Settings placeholder text |
| `https://example.com` | `Services/AssistantPromptComposer.cs` | Prompt examples |
| `https://…`, `https://url` | `Services/WebCitationExtractor.cs` | Format hints in a prompt |
| `https://evil.com/?x=teams.microsoft.com` | `Services/MeetingAttendee/TeamsMeetingUrl.cs` | A comment naming an input the host check rejects |
| `http://www.w3.org/2000/svg` | `Services/MarkdownExportService.cs` | XML namespace |
| `schemas.microsoft.com`, `schemas.openxmlformats.org`, `schemas.lepo.co` | every XAML file | XAML namespace URIs, never fetched |
| `api.nuget.org` | `NuGet.config` | Build-time only |
| `builds.dotnet.microsoft.com`, `dotnetcli.blob.core.windows.net`, `download.microsoft.com/…/vcredist_*` | `Velopack.dll` | Velopack's bootstrapper, for installing a missing .NET runtime or VC++ redist. `scripts/build-velopack.ps1` publishes `--self-contained true`, so it never fires |
| `ocsp.digicert.com`, `crl3`/`crl4.digicert.com`, `crl.microsoft.com` | the Authenticode signatures on shipped assemblies | Certificate revocation, performed by Windows when validating a signature or a TLS chain — not the app dialing out. Worth knowing on a locked-down network, where blocking them shows up as a slow start rather than an error |
| every other `github.com/<org>/<repo>` in a package assembly | `RepositoryUrl` metadata | Never fetched |

## 8. Re-running the sweep

```powershell
pwsh scripts/Test-ExternalEndpoints.ps1
```

Follows redirects, reports final status and `Content-Length` per endpoint, and exits non-zero if any
row is unhealthy. A provider host answering `401` counts as healthy — it proves the host is up, and
the script deliberately sends no key. As of **2026-08-30: 27 of 27 healthy.** The two rows that were red
on 2026-08-29 were the update feed and the asset mirror, both on `storage.pia-ai.de` and both from the
same cause; §5.1 records the fix.

## 9. Pre-fetching the downloads

```powershell
pwsh scripts/Save-RuntimeAssets.ps1              # default set, ~720 MB
pwsh scripts/Save-RuntimeAssets.ps1 -All -ListOnly
pwsh scripts/Save-RuntimeAssets.ps1 -DestinationRoot D:\pia-bundle -All
```

Fetches everything in §3 into the exact paths the app checks, so the app finds them and skips its own
download, taking the same mirror-first order the app takes (`-MirrorBaseUrl ''` forces upstream). The
asset list itself lives in `scripts/RuntimeAssetCatalogue.ps1`, shared with the publishing script
below. `-DestinationRoot` stages a bundle for an air-gapped machine — copy the resulting tree to
that machine's `%LOCALAPPDATA%\Pia`. Note that `PiaPaths` deliberately keeps downloaded artifacts on
the real profile and ignores `PIA_LOCAL_DATA_DIR`, so there is no environment variable that moves
them; the parameter exists for staging, not for redirecting a running app.

The reason it verifies rather than just downloading: the app's own presence checks are weak — a
bundle directory holding any `.onnx`, or a VAD file of non-zero length — so an interrupted download
leaves a cache the app will never re-fetch and never succeed with. Every file is written to a `.tmp`,
checked against the server's `Content-Length`, and only then moved; an existing file of the wrong
size is re-fetched, which repairs a cache poisoned by an earlier Ctrl-C.

**TTS voices are not covered.** `TtsService` gates on "the voice directory holds an `.onnx`" while
loading also needs PiperSharp's `model.json` beside it, so hand-placing the model would satisfy the
gate and then fail to load, permanently, with no self-heal. Download voices from the app's own TTS
settings instead.

## 10. Filling the mirror

**Done 2026-08-30** — all 11 keys are published and each was re-verified against its upstream
`Content-Length` on that date. The recipe below is what to re-run when an upstream project cuts a release.

```powershell
$env:PIA_STORAGE_UPLOAD_SECRET = '<the storage write secret>'
pwsh scripts/Publish-RuntimeAssets.ps1 -ListOnly     # plan: ~4.2 GB across 11 assets
pwsh scripts/Publish-RuntimeAssets.ps1
```

Run by hand, not by CI — these assets change only when an upstream project cuts a release, and the
credential grants write *and* delete on a public file service. Each asset is staged from its upstream
host, verified against that host's `Content-Length`, `PUT` to `/upload/assets/<key>`, then read back
through `/f/assets/<key>` exactly as the client will ask for it. Re-running is safe: identical bytes
answer `204`.

Three things about it are decisions, not defaults:

- **Sherpa bundles are mirrored as the `.tar.bz2`,** not as the extracted tree, so the client's
  extract step is identical whichever host answered.
- **A `409` stops the run** rather than being forced past with `-Overwrite`. The served `ETag` is
  mtime + length, so rewriting a published blob turns every in-flight resume into a full download —
  the same trap the storage server's design doc records for the release feed.
- **Writes are spaced.** `PUT /upload` and `/manage` share one 30-per-60-s window per IP and nothing
  retries a `429`.

Piper and Chromium are absent by design: PiperSharp holds its URLs internally with no override hook,
and Playwright's browser revision is pinned to the package, so mirroring it means reproducing its CDN
layout per revision. `ChromiumProvisioner.DownloadHostOverride` is the hook if that is ever wanted.
