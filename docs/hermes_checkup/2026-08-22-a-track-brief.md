# Brief — The A Track: Make the Verifier's Evidence Worth Having

**Status:** ready to execute, and small on purpose. Self-contained: paste §0 into a fresh session and it
has everything.
**Owner:** unassigned. Two rows need a Windows desktop session; the rest do not.
**Written:** 2026-08-22.
**Origin:** group A of [`2026-08-22-hermes-followup-checklist.md`](2026-08-22-hermes-followup-checklist.md),
its plan [`2026-08-22-artifact-evidence-plan.md`](2026-08-22-artifact-evidence-plan.md), its gate runbook
[`2026-08-22-a1-log-collection-runbook.md`](2026-08-22-a1-log-collection-runbook.md), and recommendation
#13 of [`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md).

**Read this before anything else.** Five of group A's seven rows — A2, A3, A4, A6, A7 — plus review
recommendation #13 sit behind one measurement, gate **A1**, and A1 has not moved since its first reading.
This brief does not unblock them and does not pretend to. What it scopes is the **instrument**: A1 as
specified cannot be read off a Release build at all, and the number the plan tells you to read
(`probed / declared`) is not the number the gate needs. Eight rows, every one `XS` or `S`, that make the
gate answerable — plus one real defect in the probe that produces false facts. If you came here to build
A2, stop and read §3.

---

## 0. The prompt

> Read `docs/hermes_checkup/2026-08-22-a-track-brief.md` and implement the batch it scopes: rows
> **A1a, A1b, A1c, A1d, A1e, A1f, A8, A9** — the instrument for gate A1, the probe's one silent failure,
> and the corrections the A docs need. One workflow, our schema: plan → implement → build gate →
> simplify → review → fix → finalize. Review is five dimensions (correctness · CLAUDE.md conformance ·
> tests · integration and architecture · scope and dead code), and every finding is killed or confirmed
> by two independent refuters with different lenses before anyone acts on it.
>
> **The hold: A2, A3, A4, A6, A7 and review recommendation #13 stay closed.** They are behind gate A1,
> which has not moved. Do not route `ArtifactRef` through the probe, do not touch the planner or replan
> prompt, do not extract `IArtifactProbe`. This batch changes **zero prompt bytes** — a prompt edit
> invalidates the sample the batch exists to collect.
>
> Three decisions the planning phase must answer in writing rather than settle silently in code:
> **(1)** does A1b's offline replay call the app's own classifier — making `FileCandidates` /
> `LooksLikeFileName` `internal` — or reimplement the rule in the script, and if the latter, what stops
> the two drifting; **(2)** does A8's new log line carry the subpath (`SensitiveDebug`, erased from
> Release IL) or only the fact that the fallback happened (`LogInformation`, release-visible), or both;
> **(3)** exactly which counters A1a adds at `AgentVerifier.cs:303` and where they are computed, given
> that the probe is deliberately `static` and logger-free.
>
> Everything else this brief says.

---

## 1. Where the repo is

Branch `feature/speaker-attribution`. Two batches have landed since the A plan was written.

| Commit | What |
|---|---|
| `ffa43b23` | Batch 2 — a pinned persona reaches the work, not just the plan (E-group) |
| `66dd3131` | Batch 2 — merge |
| `d27f5dc1` | Batch 2 — C4's seven remaining blueprints; E1–E6, E8 (per-routine persona + effort) |
| `89f7c3b4` | Merge of `feature/fill_uiautomation_gaps` — D7, eight commits of AutomationId fills |
| `b7aa30bb` `e6b145df` `8b912124` `0e825f43` | Batch 1 — D1, C1–C3, B1/B2/B5, **A5** |

**Batch 2 touched none of the A-track surface.** `AgentVerifier.cs`, `StepOutcomeSignal.cs`,
`StepExtraJson.cs`, `RunContext.cs`, `AgentPlanner.cs`, `AgentRunOrchestrator.cs` and `AgentRunService.cs`
are unchanged by it.

