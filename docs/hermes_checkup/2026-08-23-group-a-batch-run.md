# Run report — Group A buckets 1 and 2, stopped at the finish line

**Status:** WIP. Everything below is committed as work-in-progress and **nothing has been executed as a
test.** The batch is code-complete and rebuilds clean in both configurations; that is the only
verification it has.
**Owner:** unassigned. **Written:** 2026-08-23.
**Origin:** the §0 prompt of [`2026-08-22-group-a-brief.md`](2026-08-22-group-a-brief.md) — "ship §3's
bucket 1 and bucket 2 only" — run on branch `feature/speaker-attribution` on top of `c8b401c2`.

---

## 1. What ran

The brief's §8 workflow shape, executed as one orchestrated run:

| Phase | Agents | Outcome |
|---|---|---|
| Plan | 8 (one per item) | plans for N1, N2, G1–G6 |
| Decide | 1 arbiter | the two §5 decisions ruled once; cross-item contracts frozen |
| Implement | 5 (disjoint file ownership) | all five landed |
| Build gate | 1, serialized | `0 Warnung(en)` / `0 Fehler`, Debug and Release |
| Simplify | 1 (sonnet) | reviewed everything, **made no edits** — nothing to cut |
| Review | 5 dimensions | 16 findings raised |
| Verify | 32 refuters (two per finding, different lenses) | 14 refuted, 2 survived |
| Fix | 1 | both survivors applied, both gates re-run clean |
| Finalize | 1 | brief ticked against `git diff`; Release IL checked empirically |

The run was **stopped during finalize's closing report**, after it had ticked the brief and confirmed
both builds. Nothing was lost except the machine-written summary, which this doc replaces.

## 2. What landed

