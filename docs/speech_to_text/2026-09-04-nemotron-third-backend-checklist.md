# Nemotron-3.5 as a third STT backend — checklist

**Status:** Steps 1-9 complete. **G1 passed 2026-09-04.** Step 10 is all that is left, and
it needs a human at the machine — see the step itself.
**Owner:** Marco Altmann
**Written:** 2026-09-04
**Origin:** [2026-09-04-nemotron-third-backend-plan.md](2026-09-04-nemotron-third-backend-plan.md),
which is the executable plan. This file is the tracking surface — tick each box in the commit that
lands it.

**Scales.** *Effort:* `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a
new surface · `L` a week or more, a new subsystem. *Value:* `High` user-visible or a real risk
closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | What it can cancel |
|---|---|---|
| **G1** | Does nemotron beat or match Parakeet TDT v3 on German? | Everything from step 6 down. **Answered 2026-09-04: yes.** Matches Parakeet on substantive speech, punctuates and capitalises, RTF 0.35. Drops 14 of 37 very short utterances, against Parakeet inventing English on those same ones. See [2026-09-04-nemotron-german-comparison.md](2026-09-04-nemotron-german-comparison.md). |
| **G2** | Does the 560 ms chunk cadence produce text that *grows* rather than lurches? | Nothing, but a "no" sends step 1 back through all four URL pin sites for the 320 ms variant. **Provisionally yes, 2026-09-04**, from `Bench_GrowsAPartialWhileSpeechIsStillRunning` replaying a recording at real-time pace: `So` -> `So das fangen wir` -> `So das fangen wir Selbstportal`. That is the cadence question answered on real audio; step 10 still has to confirm it reads that way on screen. |

Do not tick a dependant of an open gate without revisiting it.

## Phase 1 — the third backend (shippable on its own)

- [x] **1. Pin the nemotron bundle.** Add the 560 ms int8 asset to `LiveTranscriptionModels`,
      `RuntimeAssetCatalog`, `scripts/RuntimeAssetCatalogue.ps1` and `ModelDownloadUrlTests`, then
      confirm the URL returns 200 by hand.
      *Deps:* — · *Effort:* XS · *Value:* Enabler

- [x] **2. Add the `Nemotron` backend value and route all eight `SttBackend` sites.** Append the enum
      member last, then fix five two-way `== SttBackend.Parakeet` ternaries that silently mean
      "Whisper" on a third value — including the one that stamps the model id into saved meeting
      metadata.
      *Deps:* 1 · *Effort:* S · *Value:* Enabler

- [x] **3. Build `NemotronStreamingEngine` behind `ITranscriptionEngine`.** Wrap `OnlineRecognizer`,
      decode a whole VAD segment to final text, and route both factory methods to it.
      *Deps:* 2 · *Effort:* S · *Value:* High

- [x] **4. Wire the settings UI.** Add `DownloadNemotronModelAsync` through the service interface,
      `IsNemotronSelected` and the download command on the ViewModel, and the panel in
      `GeneralView.xaml` with its automation id.
      *Deps:* 3 · *Effort:* S · *Value:* High

- [x] **5. Measure it against Parakeet on German.** Transcribed 180 s of a German standup recording
      with all three backends through a new `[BenchFact]` harness — `artifacts/wav/` no longer exists
      on any checkout, so the recording replaced it. **This is G1, and it passed.**
      *Deps:* 4 · *Effort:* XS · *Value:* High

## Phase 2 — the streaming UX (gated on G1)

- [x] **6. Add the `IStreamingTranscriptionEngine` contract.** Define the per-source session
      interface and implement it on the nemotron engine — the session queues frames onto its own
      drain task so a segment-final decode holding the shared gate can never park the audio capture
      reader.
      *Deps:* 5 (G1) · *Effort:* M · *Value:* Enabler

- [x] **7. Pump partials from `LiveTranscriptionEngineService`.** Feed the reader loop's frames to
      the session, raise `PartialTextChanged` on change, reset on speech end, dispose the session.
      *Deps:* 6 · *Effort:* S · *Value:* Enabler

- [x] **8. Surface partials in `TranscriptOverlayViewModel`.** Add `PartialText` outside the journal
      and clear it when the final utterance lands, so `RebuildBubblesFromJournal` stays equivalent to
      the incremental path.
      *Deps:* 7 · *Effort:* S · *Value:* High

- [x] **9. Render the draft text in both overlays.** Dimmed italic below the bubble list, hidden when
      empty, visually distinct from committed text.
      *Deps:* 8 · *Effort:* XS · *Value:* High

- [ ] **10. Prove it on the real desktop and write the release notes.** **Partly done.** The UI
      script is written (`tests/ui-scripts/scripts/settings-stt-backend.json`) and every selector in
      it was driven against the running app, which is what caught that
      `Settings_General_SttEngine` needs `optionIndex` rather than `optionText`. `RELEASE.md` is
      rewritten. **Still open, and it needs a person at the machine:** replaying the script through
      `Invoke-UiScripts.ps1` (WinWright's `Civyk.WinWright.Mcp.exe` is not installed on the dev box),
      and driving a real meeting with live speech plus a speaker rename to watch the partial grow and
      confirm the rebuild neither duplicates nor drops it. **This is G2.**
      *Deps:* 9 · *Effort:* XS · *Value:* High

## Suggested order

Straight through, 1 → 10. The dependency chain is genuinely linear here — every step needs the one
before it — so there is no cheaper decisive work to front-load and no parallel slice to peel off.

Two places to stop and think rather than continue on momentum:

- **After step 4.** Phase 1 is shippable. Ship it, or at least commit it, before spending step 5's
  measurement time. If G1 then says no, four steps of work still landed a third working backend.
- **After step 8.** The journal invariant is the one thing in this plan that can break existing,
  working behaviour (speaker reassignment and rename). Run the full `Pia.Tests.ViewModels` namespace
  before moving on, not just the new test.

## Not yet planned

- **Hotwords / attendee-name biasing on the live path.** Not possible with this model in
  sherpa-onnx 1.13.5 — the streaming NeMo implementations have no context graph, and the only
  decoding method that would carry one calls `exit(-1)`. If name accuracy becomes a real complaint,
  the shape that works is nemotron for live partials plus offline Parakeet with `HotwordsFile`
  re-decoding the closed VAD segment for the committed text. That is a plan of its own, and it is a
  further reason Parakeet stays rather than being replaced.
- **Decoding each segment once instead of twice.** With the streaming UX on, the preview decode and
  the segment-final decode both run over the same audio. Taking the running hypothesis as the final
  would halve the CPU, but it bypasses the segment loop where diarization and `SegmentId` assignment
  live — a reshaping of `LiveTranscriptionEngineService`, not an optimisation. Step 5's timings say
  whether it is worth planning.
- **Retiring a backend.** Three engines is three download paths, three settings branches and three
  things to regression-test. Worth revisiting once G1 has a number, but not before.
- **Sherpa's own endpointer.** `EnableEndpoint` stays 0 and Silero VAD keeps segmentation. Handing
  boundaries to sherpa would mean redoing diarization, `IsSpeakingChanged` and the echo gate against
  a different event source — a subsystem swap, not a setting.
