# Nemotron-3.5 streaming ASR — update path

**Status:** Plan, not started. Gated on two decisions (see **Decision gates**); nothing below is approved.
**Owner:** Marco Altmann
**Written:** 2026-08-30
**Origin:** The "one candidate worth wanting" in
[2026-08-30-stt-stack-update-check.md](2026-08-30-stt-stack-update-check.md). That report found no newer
Whisper and no newer *multilingual* Parakeet, leaving `nemotron-3.5-asr-streaming-0.6b` as the only
upstream model that would change what the client can do rather than just which version it runs.

No checklist file yet, deliberately: step 0 is a gate that can cancel everything below it, and a tracking
surface for work that may not happen is noise. Create
`2026-08-30-nemotron-streaming-checklist.md` when the gates clear.

## Why bother

Live transcription today is **chunk-then-decode-offline**: Silero VAD cuts a segment, the segment goes to
an `OfflineRecognizer`, text comes back when the segment is finished. Nothing appears on screen while
someone is still talking, and latency is bounded below by the VAD's trailing-silence rule plus the decode.

`nemotron-3.5-asr-streaming-0.6b` is cache-aware streaming RNNT. It emits partial hypotheses as audio
arrives, at a fixed chunk cadence chosen at download time. That is a different user experience — text
that grows while a person speaks — not a quality bump.

Secondary gains: it is multilingual over 36 languages including `en`, `de`, `fr`, so it can replace
Parakeet TDT v3 rather than sit beside it; and at 453 MiB int8 it is slightly smaller than the 465 MiB
Parakeet bundle.

## What it actually is, measured

Read off the release assets and the bundled `README.md` (extracted from a 3 MB ranged GET of the 560 ms
bundle, so no full download was needed):

- Five variants, one per chunk size: `sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-{80,160,320,560,1120}ms-int8-2026-06-11.tar.bz2`,
  **453 MiB each**, published 2026-07-09. The chunk size is baked into the exported ONNX — it is not a
  runtime knob, so picking a variant *is* picking the latency.
