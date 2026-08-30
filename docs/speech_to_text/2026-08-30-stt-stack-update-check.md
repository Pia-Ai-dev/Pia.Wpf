# sherpa-onnx / Parakeet / Whisper — update check

**Status:** Findings only, nothing changed. Five follow-ups proposed, none started.
**Owner:** Marco Altmann
**Written:** 2026-08-30
**Origin:** Ask to "check our sherpa / parakeet / whisper setup for updates". Supersedes rows 12–14 of
[../plans/2026-08-16-nuget-update-audit.md](../plans/2026-08-16-nuget-update-audit.md), which named the
same two package bumps but called them "verifiable only in a live transcription run" — they are now measured.

## Verdict

Nothing is broken and nothing is urgent. Every pinned URL still resolves, and the model bundles are
byte-for-byte the sizes recorded on 2026-08-29.

One bump is worth taking — `org.k2fsa.sherpa.onnx` 1.12.40 → 1.13.5, with `Microsoft.ML.OnnxRuntime`
1.24.4 → 1.27.1 in lockstep. It compiles clean, and Parakeet plus speaker embeddings are bit-identical
across it. It is **not** free: Whisper decoding changed, measurably, in both directions on the one clip
tested. That needs a WER pass over the bench fixtures before it lands.

No newer Whisper exists. No newer *multilingual* Parakeet exists. The one genuinely new model worth
wanting is `nemotron-3.5-asr-streaming-0.6b`, and it is a feature, not a bump.

## What is pinned today

| Thing | Pinned | Where |
|---|---|---|
| `org.k2fsa.sherpa.onnx` | 1.12.40 (2026-04-24) | `src/Pia.Wpf/Pia.Wpf.csproj:44` |
| `Microsoft.ML.OnnxRuntime` | 1.24.4 | `src/Pia.Wpf/Pia.Wpf.csproj:52` |
| Whisper tiny/base/small/medium/large | `sherpa-onnx-whisper-{tiny,base,small,medium,turbo}.tar.bz2` | `LiveTranscriptionModels.WhisperBundleUrl` |
| Parakeet | `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2` | `LiveTranscriptionModels.ParakeetBundleUrl` |
| Speaker embedding | `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx` | `LiveTranscriptionModels.SpeakerEmbeddingUrl` |
| Silero VAD | `snakers4/silero-vad` @ **`raw/master`** | `LiveTranscriptionModels.SileroVadUrl` |

Each model URL is pinned in four places — the constant, `scripts/RuntimeAssetCatalogue.ps1`,
`ModelDownloadUrlTests`, and the mirror-key comparison in `RuntimeAssetCatalogTests`. Any change touches
all four **and** requires a re-publish to `storage.pia-ai.de`.

## Health of the current pins

`pwsh scripts/Test-ExternalEndpoints.ps1` → **all 27 endpoints healthy**. Every model bundle returns 200
with a `Content-Length` matching its `SizeHint` exactly, so no asset has been silently republished.

Two `Note` strings are now stale and should be dropped: the update feed
(`storage.pia-ai.de/f/wpf/releases.win.json`) answers 200 with 736 bytes, and the asset mirror
(`.../f/assets/models/silero_vad.onnx`) answers 200 with 2 327 524 bytes. Both still say "not deployed
yet / nothing published to it yet". They live in `scripts/Test-ExternalEndpoints.ps1` and
[../external_endpoints/2026-08-29-external-endpoint-inventory.md](../external_endpoints/2026-08-29-external-endpoint-inventory.md)
section 10, and both files already carry uncommitted edits — left alone here.

## The package bump

Latest on NuGet: sherpa-onnx **1.13.5**, ONNX Runtime **1.29.0**. Upstream tagged v1.13.6 on 2026-08-18
but never pushed it to NuGet (the `.nupkg` 404s), so 1.13.5 is the ceiling.

### The two packages are one decision, and the reason is a file collision

`org.k2fsa.sherpa.onnx.runtime.win-x64` and `Microsoft.ML.OnnxRuntime` both ship
`runtimes/win-x64/native/onnxruntime.dll`. One copy reaches the output directory and both halves of the
stack use it. Measured, by building the same probe three ways and reading the DLL that landed:

| sherpa | its bundled ORT | managed ORT | DLL in output |
|---|---|---|---|
| 1.12.40 | 1.24.4 (16 036 864 B) | 1.24.4 (14 203 464 B) | **Microsoft's 1.24.4** |
| 1.13.5 | 1.27.1 (17 378 304 B) | 1.24.4 | **sherpa's 1.27.1** |
| 1.13.5 | 1.27.1 | 1.27.1 (15 383 864 B) | **Microsoft's 1.27.1** |

