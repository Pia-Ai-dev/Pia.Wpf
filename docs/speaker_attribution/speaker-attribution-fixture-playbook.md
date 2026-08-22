# Playbook: turning a meeting into scoreable diarization data

Living document — rewritten in place, not superseded, so it carries no date.

Use this to add a recording to the speaker-attribution fixture, or to re-measure an existing one.
Results and the reasoning behind them live in `2026-08-21-speaker-attribution-measurements.md`.

A WAV on its own proves nothing. Scoring accuracy needs three artifacts, and the third is the only
one that costs real time:

| Artifact | Where | Cost |
|---|---|---|
| the audio the pipeline heard | `artifacts/wav/<name>-replay.wav` | free, a flag on the run |
| the answer key | `scripts/speaker-reference/<name>.reference.json` | one command, needs a layout |
| the layout the answer key is read through | `scripts/speaker-reference/<name>.layout.json` | **hand-measured, ~1 hour** |

## Prerequisites

- A **Debug** build. `DebugWavTeeAudioCaptureService` sits inside `#if DEBUG`, so a Release build
  cannot record audio at all.
- `pwsh` 7+.
- `ffmpeg` and `ffprobe` on `PATH`, for reading the *video* when building the answer key. This is not
  the audio path — the pipeline's own Media Foundation decode is the thing under test and must never be
  replaced by ffmpeg. Reading a video's pixels is a different job.

## Privacy — read once, then it is automatic

Recordings, teed WAVs and cached embeddings are the most sensitive artifacts this repo produces: real
colleagues' voices and biometric vectors.

- They live under `artifacts/`, which is gitignored. Never move them under `scripts/`.
- Real names live only in `scripts/speaker-reference/*.names.local.json`, which is gitignored by name.
  Everything committed identifies speakers by tile position (`A`, `B`, `C`…).
- The embedding cache holds no names — segment offsets only. `*.embeddings.bin` is gitignored.

## 1. Get the audio

### From a live meeting

Set the dump variable and **no** replay path, then join a meeting normally:

```powershell
$env:PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP = 'C:\projects\Pia.Wpf\artifacts\wav\<name>.wav'
```

Use an absolute path. The app runs from its own working directory, so a relative path lands under
`bin/`, where a rebuild deletes it. Repeat captures inside one run get a number appended, so nothing
silently overwrites.

This is the only way to capture what Pia *actually* hears: device loopback of a browser tab, with a
second D/A→A/D pass and Teams' own AGC. Everything in `artifacts/meeting_recording/` is cloud-mixed
Teams audio instead, which is a different signal. How much easier the cloud mix is has never been
measured — a live dump replayed through the bench is what answers that.

### From an existing recording

```powershell
./scripts/Invoke-MeetingReplay.ps1 `
    -AudioPath 'artifacts/meeting_recording/<recording>.mp4' `
    -RosterSize <humans excluding Pia> `
    -RunName <name> `
    -DumpPath artifacts/wav/<name>.wav
```

Unattended, but it drives the real desktop by UI Automation for the length of the recording at ~0.83x
real time — budget 20 minutes per 16 minutes of audio. It runs against a throwaway profile seeded from
your real `%APPDATA%\Pia` with sync forced off, so your account is never touched.

**Stay off the machine while it runs.** The pipeline transcribes at roughly 1.04x speech time; your
build competing for CPU manufactures the very dropped segments you may be trying to measure.

Two outputs, both wanted:

- the WAV, written as `<name>-replay.wav` — the tee adds the suffix when a replay is being teed
- a fresh log, printed at the end, plus a `LABEL CHECK:` line reading the on-screen speaker numbering

Check the WAV before trusting it. At 16 kHz mono 16-bit it is exactly 32000 bytes per second:

```powershell
(Get-Item artifacts/wav/<name>-replay.wav).Length / 32000   # ≈ the recording's duration
```

A couple of seconds short is AAC decoder priming and padding. Two or three times the expected size
means stereo or 48 kHz, i.e. the tee is wrong rather than the decoder.

## 2. Build the answer key

Teams burns an indigo pill behind the speaking participant's name label. The extractor samples each
tile's name-label rect and classifies the pill by mean colour, which is why the layout has to be exact.

**The layout, hand-measured once per recording.** Open a frame where the grid is settled, measure each
name-label rect in pixels, and write:

```json
{
  "name": "<name>",
  "note": "Teams 2x5 avatar grid right of a screen share, 1920x1080 letterboxed. Tile ids run left-to-right, top-to-bottom. Rects cover each tile's name label, where Teams paints the active-speaker pill.",
  "frameWidth": 1920,
  "frameHeight": 1080,
  "tiles": [
    { "id": "A", "x": 1687, "y": 343, "w": 100, "h": 26 },
    { "id": "B", "x": 1795, "y": 343, "w": 100, "h": 26 }
  ]
}
```

This is the hour. Everything after it is a command.

```powershell
./scripts/Get-SpeakerReference.ps1 `
    -VideoPath 'artifacts/meeting_recording/<recording>.mp4' `
    -LayoutPath scripts/speaker-reference/<name>.layout.json `
    -OutputPath scripts/speaker-reference/<name>.reference.json