**A5 shipped** (`0e825f43`). `ArtifactRef` is persisted into `AgentSteps.ExtraJson` via
`src/Pia.Wpf/Services/StepExtraJson.cs:15` — written at `AgentRunOrchestrator.cs:1637` →
`AgentRunService.cs:1246`, read back at `AgentRunOrchestrator.cs:934`, where a Done step whose artifact
was persisted is seeded with `new StepOutcomeClaim(true, string.Empty, artifact)`. Both arms are pinned
(`AgentRunOrchestratorTests.cs:1012`, `:1042`). **Every "`ArtifactRef` is not persisted" sentence in the
plan doc is now false** — see A9.

**Nothing in either batch has been executed as a test.** `git diff f1a56d52..HEAD -- tests/` adds 116
`[Fact]`/`[Theory]` attributes, none of which has ever run: `net10.0-windows` cannot execute on macOS.
Checklist row **E7** is the same debt from the other side. A Windows `dotnet test` run outranks anything
written here.

**D7 removed the A1 runbook's stated blocker.** `src/Pia.Wpf/Views/AssistantView.xaml` now carries
`Assistant_Mode_Chat` (`:525`), `Assistant_Mode_Agent` (`:531`), `Assistant_RunInBackground` (`:557`),
`Assistant_Send` (`:570`), `InputTextBox` (`:305`) and `MessageScrollViewer` (`:111`), all registered in
`docs/ui_automation/ui-automation-playbook.md:26-27`. The collection loop is language-independent and
heal-able now. The runbook still says the opposite.

---

## 2. The two channels — get this right or everything below is wrong

Pia has two artifact channels and they are not the same thing.

| | `AgentStep.ExpectedArtifact` | `StepOutcomeClaim.ArtifactRef` |
|---|---|---|
| Who declares it | the **planner**, from `emit_plan` | the **executing step**, from `emit_step_result` |
| When | *before* the step runs | *after* it ran |
| Persisted | `AgentSteps.ExpectedArtifact TEXT NULL` (`SqliteContext.cs:497`) | `AgentSteps.ExtraJson` since A5 (`SqliteContext.cs:505`) |
| Probed | **Yes** — filesystem, metadata only | **No** |
| Reaches the critic as | a fact block on the **System** message (`AgentVerifier.cs:137`) | unprobed prose `produced: <ref>` on the **User** message (`AgentVerifier.cs:204-205`) |
| Capped | `Trim()` at `AgentPlanner.cs:667`; 200 chars at probe time (`AgentVerifier.cs:243`) | 300 chars at parse (`StepOutcomeSignal.cs:27`, `:58`) |

The probe's input is `ctx.CompletedSteps` filtered to a non-blank `ExpectedArtifact`
(`AgentVerifier.cs:268`) — i.e. **the prediction is checked and the report is not**. That asymmetry is the
whole of A2, and A2 is blocked.

Two things about this the plan gets wrong, and they matter for sizing later:

- **The illustrated `produced: out/q3-summary.md → found (2.1 KB)` line cannot come from "pointing the
  existing machinery at a second source."** The facts block is appended to the **System** prompt
  (`AgentVerifier.cs:137`, header at `:233`); `produced:` is emitted by `BuildExecutedSteps` into the
  **User** message (`:204-205`, composed at `:143`). That split is deliberate: `TokenizingAiClientService`
  rewrites `ChatRole.User` only (`TokenizingAiClientService.cs:275`), which is why model-authored step
  text lives there. A2 must either thread the probe result down into `BuildExecutedSteps` (static and
  ctx-only today, and it runs up to twice per verify while the probe deliberately runs once) or move a
  probed fact onto the User message. Neither is free.
- **`ArtifactRef`'s origin is as unconstrained as `ExpectedArtifact`'s.** Its description
  (`StepOutcomeSignal.cs:164`) reads *"a file path, **or a short identifier**"* — the same clause that
  licenses the unprobeable prose A4 exists to remove from the other channel. A2 does not automatically
  buy a negative signal.

Both belong in A9's corrections, not in this batch's code.

---

## 3. The gate

**A1 asks:** is the planner's `ExpectedArtifact` already file-shaped often enough that probing a second
channel buys nothing? If yes, A2–A7 are dropped.

**Its one reading, 2026-08-22:** 23 declarations over 7 verifier runs, from 3 log files on one machine
over three days, all code-shaped tasks (`Program.cs`, `Calculator.cs`, `PrioritizedActionPlan.md`) —
**13 `found` (57%) · 10 `not a file reference` (43%) · 0 `NOT FOUND`**. That refutes "already high" and is
far too small to tune on. The H1 verifier shipped 2026-07-28, which is why the corpus is thin.

