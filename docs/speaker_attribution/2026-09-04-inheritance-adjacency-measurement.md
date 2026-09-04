# Measurement: an adjacency check does not fix label inheritance

**Status.** Measured on the three reference recordings. Conclusion: option (b) is inert on the
population it was aimed at. No code changed.
**Owner.** Marco Altmann.
**Written.** 2026-09-04.
**Origin.** The judgement the step-2 constraint demanded up front — option (b) in
[2026-09-04-pin-plan-review-and-label-inheritance.md](2026-09-04-pin-plan-review-and-label-inheritance.md)
Part 2 is invisible to `Measure-SpeakerAttribution.ps1`, which scores service-side labels parsed out
of the log, while inheritance is a grouping rule above that layer. A flat fixture run would not have
been evidence either way, so this is the substitute, decided before the code was written.

## Short answer

Two numbers settle it.

- Across 71 minutes of real meeting audio, **603 blank utterances inherit a run, and not one of them
  sits more than 7.25 s after that run's last word.** The median gap is 0.75–1.0 s. Any threshold
  above 8 s changes nothing at all; any threshold low enough to bite removes **correct**
  inheritances first — at 4 s it drops 6 correct ones and 0 wrong ones.
- The leak the option was meant to narrow is large — between **38% and 83%** of those inheritances
  attribute the words to the wrong person — but the wrong ones are just as adjacent as the right
  ones. Timing does not separate them.

So (b) is a bound worth stating, not a fix. What it actually buys is listed under *What 8 s would
still do* below; the leak itself needs option (c) or (d).

## What was measured

`TranscriptGrouping.ShouldReuse` was simulated over the ground-truth speaker intervals in
`scripts/speaker-reference/{lsp,workshop,testmeeting}.reference.json` — Teams tile highlights
sampled at 4 fps, the same references `Measure-SpeakerAttribution.ps1` scores against.

Each interval is treated as one utterance. An interval shorter than the 1.5 s diarization gate
(`LiveTranscriptionEngineService._minDiarizationSamples`) arrives with no label — population (1) in
the review doc, the one that leaks most — and everything else arrives labelled with its true
speaker. The grouping then runs exactly as the shipping rule does: reuse inside
`BubbleWindowSeconds` (25) measured from the bubble's start, on equal labels or a blank one.

