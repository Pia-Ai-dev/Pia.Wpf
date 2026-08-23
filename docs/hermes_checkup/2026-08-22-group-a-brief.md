# Brief — Group A: Make the Verifier's Evidence Worth Having

**Status:** ready to execute. Self-contained: paste §0 into a fresh session and it has everything.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** group **A** of [`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md),
whose plan is [`2026-08-22-artifact-evidence-plan.md`](2026-08-22-artifact-evidence-plan.md) and whose
gate has a collection runbook at [`2026-08-22-a1-log-collection-runbook.md`](2026-08-22-a1-log-collection-runbook.md).
Batch 2's brief ([`2026-08-22-next-batch-brief.md`](2026-08-22-next-batch-brief.md)) said "the A track is
on hold; do not touch it." **This brief is the answer to *what can be touched anyway*.**

---

## 0. The prompt

> Read `docs/hermes_checkup/2026-08-22-group-a-brief.md` and implement the batch it scopes, in one
> workflow following our schema: plan → implement → build gate → simplify (sonnet) → review → fix →
> finalize. Ship §3's bucket 1 and bucket 2 only. **Do not open A2, A3, A4, A6, A7 or review #13** —
> §3 bucket 3 says which question each is waiting on.

Everything below is what that brief says.

---

## 1. Where the repo is

Branch `feature/speaker-attribution`. Batch 2 landed as `d27f5dc1` (C4 blueprints + E1–E6, E8),
`66dd3131` (merge) and `ffa43b23` (E3/E4 persona reach), on top of a large AutomationId gap-fill run
(`deeeea3b`…`c0c9f818`, D7). **`git diff 50e7b921..HEAD` touches no A-track source file and no A-track
doc.** `AgentVerifier.cs`, `StepOutcomeSignal.cs`, `StepExtraJson.cs`, `RunContext.cs`,
`AgentPlanner.cs`, `AgentRunOrchestrator.cs` and `AgentRunService.cs` are all untouched since A5.

**Nothing in batch 1 or batch 2 has been executed as a test.** `net10.0-windows` cannot run on the dev
Mac. `E7` is the open row that says so. If a Windows `dotnet test` run has happened since, its result
outranks anything written here.

### What the gate has actually read

A1 has one reading, from one client's `artifacts/Logs/pia-*.log`: **23 declarations over 7 probe lines
on one machine over three days, all code-shaped tasks** — 57% `found`, 43% `not a file reference`,
**0 `NOT FOUND`**. That refutes "already high". It is not a number to tune on, and no second reading
has landed.

### What has shipped

**A5 is done** (`0e825f43`). `ArtifactRef` is merged into `AgentSteps.ExtraJson` by
`StepExtraJson.WithArtifactRef` (`src/Pia.Wpf/Services/StepExtraJson.cs:15`) via
`AgentRunService.cs:1246`, and rehydrated on resume at `AgentRunOrchestrator.cs:934`. It also pinned
both arms of the tag semantics it changed — `Run_Resume_VerifierSeesTheArtifactRefEachStepReported`
and `Run_Resume_StepThatReportedNoArtifact_SeedsANullOutcome` in `AgentRunOrchestratorTests`. Do not
re-plan that work; it is closed, tests included.

A5 landed ahead of the gate on the argument *"persisting the field is worth doing whichever way A1
reads."* **That argument is the whole subject of §3, and A5 was the last item it fits cleanly.**

---

## 2. The two channels — get this right or everything below is wrong

The plan's §1 table is the load-bearing distinction and it is easy to collapse.

| | `AgentStep.ExpectedArtifact` | `StepOutcomeClaim.ArtifactRef` |
|---|---|---|
| Who says it | the **planner**, in `emit_plan` | the **step itself**, in `emit_step_result` |
| When | *before* the step runs — a prediction | *after* the step runs — a report |
| Persisted | `AgentSteps.ExpectedArtifact` (`SqliteContext.cs:497`) | `AgentSteps.ExtraJson.artifactRef` (**since A5**) |
| Into the context | `RunContext.RecordStep` → `CompletedStepSummary.ExpectedArtifact` (`RunContext.cs:150`) | same call → `.Outcome.ArtifactRef` |
| Probed | **Yes** — `AgentVerifier.cs:267` filters `CompletedSteps` on non-blank `ExpectedArtifact` | **No** |
| Reaches the critic as | a fact block in the **System** message (`AgentVerifier.cs:137`) | unprobed prose `produced: <ref>` in the **User** message (`AgentVerifier.cs:204-205`) |

**A2 is about routing the second one through the probe that already exists for the first.** Everything
about A2 that the plan under-prices follows from the last row: the two channels do not land in the
same chat message, and that split is deliberate. `TokenizingAiClientService.TokenizeMessages` rewrites
`ChatRole.User` only (`TokenizingAiClientService.cs:275`), which is why Batch 08 F11 moved the
executed-step listing — and with it `produced:` — onto the User message. The probed facts block is
app-generated, so it stays in System.

So the plan's illustrated line —

```
- step 3 "Write the summary" → produced: out/q3-summary.md → found (2.1 KB)
```

— **cannot be produced by "pointing the existing machinery at a second source."** Either the probe
result is threaded down into `BuildExecutedSteps` (today static, ctx-only, and it runs up to twice per
verify while the probe deliberately runs once, `AgentVerifier.cs:62`), or `ArtifactRef` joins the
System-side facts block and ships model-authored text past the tokenizer. Neither is free. The plan
names neither. That is the first thing A2's planning phase has to decide.

---

## 3. The hardest question: what can be built without prejudging the gate

A1 closes A2, A3, A4, A6, A7 and review #13. Every A-track item below is sorted into exactly one
bucket, with the verdict that would waste it named.

### Bucket 1 — GATE-INDEPENDENT

**This bucket is small, and saying so is the finding.** A5 was the one substantial item that fit the
"worth doing whichever way A1 reads" argument, and it shipped. What remains is two `XS` items. Do not
let a planning phase pad it — §3's bucket-2 list is where this week's value actually is.

- [x] **N1 · Log the probe's one silent root-resolution failure.**
  `ResolveProbeRoot` (`AgentVerifier.cs:336-355`) has four outcomes and logs three of them: a blank
  folder logs `Artifact probe skipped … no usable files folder` (`:280`), a non-existent one logs
  `… files folder does not exist` (`:299`), a timeout or fault logs `Artifact probe failed` (`:319`).
  The fourth — a `WorkingSubpath` that escapes containment or does not exist — **falls back to the base
  root at `:348-353` with no log line at all**, and every artifact the run wrote under that subpath
  then reports `NOT FOUND`. The code's own comment calls that outcome *"a confident false fact, which is
  worse than no fact."* `FilesToolHandler.ResolveEffectiveRoot` logs its equivalent fallback at
  `SensitiveDebug` (`FilesToolHandler.cs:202`); the probe, which is described in source as a mirror of
  it, does not. Add the same line.
  The concrete divergence path: the writer resolves from `TaskAmbient.Current?.WorkingSubpath` per
  step, the probe from `ctx.WorkingSubpath` read once at `BeginRun` (`LiveTurnExecutor.cs:115`). A chat
  whose working directory changes mid-run puts the two out of step, and nothing in the logs says so.
  *What A1 verdict wastes this:* **none.** If the gate closes, the probe stays shipped and so does the
  false-`NOT FOUND` path. *Effort:* **XS** · *Value:* **Med**

- [x] **N2 · Correct the artifact-evidence plan.** Five of its claims are now stale or wrong; §6 has the
  table. The two that will burn an implementer are the `ArtifactRef is not persisted` line (A5 closed
  it, and the plan repeats it in §1, §2 and move 4's notes) and A4's second edit site, **which does not
  exist** (§6).
  *What A1 verdict wastes this:* **none.** A wrong plan doc is wrong either way.
  *Effort:* **XS** · *Value:* **Med**

**The item most likely to be mis-sorted into this bucket is A6.** The argument for it is real: the
probe is ~120 lines of static private methods inside a 500-line verifier, and extracting
`IArtifactProbe` is a testability win on shipped code. It still fails the test. A6 is rated
**Enabler** — *"little standalone value, unblocks a High"* — and the High it unblocks is A7, which the
gate can kill outright. The existing tests reach the probe *through* `VerifyAsync` (ten-odd assertions
read `declared: X → …` off the System prompt), so the extraction unlocks no new test without also
moving them. If the gate closes, A6 is churn. It goes in bucket 3.

### Bucket 2 — GATE-ENABLING

Work that makes the measurement cheaper, faster or repeatable, and so closes the gate sooner instead
of depending on it. **This is the batch.**

- [x] **G1 · Put the outcome tally on the release-visible probe line.** *The single highest-leverage
  item on the A track.*
  `AgentVerifier.cs:303` emits `Artifact probe: {Declared} declaration(s), {Probed} path(s) probed.`
  at `LogInformation` — release-visible. The per-declaration facts sit one line below at
  `SensitiveDebug` (`:304`), which `SafeLog.cs:19`'s `[Conditional("DEBUG")]` **erases from Release
  IL**; no log level brings it back and `Bootstrapper.cs:351` only raises the level under `IsDevMode`,
  itself `#if DEBUG`.
  So the number the runbook says matters most — the `found` / `NOT FOUND` split — exists **only in a
  Debug build**, and `probed` cannot substitute for it: `probed` increments inside the per-*candidate*
  loop (`:397`), capped at 3 per declaration (`:240`) and 12 overall (`:238`), so `probed/declared`
  ranges over [0,3] for reasons unrelated to found-ness. The only inference Release supports today is
  `probed == 0` versus `probed > 0`.
  Fix: `ProbeDeclarations` already walks every arm; have it return the counts alongside `probed` and
  interpolate them. **Counts only, never a declaration string** — that is what makes the new line
  release-safe with a plain `LogInformation` and no `SensitiveDebug`.
  Two decisions the implementation must state, because they are exactly the ambiguity that makes §6's
  shell pipeline fragment:
  - The **file-shaped / not-file-shaped** split is counted per **declaration** (`FileCandidates`
    returned empty or not).
  - **found / NOT FOUND / folder / unresolvable / budget-capped** are counted per **candidate**, so
    they sum to `probed` and a composite declaration naming two files contributes two.
  After this, the harvest is `grep 'Artifact probe:' | sum` on **any** build. It removes the runbook's
  §2.1 "this is the big one" requirement entirely.
  *Effort:* **XS** · *Value:* **High**
  *Landed with two corrections:* `overPathCap` is **disjoint** from `probed` (both budget arms skip the
  increment), so the per-candidate counts sum to `probed` and the capped ones sit beside it; and the tally
  deliberately does **not** subdivide the not-a-file bucket for A7 — that needs a prefix classifier that
  does not exist yet.

- [x] **G2 · Recover the file-shapedness half of A1 from the database — no new runs, no Debug build,
  no desktop.**
  `ExpectedArtifact` is a persisted column (`SqliteContext.cs:497`) in the same `history.db` the
  compaction corpus script already reads, **nothing ever deletes an `AgentRuns` row** (there is no
  `DELETE FROM AgentRuns` anywhere in `src/`), and the only `DELETE FROM AgentSteps` sites are
  replan's replace-steps writes. So every declaration ever made on a machine is still there — not just
  the ones inside the three-day window where a probe line happens to exist, which is small only
  because the H1 verifier shipped 2026-07-28.
  Replaying those strings through the classifier's rule yields the exact
  `found`-vs-`not a file reference` split that produced the 57/43, on a sample plausibly an order of
  magnitude larger. The extraction precedent is already in the repo:
  `scripts/Export-CompactionCorpus.ps1` opens the DB **read-only** (safe while Pia is running),
  hard-refuses any output path inside the repository, and prints counts and never content. Mirror it
  as `scripts/Measure-ArtifactDeclarations.ps1`.
  Two honest caveats, both of which belong in the script's own `.DESCRIPTION`:
  - A replan **deletes** non-`Done`/`Skipped` rows (`KeepDoneAsync`, `AgentRunOrchestrator.cs:874` →
    `SafeReplaceSteps`), so declarations that were replanned away are gone. The recovered sample is
    biased toward surviving steps.
  - **`found` vs `NOT FOUND` is not recoverable this way.** The filesystem has moved on and a per-run
    workspace is torn down at settle. Only the file-shapedness split comes off the DB.
  That is the right split of the gate: the ratio §1 actually quotes is recoverable offline today, and
  only the `NOT FOUND` row needs live runs.
  **Decide:** a PowerShell script re-implements `LooksLikeFileName` and can drift from it. Either
  accept that and pin the duplication with a test, or have the script emit declarations to stdout and
  classify them in G3's test. Say which, and why.
  *Effort:* **S** · *Value:* **High**
  *Decided:* reimplement in PowerShell and pin it — `scripts/artifact-declaration-cases.json` is replayed
  by the script before every measurement (it throws instead of printing a ratio on a mismatch) and by
  `DeclarationClassifierParityTests` against the real verifier. *Not smoke-tested:* `pwsh` is **not**
  installed on the dev Mac, so the script has never executed; §4's item 5 is wrong on that point.

- [x] **G3 · A corpus-replay test that pins the classifier's arms.**
  The harness already exists. `AgentVerifierTests.CtxDeclaring(params string?[])` (`:44-55`) builds a
  `RunContext` whose completed steps declare arbitrary strings; `ReturnsVerdict` captures the System
  message through an NSubstitute `IAiClientService`; `LastPrompt` (`:60`) reads it; and tests at
  `:234-245` already tally arms (`Assert.Equal(12, CountOccurrences(LastPrompt, "→ NOT FOUND"))`).
  Feeding a declaration corpus through that needs **no Debug/Release reasoning, no WinWright session
  and no desktop** — only a Windows or CI `dotnet test`.
  Be exact about what it does **not** measure, or it will be mistaken for the gate: it replays whatever
  corpus you hand it, so it measures `LooksLikeFileName` and not the population; it says nothing about
  the `found`-vs-`NOT FOUND` split against a real filesystem; and it says nothing whatsoever about
  `ArtifactRef`. **It cannot close A1.** What it can do is pin the classifier so a later A4 prompt
  change is measurable, and give the composite and prefixed line forms a home — see G4.
  Natural location: alongside the compaction harness at
  `tests/Pia.Wpf.Tests/Integration/ArtifactProbe/`, matching `Integration/Compaction/`.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**

- [x] **G4 · Fix the collection runbook. It is the document standing between the gate and an answer,
  and four of its sections are now wrong.**
  - **§4.1's stated blocker is gone.** It says `AssistantView.xaml` carries *zero* AutomationIds and
    that the Agent lever and Run-in-background are reachable by localized name only. D7 landed
    `Assistant_Mode_Agent` (`AssistantView.xaml:531`), `Assistant_Mode_Chat` (`:525`),
    `Assistant_RunInBackground` (`:557`), `InputTextBox` (`:305`), `MessageScrollViewer` (`:111`) and
    `Assistant_Send` (`:570`), all registered in
    [`../ui_automation/ui-automation-playbook.md`](../ui_automation/ui-automation-playbook.md). The loop
    is language-independent and heal-able now.
  - **§6's harvest pipeline fragments on exactly the task mix §5 demands.** `AgentVerifier.cs:401-403`
    prints the bare `found` / `NOT FOUND` form **only** when a declaration has one candidate that is
    byte-identical (`Ordinal`) to the flattened, truncated declaration. `"a summary saved to report.md"`
    prints `report.md: NOT FOUND`; a multi-file declaration — which `AgentPlanner.cs:159` and the prompt
    at `:784`/`:827` explicitly *ask for* ("ONE step listing every file in expectedArtifact") — prints
    one composite line joined by `"; "` (`:405`). Neither collapses into three buckets, and
    `found, but it is a folder, not a file` (`:438`) is a fourth arm that `s/found \([^)]*\)/found/`
    leaves standing. **There are eight outcome strings, not three** (`:381`, `:385`, `:394`, `:430`,
    `:436`, `:438`, `:439`, `:443`) plus a tally line at `:416`. The existing 13/10/0 read is only
    interpretable because the corpus was code-shaped (`Program.cs`, `Calculator.cs`). Classify on the
    arm **substring per candidate**, not on the whole line — or, better, delete §6's pipeline and read
    G1's counters.
  - **§2's "four gates" misses a path.** All four refs check out
    (`AgentRunOrchestrator.cs:250`, `:516-517`; `LiveTurnExecutor.cs:91`; `AppSettings.cs:232`) — but a
    scheduled `AgentTask` routine reaches the same verify with **none of gates 2–4**. See G5.
  - **A missing caveat that changes the arithmetic.** A verify-fail that replans re-enters the drain
    loop (`AgentRunOrchestrator.cs:530-539`) and verifies **again**, while `RunContext._completed`
    accumulates and is never cleared (`RunContext.cs:145`, `:167`). So one run can emit several probe
    lines whose declaration sets **overlap**. "7 verifier runs and 23 declarations" counts probe lines,
    not runs, and the declarations are not independent samples. Say so next to the number.
  *Effort:* **XS** · *Value:* **High**
  *Decided:* §6's pipeline is **deleted**, not corrected — G1's counter line is the number of record on any
  build, and §6 keeps a short prose paragraph saying why a line-level parser could never work. §2.1 is
  demoted (either configuration carries the tally), not removed.

- [x] **G5 · Document the scheduled-`AgentTask` route as the unattended sample generator.**
  The cheapest way to produce probe lines without a person clicking is a routine, not WinWright.
  `ScheduledJobBackgroundService.cs:475` dispatches an `AgentTask` job through
  `HeadlessRunLauncher` (`:488`) into the same `orchestrator.RunAsync`, so it plans, drains and
  verifies with **no Chat/Agent lever** (there is no composer in the loop), **no plan-approval park**
  (`SupportsPlanApproval` is `true` only on `LiveTurnExecutor`, `:91`) and **no auto-approve setting
  needed** — `ScheduledJobBackgroundService.cs:516` turns the job's `GrantedTools` into the run's
  pre-granted writes, and an empty list becomes `null` becomes the launcher's
  `HeadlessRunRequest.DefaultGrantedWrites` (`{write_file}`, `HeadlessRunLauncher.cs:336`). One Debug
  build left running on a Windows box then produces samples on a schedule.
  Two caveats, both important:
  - **The eight C4 blueprint cards produce zero samples.** All of them ship `Kind: Research`
    (`RoutineBlueprint.cs:38, 58, 79, 98, 119, 144, 167, 188`), and the Research leg goes to
    `ExecuteResearchAsync`, never to the orchestrator. The sample-generating routines must be created
    as **`AgentTask`** by hand, through `Routines_Field_Kind` in the editor.
  - It still needs a Windows machine, so it does **not** answer "without a human" outright. What it
    removes is the 24-prompt babysitting loop §4.2 is built around, which is the expensive part.
  *Effort:* **XS** (a runbook section; no code) · *Value:* **High**

- [x] **G6 · One release-safe field for the *other* channel, so A2 does not inherit A1's blindness.**
  Nothing release-visible today says whether a step reported an artifact at all.
  `HeadlessTurnExecutor.cs:619-621` logs `offered / confirmed / succeeded / declarations`; the ref
  itself is `SensitiveDebug` at `:625` and at `ChatSession.cs:867`, correctly. Add an
  artifact-**presence** boolean (never the value) to that existing `LogInformation`, and the same on the
  `ChatSession` twin.
  A5 also bought most of this already and it is worth stating plainly: **once a build carrying A5
  ships, `AgentSteps.ExtraJson.artifactRef` makes the report channel offline-recoverable exactly the
  way G2 recovers the planner channel.** The corpus is empty today because A5 is one day old — which
  is an argument for shipping soon, not for waiting. (One accidental release-visible counter already
  exists: `AgentRunOrchestrator.cs:941-944` logs `{WithArtifact} with a reported artifact`, but only on
  a resume.)
  *Effort:* **XS** · *Value:* **Med**

#### Does this change review #3's priority? No — and #3 is not in group A.

Review **#3 (Send Diagnostics — consented, redacted log bundle)** sits in the checklist's *not yet
planned* table, alongside #2. Batch 2's brief gave it a second argument: *"the A1 measurement needed
logs hand-copied off a Windows box, which is precisely the workflow #3 productizes."*

**G1 and G2 mostly retire that argument.** G2 removes the need for logs entirely for the
file-shapedness half, and G1 makes the remaining number release-safe and greppable in one line rather
than reconstructable from a Debug-only facts block. What is left for #3 to carry is the `NOT FOUND`
row from users who are not the developer — real, but it is a *widening* argument, not an unblocking
one.

Net: **do not promote #3 on A1's account.** Its value is unchanged and it is still right to take it
with #2, as the checklist says — #2 names which layer broke, #3 is the action the same card offers
when naming it is not enough. One genuine synergy worth recording: G1's counters are *counts*, so a
diagnostics bundle can carry them with no new redaction work.

### Bucket 3 — GATE-BLOCKED

| Row | The question A1 must answer first |
|---|---|
| **A2** · route `ArtifactRef` through the probe | **Does the planner channel produce a negative signal at all?** A2's entire premise is that `NOT FOUND` stays at or near zero on an honest mix, so the only real negative can come from the report channel. If a widened sample shows `NOT FOUND` is *common*, the existing probe is already catching missing artifacts and A2 matters less than assumed. A2 additionally cannot be estimated until §2's System-vs-User decision and §4's budget decision are made — it is not the `S` the checklist rates it. |
| **A3** · tests for self-reported-but-missing | Blocked transitively: the case does not exist until A2 does. **G3 is the slice of A3's work that is not blocked** — pinning today's classifier arms. |
| **A4** · tighten the planner prompt | **Is the `found` share already ≥85%?** If it is, the planner is producing file-shaped strings and tightening the prompt buys nothing. The plan also sequences A4 after A2's numbers on purpose. And the row names an edit site that does not exist (§6). |
| **A6** · extract `IArtifactProbe` | The same question that decides A7. Rated **Enabler**, and the High it unblocks can be cancelled. See the counter-argument at the end of bucket 1. |
| **A7** · todo / reminder / vault probes + `kind:ref` | **What share of declarations are non-file but still checkable?** And here is a real gap in the gate's own design: `LooksLikeFileName` decides file-ness purely from extension shape (`AgentVerifier.cs:471-483`), so `todo:Call the vendor` already falls out as `not a file reference` — correct by accident. A1's 43% `not a file reference` bucket is therefore the *only* evidence for A7's size, and **A1 as specified does not subdivide it.** Even a widened A1 will not size A7 unless the harvest also breaks that bucket down by shape. Fold that into G1 or G4 if A7 is wanted. Note also that A7's prefix dispatch cannot live *inside* `FileCandidates` — it has to run before it, which is what makes A6 its prerequisite. |
| **Review #13** · reject a plan step whose `ExpectedArtifact` is unprobeable prose | Blocked on A4's question, **and unbuildable as written.** There is no per-step rejection seam: `AgentPlanner.cs:637-651` validates all-or-nothing and a `false` return degrades the entire plan to `SingleTurn` (`:218-222`); the only per-step *drop* precedent (`:653-660`) drops a persona assignment, never a step. #13 needs that seam designed first, or it must be reframed as a prompt change — in which case it **is** A4 and should stop being tracked twice. |

---

## 4. If the only Windows time this week is short

Sharp split. **An agent on macOS can write every item in buckets 1 and 2 and compile none of the
tests.** Only a human at a Windows machine can execute anything.

### On macOS, in this order (all of it, no Windows needed to *write* it)

1. **G4 + N2** — the doc corrections. Cheapest, and they stop the next Windows session being spent on
   a stale runbook. Do these first so the Windows list below is correct when someone picks it up.
2. **G1** — the release-safe tally. Compiles here (`-p:EnableWindowsTargeting=true`), cannot be tested
   here. This is the item that changes what every later measurement costs.
3. **N1** and **G6** — one log line each, same build, same review.
4. **G3** — the replay test. Written here, executed on Windows.
5. **G2** — the script. PowerShell and `sqlite3` both exist on macOS, so it can be smoke-tested
   against a throwaway DB locally even though the real corpus is on the Windows box.

### At the Windows machine, in strict value-per-minute order

1. **`dotnet test`, no filter.** Not an A row — it is `E7`, and it is the precondition for trusting
   anything in buckets 1–2. 116 `[Fact]`/`[Theory]` methods added across batches 1 and 2 have compiled and
   never executed (`git diff 821adcfc~1..HEAD -- tests/`).
   If the suite is red, every number below is guesswork. **Do this first even at the cost of dropping
   item 4.**
2. **Run G2's script.** Minutes. No app launch, no agent runs, no log copying. It moves the
   file-shapedness ratio from *n=23* to whatever that machine's whole history holds. **Highest
   information per minute on the entire A track.**
3. **Create two or three `AgentTask` routines per G5 and walk away.** ~10 minutes of editor work with
   a Debug build installed, then zero attention. This is what makes the `NOT FOUND` row closable next
   week without a person present. Remember the routines must be `AgentTask`, not the blueprint cards.
4. **Only if time remains:** the runbook's §4.2 interactive loop, now cheaper because D7's
   AutomationIds landed. It is still the most expensive way to buy a sample — one click, one run, one
   line — and item 3 supersedes it for anything unattended.

**Be honest about what this week can achieve: nothing on that list closes the gate.** Items 2 and 3
are what make it closable, and item 1 is what makes the answer trustworthy. Do not open A2 this week.
Its honest cost is above its `S` rating (§2's message-role decision, §4's budget decision, the
`artifact_ref` tool-description edit, and roughly ten assertion strings that move), and the gate can
still kill it.

---

## 5. Decide before implementing

Two questions the planning phase must answer out loud, not silently.

**(a) Where does G2's classifier live?** A PowerShell reimplementation of `LooksLikeFileName` can
drift from the real one, silently, and the drift shows up as a wrong ratio rather than a failure.
Options: reimplement and pin the duplication with a test that feeds the same table to both; or have
the script emit declarations and let G3's test classify them, at the cost of a two-step workflow.
Either is defensible. Say which.

**(b) Does G1 replace §6's harvest or supplement it?** If the counters are trusted, §6's shell
pipeline should be **deleted**, not fixed — it is a parser for a format that no longer needs parsing,
and G4's fragmentation finding says it never worked on prose declarations anyway. If it is kept as a
Debug-only cross-check, say that it is a cross-check and that G1's line is the number of record.

Not a decision, but state it in G1's PR: the new counters change what `Artifact probe:` lines mean, so
the 23-declaration reading in the checklist and in the runbook §1 is **not comparable** to anything
measured afterwards. Record the cut.

---

## 6. Corrections the A docs need

Every row here was checked against source. Fix them in the same commits that touch the docs.

| Doc | Claim | Reality |
|---|---|---|
| plan §1, §2, move 4 | `ArtifactRef` is *"not persisted — in-process for one step exchange"* (said three times) | **A5 closed it.** `StepExtraJson.cs:15`, `AgentRunService.cs:1246`, `AgentRunOrchestrator.cs:1637` |
| plan §2 code block | `SafeSeedResumeContext` rebuilds pre-pause steps with `← no Outcome` | `AgentRunOrchestrator.cs:930-936` now seeds `new StepOutcomeClaim(true, string.Empty, artifact)` when an artifact was persisted |
| plan §1 | `ExpectedArtifact TEXT NULL` at `SqliteContext.cs:493` | line drifted to **`:497`**; the column is there |
| plan §1, runbook §6 | three outcome buckets: `found` / `NOT FOUND` / `not a file reference` | **eight** outcome strings — `AgentVerifier.cs:381, 385, 394, 430, 436, 438, 439, 443` — plus a tally line at `:416` |
| plan §2 / move 3, **checklist A4** | `AgentPlanner.cs:782` *"and the replan twin at `:827`"* both say *"include an expectedArtifact when there is a concrete deliverable"* | **`:827` does not say it.** It is the verbatim twin of `:784` (*"Group by logical change…"*), and `BuildReplanMessages` (`:817-830`) never carries the sentence at all. `:782` is the only prose instruction about the field anywhere. The real second surface is the **tool schema** — `:139` and `:148` (*"an optional expected artifact"*) and `:159` (*"The concrete artifact(s)/result this step should produce — may name several files when they are one logical change"*) — which `AIFunctionFactory` ships on every plan **and** every replan turn. So on a replan the schema descriptions are the only thing describing the field. Rewrite A4 as `:782` (prose) + `:159` and `:148` (schema), and note that `:784`/`:827` actively pull the other way by asking for several filenames in one string — the same construction that defeats §6's harvest |
| plan §5 | persisting `ArtifactRef` *"lets the run timeline show what each step actually produced"* | nothing reads it except the resume seed. `StepRowViewModel.cs:20-23` carries `ExpectedArtifact` and says *"round-tripped, not displayed"*; there is no `ArtifactRef` member and no XAML binds either field. The timeline benefit is unbuilt, not shipped |
| plan §5 | `SensitiveDebug` at `HeadlessTurnExecutor.cs:619` | **`:625-626`**; `:619` is the `LogInformation` counter line |
| plan move 1, checklist A1 | *"Read `probed / declared` off the existing line"* closes the thread | that line carries **no outcome**, and `probed` counts candidate paths (0–3 per declaration). `found` / `NOT FOUND` / `not-a-file-reference` is not derivable from it — see G1 |
| plan §2, attribution | `artifact_ref`'s description is in `AgentStepTools.BuildEmitStepResultTool()` | the string is verbatim correct but lives at **`StepOutcomeSignal.cs:164`** |
| runbook §4.1 | `AssistantView.xaml` carries zero AutomationIds; the flow needs English and cannot be healed | stale — D7 landed six ids; see G4 |
| runbook §1 | *"7 verifier runs and 23 declarations"* | 7 **probe lines**. A replan re-verifies over an accumulating `CompletedSteps`, so lines from one run overlap; see G4 |
| runbook §2 | four gates | a scheduled `AgentTask` opens none of gates 2–4; see G5 |

Two further facts worth carrying into whichever doc grows next, neither of which is a correction:

- **The probe's population is selected on a clean drain.** `AgentRunOrchestrator.cs:516-517` breaks
  *before* verify on a cancel or an unrecovered step failure, so a run whose step failed and could not
  be replanned emits no probe line at all. Steps that failed and were replanned *past* do stay in the
  probed set (`ctx.RecordStep` at `:491` runs before the `!r.Succeeded` check at `:506`). So
  `NOT FOUND = 0/23` measures a population conditioned on eventual success — not only a channel
  incapable of negatives. **A2 inherits the identical frame**, which is the population least likely to
  hold a self-reported-but-missing artifact. Say it out loud or A2's numbers will read as stronger than
  they are.
- **A2 will cover zero delegated steps.** `FanOutStepResult` (`AgentRunOrchestrator.cs:1368-1370`)
  constructs the sibling's `StepTurnResult` with no `Outcome`, pinned by
  `AgentRunOrchestratorFanOutTests.cs:710-724` — while the sibling's `ExpectedArtifact` *is* carried
  into `CompletedSteps` and probed. On any parallel plan the planner channel stays the only channel.
  Same for the `R10` single-turn fallback, which owns no step row (`:687`) and feeds neither channel.

---

## 7. Constraints

Read `CLAUDE.md` in full first. The ones that bite hardest here:

- **Privacy logging is the whole risk surface of this batch.** G1's counters are safe *because* they
  are counts; the moment a declaration string is interpolated into a non-`SensitiveDebug` line, the
  batch has shipped user content into a release log. `ProbeDeclarations` and `Probe` are `static` and
  **logger-free on purpose** (`AgentVerifier.cs:361-363`) so artifact names cannot be logged even by
  accident — **preserve that.** Return counts to the caller; do not give the probe a logger. Same for
  G6: presence, never the value. N1's line follows `FilesToolHandler`'s precedent and is
  `SensitiveDebug` because it carries a subpath.
- **G2's script must inherit `Export-CompactionCorpus.ps1`'s guardrails, all of them:** read-only DB
  open, hard refusal of any output path inside the repository with no override switch, and counts to
  stdout — never a declaration string to a file. A declaration is user content. `artifacts/` is
  gitignored; commit the derived counts, never the extract.
- **Comment discipline.** Default to no comment; one short line when the WHY is non-obvious. **Never
  cite a task, batch, gate or plan ID in source** — no `A1`, `G1`, `§3`, `move 2`. Existing files in
  this area violate this heavily (`AgentVerifier.cs` is full of `H1` / `Batch 08 F11`); do not imitate
  them, and do not go on a cleanup crusade in the same batch either.
- **Namespaces are `Pia`, not `Pia.Wpf`.** `tests/Pia.Wpf.Tests/Architecture/` holds NetArchTest
  layering rules — a new type in the wrong layer compiles and fails there.
- **Docs layout.** Everything here lives in `docs/hermes_checkup/`; links between docs in this folder
  are relative. A living reference (a runbook) keeps its written date when revised.

### Build

`Directory.Build.props` sets `TreatWarningsAsErrors=true`, so a warning is a build failure. MSBuild
output on the dev Mac is German-localized (`Warnung(en)` / `Fehler`).

```bash
dotnet build -t:Rebuild -p:EnableWindowsTargeting=true -v:m 2>&1 | tail -40
dotnet build -t:Rebuild -c Release -p:EnableWindowsTargeting=true -v:m 2>&1 | tail -40
```

The bar is `0 Warnung(en)` / `0 Fehler` in **both**. **Never run `dotnet test` on macOS** — compiling
the test project is the check available there. Never run two builds concurrently. Do not commit unless
asked.

---

## 8. Workflow shape

Same schema as batches 1 and 2.

1. **Plan** — one agent per item, grounded by reading the source. §5's two decisions get answered here.
2. **Implement** — parallel over **disjoint file ownership**. The ownership map is unusually clean:
   G1 + N1 own `AgentVerifier.cs`; G6 owns `HeadlessTurnExecutor.cs` and `ChatSession.cs`; G3 owns a
   new test folder; G2 owns a new script; N2 + G4 + G5 own the two docs. **G1 and N1 must be one
   agent** — they are the same file and the same method chain.
3. **Build gate** — serialized, single agent, Debug then Release, drive to zero.
4. **Simplify** — sonnet, quality only. Comment discipline is the most-violated rule in this area.
5. **Review** — five dimensions (correctness · CLAUDE.md conformance · tests · integration and
   architecture · scope and dead code), each finding killed or confirmed by two independent refuters.
   **Point them at the privacy line specifically:** the one way this batch can do real harm is a
   declaration string reaching a release log.
6. **Fix** — apply what survived, rebuild.
7. **Finalize** — tick only what `git diff` proves landed; final Debug and Release rebuild.

---

## 9. Done means

- `Artifact probe:` carries an outcome tally that is readable from a **Release** build, counts only.
- The probe's silent root fallback logs, like the file tool's does.
- A step-outcome line says whether an artifact was reported, without saying what it was.
- `scripts/Measure-ArtifactDeclarations.ps1` exists, refuses repo output paths, prints counts, and its
  `.DESCRIPTION` states both caveats (replanned-away rows are gone; `NOT FOUND` is not recoverable).
- A classifier-replay test exists and states in its own file what it does *not* measure.
- The runbook no longer claims `AssistantView` is unaddressable, no longer hands the reader a shell
  pipeline that fragments, documents the scheduled-`AgentTask` route, and says that probe lines within
  one run overlap.
- The plan doc's `ArtifactRef is not persisted` line is gone, and A4's row names sites that exist.
- Both configurations rebuild clean; checklist ticked against `git diff`; nothing committed unasked.
- **A report naming what still needs a human on Windows** — `dotnet test` above all, then G2's script
  and G5's routines.
- **A1 is still open.** Nothing in this batch is allowed to tick it. If a planning agent proposes
  closing it, that is a bug in the plan.

---

## 10. Swaps, if this batch is too big or too small

**Drop first:** **G3**. It is the least coupled and the only item whose value is entirely downstream —
it pins a classifier for an A4 that may never happen. Then **G6**, which is insurance for an A2 that
is still gated.

**Never drop:** **G1**, **G2** and **G4**. Those three are the batch. G1 makes the gate readable, G2
answers half of it offline, G4 stops the next Windows session being wasted.

**Add, if there is room:** review **#7** (global pause — a tray toggle plus a flag checked by the
scheduler tick and the headless launcher, never kills in-flight work, `S`). It touches
`ScheduledJobBackgroundService`'s tick and `HeadlessRunLauncher`, the same two seams G5's measurement
route runs through, so the reader is already in that code — and an unattended sample-generating
routine left running on a Windows box is precisely the situation where wanting a pause button becomes
concrete.

**The bigger prize, deliberately not here:** review **#2** (error layer + recovery actions, `M`) with
**#3** (Send Diagnostics, `S–M`). The checklist is right that they go together, and §3's note on #3
stands: take them on their own merits, not on A1's account.