### Why the gate is unreadable today

- **`AgentVerifier.cs:303` carries no outcome.** It logs `{Declared}` and `{Probed}` only, and `Probed`
  counts **candidate paths** — incremented inside the per-candidate loop (`:397`), capped at 3 per
  declaration (`:240`) and 12 overall (`:238`). `probed / declared` therefore ranges over [0,3] for
  reasons unrelated to found-ness. The only inference a Release build supports is `probed == 0` versus
  `probed > 0`.
- **The found/`NOT FOUND` split exists only in the `SensitiveDebug` facts line at `:304`**, which
  `SafeLog.cs:19`'s `[Conditional("DEBUG")]` erases from Release IL. No log level brings it back;
  `Bootstrapper.cs:351` raises the level only under `IsDevMode`, itself `#if DEBUG`. **A1 is structurally
  a Debug-only measurement**, so widening the sample by shipping to release users is impossible until a
  release-safe counter exists. That is row A1a.
- **There are eight outcome arms, not three.** `not a file reference` (`:381`), `not probed (probe budget
  reached)` in two forms (`:385`, `:394`), `found (size, modified …)` (`:436`), `found, but it is a folder,
  not a file` (`:438`), `NOT FOUND` (`:439`), `not a resolvable path inside the assistant files folder`
  (`:430`), `not probed (could not be inspected)` (`:443`), plus a tally line (`:416`). The bare form is
  printed **only** when a declaration has exactly one candidate and that candidate is byte-identical to
  the flattened, truncated declaration (`:405-409`); otherwise the line reads `token: NOT FOUND`, and a
  multi-candidate declaration joins its parts with `"; "` (`:411`). The runbook's `sed`/`uniq` harvest
  silently fragments on exactly the prose-heavy mix its own §5 demands. That is row A1d.
- **The population is selected on a clean drain.** `AgentRunOrchestrator.cs:516-517` breaks *before*
  verify on a cancel or an unrecovered step failure, so a run whose step failed and could not be
  replanned emits no probe line at all. `NOT FOUND = 0/23` is measured on a population conditioned on
  eventual success. Say this out loud or the number reads stronger than it is.
- **One root-resolution failure is completely silent, and it is the one that manufactures false
  negatives.** The ladder is `ctx.WorkspaceRoot ?? TaskAmbient.Current?.WorkspaceRoot ??
  settings.AssistantFilesFolder` (`AgentVerifier.cs:277-278`). Blank logs `Artifact probe skipped`
  (`:280`); non-existent logs it too (`:299`); a fault logs `Artifact probe failed` (`:319`). But a
  `WorkingSubpath` that escapes containment or does not exist falls back to the base root at `:348-353`
  with **no log line at all**, and every artifact the run wrote under the subpath then reports
  `NOT FOUND`. The code's own comment calls that *"a confident false fact, which is worse than no fact."*
  The runbook's "zero `Artifact probe skipped` lines means the profile was fine" check does not cover it.
  That is row A8.

### What is blocked and what is not

**Blocked, do not open:** **A2** (route `ArtifactRef` through the probe), **A3** (its tests), **A4**
(planner prompt wording), **A6** (`IArtifactProbe`), **A7** (todo/reminder/vault probes + typed prefix),
and review **#13** (reject a plan step whose `ExpectedArtifact` is unprobeable prose).

Two of those deserve a specific warning:

- **A6 is the most temptingly mis-sorted row in the group.** It is rated `Enabler`, and the `High` it
  unblocks is A7 — which the gate can cancel outright: the runbook's own reading table says a high
  `found` share drops "A2–**A7**". A6 is inside the drop set. See §5.
- **#13 is unbuildable as written.** There is no per-step rejection seam: `ValidatePlan`
  (`AgentPlanner.cs:637-651`) is all-or-nothing, and a `false` return degrades the **entire** plan to the
  SingleTurn fallback (`:218-222`). The only per-step drop precedent (`:653-660`) drops a persona
  assignment, never a step. Rejecting a prose artifact today means throwing away the plan.

**Not blocked:** everything in §4. Six rows make the gate answerable; two are worth doing whichever way
it reads.

### Why the gate still needs a human on Windows

