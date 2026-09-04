# Nemotron 3.5 vs Parakeet TDT v3 vs Whisper medium on German — G1

**Status:** Done. **G1 passes** — nemotron is competitive on German; Phase 2 proceeds.
**Owner:** Marco Altmann
**Written:** 2026-09-04
**Origin:** Task 5 of
[2026-09-04-nemotron-third-backend-plan.md](2026-09-04-nemotron-third-backend-plan.md), the decision
gate that decides whether the streaming UX gets built. Tracked in
[2026-09-04-nemotron-third-backend-checklist.md](2026-09-04-nemotron-third-backend-checklist.md).

## What was measured, and what it is not

Three Teams recordings of a German daily standup were available; this compares the first **180
seconds** of `Daily SP _ PR-20260821`. The audio is 16 kHz mono in the container already, so nothing
was resampled.

**There is no ground-truth transcript.** This compares the three models against each other, with
Whisper medium as a pseudo-reference because it is the slowest and most heavily supervised of the
three — not because it is right. Whisper is visibly wrong in places (see segment 19 below). No WER
number is claimed here, and none should be quoted from this document.

All three engines saw **identical input**: one Silero VAD pass produced 37 segments, and the same 37
float buffers were handed to each engine in turn. Language was forced to `de` rather than `auto`, so
this does not measure auto-detection. Everything ran on the CPU provider at `NumThreads = 1`, which
is the production shape.

Reproduce with the bench that produced it:

```powershell
$env:PIA_STT_BENCH_MEDIA    = 'G:\tmp\Recordings\Daily SP _ PR-20260821_073032UTC-Meeting Recording.mp4'
$env:PIA_STT_BENCH_SECONDS  = '180'
$env:PIA_STT_BENCH_BACKENDS = 'Whisper,Parakeet,Nemotron'
$env:PIA_STT_BENCH_LANGUAGE = 'DE'
tests\Pia.Wpf.Tests\bin\Debug\net10.0-windows10.0.17763.0\Pia.Wpf.Tests.exe `
  -explicit only -method '*Bench_ComparesEveryBackend*'
```

## Numbers

127.5 s of the 180 s was speech, across 37 segments.

| | Whisper medium | Parakeet TDT v3 | Nemotron 3.5 560 ms |
|---|---|---|---|
| Engine load | 344 s (includes the 1.9 GB first download) | 1.5 s | 1.5 s |
| Total decode | 169.4 s | 13.7 s | 44.5 s |
| Real-time factor | 1.33 | 0.11 | 0.35 |
| Per segment | 4578 ms | 371 ms | 1203 ms |
| Characters | 1621 | 1619 | 1593 |
| Empty segments | 0 of 37 | 2 of 37 | 14 of 37 |
| Punctuates | yes | yes | yes, more sparsely |
| Capitalises | yes | yes | yes |

Nemotron is roughly **3x slower than Parakeet and 4x slower than real time** — comfortably fast
enough for the live path, and the only one of the three that can produce a partial at all.

## Punctuation and casing — the question inherited unanswered

**Nemotron punctuates and capitalises.** This was flagged unverified by the 2026-08-30 plan and
matters because the downstream Summarize prompt benefits from it. It punctuates less consistently
than Parakeet — it frequently ends an utterance with no full stop — but sentence-internal commas and
capitalised nouns are both present. It is not a bare lowercase stream, so no summary-quality
regression is expected on that axis.

One caveat found while fixing the cold-decode bug: **the punctuation depends on the padding.**
Unpadded, the same clip came back both truncated and uncased.

## Where each model wins and loses

On substantive utterances the three are close, and nemotron sometimes wins:

```
--- 19 ---
  W: Weiß, wenn der Weihendukus wieder zurück          <- Whisper garbles the name
  P: Also wenn Lukas wieder zurückkommt.
  N: Also wenn Lukas wieder zurückkommt

--- 17 ---
  W: Weiß man denn, wann Lucas wieder zurückkommt?
  P: Weiß man denn, wann Lukas wieder zurückkommt?
  N: Weiß man denn wann Lukas wieder zurückkommt
```

**Nemotron drops short utterances.** 14 of 37 segments came back empty — greetings, "Hm?", "Ja." —
against Parakeet's 2 and Whisper's 0. This is the one clear quality loss.

**Parakeet answers those same short segments in the wrong language**, which is arguably worse than
answering not at all, because a wrong word enters the transcript and the summary:

```
--- 10 ---            --- 11 ---            --- 13 ---            --- 24 ---
  W: Blumen.            W: Ich                W: Wie jetzt.         W: Ja.
  P: Hello.             P: Oh yeah.           P: pět                P: Sure.
  N: <empty>            N: <empty>            N: <empty>            N: <empty>
```

`pět` is Czech. On a German meeting, Parakeet's auto-detection is the weaker failure mode.

**Nemotron keeps disfluencies the other two smooth away** — "was was die äh was das was die
Teamstärke angeht". More faithful to what was said, noisier for a summary.

## The bug this gate found

The first run returned "Nur die Wurst" for the bundle's own `de.wav`, whose content is "Alles hat
ein Ende, nur die Wurst hat zwei". A VAD segment arrives silence-trimmed and meets an empty encoder
cache, so a cache-aware streaming model loses the words before its first full chunk and never
flushes the last partial one:

| Lead pad | Tail pad | Result on `de.wav` |
|---|---|---|
| none | none | `hat ein Ende nur die Wurst hat` |
| none | 560 ms | `hat ein Ende nur die Wurst hat zwei` |
| 560 ms | 560 ms | `Alles hat ein Ende, nur die Wurst hat zwei` |
| 1120 ms | 1120 ms | identical to 560 ms |

One chunk of silence at each end is now applied by `NemotronStreamingEngine.PadForColdDecode`. On
the meeting audio, doubling it again recovers only 2 of the 14 empty segments for 30% more CPU
(RTF 0.35 to 0.47), so 560 ms is the pin. The short-utterance drops are a property of the model on
very short input, not a padding shortfall.

The streaming path in Phase 2 needs no padding — there the cache is already warm.

## Verdict

**G1 passes. Build Phase 2.**

Nemotron matches Parakeet on the speech that matters, punctuates and capitalises, and runs at 0.35
RTF. Its short-utterance drops are a real but narrow loss, and they are traded against Parakeet's
habit of inventing English on exactly those segments. Neither is clearly better there.

Nothing here argues for retiring Parakeet — it is 3x faster, does not drop short utterances, and is
the only one of the three that supports hotwords. All three stay.

## Follow-ups this raised, not planned here

- **Short-utterance drops.** 14 of 37. Worth a look if users notice missing "ja"/"nein" answers in
  meeting transcripts. Padding is not the lever; a minimum-segment-length fallback to Parakeet
  would be, and that is a plan of its own.
- **Whisper medium's 344 s load** is the first-download cost, not steady state, but no separate
  warm-load number was taken. Do not quote it as a load time.
- **The double decode** the plan already lists as an open question is now measurable: at RTF 0.35,
  running the streaming preview and the segment-final decode over the same audio costs 0.70 total.
  Still under real time, so it is an efficiency question rather than a blocker.
