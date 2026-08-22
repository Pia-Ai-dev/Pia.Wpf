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
| the layout the answer key is read through | `scripts/speaker-reference/<name>.layout.json` | ~15 min, see below |

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

**Do not measure the rects by eye — derive them from the pill itself.** The pill is a strongly
coloured rectangle, so one pass over the video that flags pill-coloured pixels
(`b - (r+g)/2 >= 25 && b >= 80`), heat-maps them across frames and takes connected components of at
least ~200 px lit in two or more frames returns the label rects directly. Keep the components that are
label-shaped (a ~22–26 px tall bar) and inset them 2 px. Three things this buys over eyeballing a grid:

- it finds name labels **burned over video** — a camera tile's label sits in the tile's letterbox
  padding, not where a grid-relative guess puts it;
- it separates the pill from a pale avatar circle behind it, which passes the same test marginally
  (a blue lead of ~25 against the pill's ~56–67);
- a tile that never lights (the attendee's own) produces no component, which tells you it is a tile to
  place from the rail's pitch rather than one you have mismeasured.

Everything after the layout is a command.

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
$env:PIA_BENCH_ENROLL    = '12'   # optional; default 30, too big for a recording under ~10 min
dotnet test -- --explicit only --filter-method "*Bench_MeasuresARecording*"
```

Set them in **one** call — each PowerShell invocation is a fresh shell, and a missing `PIA_BENCH_WAV`
makes the test skip rather than fail, which reads like success.

Two parts of the report earn most of the attention:

- `separation`, a per-pair matrix of mean cosine with each speaker's self-similarity on the diagonal,
  and a `closest pair` line giving the margin between a pair's cross-similarity and the tighter of
  their two self-similarities. **A pooled `d'` cannot see one bad pair**; this can. On LSP and
  `testmeeting` the pair it names is exactly the pair the run confuses; on the workshop it names E/H,
  where E loses its label and H keeps it — so read the matrix, not only the summary line.
- `ORACLE enrollment`, which is the bound that decides whether a failure belongs to the matching policy
  or to the embedding model. Read the per-speaker lines first (see trap 4).

Outputs land in `PIA_BENCH_OUT`: `*.report.txt`, `*.segments.jsonl`, `*.passes.log`, `*.embeddings.bin`.
The first run computes every embedding; later runs reuse the cache and cost milliseconds.

## 4. Score it

Same scorer for both inputs, so the app and the bench stay comparable:

```powershell
# the app's log
./scripts/Measure-SpeakerAttribution.ps1 -LogPath <log> `
    -ReferencePath scripts/speaker-reference/<name>.reference.json `
    -NameMapPath scripts/speaker-reference/<name>.names.local.json

# the bench's segments
./scripts/Measure-SpeakerAttribution.ps1 -SegmentsPath artifacts/wav/bench/<name>.segments.jsonl `
    -ReferencePath scripts/speaker-reference/<name>.reference.json
```

Use the reference belonging to the run. Scoring one recording against another's key produces a
confident, meaningless number.

The header must read:

```
Align   : exact — every identified segment reported its own stream position
```

If it prints a fitted offset instead, the log predates `start=` on the identify line and the number
carries an alignment caveat. Treat a fitted figure as indicative only.

### Scoring a live meeting

A live log scores through the same script — it selects runs on `Meeting attendee admitted to the call`
as well as on the replay marker, so `-RunIndex -1` picks the last run of either kind out of a day's log.
One thing differs and it is not optional: the recording was already running when Pia was admitted, so
stream 0 sits *inside* the reference. The script fits that origin from the speech masks at rate 1.0 and
prints it:

```
Origin  : stream 0 s = recording 27.15 s (fitted); reference covers stream -27.1..253.3 s
          runner-up peak 30.75 s scored 2650 against 3415 — a near tie here would void the fit
```

Check three things before believing the run. The runner-up margin (a near tie means the fit found
structure, not the join). The origin against an independent witness — the admission timestamp minus the
recording's start, and the end of the reference's first `invalidRanges` entry, which *is* the grid
reflowing as Pia joins. And the coverage window, because a recording can be stopped before the meeting
ends; segments past it land in `outside the video` and are excluded. Pin the origin with
`-StreamOriginSeconds` to compare two runs of one meeting.

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
4. **Pooled oracle accuracy, and the budget that produced it.** Enrollment spends each speaker's first
   `PIA_BENCH_ENROLL` seconds (default 30), so anyone with less than that scores zero segments and
   vanishes from the pooled figure. Read the per-speaker lines; an `untested: enrollment took every
   segment` row means the headline is not a bound on that person.
   **Then sweep the budget, because on a short recording the pooled figure tracks the budget rather
   than the pipeline.** `testmeeting` (4:40) reads 89.5 % over 38 segments at 8 s and 100 % over 14 at
   30 s — a 10.5-point swing that is purely the scored set shrinking. LSP moves 0.5 points over the
   same range because 30 s barely dents 434 segments. So: quote the smallest budget that still leaves a
   real sample, say which budget it was, and **never compare two recordings at different budgets** —
   that manufactures headroom.
5. **Comparing the bench's absolute attribution to the app's.** The app's pass trigger measures wall
   clock between identify calls, which STT throughput gates; the bench measures stream time. That costs
   a few passes over a long recording and about 2 points of attribution, in either direction. The bench
   is deterministic, so it is sound for comparing settings against each other — but confirm a small
   margin on a real app replay.
6. **Your own CPU.** A build or test run alongside a replay creates dropped segments that look like a
   pipeline finding.
7. **A pooled `d'` that looks healthy.** One inseparable pair does not move it. `testmeeting` reads
   `d' 1.88`, between the two work recordings, while the pair that fails sits at a margin of 0.103
   against 0.166 and 0.215. Read the `separation` matrix, not the scalar.
8. **A headline percentage on a run whose errors are all one person.** `testmeeting` reads 84.1 %, and
   the useful statement is "37 for 37 on three speakers, 0 for 7 on the fourth". Always read the
   confusion matrix before quoting the percentage.

## What is still unmeasured

- Dropped segments at 1.0x. Both long fixture replays run at ~0.83x and drop nothing.
- **Device loopback.** The cloud mix is now known to stand in faithfully for the *in-browser* tap —
  same meeting, same key, confusion cells identical to the tenth of a second. Loopback, with its second
  D/A-A/D pass and Teams' own AGC, is a different signal and still untested. Set
  `PIA_DEBUG_MEETING_ATTENDEE_AUDIO_DUMP` before a live meeting to capture one; a live run without it
  cannot be benched or cross-correlated afterwards.
- What the UI attributes an unlabelled utterance to. A fifth to a quarter of segments fall below
  `MinClusterSegmentSeconds` and still produce transcript text.