A Debug build, Agent mode, **Run in background** (`AgentRunOrchestrator.cs:250` parks any 3+ step plan for
approval on the live executor only — `LiveTurnExecutor.cs:91`), auto-approve for built-in writes
(`AppSettings.cs:232`, default false), and a run allowed to finish. That is the runbook's loop and it is
still correct. What the runbook misses: **a scheduled `AgentTask` routine reaches the same verify with
none of gates 2–4** — `ScheduledJobBackgroundService.cs:475` dispatches it through
`HeadlessRunLauncher.cs:488` into the same `orchestrator.RunAsync`. That is row A1e, and it is how the
sample gets collected without 24 babysat prompts. Caveat, verified: **all eight C4 blueprints ship
`Kind: ScheduledJobKind.Research`** (`RoutineBlueprint.cs:38, 58, 79, 98, 119, 144, 167, 188`), and the
Research leg goes to `ExecuteResearchAsync`, never to the orchestrator. Blueprint cards produce **zero**
samples; the routines have to be created as `AgentTask` by hand.

---

## 4. Scope — eight rows

None of these is on the checklist yet. The finalize step adds them: A1a–A1f under gate A1 as its
instrument, A8 and A9 as standalone group-A rows.

### A1a — Put the outcome tally on the release-visible line *(Deps: none · XS · High)*

`ProbeDeclarations` already returns `(string Facts, int Probed)` (`AgentVerifier.cs:363`). Widen it to
carry the per-arm counts and log them at `:303`, which is already `LogInformation`. **Counts only, never
a name** — that is what makes it release-safe by construction, and it is why this does not violate the
"artifact names are user content" rule the facts line is at `SensitiveDebug` for.

- Files: `src/Pia.Wpf/Services/AgentVerifier.cs`, `tests/Pia.Wpf.Tests/Services/AgentVerifierTests.cs`.
- The probe is `static` and logger-free **on purpose** (`:359-361`) so artifact names cannot be logged
  even by accident. Do not hand it a logger. Return the counts; log at the call site.
- Zero prompt bytes change. The facts block's text must be byte-identical afterwards, which is what keeps
  the ~15 `declared:` assertions in `AgentVerifierTests.cs` and `AgentVerifierWorkspaceRootTests.cs`
  green.
- After this, a Release build can answer the gate's central row. Today it cannot.

### A1b — Recover the file-shapedness half of the gate offline *(Deps: A1a's classifier decision · S · High)*

`ExpectedArtifact` is a persisted column (`SqliteContext.cs:497`) and nothing in `src/` deletes an
`AgentRuns` row, so **every declaration ever made on a machine is still in the database** — not just the
ones inside the three-day window where a probe line happens to exist. Replaying those strings through the
classifier gives the exact `found`-vs-`not a file reference` split that produced the 57/43, on a sample
potentially an order of magnitude larger, with no new runs and no Debug build.

- Files: a new `scripts/Export-ArtifactDeclarations.ps1` (or equivalent), modelled on
  `scripts/Export-CompactionCorpus.ps1` — read-only DB open, hard refusal of any output path inside the
  repo, counts printed, **content never printed and never written**. A declaration is user content.
- Two honest caveats to state in the script's own header: a replan deletes non-Done step rows
  (`KeepDoneAsync`, `AgentRunOrchestrator.cs:874`), so declarations that were replanned away are gone;
  and found-vs-`NOT FOUND` is **not** recoverable this way, because the filesystem has moved on and
  per-run workspaces are torn down.
- The open decision is decision (1) in §0. Calling the app's classifier means marking `FileCandidates`
  (`:454`) and `LooksLikeFileName` (`:471`) `internal` — `InternalsVisibleTo Include="Pia.Wpf.Tests"` is
  already present (`src/Pia.Wpf/Pia.Wpf.csproj:69`) and this very file already uses internal-for-test
  twice (`:224`, `:233`). Reimplementing the rule in PowerShell measures a *different* classifier.

### A1c — Pin the classifier's arms with a replay test *(Deps: none · S · Med)*

The harness exists. `CtxDeclaring(params string?[])` (`AgentVerifierTests.cs:45`) builds a `RunContext`
whose completed steps declare arbitrary strings; `ReturnsVerdict` captures the System message through an
NSubstitute `IAiClientService`; `LastPrompt` (`:60`) reads it; tests at `:240-246` already count arms
(`Assert.Equal(12, CountOccurrences(LastPrompt, "→ NOT FOUND"))`). Feeding a corpus of declaration shapes
through it needs no Debug/Release reasoning, no WinWright and no desktop — only a Windows or CI
`dotnet test`.

