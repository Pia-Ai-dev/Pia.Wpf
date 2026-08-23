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

Final probe line per run — **not** the sum of its lines; see trap 1 in §5.

| Run | declared | fileShaped | notFileShaped | probed | found | notFound |
|---|---|---|---|---|---|---|
| `a6898d91` | 1 | 1 | 0 | 1 | 1 | 0 |
| `741d3aed` | 7 | 0 | 7 | 0 | 0 | 0 |
| `be5767e1` | 7 | 4 | 3 | 8 | 4 | 4 |
| `0bbff806` | — no probe line at all — |
| **total** | **15** | **5** | **10** | **9** | **5** | **4** |

### The denominator is not 15

`declared` counts *step rows*, and a replan re-declares the same artifact against a new row with
sharper wording. Both copies survive into the final facts block, and the vague original always lands in
`notFileShaped` while its concretized twin lands in `fileShaped`. The pairing is unmistakable in both
multi-pass runs:

| Run | original | replan twin |
|---|---|---|
| `741d3aed` | step 1 "Confirmation of domain renewal (e.g., receipt or updated expiry date)" | step 4 "Domain renewal confirmation (receipt or updated expiry date)" |
| `741d3aed` | step 2 "Backup file or confirmation of vault backup completion" | step 5 "Vault backup confirmation (e.g., timestamped backup file in the vault)" |
| `741d3aed` | step 3 "Summary or report of Q3 numbers with key insights" | step 6 "Validated Q3 numbers report (cross-checked against 10-Q filings)" |
| `be5767e1` | step 2 "Document summarizing the two approaches" | step 6 same, plus `(e.g., approaches_summary.md or approaches_report.pdf)` |
| `be5767e1` | step 3 "Comparison table or report" | step 7 same, plus `(e.g., comparison_table.xlsx or comparison_report.md)` |
| `be5767e1` | step 4 "Recommendation with reasoning" | step 8 "Recommendation document with reasoning", plus `(e.g., …)` |

Collapsing each pair leaves **9 distinct intended artifacts, 5 of them file-shaped — 56%.**

So the honest reading is not "a broader mix pushed file-shapedness down to 33%". It is that **the ratio
barely moved**: 56% here against 57% on the historical corpus, on a deliberately mixed prompt set rather
than a code-shaped one. The 33% is a replan artifact, and any harvest that counts step rows carries the
same inflation — including, in unknown proportion, the original 23.

**The gate does not close on any of these numbers.** It needed ≥85%. **A2–A4, A6, A7 stay open.**

Report-channel supply (`artifactReported=`, the G6 counter): **2 of 17 step outcomes**. Only the two
runs that actually wrote files reported an artifact.

## 3. The headline: `notFound` went non-zero, and it does not mean what the gate wants it to mean

`NOT FOUND` was 0/23 across the whole historical corpus, and the runbook's reading table calls that
"the strongest result available here: the planner channel produces no negative signal at all". This
pilot produced 4. That looks like the awaited refutation. It is not — read the facts block:

```
… (e.g., criteria_list.md or criteria_summary.pdf)       → criteria_list.md: found; criteria_summary.pdf: NOT FOUND
… (e.g., approaches_summary.md or approaches_report.pdf) → approaches_summary.md: found; approaches_report.pdf: NOT FOUND
… (e.g., comparison_table.xlsx or comparison_report.md)  → comparison_table.xlsx: NOT FOUND; comparison_report.md: found
… (e.g., recommendation.md or recommendation_report.pdf) → recommendation.md: found; recommendation_report.pdf: NOT FOUND
```

Every probed declaration is an `(e.g., A or B)` **disjunction**. The step writes one of the pair; the
probe correctly reports that the other is absent. All four files exist on disk and all four steps
succeeded.

Each per-candidate fact is true. What misleads is the **aggregate**: `found` and `notFound` count
candidate paths, so a declaration naming two alternatives contributes one of each no matter how well
the step performed. `notFound=4` here, and `notFound=4` on a run where four artifacts were genuinely
never written, are the same line.

This is not the N1 fallback misfiring — the subpath-fallback line fired **zero** times, and
`Artifact probe skipped` zero times. The instrument reports exactly what it was built to report.