In these three configurations the higher-versioned native DLL landed, and on equal versions Microsoft's
did. That is the observation, not a resolution rule — two of the three rows are ties, so the tie-break
could equally be package-name ordering or `PackageReference` order rather than anything about versions.
Do not rely on it surviving a csproj reshuffle. The consequence, however, holds regardless of mechanism:

- The current 1.24.4 pin is not arbitrary — it is exactly the ORT that sherpa 1.12.40 bundles. The pair
  is deliberately aligned.
- Bumping sherpa alone silently raises the *native* ORT to 1.27.1 while `EmbeddingService` and
  `SileroVadDetector` keep loading the 1.24.4 *managed* assembly. That combination does work (ORT keeps
  ABI back-compatibility) but it is not a combination Microsoft ships or tests. Bump both to 1.27.1, not
  to 1.29.0 — 1.27.1 is what sherpa 1.13.5 was built against.

### What it costs to compile: nothing

The whole sherpa API surface the client uses is four types — `OfflineRecognizer`,
`OfflineRecognizerConfig`, `SpeakerEmbeddingExtractor`, `SpeakerEmbeddingExtractorConfig` — across
`ParakeetSherpaEngine`, `WhisperSherpaEngine`, `SherpaEmbeddingExtractor` and
`SpeakerIdentificationService`. No config field any of them sets was renamed.

`dotnet build src/Pia.Wpf/Pia.Wpf.csproj -t:Rebuild` at 1.13.5 / 1.27.1: **0 Warning(s), 0 Error(s) in
both Debug and Release**. Reverted afterwards; the working tree and `bin/` are back on 1.12.40 / 1.24.4.

### What it costs at model load: measured, on real models

A throwaway console probe loaded the models already in `%LOCALAPPDATA%\Pia\Models` and decoded the
loudest 12 s window of `artifacts/wav/lsp-replay.wav` (German). Run in both orders to rule out
cold-cache effects.

| Check | 1.12.40 / 1.24.4 | 1.13.5 / 1.27.1 |
|---|---|---|
| managed ORT loads `silero_vad.onnx` | OK | OK |
| managed ORT loads the text-embedding model | OK | OK |
| CAM++ speaker embedding | dim 192, `[0.6317, -0.3841, 0.4065]` | **identical** |
| Parakeet TDT v3 decode | full sentence | **identical, character for character** |
| Whisper tiny decode | see below | changed |
| Whisper medium decode | see below | changed |

Decode latency is unchanged within noise — Parakeet 2.4–3.1 s and Whisper medium 14.0–19.5 s across runs
regardless of version. No speed claim either way.