**What it cannot do:** it does not measure what real planners declare (it replays whatever corpus you
hand it, so it measures `LooksLikeFileName`, not the population), it says nothing about found-vs-`NOT
FOUND` against a real filesystem, and nothing at all about `ArtifactRef`. **It cannot close A1.** What it
can do is pin the composite and prefixed-line cases the harvest currently mangles, and make a later A4
prompt change measurable.

### A1d — Correct the collection runbook *(Deps: none · XS · High)*

The runbook is the thing standing between the gate and an answer, and it is wrong in the two sections
that matter operationally.

- §4.1 — the AutomationId table and the "zero AutomationIds, English-only, `winwright heal` cannot repair
  this" note. D7 landed; use `Assistant_Mode_Agent`, `Assistant_RunInBackground`, `Assistant_Send`.
- §6 — the harvest. Classify on the **arm substring per candidate**, not on the whole line: there are
  eight arms, the bare form is conditional (`:405-409`), multi-candidate declarations join with `"; "`
  (`:411`), and `s/found \([^)]*\)/found/` leaves `found, but it is a folder, not a file` standing as a
  fourth bucket. The existing 13/10/0 read is interpretable only because the corpus was code-shaped.
- §2 — add the scheduled-`AgentTask` path (A1e) as a fifth route, and add the clean-drain selection
  caveat to §1's reading.
- §2.5 — the "zero `Artifact probe skipped` lines means the profile was fine" check does not cover the
  silent subpath fallback (A8).

### A1e — Document and stand up unattended collection *(Deps: A1d · S · High)*

A scheduled `AgentTask` routine plans, drains and verifies with no Chat/Agent lever, no plan-approval
park and no per-run babysitting: `ScheduledJobBackgroundService.cs:475` → `HeadlessRunLauncher.cs:488` →
`orchestrator.RunAsync`. One Debug build left running on a Windows box produces probe lines on a schedule.
This row is: a runbook section describing it, plus two or three routines created by hand as `AgentTask`
with a task mix drawn from the runbook's §5 categories (not four "write me a file" prompts — that
falsely closes the gate).

- It still needs a Windows machine, so it does not make the gate human-free. It removes the 24-prompt
  click loop, which is the expensive part.
- State the blueprint caveat from §3: the eight cards are all `Research` and produce nothing.

### A1f — Count the report channel's supply, without probing it *(Deps: none · XS · Med)*

Nothing today logs whether a step reported an artifact at all. `HeadlessTurnExecutor.cs:619-621` logs
offered/confirmed/succeeded and a declaration count, not artifact presence; the one release-visible
counter A5 accidentally shipped is `AgentRunOrchestrator.cs:940-944`, which fires **only on a resume**.
So the question "would A2 even have anything to probe?" is unanswerable, and if A2 lands blind it
inherits A1's exact blindness.

Add one release-safe count at verify time: how many completed steps carry a non-blank `ArtifactRef`,
alongside `{Declared}`. No probe, no prompt change, no name logged. This is the supply number that sizes
A2 when the gate opens — and it is the one row here that costs almost nothing and pays into the blocked
half.

### A8 — Log the probe's silent root fallback *(Deps: none · XS · Med)*

`AgentVerifier.cs:348-353` falls back to the base root with no log line when a `WorkingSubpath` escapes
containment or does not exist, and every artifact written under that subpath then reports `NOT FOUND`.
`FilesToolHandler.cs:202` — the code this block explicitly mirrors — logs exactly that fallback via
`SensitiveDebug`. Match it, and decide decision (2): a subpath is user content, so a `SensitiveDebug`
line carrying it is erased from Release, which is precisely the blindness A1a exists to fix. A content-free
`LogInformation` ("the probe's working subpath did not resolve; probing the base root instead") plus a
`SensitiveDebug` carrying the subpath gives both. Say which you chose.

Worth doing whichever way A1 reads: this is the one case where a `NOT FOUND` in the corpus is an
instrument error, and there is currently no way to detect it from the logs.

### A9 — Correct the A docs *(Deps: none · XS · High)*