### Two consequences, one per layer

- **The planner prompt.** Declaring alternatives is what makes the negative signal unreadable. That is
  **A4** — "say what checkable means, say to omit the field otherwise" — rated `XS` and currently
  sequenced behind A2 as an afterthought (*"decide after A2's numbers — it may be unnecessary"*). On
  this evidence it is the highest value-per-effort row in group A and should run first. It is also
  cheap to re-measure: A4 changes prompt bytes, and this pilot is the before-reading.
- **The counter.** Even with a perfect prompt, `notFound` at candidate granularity cannot express
  "this declaration was not satisfied". A declaration should count as not-found only when **every**
  candidate misses. Until then nobody can read a future `notFound=12` as twelve failures — so this is
  a defect in G1's counter design, not only in the planner's wording.

## 4. Instrument verification

Everything the batch shipped was exercised for the first time and works.

- **G1's tally emits** and its counters are internally coherent: `fileShaped + notFileShaped = declared`,
  and `found + notFound + folder + unresolvable + uninspectable = probed` (4+4=8 on `be5767e1`).
- **G6's `artifactReported=`** appears on every step-outcome line.
- **N1's subpath-fallback line** did not fire, which is the correct outcome here and means no
  `notFound` in this sample is an instrument error.
- **A5's `ExtraJson` persistence works** — first confirmation. The throwaway DB has 17 step rows, 15
  with a non-blank `ExpectedArtifact`, and exactly **2** with an artifact in `ExtraJson`, matching the
  2 `artifactReported=True` outcomes one-for-one.

One design wrinkle beyond §3's: **the tally mixes two granularities on one line.** `declared`,
`fileShaped` and `notFileShaped` count declarations; `probed`, `found`, `notFound` and the rest count
candidate paths. On `be5767e1` that is 7 declarations against 8 candidates. A "found share" computed
across the line is a share of nothing.

## 5. Three traps in the collection method

1. **`declared` accumulates across verify passes, and carries replan twins.** `741d3aed` emitted three
   probe lines — `declared=3`, then `6`, then `7` — because the verifier reads `ctx.CompletedSteps`,
   which grows after each replan. Summing lines triple-counts; reading the last line still
   double-counts the re-declared artifacts (§2).
2. **Runs execute concurrently.** The runbook's §4 loop polls until the *N*th `Artifact probe:` line
   appears and then dispatches the next prompt. Runs `741d3aed` and `be5767e1` overlapped by 36 seconds,
   so line order does not track prompt order. Attribution requires the `[run <id>]` prefix, not a count.
3. **A whole category is structurally invisible.** The answer-only control completed normally with
   `offered=False` — the SingleTurn fallback, no plan steps, no declarations, **no probe line**. Runs
   that legitimately produce nothing never enter the population, so every ratio the gate reads is
   conditioned on the run having produced a plan.

## 6. What this costs to widen

Four runs took about eight minutes of wall clock and needed no attention beyond dispatching each
prompt. The expensive part of the runbook's original loop — babysitting, Debug-only reading, harvesting
a `sed` pipeline — is gone: the tally is one greppable release-visible line per verify pass.

## 7. Limits

Four runs, one machine, one provider (Pia Cloud), one afternoon. Nine distinct artifacts, nine probed
candidates. The 56% rests on three probed runs, and the entire `notFound` finding rests on **one** run.
It is an existence proof — a disjunctive declaration produces an unreadable negative, reproducibly, in
the shape the planner actually emits — not a rate.

The 56%-versus-57% comparison is directional only: the historical 23 was harvested from facts-block
lines and carries an unknown amount of the same replan inflation, so the two are not strictly the same
unit. What survives that caveat is that nothing here is anywhere near the 85% the gate needs.

What this is enough for: A4 before A2, G1's `notFound` counter needs a per-declaration form, and the
"`notFound` = 0 means the channel cannot produce a negative" reading in
[`2026-08-22-a1-log-collection-runbook.md`](2026-08-22-a1-log-collection-runbook.md) §6 should be struck.
What it is not enough for: any decision that turns on the *size* of the file-shaped share.