| Item | Files |
|---|---|
| **G1** release-safe outcome tally · **N1** silent-fallback log | `src/Pia.Wpf/Services/AgentVerifier.cs`, `tests/Pia.Wpf.Tests/Services/AgentVerifierTests.cs` |
| **G6** artifact-presence boolean | `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs`, `src/Pia.Wpf/ViewModels/Models/ChatSession.cs`, their two `*StepOutcomeSignalTests` |
| **G2** offline declaration measurement | `scripts/Measure-ArtifactDeclarations.ps1`, `scripts/artifact-declaration-cases.json` |
| **G3** classifier-replay test | `tests/Pia.Wpf.Tests/Integration/ArtifactProbe/` (4 files incl. `README.md`) |
| **N2** plan-doc corrections · **G4** runbook corrections · **G5** scheduled-route section | [`2026-08-22-artifact-evidence-plan.md`](2026-08-22-artifact-evidence-plan.md), [`2026-08-22-a1-log-collection-runbook.md`](2026-08-22-a1-log-collection-runbook.md), [`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md) (A1 and A4 row *descriptions* only) |

## 3. The two decisions §5 demanded, as ruled

**(a) Where G2's classifier lives — reimplement in PowerShell and pin the duplication.** The script
carries its own copy of the probe's extension-shape rule. `scripts/artifact-declaration-cases.json` is
the shared table: the script replays it before **every** measurement and *throws* rather than printing a
drifted ratio, and `DeclarationClassifierParityTests` replays the same file against the real verifier. The
pin is therefore cross-language and enforced from both sides.

**(b) Does G1 replace the runbook's §6 harvest or supplement it — replace.** The pipeline is **deleted**,
not corrected: both the PowerShell `→` parser and its `grep`/`sed` twin are gone, replaced by a short
paragraph saying why a line-level parser over the facts block could never work, and by pointing at G1's
counter line as the number of record on any build. §2.1 ("Build Debug — this is the big one") is demoted
rather than removed, because either configuration now carries the tally.

Two further calls the implementation had to make and stated:

- `overPathCap` is **disjoint** from `probed` — both budget-capped arms skip the increment — so the
  per-candidate counters sum exactly to `probed` and the capped ones sit beside it.
- The tally **does not** subdivide the not-a-file-reference bucket by shape. That would need a prefix
  classifier that does not exist, and the row wanting it (A7) is gate-blocked.

## 4. The log surface, exactly

This is the part to get right before reading any log written by a build carrying this batch.

- **Reshaped.** `Artifact probe: {N} declaration(s), {M} path(s) probed.` became
  `Artifact probe: declared= fileShaped= notFileShaped= overReportCap= probed= found= notFound= folder= unresolvable= uninspectable= overPathCap=`,
  still a plain `LogInformation` and still counts only. **Any grep on the old form breaks** — but the old
  form carried no outcome, so nothing in the existing reading depended on it.
- **Unchanged, byte-identical.** The `Artifact probe facts:` `SensitiveDebug` block — the per-declaration
  `declared: X → found (…)` lines that produced the 57/43 read. Samples collected after this batch remain
  directly comparable to the 23-declaration reading.
- **Additive.** G6 appends ` artifactReported=True|False` to the end of the two existing step-outcome
  `LogInformation` lines. Old greps still match. Presence is `ArtifactRef` non-blank, never the value.
- **New line.** N1 emits `Working subpath did not resolve to an existing folder under the sandbox` at
  `SensitiveDebug`, so it is Debug-only — the same channel and wording `FilesToolHandler` uses.

**The cut, stated for the record:** the counters change what an `Artifact probe:` line *means*. Counts
read off a build carrying this batch are not comparable to the old line. The underlying evidence — the
facts block — is untouched, so the cut is in the summary line, not in the sample.

## 5. Why it was stopped

Mid-run the owner flagged that **more probe logs are still to be collected for the A run**, and asked to
stop before anything that would interfere. It had already finished by then. The working tree was left
intact after establishing the point in §4: because the facts block is unchanged, landing this batch does
not spoil further collection — it makes it cheaper, since the tally is release-safe and greppable in one
line on any configuration.

One friction survives that decision: **if logs are collected from the build currently installed on the
Windows box**, that build emits the *old* `Artifact probe:` form, and the runbook no longer carries a
recipe for parsing it (G4 deleted §6's pipeline). Harvest the facts block in that case, or install a build
carrying this batch first.

## 6. What the review actually caught

16 findings across five dimensions; each was put to two independent refuters (one attacking the facts, one
attacking the consequence, both instructed to default to *refuted* when uncertain). **14 were refuted.**
Both survivors were `minor` and both were applied:

1. **The subpath-fallback line's emission was never asserted** — only its *absence* from release-visible
   levels. Deleting the flag assignment would have left both tests green with the diagnostic silently dead.
   Fixed by adding a `#if DEBUG` positive assertion on the captured Debug entry, so one test now pins both
   halves: emitted, and not on a release-visible line.
2. **Dead code** — `DeclarationCase.IsFileShaped`, a third hand-written definition of file-shapedness with
   no consumer, inside the folder whose whole purpose is that the boolean is pinned against the real
   verifier. Deleted; the claim lives in the JSON table and the arm text in `ExpectedOutcome`.

Nothing was found on the privacy line, which the review was pointed at specifically: the probe's
`ProbeDeclarations` and `Probe` remain `static` and logger-free, and finalize confirmed against the
**Release IL** that the tally survives and the sensitive lines do not.

## 7. What is deliberately not here

**A2, A3, A4, A6, A7 and review recommendation #13 were not opened.** Bucket 3 of the brief's §3 names the
question each is waiting on. Review #7 (global pause) was offered as an optional add and not taken.

**Gate A1 is still open.** Nothing in this batch ticks it, no A row in the checklist was ticked, and the
decision-gates table is untouched. Only the A1 and A4 row *descriptions* were corrected, per §6 of the
brief.

## 8. What still needs a human on Windows

In value-per-minute order.

1. **`dotnet test`, no filter.** The precondition for trusting anything above. 116 `Fact`/`Theory` methods
   across the two previous batches have compiled and never executed, plus everything this batch added:
   `DeclarationCorpusReplayTests`, `DeclarationClassifierParityTests`, and new assertions in
   `AgentVerifierTests`, `HeadlessStepOutcomeSignalTests` and `ChatSessionStepOutcomeSignalTests`. If the
   suite is red, every number below is guesswork.
2. **Run the measurement script.** Minutes, no app launch, safe while Pia is open:
   `./scripts/Measure-ArtifactDeclarations.ps1 -SelfTest` then
   `./scripts/Measure-ArtifactDeclarations.ps1`. This moves the file-shapedness ratio off *n=23* onto the
   machine's whole history. It cannot answer `found` vs `NOT FOUND`.
3. **Create two or three `AgentTask` routines and walk away.** ~10 minutes of editor work, then no
   attention. They must be created as `AgentTask` through the routine editor — the eight blueprint cards
   all ship `Kind: Research` and produce zero samples. The runbook's new section has the detail.
4. **Only if time remains:** the runbook's interactive loop, now cheaper because the AutomationIds landed,
   but still one click per probe line.

## 9. Known gaps in what landed

- **Nothing has been executed.** `net10.0-windows` cannot run on the dev Mac; a clean rebuild in both
  configurations is the whole of the verification.
- **`Measure-ArtifactDeclarations.ps1` has never run.** `pwsh` is not installed on the dev Mac, so not even
  a syntax-level smoke test happened. Treat its first Windows invocation as the smoke test, and run
  `-SelfTest` first.
- **The new tests read source text** for two assertions (that the fallback is logged through the sensitive
  channel only). That has precedent in `tests/Pia.Wpf.Tests/Architecture/`, but it is a weaker pin than a
  behavioural one and will break on an innocent rewording.