The plan doc is the document a cold reader executes from, and a third of its load-bearing citations are
now stale or wrong. Fix in place; keep the plan's shape.

| What the plan says | What the source says |
|---|---|
| `ExpectedArtifact` at `SqliteContext.cs:493` | `:497` |
| `ArtifactRef` "Persisted: **No** — in-process for one step exchange" (§1 table, §2, move 4) | Persisted since A5: `AgentRunOrchestrator.cs:1637` → `AgentRunService.cs:1246` → `StepExtraJson.cs:15` |
| §2's code quote, `// ← no Outcome` | `AgentRunOrchestrator.cs:930-936` seeds `new StepOutcomeClaim(true, string.Empty, artifact)` when an artifact was persisted |
| Move 1: "read `probed / declared` off the existing line; a high ratio closes the thread" | `:303` carries no outcome and `Probed` counts candidate paths — see §3 |
| "Three buckets: `found` / `NOT FOUND` / `not a file reference`" | Eight arms plus a tally line — see §3 |
| Move 3: `AgentPlanner.cs:782` "and the replan twin at `:827`" | `:782` is the only prose instruction about `expectedArtifact` anywhere; `:827` is the verbatim twin of `:784` ("Group by logical change…"), and `BuildReplanMessages` (`:817-830`) never mentions the field. The real second surface is the **tool schema** — `:139`, `:148` and `:159` — which `AIFunctionFactory` ships on every plan *and* every replan turn |
| Move 2: "the plumbing for it is already end-to-end" | The two channels render into different chat messages — see §2 |
| §5: persisting `ArtifactRef` "lets the run timeline show what each step produced" | Nothing reads it but the resume seed. `StepRowViewModel.cs:20-23` carries `ExpectedArtifact` expressly "round-tripped, not displayed", has no `ArtifactRef` member, and no XAML binds either |
| §5 sensitivity: `HeadlessTurnExecutor.cs:619` | `:625-626`; `:619` is a `LogInformation` counter line |

Three facts to add while the file is open, because they change how the blocked rows will be sized:

- **A1 as specified does not subdivide the 43%.** `not a file reference` is one bucket, so even a widened
  A1 cannot tell a `todo:`-shaped declaration from genuine prose, and therefore cannot size A7.
- **A2 covers zero delegated steps.** `FanOutStepResult` (`AgentRunOrchestrator.cs:1368-1370`) constructs
  a sibling's result with no `Outcome` at all, while the sibling's `ExpectedArtifact` *is* still probed. On
  any parallel plan the planner channel stays the only channel. Same for the SingleTurn fallback, which
  owns no step row.
- **A5 shifted what `[ok, declared]` means.** `OutcomeTag` (`AgentVerifier.cs:224-230`) now renders a
  resumed step `[ok, declared]` — the prompt defines that as "the step called `emit_step_result` and this
  is its own structured verdict" (`:190-193`) — off a success declaration nobody stored; only the artifact
  was. Nothing is wrong (only Done rows are seeded), but the critic's confidence tag for a resumed step is
  now a function of whether the step happened to name an artifact.

---

## 5. The ordering decision

**Inside this batch, on macOS, in order:** A1d + A9 (so the Windows list is correct when someone picks it
up) → A1a → A8 + A1f → A1c → A1b.

**At the Windows machine, strict value per minute:**

1. `dotnet test`, no filter. 116 test methods from batches 1 and 2 have never executed, and E7 is open.
   This outranks every A-track item on this page.
2. Run A1b's script. Minutes, no app launch, and it moves the ratio off *n = 23*.
3. Create two or three `AgentTask` routines per A1e and walk away.
4. Only then the runbook's §4.2 click loop.

**Nothing on that list closes the gate.** Items 2 and 3 are what make it closable.

### When the gate does open: A2 → A3 → (A4) → A7, with A6 as A7's first commit

**Do not change the checklist's `A6 · Deps: A2` line.** It is recorded correctly. A6-first loses in every
branch: if A1 closes, A6 is inside the drop set the runbook itself names ("drop A2–**A7**"); if A1 stays
open and A7 proceeds, A6's signature is fixed by A7, which is downstream; if A1 stays open and A7 is
dropped, A6 is an interface with one implementation and one caller forever.