A third configuration was built to isolate which package moved the Whisper output: sherpa **1.13.5** with
the managed ORT left at **1.24.4** (so the native DLL is sherpa's 1.27.1). Its Whisper output is identical
to the 1.13.5 / 1.27.1 column, on both tiny and medium. Two different managed ORT versions agreeing with
each other and disagreeing with 1.12.40 attributes the change to **sherpa 1.13.5, not to ONNX Runtime**.

### The one real blocker: Whisper output changed

Reproduced in both run orders.

Whisper **tiny** got better. It now agrees with Parakeet and with medium on a clause it previously
invented a sentence break in:

- 1.12.40: `… mehr als ein. Das ist auch immer noch schwachstehliche Systeme.`
- 1.13.5: `… mehr als ein und dann wird es auch immer noch schwachstelle Systemen.`

Whisper **medium** got worse. It dropped a clause the older version transcribed, and which Parakeet
independently confirms is really there (`Anderes Thema. So, also das heißt …`):

- 1.12.40: `… Schwachstelle unterstehen. Ein anderes Thema. So, das heißt, was haben wir jetzt gelernt?`
- 1.13.5: `… schwachstellen. Das heißt, was haben wir jetzt gelernt?`

One clip is not a verdict. But it disproves "behaviour-neutral", so the bump needs a WER comparison over
the replay fixtures in `artifacts/wav/` before it ships — see
[../speaker_attribution/2026-08-21-speaker-attribution-measurements.md](../speaker_attribution/2026-08-21-speaker-attribution-measurements.md)
for the harness and the traps.

### What the bump actually buys

From the 1.13.0–1.13.5 notes, in paths the client runs:

- **Windows non-ASCII paths.** #3710 and #3255 replaced narrow-char Win32 file calls with wide-char /
  `std::filesystem`. Model paths run through `%LOCALAPPDATA%\Pia\Models`, which contains the Windows
  account name, so those fixes close a failure mode the client is exposed to. **Not tested here** — no
  attempt was made to reproduce a model-load failure under a non-ASCII account name, so treat this as a
  risk closed, not a bug observed.
- **Transcript whitespace.** #3709 removes spaces before punctuation, #3711 removes leading spaces from
  ASR results. Both apply to every transcript the client produces.
- **NeMo streaming transducer greedy search** (#3785) and **FireRedASR KV-cache sizing** — needed only by
  models the client does not use yet.

Explicitly *not* a reason to bump, despite looking like one: the speaker-diarization SIGSEGV bounds check
(#3563) and the pyannote window-shift knob (v1.13.6) are in sherpa's `OfflineSpeakerDiarization`, which
the client never constructs. Pia extracts embeddings with `SpeakerEmbeddingExtractor` and clusters them
itself.

## Models: what is new upstream

The `asr-models` release holds 499 assets. Diffed by name and creation date against the six pinned slugs.

**Whisper — nothing new.** The newest Whisper assets are `distil-large-v3` and `distil-large-v3.5`
(2025-08-17, ~505 MiB), both English-only. The five pinned bundles are still the current ones.

**Parakeet — newer, but English-only.** `parakeet-unified-en-0.6b-int8` shipped non-streaming
(2026-04-27) and streaming at 240/560/1120 ms (2026-05-12), 478 MiB each, plus
`parakeet_tdt_transducer_110m-en` (2026-05-03, 103 MiB int8). All English. The pinned
`parakeet-tdt-0.6b-v3-int8` remains the newest multilingual Parakeet, so for a German-first product it is
still the right choice.

**Speaker embedding — nothing new.** Every asset in the `speaker-recongition-models` release dates from
2024-10-14. The pinned CAM++ model is current.

**The one candidate worth wanting:** `sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-{80,160,320,560,1120}ms-int8`
(2026-07-09, 453 MiB each). NVIDIA's model card lists 36 languages including `en de fr`. It is
**cache-aware streaming** RNNT, so it would give partial results as someone speaks instead of the current
VAD-chunk-then-decode-offline loop. Adopting it means a new engine class on sherpa's `OnlineRecognizer`
— `ParakeetSherpaEngine` and `WhisperSherpaEngine` are both `OfflineRecognizer` — and it needs sherpa
≥ 1.13.3 for the model support plus 1.13.5 for the re-export and the greedy-search fix. That is a
feature, not an upgrade.

Two cheaper curiosities, both ~102 MiB int8 and both from 2026-05-03:
`nemo-fast-conformer-transducer-en-de-es-fr-14288-int8` covers exactly the client's three UI languages at
a fifth of Parakeet's download, and `nemo-transducer-stt_de_fastconformer_hybrid_large_pc-int8` is
German-only with punctuation and capitalisation. Neither is benchmarked here.

Mirror ceiling: any new bundle has to clear `storage.pia-ai.de`'s 2 GiB body limit. Whisper medium is
already at 1.80 GiB. Every model named above is under 500 MiB, so none is blocked.

## Silero VAD: pinned to a branch, but stable in practice

`SileroVadUrl` pointed at `raw/master`, which is not a pin — the only asset in the catalogue that could
change without any URL changing.

**Re-pinned to `v6.2.1` on 2026-08-30**, and the swap is provably a no-op. SHA256 of `silero_vad.onnx`
is `1a153a22f4509e29…` at all four of: the `v6.2.1` tag, `master`, the `storage.pia-ai.de` mirror copy,
and the local `%LOCALAPPDATA%\Pia\Models` cache. Same 2 327 524 bytes at `v5.1.2` as well. So no mirror
re-upload is needed and no installed client re-downloads anything — only the upstream fallback URL moved.

## Suggested order

Effort: `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface.
Value: `High` user-visible or a real risk closed · `Med` worthwhile, not headline.

1. ~~**Fix the two stale endpoint notes.**~~ **Done 2026-08-30** — and it was worse drift than a note:
   `storage.pia-ai.de` got its Let's Encrypt certificate that morning, so §5.1 of the endpoint inventory
   ("the update feed serves no TLS certificate") was resolved, not open. Corrected there too.
2. ~~**Pin Silero to `v6.2.1`.**~~ **Done 2026-08-30** — four files, proven byte-identical by SHA256.
3. **WER pass on sherpa 1.13.5 / ORT 1.27.1.** *Deps:* — · *Effort:* S · *Value:* High. Decide the bump
   on the `artifacts/wav/` fixtures rather than on one clip. Parakeet and speaker embeddings are already
   proven bit-identical, so the pass only has to cover Whisper.
4. **Bump both packages, together, to 1.13.5 / 1.27.1** if step 3 holds. *Deps:* 3 · *Effort:* XS ·
   *Value:* High. Buys the Windows wide-char path fix and the transcript-whitespace fixes. Needs a human
   smoke test of a real meeting — a green build proves nothing about model load, which is why step 3's
   probe exists.
5. **Evaluate `nemotron-3.5-asr-streaming-0.6b`.** *Deps:* 4 · *Effort:* M · *Value:* High, speculative.
   A streaming engine on `OnlineRecognizer` would change live transcription from chunked to continuous.