The gap the proposed condition would test is `utterance.Timestamp - last.EndTimestamp`. Both are
recognizer-return times (`TranscriptUtterance.Timestamp` is "when the segment finished
transcribing"), so an interval's **end** stands in for both, and the VAD tail cancels in the
subtraction. What is left is `silence + the blank utterance's own duration + Δlatency`.

For every inheritance the simulation records that gap and whether the blank interval's true speaker
is the run's speaker.

## The gap distribution

| fixture | blank utterances | inherited | blocked by the 25 s window | median gap | p99 | max |
|---|---|---|---|---|---|---|
| lsp (50 min, 5 talkers) | 391 | 378 | 9 | 0.75 s | 3.25 s | 5.25 s |
| workshop (16 min, 4 talkers) | 111 | 109 | 1 | 1.00 s | 5.75 s | 7.25 s |
| testmeeting (5 min, 4 talkers) | 132 | 129 | 1 | 0.75 s | 3.00 s | 4.75 s |

Three of 616 inheritances exceed 6 s. None exceeds 8 s.

The "blocked" column is why. The review doc's motivating case — a "genau" 18 s after the run went
quiet — is already refused today, because a run that has been silent that long is usually more than
25 s old measured from its **start**. The existing window bounds adjacency as a side effect. That
bound is fragile (a long monologue bubble stays inside the window for its whole 25 s), but it is
load-bearing today, and it is why the eighteen-second case does not appear in any of the three
recordings.

## What a threshold would cost

Inheritances kept and lost, all three fixtures pooled, scoring only the intervals with a single
ground-truth speaker:

| threshold | inherits | correct | wrong | correct lost | wrong lost |
|---|---|---|---|---|---|
| 3 s | 268 | 98 | 170 | 11 | 6 |
| 4 s | 279 | 103 | 176 | 6 | 0 |
| 6 s | 284 | 108 | 176 | 1 | 0 |
| 8 s | 285 | 109 | 176 | 0 | 0 |
| 25 s (today) | 285 | 109 | 176 | 0 | 0 |

Read the last two columns. The threshold only ever removes correct inheritances — it has to reach
3 s before it catches a single wrong one, and by then it has thrown away eleven right ones. An
interjection that belongs to somebody else follows the run just as closely as one that belongs to
it, because both are answers to what was just said.

## How big the leak is

Between **38% and 83%**, and the references cannot narrow it further.

331 of the 616 inherited intervals are **overlap** intervals — two tiles lit at once, the canonical
"genau" spoken over somebody else's sentence. There is no single true speaker to score against.

- **38%** counts an overlap that includes the run's speaker as correct — the most generous reading.
- **83%** counts only a solo interval by the run's speaker as correct — the harshest.
- **63%** is the figure over the 272 solo intervals alone, which sits inside that range.

Two artifacts were checked and do not move it:

- **A speaker's own onset counted against them.** `{4.25–5.0, A}` followed by `{5.5–32.25, A}` reads
  as a sub-gate blank inheriting the *previous* speaker, where the real VAD (512 ms to close a
  segment) would likely have folded the two into one. Excluding every blank whose next interval is
  the same speaker within 0.5 s removes 13 of 616 and leaves the rate at 63%.
- **4 fps quantisation.** The shortest intervals are 0.25 s. They are a minority and dropping them
  does not change the direction.

## What 8 s would still do

Three things, none of them measured here, all of them small:

- It converts a bound the 25 s start-window currently supplies **by accident** into one the rule
  states. A single long bubble — a monologue that keeps the window open for its full 25 s — is the
  shape where today's bound stops holding, and it is exactly the shape a meeting produces.
- It caps inheritance by **duration** as well as by silence, because the gap includes the blank
  utterance's own length. A pass-nulled segment (population (4) in the review doc, up to the VAD's
  20 s flush cap) can no longer inherit a run it merely follows. Rare, and not present in these
  references, but it is the one population where 8 s does real work.
- It costs nothing: zero of 616 real inheritances are above it.

## Caveats

- Tile highlights are not VAD segments. The reference has attack and release lag, merges what the
  VAD would split at 512 ms of silence, and misses an interjection too quiet to light a tile. The
  simulated blank population is therefore biased toward interjections loud and long enough to be
  seen, which if anything **understates** how short and how adjacent real ones are.
- Only population (1) is modelled. Populations (2)–(4) — segments at or above the gate that matched
  no centroid — are absent, and they are longer, so their gap is larger by their own duration.
- Recognizer latency is not modelled. Segments are transcribed serially off one queue, so the jitter
  between two consecutive returns is small and signed both ways.

None of these touch the conclusion, which is a statement about shape rather than about a number: the
right and the wrong inheritances have the same timing distribution.

## Rebuilding it

The harness is one self-contained ~40-line script and is not in the tree — `scripts/` is PowerShell
and this is a one-off analysis, not a gate. Everything needed is above: read `intervals` from a
reference json, label anything under 1.5 s as blank, replay `ShouldReuse` with the 25 s window on
the bubble start, and for each inheritance record `thisInterval.end - lastAbsorbed.end` together
with whether the run's speaker is in `interval.speakers`.

## What this leaves

Option (b) is a guard. The leak itself is a **headcount** problem, exactly as the review doc's Part 2
argued before proposing (b): in a nine-person meeting the short interjections are largely what the
other eight produce. That points at (c) — skip inheritance above two known speakers — or (d) — mark
inherited text instead of absorbing it silently — and (d) is the better answer if the goal is to stop
the transcript asserting something it does not know. Both need a decision (b) does not.