- Bundle contents: `encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, `tokens.txt`, `README.md`.
  Same transducer shape as the Parakeet bundle, so `ResolveTransducerFile`-style file picking carries over.
- The bundled README says: *"Use per-stream language strings such as `en`, `ja`, or `auto`."*
- Upstream model card: `nvidia/nemotron-3.5-asr-streaming-0.6b`, 36 languages, `en de fr` among them.

### The sherpa API it needs

Verified by reflecting over `sherpa-onnx.dll` 1.13.5 and reading `sherpa-onnx/csrc` at tag `v1.13.5`:

- `OnlineRecognizer` / `OnlineRecognizerConfig` / `OnlineModelConfig.Transducer.{Encoder,Decoder,Joiner}`
  plus `Tokens`. Loop shape is `AcceptWaveform` → `while (IsReady(s)) Decode(s)` → `GetResult(s)`, with
  `IsEndpoint(s)` and `Reset(s)` for utterance boundaries.
- Per-stream language is `stream.SetOption("language", "de")`, or `"auto"`. The key string is literally
  `"language"` (`online-recognizer-transducer-nemo-impl.h`, `GetLanguagePromptIds`), and the model carries
  a JSON language→prompt-id map in its encoder metadata. `TargetSpeechLanguage` maps straight onto it.
- **Trap:** on the CPU provider sherpa picks the implementation by *inspecting the decoder ONNX*
  (`IsNeMoParakeetUnifiedStreaming`, then decoder output-node count), **not** from
  `ModelConfig.ModelType`. Setting `ModelType = "nemo_transducer"` the way `ParakeetSherpaEngine` does is
  harmless but load-bearing only for the QNN provider. Do not spend time debugging it.
- Endpointing is built in — `EnableEndpoint`, `Rule1/2/3MinTrailingSilence`, `Rule3MinUtteranceLength`.
  This *overlaps* the client's Silero VAD segmentation. Deciding who owns segmentation is the main design
  question below, not an implementation detail.

## Hard prerequisites

1. **sherpa-onnx ≥ 1.13.5.** Support for these models landed in 1.13.3 (#3671), and 1.13.5 carries both
   the ONNX re-export (#3732, #3734) and the greedy-search fix for NeMo streaming transducers (#3785).
   Support landed after 1.12.40, which is what is pinned today, so 1.12.40 is not expected to load these
   bundles — **untested**, no attempt was made to load one on the old version.
2. **`Microsoft.ML.OnnxRuntime` bumped in lockstep to 1.27.1.** Both packages ship
   `runtimes/win-x64/native/onnxruntime.dll`; see the update-check report for why they are one pin.
3. Which means this plan inherits that bump's own gate: **sherpa 1.13.5 changes Whisper decoding.** On one
   German clip, tiny improved and medium dropped a clause. That needs a WER pass over `artifacts/wav/`
   before the bump lands, and this work cannot start until it does.

Parakeet and CAM++ speaker embeddings are already proven bit-identical across the bump, so the WER pass
only has to cover Whisper.

## The design fork

`ITranscriptionEngine` is offline-shaped by construction:

```csharp
Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken);
```

One call, one finished string. It has three consumers: `LiveTranscriptionEngineService` (the VAD-segment
loop for meetings), `TranscriptionService` (whole-file, with its own chunking path), and
`TranscriptionEngineFactory`. A streaming recognizer does not fit that signature without throwing away
the reason to want it.

| Option | What it means | Verdict |
|---|---|---|
| **A. Wrap it as offline** | New engine implements `ITranscriptionEngine`; feed the whole VAD segment, drain, return the final text. Zero changes outside the engine and the factory. | Cheapest, and pointless on its own — same UX as today, just a different model. Useful only as a stepping stone that proves model load and language selection. |
| **B. Second interface** | Add `IStreamingTranscriptionEngine` (`Feed`, `Partial`, `Endpoint`). `LiveTranscriptionEngineService` uses it when the engine offers it, falls back to `ITranscriptionEngine` otherwise. `TranscriptionService` keeps the offline path via A. | **Recommended.** Keeps Whisper and Parakeet untouched, keeps the file-transcription path untouched, and the fallback means a half-finished streaming path cannot break meetings. |
| **C. Make everything streaming** | Recast `ITranscriptionEngine` as streaming and adapt Whisper/Parakeet behind it. | No. Whisper is inherently offline; the adapter would be a buffer pretending to stream, and it touches every consumer. |

Under B, segmentation stays with Silero VAD (it already feeds speaker attribution, which needs segment
boundaries and cannot be handed sherpa's endpointer without redoing that work). Sherpa's `EnableEndpoint`
stays **off**; nemotron's job is to fill in text *within* a segment the VAD has already opened.

## Touch points

Adding an `SttBackend` value is a six-place change plus assets. From the current tree:

- `src/Pia.Wpf/Models/AppSettings.cs:19` — the `SttBackend` enum.
- `src/Pia.Wpf/Converters/EnumToLocalizedStringConverter.cs:20` — new `Enum_Stt…` key.
- The three resx files (en/de/fr) — parity is test-enforced; do not hand-edit `Designer.cs`.
- `src/Pia.Wpf/ViewModels/GeneralSettingsViewModel.cs` — `IsWhisperSelected` / `IsParakeetSelected` at
  :144–145 gate the per-backend UI; a third needs the same treatment, plus the chunk-size picker.
- `src/Pia.Wpf/Views/SettingsViews/GeneralView.xaml:302` — the backend combo already binds
  `SttBackends`, so it picks the new value up for free; a chunk-size combo is new and needs an
  `AutomationProperties.AutomationId` plus its `[InlineData]` row in `ViewAutomationIdTests`.
- `src/Pia.Wpf/Services/LiveTranscription/TranscriptionEngineFactory.cs:21` — the `switch`, and
  `EnsureModelsAsync` at :51, which is a two-way `settings.SttBackend == Parakeet` ternary and has to
  become a switch.
- `src/Pia.Wpf/Services/VoiceInputService.cs:187,194,204` — three more `== Parakeet` ternaries in the
  download/first-run path, same shape.
- `src/Pia.Wpf/Services/LiveTranscription/DirectTranscriptionService.cs:922` — one more.

Assets, and this is the part that bites: **each model URL is pinned in four places** and they are
compared against each other by tests.

- `LiveTranscriptionModels` — the URL constant plus `Ensure…Async` / `Is…Available` helpers.
- `scripts/RuntimeAssetCatalogue.ps1` — a new group with `MirrorKey` and `SizeHint`.
- `src/Pia.Wpf/Services/Assets/RuntimeAsset.cs` — a `RuntimeAssetCatalog` entry, added to `All`.
- `tests/.../ModelDownloadUrlTests.cs` and `RuntimeAssetCatalogTests` — the pins and the key comparison.

Because the chunk size is part of the *file name*, a user-selectable chunk size means **five** catalogue
entries, five mirror keys and 2.2 GiB of mirror traffic. Shipping one fixed variant is one entry and
453 MiB. Strong argument for fixing the variant.

## Decision gates

| Gate | Question it answers | Cancels |
|---|---|---|
| **G0** | Does sherpa 1.13.5 hold up on the Whisper WER pass, and does the bump land? | Everything. Nemotron cannot load on 1.12.40. |
| **G1** | Fixed chunk size, or user-selectable? | If fixed: the settings work, the chunk-size combo, four catalogue entries and 1.8 GiB of mirror traffic. |
| **G2** | Does nemotron beat Parakeet TDT v3 on German, measured on `artifacts/wav/`? | If not, stop after step 3 — keep it as an option, do not make it the default or replace Parakeet. |

Recommendation on G1: **fix it at 560 ms**. It is the middle of the range, and 80 ms buys latency the UI
cannot show anyway — the transcript view does not repaint per 80 ms. Revisit only if a real meeting feels
laggy. (No variant has been loaded yet; 560 ms is only the one whose bundled README was read.)

## Steps

Effort: `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface.
Value: `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

- [ ] **1. Land the package bump.** sherpa 1.13.5 + ORT 1.27.1, after the Whisper WER pass.
      *Deps:* G0 · *Effort:* XS · *Value:* Enabler
- [ ] **2. Mirror one nemotron bundle.** Add the 560 ms int8 asset to the catalogue in all four places and
      publish it to `storage.pia-ai.de`. *Deps:* 1, G1 · *Effort:* XS · *Value:* Enabler
- [ ] **3. Offline-shaped engine first.** `NemotronSherpaEngine : ITranscriptionEngine` over
      `OnlineRecognizer` — feed the segment, drain, return final text, with
      `SetOption("language", …)` wired to `TargetSpeechLanguage`. Proves model load, language selection
      and the transducer file picking without touching any consumer. *Deps:* 2 · *Effort:* S ·
      *Value:* Enabler
- [ ] **4. Measure it against Parakeet.** Same clips, same harness as the update-check probe. This is G2.
      *Deps:* 3 · *Effort:* S · *Value:* High
- [ ] **5. Wire it into settings.** Enum value, loc keys in three resx, the four `== Parakeet` ternaries,
      the download path. Selectable but still offline-shaped. *Deps:* 4 · *Effort:* S · *Value:* Med
- [ ] **6. `IStreamingTranscriptionEngine` + partials in the live path.** The actual feature: partial text
      inside an open VAD segment, with `LiveTranscriptionEngineService` falling back for engines that do
      not implement it. *Deps:* 5 · *Effort:* M · *Value:* High
- [ ] **7. Human smoke test on a real meeting.** Non-negotiable — a green build proves nothing about
      streaming behaviour under real audio. *Deps:* 6 · *Effort:* XS · *Value:* High

## Suggested order

Cheapest decisive work first: 1 → 2 → 3 → **4 (stop and look)** → 5 → 6 → 7.

Step 4 is the decision point. Steps 1–4 are ~2–3 days and answer "is this model actually better on German
than what we ship". If it is not, stop there: the engine exists, nothing is exposed to users, and no UI or
interface work was spent. Only steps 6–7 deliver the streaming UX, and only they justify the `M`.

## Open questions

- **Does the 12 s Parakeet-vs-nemotron comparison need ground truth?** There is no reference transcript for
  the `artifacts/wav/` fixtures — the speaker-attribution fixture provides speaker *labels*, not text. So
  step 4 can only compare models against each other, or against Whisper medium as a pseudo-reference.
  Worth deciding before step 4, not during it.
- **Does nemotron punctuate and capitalise?** Parakeet TDT v3 does, and the Summarize prompt downstream
  benefits. Unverified for nemotron; step 3 answers it as a side effect.
- **Speaker attribution interaction.** Attribution keys off VAD segment boundaries. Under option B those
  are unchanged, so the expectation is "no effect" — but it is an expectation, not a measurement, and the
  attribution work has a history of changes that improve one metric while moving another. See
  [../speaker_attribution/2026-08-22-attribution-levers-brief.md](../speaker_attribution/2026-08-22-attribution-levers-brief.md).
