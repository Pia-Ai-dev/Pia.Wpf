# Brief: decouple the match threshold from the cut (Tasks 8 and 9)

**Status.** Task 8 executed 2026-08-22; Task 9 not started. Gated on Monday 2026-08-24's live smoke test for
*merge*, not for work.
**Owner.** Marco Altmann.
**Written.** 2026-08-22.
**Origin.** Tasks 8 and 9 of `docs/superpowers/plans/2026-08-22-diarization-bench-and-threshold.md`,
released by the Task 7 measurement recorded in `2026-08-21-speaker-attribution-measurements.md`
(commit `a169a7d4`). Both were conditional on that number; it came back in their favour.

## Task 8 outcome, read this before the sections below

Executed 2026-08-22 on `feature/diarization-threshold`. Full numbers in
`2026-08-21-speaker-attribution-measurements.md`, section *Task 8*. Two premises below did not survive
the measurement, and the sections that rest on them should be read with that in mind:

- **There is no threshold/cut feedback loop.** The clusterer's inputs never include the match
  threshold; the cut trace is identical to the digit across all 11 settings on both recordings. The
  coupling is one-directional. Policies (c) and (d) were damping a loop that does not exist, and both
  lost — (d) worst of the four.
- **The Alexander/Andreas residue is not a matching error.** 127.4 s inside B's label, unmoved to the
  decimal by every setting from 0.20 to 0.60. It is the dendrogram's partition, out of the threshold's
  reach by construction.

A fixed threshold in the 0.20–0.345 plateau does win — +6 correct segments on LSP, +1 on the workshop,
and live label churn down from 13 labels to 9 — but every accuracy margin is under the bench's own
±2.5-point confirmation rule. **The shipping default is unchanged**; 0.30 is reachable from the bench
via `PIA_BENCH_MATCH` and flipping it waits on an app replay of LSP.

## Why this work is now justified

Task 7 asked one question: is the accuracy ceiling the embedding model, or the matching policy? The
answer, from both fixture recordings with the answer key in hand:

| | workshop | LSP (the trustworthy half) |
|---|---|---|
| live attribution, by segment / duration | 91.9 % / 91.5 % | **92.1 % / 93.2 %** |
| oracle: perfect enrollment on the *current* model | 98.7 %¹ | **95.2 % / 97.8 %** |
| oracle: real clusterer, k pinned to true talkers | 87.8 % | 89.4 % |
| embedding separation, d' | 1.82 | **2.04** |
| best single fixed match threshold | **0.400** | **0.345** |

¹ Not a bound — the 30 s enrollment budget consumed two of four speakers whole. Quote LSP's 95.2 %.

