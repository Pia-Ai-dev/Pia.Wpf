# Brief: decouple the match threshold from the cut (Tasks 8 and 9)

**Status.** Ready to execute, with the amendment at the end of Task 8 folded in. This brief is now
**lever 4** of [2026-08-22-attribution-levers-brief.md](2026-08-22-attribution-levers-brief.md), which
supersedes its scope and reorders it behind three cheaper levers — start there. Gated on a live smoke
test for *merge*, not for work — note that one live meeting was measured on 2026-08-22 (see the last
section of the measurements doc); whether that discharges Monday 2026-08-24 is the owner's call.
**Owner.** Marco Altmann.
**Written.** 2026-08-22.
**Origin.** Tasks 8 and 9 of `docs/superpowers/plans/2026-08-22-diarization-bench-and-threshold.md`,
released by the Task 7 measurement recorded in `2026-08-21-speaker-attribution-measurements.md`
(commit `a169a7d4`). Both were conditional on that number; it came back in their favour.

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

**Acceptance.** Attribution on **all three** recordings for all four policies, from the warm cache. The
winner beats (a) on LSP, does not lose on the workshop, **and does not lose on `testmeeting`**. Also
report the **variance of `_matchSimilarity` across the meeting** — stability is the point, and a policy
that scores the same while holding still is the better one.

### Amendment 2026-08-22: `testmeeting` is the counterweight, and it changes the answer

A third recording landed after this brief was written — a live four-person meeting, three of them
women, measured in the last section of `2026-08-21-speaker-attribution-measurements.md`. It inverts the
sign of the pressure on the threshold, and the two-recording acceptance test above would have shipped a
regression.

| | workshop | LSP | testmeeting |
|---|---|---|---|
| live attribution, by segment | 91.9 % | 92.1 % | **84.1 %** |
| best fixed threshold | 0.400 | 0.345 | 0.375 |
| oracle enrollment, 8 s budget | 100.0 %¹ | 94.9 % | 89.5 % |
| headroom to the oracle | 8.1 pts¹ | 2.8 pts | **5.4 pts** |
| closest pair, margin | 0.166 | 0.215 | **0.103** |

¹ Not a bound — one workshop speaker is `untested` at every enrollment budget. Comparisons here are at
8 s, the smallest budget, because it leaves the largest scored set; `testmeeting` swings 10.5 points
across the 8–30 s range where LSP moves 0.5, so a mixed-budget comparison invents headroom that is not
there. The `PIA_BENCH_ENROLL` sweep is in the measurements doc.

The failure there is a single speaker absorbed whole: she is filed under another woman's label for
25.3 s and her own label is never minted, so **every one of her scored segments is wrong and every
other speaker's is right** (37/37 excluding her).

**Read it as a constraint on this brief, not as a mandate for it.** Her provisional and final labels
agree on five of her seven scored segments, and the two that differ move her between two *wrong*
labels — so the match threshold is not what is placing her. The two reassignments that matter both hit
her first segment: a pass moved it out into a fresh cluster, **took a genuinely-correct segment of the
absorbing speaker with it**, and a later pass dissolved that cluster back. The clusterer tried to split
this pair, split it wrongly, and gave up. That is `ChooseCut`'s partition, which is where LSP's residue
already lived.

Consequences for Task 8:

- **Both candidate values in option (b) are below the 0.50 that already absorbed her**, so neither can
  help, and tuning on LSP and the workshop alone moves the threshold further down. `testmeeting`'s role
  in the A/B is as the **regression guard** — the recording a merge-happy winner must not make worse.
- **A single global fixed threshold looks weaker than this brief assumed.** Option (d), derived from the
  observed separation distribution, is the only one of the four that can hold 0.345-like behaviour on
  LSP and a higher bar on a close pair. Sweep (b) anyway; it is cheap, and it is now a control.
- **A young centroid is worth one experiment on its own.** The provisional absorption happened against
  a centroid one segment old. Damping the bar while a cluster is thin is smaller than any of the four
  policies and testable separately — but it moves the provisional label, so measure it with
  `-Provisional` and do not expect it to move the score much.

Two findings from that recording are **not** Task 8's and should not be folded into it:

- **Minting on overlapped speech.** One of the two phantom labels there was born on a segment the
  reference says is three people at once; the other was minted past the end of the video and cannot be
  attributed. A mixture lands far from every centroid, which the mint branch reads as a new voice. One
  confirmed case, not yet a pattern — but it is the likeliest explanation for five names on four people,
  and no threshold fixes it.
- **Consent-phase enrollment.** The oracle's budget is not hypothetical on this recording: the meeting
  opened with each participant saying their own name for roughly 12 s. Worth about **5 points** at a
  like-for-like budget, and — the part that matters more than the points — it is the only lever measured
  so far that gives the absorbed speaker a label at all (80 % against the live run's 0 %) instead of
  trading her against someone else. Still a feature with UI and a privacy story, still not this brief.

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
artifacts/wav/lsp-replay.wav         roster 5    scripts/speaker-reference/lsp.reference.json
artifacts/wav/workshop-replay.wav    roster 10   scripts/speaker-reference/workshop.reference.json
artifacts/wav/testmeeting-replay.wav roster 4    scripts/speaker-reference/testmeeting.reference.json
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