```

Then hand-read the tile-to-name map once, into a file that is never committed:

```json
{ "layout": "<name>", "note": "Local only. Never commit.", "names": { "A": "…", "B": "…" } }
```

Judge the result by what it *refuses* to claim. The output keeps simultaneous highlights as overlap
truth rather than collapsing them, and parks frames where the grid moved into `invalidRanges` instead
of mislabelling them. A recording that loses a lot of time to `invalidSeconds` or `silenceSeconds` is a
weak reference — the workshop loses 95 s to a layout change and 191 s to silence, and that is enough to
make a fitted alignment wander 15 points.

## 3. Bench it

Runs the production segmenter, embedder and identification service over the WAV with no STT, no UI and
a stream-time clock. About 100x real time — a 50-minute recording takes half a minute.

```powershell
$env:PIA_BENCH_WAV       = (Resolve-Path 'artifacts/wav/<name>-replay.wav').Path
$env:PIA_BENCH_ROSTER    = '<humans>'
$env:PIA_BENCH_REFERENCE = (Resolve-Path 'scripts/speaker-reference/<name>.reference.json').Path
$env:PIA_BENCH_OUT       = (Join-Path (Get-Location) 'artifacts/wav/bench')
$env:PIA_BENCH_MATCH     = '0.30,0.345'   # optional: fixed match thresholds, one run each
dotnet test -- --explicit only --filter-method "*Bench_MeasuresARecording*"
```

Set every one you need in **one** call — each PowerShell invocation is a fresh shell, and a missing
`PIA_BENCH_WAV` makes the test skip rather than fail, which reads like success.

Outputs land in `PIA_BENCH_OUT`: one `<name>.<setting>.segments.jsonl` and `.passes.log` per
setting, plus a shared `<name>.report.txt` and `<name>.embeddings.bin`. With `PIA_BENCH_MATCH` unset the
single setting is `derived` — the shipping policy, which recomputes the threshold from each pass's cut.
The first run computes every embedding; later runs reuse the cache and cost milliseconds.

## 4. Score it

Same scorer for both inputs, so the app and the bench stay comparable:

```powershell
# the app's log
./scripts/Measure-SpeakerAttribution.ps1 -LogPath <log> `
    -ReferencePath scripts/speaker-reference/<name>.reference.json `
    -NameMapPath scripts/speaker-reference/<name>.names.local.json

# the bench's segments
./scripts/Measure-SpeakerAttribution.ps1 -SegmentsPath artifacts/wav/bench/<name>.derived.segments.jsonl `
    -ReferencePath scripts/speaker-reference/<name>.reference.json
```

Use the reference belonging to the run. Scoring one recording against another's key produces a
confident, meaningless number.

Add `-Provisional` to a bench run to score the instant label instead of the corrected one — the only
part of a run the match threshold owns (trap 7). An app log does not carry it, so the switch refuses
`-LogPath`.

The header must read:

```
Align   : exact — every identified segment reported its own stream position
```

If it prints a fitted offset instead, the log predates `start=` on the identify line and the number
carries an alignment caveat. Treat a fitted figure as indicative only.

## Traps that silently invalidate a measurement

Each of these has produced a plausible wrong number at least once.

1. **An empty replay roster.** Without `PIA_DEBUG_MEETING_ATTENDEE_ROSTER` the run measures
   `expected=0` and the speaker-count ceiling is off. `Invoke-MeetingReplay.ps1` sets it from
   `-RosterSize`; a hand-rolled run must set it too.
2. **A fitted alignment on a weak reference.** It can saturate its own ±6 s search bound and report the
   clamp as a fit. Check the printed offset against that bound; prefer exact alignment always.
3. **A degenerate oracle.** Sanity-check `d'` before believing any bound. `d' NaN` with a 50 %
   pair-decision error is chance, not a result — it once reported 86.3 % from zeroed vectors and
   pointed the whole plan at the wrong work.
4. **Pooled oracle accuracy.** Enrollment spends each speaker's first 30 s, so anyone with less than
   that scores zero segments and vanishes from the pooled figure. Read the per-speaker lines; a
   `untested: enrollment took every segment` row means the headline is not a bound on that person.
5. **Comparing the bench's absolute attribution to the app's.** The app's pass trigger measures wall
   clock between identify calls, which STT throughput gates; the bench measures stream time. That costs
   a few passes over a long recording and about 2 points of attribution, in either direction. The bench
   is deterministic, so it is sound for comparing settings against each other — but confirm a small
   margin on a real app replay.
6. **Your own CPU.** A build or test run alongside a replay creates dropped segments that look like a
   pipeline finding.
7. **Scoring only the final label.** Every eligible segment's final label comes from the last pass's
   partition, and the pass never sees the match threshold — so a threshold change is almost invisible
   in the final-state number and plainly visible in `-Provisional`, which scores the instant label the
   meeting actually showed. Score both, or a real change reads as no change.
8. **Reading the attribution percentage across settings.** Unlabelled segments leave the denominator,
   so refusing to label raises the percentage. Compare the *correct count* — the segment set is
   identical across settings — and read the percentage second.

## What is still unmeasured

- Dropped segments at 1.0x. Both fixture replays run at ~0.83x and drop nothing.
- The roster size `TeamsMeetingSession` actually reports; every number so far used an env-var roster.
- The gap between cloud-mixed recordings and real device loopback. Capture one live dump and bench it
  against these to find out.