LSP's 95.2 % is the plan's `88–96 %` bucket: *"real headroom in both — Tasks 8 + 9 first (cheaper),
then Task 11."* The `< 88 %` bucket, which would have made an embedding swap a precondition and this
work mostly wasted, is excluded on both recordings by a wide margin. **The matching policy owns roughly
3 points by segment and 4.6 by duration.** (Not scored over identical sets — the oracle scores 420
segments against the live run's 433 — so treat the delta as indicative.)

## The specific defect, and the number that names it

`AdaptiveSpeakerIdentificationService` recomputes the online match threshold from the clustering cut
every pass:

```csharp
internal const float InitialMatchSimilarity = 0.50f;
internal const float MatchSimilarityMin     = 0.40f;
internal const float MatchSimilarityMax     = 0.60f;
// _matchSimilarity = clamp(1 - cut, MatchSimilarityMin, MatchSimilarityMax)
```

Matching and clustering feed each other undamped, and the LSP baseline traverses the whole band inside
one meeting — pinned at 0.60 for passes 1–10, at 0.40 by passes 19–21. The clamp bounds the swing and
does nothing to stabilise it.

The measured optimum is **0.345 on LSP** and **0.400 on the workshop**. So the best fixed threshold sits
*at the floor* of the clamp on one recording and **below the clamp's reachable range** on the other,
while the live policy spends its opening ten passes at the opposite end of the band. That is the defect
stated as a number rather than as an argument.

Corroborating detail: the residue this fixture cares about is Alexander filed inside Andreas's label —
**127.4 s there against 8.0 s in his own** on LSP. A threshold that is too high mints and merges wrongly
in exactly that way.

## Task 8 — implement, A/B, delete the losers

Make the knobs injectable, then measure. They are `internal const` today, so a sweep is impossible
without this step — which is the whole reason Task 6's sweep driver was deliberately not built.

Knobs the experiment needs: `InitialMatchSimilarity`, `MatchSimilarityMin`, `MatchSimilarityMax`, the
`1 − cut` derivation itself, and `MinClusterSegmentSeconds`. `WarmupSegments` and `PassSegmentStride`
are worth exposing while you are there but are not the hypothesis.

Four policies:

- **(a)** current, derived per pass — the baseline
- **(b)** fixed at `InitialMatchSimilarity`. **Try 0.40 and 0.345 first**; the data says the answer is
  probably here, and it is the cheapest thing in the plan
- **(c)** damped: EMA toward `1 − cut`, α ≈ 0.2
- **(d)** derived from the observed centroid-separation distribution rather than the cut, shaped by the
  intra/inter statistic (`intra 0.539 ± 0.183` vs `inter 0.222 ± 0.122` on LSP)

Implement as a selectable policy so the bench can A/B rather than argue, **then delete the losers.** A
mode enum that outlives the experiment is a configuration surface nobody asked for.

**Acceptance.** Attribution on both recordings for all four policies, from the warm cache. The winner
beats (a) on LSP and does not lose on the workshop. Also report the **variance of `_matchSimilarity`
across the meeting** — stability is the point, and a policy that scores the same while holding still is
the better one.

## Task 9 — roster as a target for k, and expect to refuse

`ChooseCut` applies `expectedSpeakers` as a downward-only cap, deliberately: it protects the
mostly-muted case, where 10 on the roster and 2 talking must not become 10 labels.

**Expect this to come back negative, and write that down if it does.** Pinning k to the *true* talker
count scores below the shipping pipeline on both recordings — 87.8 % against 91.9 % on the workshop,
89.4 % against 92.1 % on LSP. The adaptive online-plus-repass design already beats naive offline
clustering handed the answer for k, so "largest gap, capped by the roster" is not obviously the weak
link. The assessment recommends this change; a measured refusal is worth more than a silent one.

The workshop's final label count is the **veto**: it must not inflate. It currently settles at 5 labels
against a roster of 10 and 4 true talkers.

## How to measure — and the trap in it

The bench runs a 50-minute recording in **34 seconds** and is deterministic: a warm run reproduces a
cold one to the digit. Full procedure in `speaker-attribution-fixture-playbook.md`. Inputs already on
disk:

```
artifacts/wav/lsp-replay.wav        roster 5    scripts/speaker-reference/lsp.reference.json
artifacts/wav/workshop-replay.wav   roster 10   scripts/speaker-reference/workshop.reference.json
```

**The trap.** The bench's *absolute* attribution is about 2 points off the app's, in either direction
(+2.3 on the workshop, −2.3 on LSP), because the app's 30 s pass trigger measures wall clock between
identify calls while the bench measures stream time. So:

- comparing policies **against each other on the bench** is sound — that is what it is for
- a winning margin under about 2.5 points is **not** established until you confirm it on a real app
  replay (`Invoke-MeetingReplay.ps1`, ~20 min for the workshop, ~65 for LSP)

Report Task 8 as bench-relative, and say which margins were confirmed against the app.

## Constraints

- **Branch `feature/diarization-threshold`.** This changes which voice gets which label. It must not be
  in the build Monday's smoke test runs against, or the test cannot tell you which change caused what it
  shows. Merge after Monday.
- **Gate:** `dotnet test`, no filter, `failed: 0`. The bench stays `Explicit`.
- **Warnings:** `dotnet build -t:Rebuild -v:n`, Debug **and** Release, `0 Warning(s)`. An incremental
  build does not re-emit warnings from projects it skips.
- Comment discipline: one short line, only where the why is non-obvious, and no task or spec citations
  in the source.
- Update `2026-08-21-speaker-attribution-measurements.md` with the before/after table on both
  recordings, including the `_matchSimilarity` variance and the Alexander/Andreas confusion matrix.

## Not in scope

Task 11 (embedding model A/B) is a follow-up, not a gate — Task 7 excluded the bucket that would have
made it a precondition. Consent-phase enrollment is the strongest product lever the assessment named
and is exactly what the oracle simulates, but it is a feature with UI and a privacy story, and it is
not this brief.