The decisive technical reason is a shape conflict only A7 can resolve. `Probe` is
`private static string Probe(string root, string candidate)` (`AgentVerifier.cs:427`), synchronous by
construction inside a `Task.Run` with a 2 s `WaitAsync` box (`:290-297`). Every kind A7 names is async —
`ITodoService.GetAsync`/`GetAllAsync` are `Task`-returning (`ITodoService.cs:10-12`), as is all of
`IReminderService`. So A6's own acceptance criterion, *"behaviour-preserving refactor of today's probe"*,
and A7's requirements are in direct conflict: extracted sync it is the wrong shape, extracted async it is
guessing A7 without A7.

Two changes that row does need, for whoever picks it up:

1. **Strike "behaviour-preserving refactor of today's probe."** Replace with: *async, per-declaration
   seam; budget passed in, not owned; no new constructor dependency on `AgentVerifier`; zero prompt-byte
   change.*
2. **Mark it "land with A7 — not viable standalone"**, and record it in the drop set alongside A2–A7 if
   the gate closes, which the plan's own work-breakdown table implies and the checklist row does not say.

Also note for A7's planner, not for now: the typed-prefix split cannot live inside the existing
classifier. `LooksLikeFileName` decides file-ness purely from extension shape (`:470-482`), so
`todo:Call the vendor` already falls out as "not a file reference" — correct by accident. The prefix
dispatch has to run *before* `FileCandidates`.

And one decision A2 must make that the plan never mentions: `MaxProbedPaths = 12` and
`MaxReportedDeclarations = 20` (`:238-239`) are consumed inside one per-declaration loop in step-ordinal
order. A second channel can present two declarations per step — 40 on a 20-step plan. **Whether the
budgets are shared or split per channel decides whether the new, stronger evidence gets starved behind
the old prose**, and unless the split is recorded, the pre-A2 and post-A2 A1 numbers are not comparable.

---

## 6. Constraints

Read `CLAUDE.md` in full first. The ones that bite hardest here:

- **Privacy logging, and it is the reason this whole batch exists.** A declared artifact path is user
  content, and so is a rendered probe fact line — which is exactly why the per-declaration facts are at
  `SensitiveDebug` (`AgentVerifier.cs:304`) and therefore why the gate needs a Debug build.
  `SensitiveDebug` is `[Conditional("DEBUG")]` (`SafeLog.cs:19`): the call **and its argument
  evaluation** are erased from Release IL. A1a and A1f are release-safe **only** because they log
  integers. Never add a name, a path or a declaration to a `LogInformation`. Do not hand the probe a
  logger — `:359-361` says why in one line.
- **Comment discipline.** Default to no comment. One short line when the WHY is genuinely non-obvious;
  never a `<para>`, never a restatement. **Never cite a task, batch, gate, spec, plan or ticket ID in
  source or XAML** — no "A1a", no "gate A1", no "§3". That belongs in the commit message. Existing files
  in this area violate it (`AgentVerifier.cs` carries "H1", `AgentRunOrchestrator.cs` carries "R2"/"D15");
  do not imitate them, and do not go on a cleanup spree in this batch either.
- **Documentation layout.** Docs live in `docs/<topic>/`, file name `YYYY-MM-DD-<slug>.md`, dated when
  written — the date does **not** change when a doc is revised, so A9 and A1d edit
  `2026-08-22-artifact-evidence-plan.md` and `2026-08-22-a1-log-collection-runbook.md` in place and keep
  their names. Links between docs in the same folder stay relative.
- **Data paths.** Anything touching a profile path goes through `Pia.Paths.PiaPaths`, never
  `Environment.GetFolderPath`. A1b's script reads the DB path the same way the compaction export does.
- **Architecture.** `tests/Pia.Wpf.Tests/Architecture/` holds NetArchTest layering and naming rules; a
  new type in the wrong layer compiles and fails there. `AllServiceInterfaces_MustHaveRegisteredImplementation`
  (`DiRegistrationTests.cs:25`) is a permanent obligation attached to any new service interface — another
  reason not to sneak A6 in.

### Build

`Directory.Build.props` sets `TreatWarningsAsErrors=true`, so a warning is a build failure. MSBuild output
on the dev Mac is German-localized (`Warnung(en)` / `Fehler`).

