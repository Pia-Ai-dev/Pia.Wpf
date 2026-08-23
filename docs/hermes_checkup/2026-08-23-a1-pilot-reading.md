# Pilot reading — gate A1 on four live runs

**Status:** measured, and it moves the gate. Four runs only; read §7 before generalising.
**Owner:** unassigned. **Written:** 2026-08-23.
**Origin:** §8 of [`2026-08-23-group-a-batch-run.md`](2026-08-23-group-a-batch-run.md) — the Windows
list that batch left behind — and gate **A1** of
[`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md). Run on
`feature/agent-run-spine` at `251da05b`, a Debug build carrying the instrument batch.

---

## 1. What ran

A throwaway profile at `C:\temp\pia-a1` (`PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR`), seeded from the real
`settings.json` so the providers work, with three keys overridden: `AssistantFilesFolder` repointed
into the throwaway tree, `DefaultWindowMode=1` (Assistant), `AssistantAgentModeDefault=true`. The real
profile was not written to — confirmed by log timestamps.

`PIA_DATA_DIR` alone does **not** isolate the vault or the assistant files folder: the vault is
`<AssistantFilesFolder>\Vault` (`Bootstrapper.cs:310` → `AssistantWorkspace.cs:37`) and that folder is a
settings value. Repointing it is what makes the profile throwaway.

Four prompts, one per runbook §5 category, dispatched through `Assistant_RunInBackground`:

| Run | Category | Prompt |
|---|---|---|
| `a6898d91` | A — file-producing | draft a README.md for a project called "Ledger" |
| `741d3aed` | C — todos | create three todos, one high priority |
| `be5767e1` | B — research/prose | compare two speaker-diarization approaches |
| `0bbff806` | F — answer-only control | explain todos versus reminders |

## 2. The reading

Final probe line per run — **not** the sum of its lines; see trap 1 in §6.

| Run | declared | fileShaped | notFileShaped | probed | found | notFound |
|---|---|---|---|---|---|---|
| `a6898d91` | 1 | 1 | 0 | 1 | 1 | 0 |
| `741d3aed` | 7 | 0 | 7 | 0 | 0 | 0 |
| `be5767e1` | 7 | 4 | 3 | 8 | 4 | 4 |
| `0bbff806` | — no probe line at all — |
| **total** | **15** | **5 (33%)** | **10 (67%)** | **9** | **5** | **4** |

**File-shapedness is 33%, against 57% on the historical corpus.** That corpus was all code-shaped
tasks; a broader mix pushes the ratio down, as expected. The gate needed ≥85% to close. **It does not
close. A2–A4, A6, A7 stay open.**

Report-channel supply (`artifactReported=`, the G6 counter): **2 of 17 step outcomes**. Only the two
runs that actually wrote files reported an artifact.

## 3. The headline: `NOT FOUND` is non-zero, and every one of them is false

`NOT FOUND` was 0/23 across the whole historical corpus. This pilot produced **4**. That looks like the
finding the runbook's reading table was waiting for. It is not — read the facts block:

```
… (e.g., criteria_list.md or criteria_summary.pdf)  → criteria_list.md: found; criteria_summary.pdf: NOT FOUND
… (e.g., approaches_summary.md or approaches_report.pdf) → approaches_summary.md: found; approaches_report.pdf: NOT FOUND
… (e.g., comparison_table.xlsx or comparison_report.md)  → comparison_table.xlsx: NOT FOUND; comparison_report.md: found
… (e.g., recommendation.md or recommendation_report.pdf) → recommendation.md: found; recommendation_report.pdf: NOT FOUND
```

Every probed declaration is an `(e.g., A or B)` **disjunction**. The step writes one of the pair; the
probe reports the other as missing. All four files exist on disk, all four steps succeeded, and all
four `NOT FOUND`s are semantically false.

This is not the N1 fallback misfiring — the subpath-fallback line fired **zero** times, and
`Artifact probe skipped` zero times. The instrument is behaving correctly; the *declaration* is the
problem.

**Consequence for the gate.** A `NOT FOUND` on the planner channel is not a reliable "the step did not
do its job" signal, and `NOT FOUND = 0` was never evidence that the channel cannot produce one. The
defect is that the planner is allowed to declare alternatives — which is **A4**, rated `XS`, currently
sequenced behind A2 as an optional afterthought ("decide after A2's numbers — it may be unnecessary").
On this evidence A4 is the highest value-per-effort row in group A and should run first. It is also
cheap to re-measure: A4 changes prompt bytes, and this pilot is the before-reading.

## 4. Instrument verification

Everything the batch shipped was exercised for the first time and works.

- **G1's tally emits** and its counters are coherent: `fileShaped + notFileShaped = declared`, and
  `found + notFound + folder + unresolvable + uninspectable = probed` (4+4=8 on `be5767e1`).
- **G6's `artifactReported=`** appears on every step-outcome line.
- **N1's subpath-fallback line** did not fire, which is the correct outcome here and means no
  `NOT FOUND` in this sample is an instrument error.
- **A5's `ExtraJson` persistence works** — first confirmation. The throwaway DB has 17 step rows, 15
  with a non-blank `ExpectedArtifact`, and exactly **2** with an artifact in `ExtraJson`, matching the
  2 `artifactReported=True` outcomes one-for-one.

One design wrinkle worth recording: **the tally mixes two granularities on one line.** `declared`,
`fileShaped` and `notFileShaped` count *declarations*; `probed`, `found`, `notFound` and the rest count
*candidate paths*. On `be5767e1` that is 7 declarations but 8 candidates. A "found share" computed
across the line is not a share of anything.

## 5. Three traps in the collection method

1. **`declared` accumulates across verify passes within a run.** `741d3aed` emitted three probe lines —
   `declared=3`, then `6`, then `7` — because the verifier reads `ctx.CompletedSteps`, which grows after
   each replan. Summing probe lines triple-counts. **Read the last line per run id.**
2. **Runs execute concurrently.** The runbook's §4.2 loop polls until the *N*th `Artifact probe:` line
   appears and then dispatches the next prompt. Runs `741d3aed` and `be5767e1` overlapped by 36 seconds,
   so line order does not track prompt order. Attribution requires the `[run <id>]` prefix, not the
   line count.
3. **A whole category is structurally invisible.** The answer-only control completed normally with
   `offered=False` — the SingleTurn fallback, no plan steps, no declarations, **no probe line**. Runs
   that legitimately produce nothing never enter the population, so every ratio the gate reads is
   conditioned on the run having produced a plan.

## 6. What this costs to widen

Four runs took about eight minutes of wall clock and needed no attention beyond dispatching each
prompt. The expensive part of the runbook's original loop — babysitting, Debug-only reading, harvesting
a `sed` pipeline — is gone: the tally is one greppable release-visible line per verify pass.

## 7. Limits

Four runs, one machine, one provider (Pia Cloud), one afternoon. `n = 15` declarations, `n = 9` probed
candidates. The 33% file-shapedness rests on three probed runs, and the entire `NOT FOUND` finding
rests on **one** run's four declarations. It is an existence proof — a disjunctive declaration produces
a false negative, reproducibly, in the shape the planner actually emits — not a rate.

What it is enough for: A4 is worth doing before A2, and the "`NOT FOUND` = 0 means the channel cannot
produce a negative" reading in
[`2026-08-22-a1-log-collection-runbook.md`](2026-08-22-a1-log-collection-runbook.md) §6 should be struck.
What it is not enough for: any decision that turns on the *size* of the file-shaped share.