```bash
dotnet build -t:Rebuild -p:EnableWindowsTargeting=true -v:m 2>&1 | tail -40
dotnet build -t:Rebuild -c Release -p:EnableWindowsTargeting=true -v:m 2>&1 | tail -40
```

The bar is `0 Warnung(en)` / `0 Fehler` in **both**. Never run two builds concurrently. **`dotnet test`
never runs on macOS** — `net10.0-windows` cannot execute there; compiling the test project is the only
check available, and A1c's value is realized on Windows or in CI, not here.

Do not commit unless asked. One commit per group, with the checklist ticks riding in the commit that
earns them.

---

## 7. Workflow shape

1. **Plan** — one agent per row, grounded by reading the source rather than this brief. It must answer
   §0's three decisions in writing.
2. **Implement** — parallel over **disjoint file ownership**. A1a, A1f and A8 all touch
   `AgentVerifier.cs`: that is **one** agent, not three. A1b (script), A1c (tests), A1d + A9 (docs) are
   three more. Give every agent an explicit file list and a handoff channel for anything outside it.
3. **Build gate** — serialized, single agent, Debug then Release, driven to zero.
4. **Simplify** — quality only. Comment discipline is the most-violated rule in this area; assume there
   are violations, including ones you just wrote.
5. **Review** — five dimensions (correctness · CLAUDE.md conformance · tests · integration and
   architecture · scope and dead code), each finding killed or confirmed by **two independent refuters**
   with different lenses. Point them at two things specifically: any new log line that could carry user
   content into Release, and any change that moved a prompt byte.
6. **Fix** — apply what survived, rebuild both configurations.
7. **Finalize** — add rows A1a–A1f, A8 and A9 to the checklist and tick what actually landed, verified
   against `git diff` rather than trusted. Final Debug and Release rebuild.

---

## 8. Done means

- `AgentVerifier.cs:303`'s line carries per-arm outcome counts and a report-channel supply count, both
  release-visible, both name-free (A1a, A1f).
- The silent subpath fallback logs (A8), with the `SensitiveDebug`-versus-`LogInformation` choice recorded.
- A script that replays every persisted `ExpectedArtifact` through the classifier and prints counts only,
  never content, with its two caveats in its own header (A1b).
- A test that pins the classifier's arms, including a composite declaration and a prefixed candidate line
  (A1c).
- The runbook's §4.1 selectors, §6 harvest and §2 route list are correct, and the plan doc no longer says
  `ArtifactRef` is unpersisted (A1d, A9).
- **Zero prompt bytes changed.** `git diff` on `AgentVerifier.cs` and `AgentPlanner.cs` shows no change to
  any string that reaches a model.
- Both configurations rebuild clean; checklist rows added and ticked; nothing committed unasked.
- A handoff naming what needs Windows: `dotnet test` with no filter first, then A1b's script, then A1e's
  routines.

---

## 9. Swaps, if this batch is too big or too small

**Drop first:** A1c. It is the only row whose value is realized on a machine nobody is sitting at, and it
pins a classifier that A7 will eventually change anyway. **Drop second:** A1e's routine-creation half —
keep the runbook section, defer the hand-created routines to whoever is at the Windows box.

**Never drop:** A1a and A9. A1a is what makes the gate readable at all; A9 is what stops the next session
executing a plan whose channel table is false.

**Add, if this is too thin, in this order:**

- **Widen the `not a file reference` bucket into two counters** — declarations that contain a `kind:`-like
  prefix versus genuine prose. `XS` on top of A1a, and it is the only cheap way to size A7 before A7.
  A1 as specified cannot do it.
- **Review #7, global pause** (`S`, deps satisfied, no plan doc): tray toggle plus a flag checked by the
  scheduler tick and the headless launcher, never kills in-flight work. It composes with A1e — an
  unattended collection session you cannot stop is a liability.
- **E9** (`S`, deps satisfied, planned): persist the resolved run persona and effort on the `AgentRuns`
  row. It is the resume gap batch 2 left open, and it touches the same orchestrator seam A5 did.

**Do not add:** A6, for the three reasons in §5. Nor review #13, which has no seam (§3). Nor review #3
(Send Diagnostics) *on A1's account* — A1a and A1b largely retire the "we had to hand-copy logs off a
Windows box" argument for it. #3 is still worth building; take it with #2 on its own merits, as the
checklist's "not yet planned" list already says.
