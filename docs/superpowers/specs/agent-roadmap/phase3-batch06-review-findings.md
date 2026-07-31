# Phase 3 — the Batch 06 review's findings, and which of them have a verdict

**Recovered, not re-derived.** These are the verbatim findings of the review phase that ran over Batch 06
(G1–G5) on 2026-07-31, read back out of that workflow run's own journal
(`wf_180b774e-ef7/journal.jsonl`). The run filed **14 findings** from three opus lenses and then handed each to
an adversarial skeptic — but **11 of those verifiers died on a usage limit and 2 were never sent** (the verify
fan-out was capped at 12). So exactly **one** finding ever received a verdict.

The reason this file exists: a verifier that dies returns nothing, and nothing is not a refutation. Without the
findings written down, "13 findings whose verdict is UNKNOWN" decays into "13 findings that were dismissed"
the moment the run's journal is forgotten. They are recorded here so they can be adjudicated instead.

## Status of the 14

Ten of the fourteen were **re-verified independently by the Phase 3 review pass of 2026-07-31** (a second
review whose scope was G1–G10, with these findings handed to it as inherited items) and then **fixed** by that
pass. The `Verdict` column below records the outcome.

**Correction, 2026-07-31, read off that pass's own verdict record.** An earlier revision of this paragraph and of
three rows below said Lens A 4, Lens C 5 and Lens C 6 had **NO VERDICT** and were "outside the in-scope group
list". That is wrong on both counts. All thirteen unadjudicated findings were sent to a skeptic and **all thirteen
returned a verdict**: ten CONFIRMED, three REFUTED — and the three refutations are Lens A 4, C 5 and C 6. The
mistake has a mechanism worth knowing, because it will recur otherwise: the fix pass only ever receives the
CONFIRMED list, so a finding it never sees is indistinguishable, from inside that pass, from one nobody checked.
It inferred "no verdict" from its own silence. C 6's verifier states explicitly *"this is not a scope-based
refutation"*; A 4's ran two throwaway experiments (a real git worktree, and a locked-file recursive delete) to
establish that the harm cannot follow from the premise. Refuted-with-evidence and never-checked are different
outcomes and this file must not blur them.

| Filed by | Finding | Severity as filed | Verdict |
|---|---|---|---|
| Lens A | 1 — worktree teardown destroys the run's output | must-fix | **CONFIRMED** by the 2026-07-31 pass — **FIXED** in `3b66603` |
| Lens A | 2 — the "output is on branch X" line cannot render on success | should-fix | **CONFIRMED** (same defect as Lens B 1) — **FIXED** in `3b66603` |
| Lens A | 3 — `StepPersonaResolver`'s persona lookup is unguarded | should-fix | **CONFIRMED** — **FIXED** (persona taken from the roster list already in hand) |
| Lens A | 4 — metadata deleted even when directory removal failed | should-fix | **REFUTED**, high confidence — premise true, inferred harm false: `TearDownWithoutMetadataAsync` already runs `git worktree prune` unconditionally when `remove --force` fails, and prune reclaims a registration from live git state once the worktree's `.git` pointer file is gone. Two experiments in the verdict. `3b66603` narrowed the worktree half further (see the note under the table) |
| Lens A | 5 — automatic promotion with conflicts tears the workspace down | should-fix | **CONFIRMED** — **FIXED IN PART** in `3b66603` (the workspace is retained; the count still reaches the user only through the publish note — reasons in that commit) |
| Lens B | 1 — the branch line cannot render for a successful worktree run | must-fix | **CONFIRMED** — **FIXED** in `3b66603` (torn-down worktree metadata stub) |
| Lens B | 2 — nothing commits the work, so worktree mode deletes the deliverable | must-fix | **CONFIRMED** — **FIXED** in `3b66603` (app-side commit onto the run branch) |
| Lens B | 3 — an automatic promotion's conflicts are logged but never shown | should-fix | **CONFIRMED** — **FIXED IN PART** in `3b66603`, as Lens A 5 |
| Lens C | 1 — French `Run_Output_Branch` says "branch", not "branche" | should-fix | **CONFIRMED** (refuted: false, high confidence) — **FIXED** in `914730d` |
| Lens C | 2 — git-parity test compares a raw `GetTempPath` expectation | nit | **CONFIRMED** — **FIXED** (`_runRoot` canonicalized in the fixture ctor) |
| Lens C | 3 — G1's guard facts carry no REGRESSION/GUARD label | nit | **CONFIRMED** — **FIXED** (both labels added at the facts) |
| Lens C | 4 — `RunWorkspaceRedirectsTests`' shared-collection premise is false | nit | **CONFIRMED** — **FIXED** (`RunWorkspacePromotionTests` joined the collection; the comment now names both co-members) |
| Lens C | 5 — the G5 commit records an incomplete gate | nit | **REFUTED**, high confidence — the scenario needs the missing Release count never to be supplied, but `08e20ab6` is the very next commit and its gate report anchors back to G5's own test count (2505) with both configurations at 0/0. The record gap is real; the hazard closes one commit later |
| Lens C | 6 — architecture-rule message states a different threshold | nit | **REFUTED**, high confidence — "declared and called at least twice" *is* 3, the same threshold the assertion enforces; and the failure scenario cannot fire (the substring count in that file is 4 at `286ea09` and 5 at HEAD, so dropping a call site still passes `>= 3`). Explicitly **not** a scope refutation |

**The word "Ten" above does not reconcile with the table under it, counted by the roadmap pass of 2026-07-31.**
Row by row the table carries **11 CONFIRMED** and **3 REFUTED** (Lens A 4, Lens C 5, Lens C 6 — recorded as
NO VERDICT until the correction above) — and of the 11,
**9 read FIXED and 2 read FIXED IN PART** (Lens A 5 / Lens B 3, which are one defect). "Ten" is reachable by
exactly one reading and it is probably the intended one: **Lens C 1 already had its verdict** from the first
review's single surviving skeptic and was fixed in `914730d`, *before* the pass this sentence is about, so
11 − 1 = 10 rows were re-verified by the 2026-07-31 pass. Two of those ten are still only partly fixed, so
"and then **fixed**" overstates them. And by **distinct defect** the number is **8**, not 10 or 11, because three
pairs are one defect filed twice (A1=B2, A2=B1, A5=B3). Recorded rather than corrected in place because all four
counts are defensible answers to four different questions; what is not defensible is a bare number. If you need one
figure: **8 distinct defects confirmed — 7 fully fixed, 1 (the conflict path) fixed in part — and 3 findings
refuted with evidence, none left unadjudicated.** The eight, so the arithmetic is checkable rather than asserted:
A1=B2, A2=B1, A3, A5=B3 (the partial), C1, C2, C3, C4.

Lens A finding 2 and Lens B finding 1 are the **same defect** found twice, independently, by two lenses. Lens A
finding 1 and Lens B finding 2 likewise overlap. That is worth knowing before verifying them: two lenses
converging is evidence, but it is not a verdict either. Both pairs turned out to be real.

**Lens A 4 was refuted on its harm, and `3b66603` moved it besides.** That commit makes `TearDownAsync` leave a torn-down
STUB behind for worktree mode instead of deleting the document, and the stub keeps `MainWorktree`. The scenario
Lens A 4 describes — the directory survives a failed removal while the document is deleted, so no later pass can
ever prune the registration — therefore no longer applies to the worktree case at all: the document that knows
which repository holds the registration is still there, and the metadata sweep prunes through it when the stub
ages out. What remains unaddressed is the finding's other half (`TearDownWithoutMetadataAsync` still reports no
success signal, and the `OnChatsChanged` teardown still does not await the cancelled dispatch's unwind). That is
a live item, not a closed one.

## Live items the fix pass OPENED or deliberately left, 2026-07-31

Recorded rather than built, because each fix is a redesign and the fix pass's brief is to fix the defect and
stay inside the finding's scope. None of these existed before `3b66603`; the first two are the price of it.

1. **A worktree run whose run-branch commit FAILED still gets the branch line.** On that arm `PromoteAsync`
   returns `RetainWorkspace: true`, so teardown never runs, so the metadata document is intact and
   *un-stamped* — and `DescribeAsync`'s stub arm keys on `tornDownAtUtc`, which is null there. It therefore
   falls through to the directory-exists path and answers
   `RunWorkspaceOutcome(Worktree, meta.Branch, HasUnpublishedFiles: false)`: the panel names a branch that
   received nothing, and worktree mode offers no publish button, so the UI shows no recovery path at all. The
   files are really in `%LOCALAPPDATA%\Pia\runs\<runId>` for seven days. Suppressing the line properly means
   `DescribeAsync` learning whether the branch actually carries a commit, which is a new question for that
   method to answer — a design item, not a one-line guard.
2. **`Publish()` still ignores `RetainWorkspace`.** The manual path tears the workspace down unconditionally
   on a non-null result and clears `HasUnpublishedFiles`, so publishing a conflicted workspace deletes the
   run's version of the conflicted file — the exact loss Lens A 5 / Lens B 3 filed against the AUTOMATIC path,
   now surviving on the manual one. Defensible as it stands (the path is user-initiated and the note it
   renders carries the conflict count), and left alone on purpose: retaining there would leave an offer
   standing that the user has just answered. Named here so the asymmetry is a decision rather than an
   oversight.
3. **Lens A 4's other half is untouched** (see the note above the table): `TearDownWithoutMetadataAsync` still
   returns no success signal, and `OnChatsChanged` still starts a teardown without awaiting the cancelled
   dispatch's unwind. Its verdict refuted the *permanent* leak, not the shape — the unconditional
   `git worktree prune` on a failed removal is what carries it, so that call is load-bearing and a future
   simplification pass must not fold it into the success arm.

## History defects in the Phase 3 commit record

Two, both recorded here rather than in two places, and neither is a defect at HEAD:

- **`695e123` (G5) records an incomplete gate** — no warning count and no Release configuration, where every
  other commit in the run states both. Nothing was shipped red: the 2026-07-31 review pass re-measured a clean
  detached worktree at Debug and Release `-t:Rebuild`, both **0 Warning(s) / 0 Error(s)** with 4 CoreCompile
  invocations each. §7's requirement that a builder state both configurations explicitly stands.
- **`914730d` claims a gate that was actually FAILING.** Its message says "Value-only change: LocalizationTests
  enforces en/de/fr KEY parity, not translation quality, so nothing was red before this and nothing goes green
  after it." Its +4/−1 diff carries the French branch-label fix it describes **plus three brand-new keys** —
  `Run_Children_Header`, `Run_Children_Count`, `Run_Children_Timeline_Empty` — added to `ViewStrings.fr.resx`
  only. Those keys belong to G10, which landed in the NEXT commit (`9c32999`), and `9c32999`'s diff touches only
  `ViewStrings.resx` and `ViewStrings.de.resx`. Measured at that boundary:
  `git show 914730d:…/ViewStrings.resx | grep -c 'Run_Children_'` = 0, same for `de.resx` = 0, while `fr.resx`
  has all three. `LocalizationTests.AllTranslations_MustBeComplete` computes
  `orphaned = translatedKeys.Except(baseKeys)` per culture with `tryParents: false`, so at `914730d` the suite
  had three orphaned FR entries and the fact was **red**. HEAD is fine — `9c32999` supplied the base and DE
  values. The lesson worth keeping is not the redness but its cause: G10's resx trio was split across two
  commits, one of which does not mention resx at all, so a bisect lands on a "value-only" commit whose message
  asserts the opposite of what its own diff did.

## How to read the anchors

Every `file:line` below was measured against the tree as it stood at `286ea09` (Lens C says so explicitly).
The tree has moved since — `914730d`, `676f629c`, `b2f46a2e`, `08e20ab6` and `9c32999` all landed after these
findings were filed. **Re-anchor by symbol, not by line number**, and treat "the line does not say that" as a
drift question before treating it as a refutation. One finding (Lens C 1) has already been fixed; others may
have been incidentally closed by the simplification commits, and a verifier that finds one closed should say
which commit closed it.

---
### LENS A (reviewer 1) · finding 1 — Worktree mode destroys the run's output at teardown — nothing commits it, and the default grant set forbids committing

- **Anchor:** `src/Pia.Wpf/Services/RunWorkspaceService.cs` line 219
- **Severity as filed:** must-fix
- **Claim:** In worktree mode PromoteAsync copies nothing and returns a NON-NULL result (`RunWorkspaceService.cs:211-219`), so `AgentRunOrchestrator.SafePromote` treats it as success and calls `TearDownAsync` (HEAD `AgentRunOrchestrator.cs:457` then `:470`). Teardown runs `git worktree remove --force` and then `TryDeleteDirectory(root)` UNCONDITIONALLY, whether or not the remove succeeded (`RunWorkspaceService.cs:677-692`) — so the workspace directory and everything uncommitted in it is deleted regardless of git semantics. Nothing in Batch 06 commits the run's work: the only committer would be the model itself via `git_commit`/`git_add`, and an unattended run cannot run them. `HeadlessRunRequest.DefaultGrantedWrites = ["write_file"]` (IHeadlessRunLauncher.cs:38) and the resume floor is the same single name (HeadlessRunLauncher.cs:54); `RunAutonomyPolicy.PresetClasses` deliberately EXCLUDES `ToolClass.Git` (RunAutonomyPolicy.cs:21-28); the headless exchange gates every deferred tool through `BackgroundAssistantTurnRunner.HandleToolCallAsync` → `ToolAutonomy.Resolve(... IsNamedGrant: grantedWrites.Contains(name) ...)` (BackgroundAssistantTurnRunner.cs:416-423, reached from HeadlessTurnExecutor.cs:366 via `_engine.RunExchangeAsync(..., _grantedWrites, ...)`), which for `git_commit` yields `Refuse/DeniedNotGranted`. So for a default-grant run in worktree mode the branch never receives a commit and the files exist only in the deleted directory. `DescribeAsync` also hard-codes `HasUnpublishedFiles = false` for worktree mode (`RunWorkspaceService.cs:396-398`), so the publish affordance can never offer them either. The impl spec's release-note list for worktree mode covers only 'uncommitted USER files are invisible to the run' and 'the repo is mutated' (06-...impl.md:597-598) — the run's own output being discarded is not a recorded decision.
- **Failure scenario:** AssistantFilesFolder is a git repo with at least one commit and git is installed (so provisioning picks Worktree). A scheduled job runs with the default grant set and the agent calls `write_file` to produce `report.md` in the workspace. The run drains cleanly, verify passes (the artifact probe resolves against the run root, so it CONFIRMS the file), SafePromote gets `RunPromotionResult(Worktree, 0,0,0, "pia/run/<id>")`, then TearDownAsync deletes the worktree directory. `git_commit` was refused, so branch `pia/run/<id>` points at the pre-existing HEAD commit and contains no report.md. The run is marked Completed with a passing verdict and the deliverable no longer exists anywhere on disk.
- **Suggested fix as filed:** Do not tear down a worktree that still holds uncommitted/untracked work. Either (a) have PromoteAsync return null for worktree mode when `git status --porcelain` in the worktree is non-empty, so the workspace is retained and the publish offer can surface it, or (b) commit the run's work onto the run branch as part of promotion (an app-side `git add -A && git commit` through IGitProcessRunner, not through the gated tool surface) before teardown. Option (b) makes D5b's 'the branch is the deliverable' literally true; option (a) at minimum stops the silent loss.

### LENS A (reviewer 1) · finding 2 — D5b's 'output is on branch X' line can never render for a successful worktree run — the metadata it reads is deleted first

- **Anchor:** `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs` line 245
- **Severity as filed:** should-fix
- **Claim:** `RunProgressViewModel.RefreshAsync` reads the workspace outcome only when the run is TERMINAL (HEAD `RunProgressViewModel.cs:244-245`) and takes the branch name solely from `DescribeAsync` (`:268`, applied at `:291`). But on the success path `SafePromote` calls `TearDownAsync` (HEAD `AgentRunOrchestrator.cs:470`) BEFORE `SafeComplete` (`:471`), and `TearDownAsync` deletes the metadata document (`RunWorkspaceService.cs:421`). `DescribeAsync` starts with `ReadMetadata` and returns null when the document is absent (`RunWorkspaceService.cs:384-389`). So by the time the first terminal RunChanged fires, DescribeAsync answers null, `OutputBranchName` is set to null, `HasOutputBranch` is false and the branch TextBlock stays collapsed. The branch line therefore only ever appears for a FAILED/CANCELLED worktree run — the exact inverse of the case B15/D5b exists for ('without that line the honest user question is where is my file?', 06-...impl.md:546-548). The coverage is vacuous on this ordering: `WorktreeOutcome_SurfacesTheBranchName` (tests/Pia.Wpf.Tests/ViewModels/RunProgressPublishTests.cs:250) injects `FakeRunWorkspaceService.Outcome` directly, while that fake's own doc comment records that 'a cleanly promoted run' describes as null (tests/Pia.Wpf.Tests/Services/FakeRunWorkspaceService.cs:48-50).
- **Failure scenario:** A worktree-mode run completes cleanly. Promotion copies nothing, teardown deletes `<runsBase>\<runId>.workspace.json`, then the run flips to Completed. The panel refreshes, DescribeAsync returns null, and the user sees a successful run with no branch line and no publish button — no indication anywhere in the UI that the output was supposed to be on `pia/run/<runId>`.
- **Suggested fix as filed:** Project the branch from the promotion result rather than from a post-teardown describe: have SafePromote surface `RunPromotionResult.BranchName` onto something the panel reads (e.g. persist it on the run row's completion reason, or keep the metadata document alive for worktree mode and let the retention sweep remove it). Add a fact that drives the real ordering — promote, tear down, THEN refresh — instead of injecting an Outcome the production sequence cannot produce.

### LENS A (reviewer 1) · finding 3 — StepPersonaResolver's persona lookup is unguarded, so a persona-store fault fails the whole run instead of degrading

- **Anchor:** `src/Pia.Wpf/Services/StepPersonaResolver.cs` line 114
- **Severity as filed:** should-fix
- **Claim:** `ResolveAsync` wraps `GetRosterAsync` (:103, self-guarding), `ResolveProviderAsync` (:159-178, try/catch) and `_composer.PrepareTurn` (:126-140, try/catch), but `await _personas.GetPersonaAsync(id)` at `:114` is bare. `PersonaService.GetPersonaAsync` executes raw SQLite I/O for any non-built-in id (PersonaService.cs:71-90) and can throw. The throw escapes ResolveAsync, escapes `HeadlessTurnExecutor.ExecuteStepAsync` (HeadlessTurnExecutor.cs:275-282, resolution happens before the exchange's try/catch) and `LiveTurnExecutor.ExecuteStepAsync` (resolution is before PostAsync), and lands in the orchestrator's outer `catch (Exception ex)` → LogError + SafeFail, i.e. the RUN FAILS. That contradicts the type's own contract ('Nothing in here may fail a step … every arm that cannot produce one returns the run default instead of throwing', :19-22) and the call-site comment ('Never throws — every arm of the ladder ends at _runDefault', HeadlessTurnExecutor.cs:276-277). The lookup is also redundant: `GetRosterAsync` already fetched the full `Persona` objects via `GetPersonasAsync` (:225-232) using an identical column list and the same `MapPersona`, and `:104` has just proved the id is in that list. Honest about reachability: only a step carrying an on-roster `AssignedPersonaId` reaches `:114`, and the roster has no UI until G7 — so today it needs a hand-edited settings file. It is nonetheless the feature's only path.
- **Failure scenario:** A roster is configured and a plan assigns step 3 to a roster persona. The SQLite connection is momentarily busy (or the DB is locked by the sync/vault writer) when `GetPersonaAsync` runs. `SqliteException` propagates out of ExecuteStepAsync; the orchestrator logs 'Agent run {RunId} failed' and settles the run Failed — losing the completed steps' progress on a run that, with `AssignedPersonaId = null`, would have finished normally.
- **Suggested fix as filed:** Take the persona from the roster list already in hand — `roster.First(p => p.Id == id)` — deleting the redundant store round-trip and the throw path with it. If the second read is wanted for freshness, wrap it in the same try/catch shape as the PrepareTurn arm and `_degraded.Add(id)` + return runDefault on fault.

### LENS A (reviewer 1) · finding 4 — TearDownAsync deletes the metadata document even when the directory removal failed, permanently orphaning a worktree registration

- **Anchor:** `src/Pia.Wpf/Services/RunWorkspaceService.cs` line 417
- **Severity as filed:** should-fix
- **Claim:** `TearDownWithoutMetadataAsync` returns no success signal (`RunWorkspaceService.cs:669-699`; both `git worktree remove` failure and `TryDeleteDirectory` failure are logged and swallowed), so `TearDownAsync` runs `TryDeleteFile(MetadataPathFor(runId))` at `:421` unconditionally. The metadata document is the ONLY record of `MainWorktree`, i.e. which repository holds the `.git/worktrees/<id>` registration, and `SweepOrphanMetadataAsync` prunes only via that document (`:453-455`). Once the document is gone while the directory survives, no pass can ever prune: the startup directory sweep will later call TearDownAsync again, ReadMetadata returns null, mode is treated as `None`, and it does a plain recursive delete with NO prune (`:411-418`). That is exactly the leak plan R5 names. The comment at `:420` ('Last, so a crash between the two leaves a metadata document the orphan sweep can still act on') only covers a crash, not a failed delete. The reachable trigger is the R4 handler: `OnChatsChanged` calls `entry.Cts.Cancel()` and does NOT await the dispatch's unwind — `_inflight` holds `(Cts, Task)` but only the Cts is used (HEAD `HeadlessRunLauncher.cs:578-587`) — so the teardown runs while a step may still hold a file open, which is precisely when both the `worktree remove` and the recursive delete fail.
- **Failure scenario:** A worktree-mode run is mid-`write_file` when the user deletes its chat. `Cancel()` returns immediately; `TearDownWorkspaceAsync` fires unawaited; `git worktree remove --force` fails on the open handle and `Directory.Delete(recursive)` throws part-way through (leaving a partial tree); `git worktree prune` is a no-op because the directory still exists; then `:421` deletes the metadata anyway. The next startup deletes the leftover directory with no prune, and `.git/worktrees/<runId>` stays in the user's repository forever (visible in `git worktree list` as a prunable-but-never-pruned entry).
- **Suggested fix as filed:** Have `TearDownWithoutMetadataAsync` return whether the directory is actually gone, and keep the metadata document when it is not — the orphan sweep will then finish the prune on a later run. Additionally, in `OnChatsChanged` await (with a short timeout) the `entry.Task` already stored in `_inflight` before starting teardown, which is what plan R4's 'cancel first rather than deleting under a live writer' asks for.

### LENS A (reviewer 1) · finding 5 — Automatic promotion with conflicts tears the workspace down, and the conflict string the panel owns is never used on that path

- **Anchor:** `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` line 470
- **Severity as filed:** should-fix
- **Claim:** `CopyOut` leaves a conflicted file in the workspace and only counts it (`RunWorkspaceService.cs:314-320`; the per-path line is `SensitiveWarning`, which is `[Conditional("DEBUG")]` and therefore absent from a release log). It still returns a non-null result, so `SafePromote` proceeds to `TearDownAsync` (HEAD `AgentRunOrchestrator.cs:457-470`) and the workspace — including every conflicted file — is deleted. The only user-facing report of conflicts is `Run_Publish_Conflicts`, which exists in all three resx files and is set exclusively inside the manual `Publish()` command (HEAD `RunProgressViewModel.cs`, `PublishNote` assembly in `Publish`); `SafePromote` sets no VM state at all, and after teardown `DescribeAsync` returns null so `HasUnpublishedFiles` is false and no note or button appears. So on the automatic path a conflict is invisible to the user and irreversible.
- **Failure scenario:** A scheduled run rewrites `notes.md` in copy mode. While it works, the user edits `notes.md` in the assistant folder. On completion, promotion sees `dest.LastWriteTimeUtc > provisionedAtUtc`, counts `Conflicts = 1`, keeps the user's file (correct), and then teardown deletes the workspace. The agent's rewrite is gone; the run shows as Completed with no note, no publish offer, and in a Release build not even a log line naming the file.
- **Suggested fix as filed:** Treat `Conflicts > 0` as 'do not tear down': keep the workspace so the publish offer can still surface it, and set the panel's conflict note from the automatic promotion result too (the localized string already exists in all three resx files). At minimum, log the conflicting relative path at a DEBUG-erased-but-release-visible-count granularity that lets support tell the user which file was left behind.

### LENS B (reviewer 2) · finding 1 — D5b's "output is on branch X" line can never render for a successful worktree run

- **Anchor:** `src/Pia.Wpf/Services/RunWorkspaceService.cs` line 390
- **Severity as filed:** must-fix
- **Claim:** Plan D5b and impl B10/B15 require a worktree-mode run's panel to say "Output is on branch pia/run/<id>" precisely because nothing was copied anywhere. The only feeder of `RunProgressViewModel.OutputBranchName` is `DescribeAsync` (committed RunProgressViewModel.cs:291), and it is only called in the terminal-only branch at committed RunProgressViewModel.cs:244, i.e. after `SafeComplete` raises `RunChanged(Completed)`. By then `SafePromote` has already called `TearDownAsync` (committed AgentRunOrchestrator.cs:470), which deletes the workspace directory and then the metadata document (RunWorkspaceService.cs:417-421) — so `DescribeAsync` bails at `if (meta is null …) return null` (:385) and `if (!Directory.Exists(root)) return null` (:389-390). `IRunWorkspaceService.DescribeAsync`'s own doc concedes it returns null when the workspace "was already promoted and torn down", so the interaction was reasoned about and the B15 requirement was lost anyway. Premise checked: `git grep Run_Output_Branch/BranchName -- src/Pia.Wpf` shows RunProgressViewModel.cs:173/175/181/291 as the only consumers — there is no other surface, log line or notification that names the branch after teardown, and the branch name is not persisted anywhere once the metadata file is gone.
- **Failure scenario:** The assistant files folder is a git repo with at least one commit and git is installed, so provisioning takes worktree mode. An unattended run finishes cleanly: PromoteAsync returns RunPromotionResult(Worktree, Promoted:0…) (:211-219), SafePromote logs and tears the workspace down, then CompleteAsync fires RunChanged(Completed). The panel's terminal DescribeAsync now returns null → OutputBranchName stays null → HasOutputBranch false → the branch TextBlock (RunProgressPanel.xaml, Visibility bound to HasOutputBranch) is collapsed and CanPublish is false. The user sees a run that reports success, no file in the assistant folder, and no statement anywhere of where the output went — exactly the "where is my file?" outcome D5b exists to prevent. The gate is green because T-G4-19 (`WorktreeOutcome_SurfacesTheBranchName`, tests/Pia.Wpf.Tests/ViewModels/RunProgressPublishTests.cs:250) drives `FakeRunWorkspaceService`, whose DescribeAsync returns the canned `Outcome` regardless of TearDownAsync (tests/Pia.Wpf.Tests/Services/FakeRunWorkspaceService.cs:89-94) — it asserts a state the real service cannot produce for a Completed run, while its own docstring says "after a successful run".
- **Suggested fix as filed:** Do not let the branch name die with the metadata. Either (a) have SafePromote carry `result.BranchName` forward so the VM can render it without a Describe (e.g. keep a short-lived process-local map beside RunWorkspaceRedirects, or persist the branch on the run row), or (b) have TearDownAsync retain a minimal worktree-mode metadata stub (mode + branch, directory gone) so DescribeAsync can still answer `RunWorkspaceOutcome(Worktree, branch, HasUnpublishedFiles: false)` after teardown, and let SweepOrphanMetadataAsync age it out. Then add a fact that drives the REAL service through provision → promote → teardown → DescribeAsync in worktree mode and asserts a non-null branch, since the fake cannot discriminate this.

### LENS B (reviewer 2) · finding 2 — Nothing commits the run's work to the run branch, so worktree mode force-deletes the deliverable

- **Anchor:** `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` line 470
- **Severity as filed:** must-fix
- **Claim:** Plan D5b decides "the branch is the deliverable", and impl §10.2's smoke item expects "the run branch exists, the agent's commits are on it". No group in G1-G6 ever commits: grep for `commit` across RunWorkspaceService.cs, HeadlessTurnExecutor.cs and AgentPlanner.cs finds only comments about unborn HEAD, and the run prompt says nothing about committing. Worse, an unattended run cannot commit even if the model tries — `HeadlessRunRequest.DefaultGrantedWrites = ["write_file"]` (src/Pia.Wpf/Services/Interfaces/IHeadlessRunLauncher.cs:38) and a deferred mutating tool is denied inline unless its name is in the run's write-grant set (HeadlessTurnExecutor.cs:200-204). Meanwhile the clean-success path unconditionally destroys the worktree directory: PromoteAsync's worktree arm copies nothing (RunWorkspaceService.cs:211-219), SafePromote then calls TearDownAsync (AgentRunOrchestrator.cs:470), and TearDownWithoutMetadataAsync runs `git worktree remove --force` and — regardless of whether that succeeded — `TryDeleteDirectory(root)`, a plain `Directory.Delete(dir, recursive: true)` (RunWorkspaceService.cs:672-698, :880-891). So the directory holding the run's output is recursively deleted by our own code, and the branch it was supposed to be "on" still points at the base commit. Premise stated: B10 does prescribe tearing the directory down on a clean run, so the defect is not the teardown — it is that no group makes the branch actually carry the work, which leaves D5b unmet and turns B10's teardown into data loss. The inbound direction of this hazard is documented (plan R16: uncommitted files in the USER's tree are invisible to the run); the outbound direction is documented nowhere in either spec.
- **Failure scenario:** The assistant files folder is a git repo with ≥1 commit, git installed → worktree mode. A scheduled/background run is asked to "write a summary to report.md". The step calls write_file, which lands report.md (untracked) in runs\<runId>; the verifier probes the run root (B3) and confirms the artifact, so the verdict passes. SafePromote: PromoteAsync returns Promoted:0 for worktree mode, SafePromote logs "promoted no files" and calls TearDownAsync → the worktree directory (and report.md with it) is deleted. The run settles Completed with a passing verdict. Branch pia/run/<runId> exists and is byte-identical to the base commit; report.md exists nowhere. Combined with finding 1, the panel says nothing at all. Copy mode is unaffected, so the loss only appears for users whose assistant folder is a repo — i.e. exactly the users D5 added worktree mode for.
- **Suggested fix as filed:** Make the branch really be the deliverable before the workspace is removed: in worktree mode have PromoteAsync (which already owns the mode and the git runner) commit the run's work on the run branch — `git -C <runRoot> add -A` then `git -C <runRoot> commit -m "pia run <runId>"` — and report the committed file count as `Promoted`; skip the commit (and report 0) when `status --porcelain` is empty. That keeps the no-merge rule intact and stays app-side (no new agent capability, R18). If committing app-side is rejected, then worktree mode must not tear the directory down on a clean run at all (retain it under the terminal retention rule and offer publish), because deleting it is unrecoverable. Either way add a fact asserting that after a clean worktree run something durable holds the file.

### LENS B (reviewer 2) · finding 3 — An automatic promotion's conflicts are logged but never shown, and the workspace copy is then deleted

- **Anchor:** `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` line 467
- **Severity as filed:** should-fix
- **Claim:** Impl B7 decides a destination file modified during the run is a CONFLICT and is skipped, and B15 gives that outcome a user-facing string, `Run_Publish_Conflicts`. That string is only ever produced on the MANUAL path — `RunProgressViewModel.Publish()` appends it when `result.Conflicts > 0` — while the automatic terminal promotion reports the same count only to the log (AgentRunOrchestrator.cs:467-469) and then removes the workspace at :470. The asymmetry is the claim: the identical event informs the user on one path and is silent on the other, and on the silent path the only remaining copy of the run's version of the conflicted file is deleted immediately afterwards. Premise stated: B7 (skip) and B8 (teardown on a non-null result) are both followed faithfully, so this is not a divergence from those sections; what no decision sanctions is that D3's "not silent loss" fails for this sub-case.
- **Failure scenario:** Copy mode. A background run rewrites notes.md in its workspace. While the run is working, the user edits notes.md in the real folder, so the destination mtime is newer than provisionedAtUtc. CopyOut counts it as a conflict and skips it (RunWorkspaceService.cs:314-321), returns Promoted:0/Conflicts:1; SafePromote logs the counts and calls TearDownAsync, which deletes the workspace and with it the run's version of notes.md. The run shows Completed, the panel shows no note (PublishNote is only set by the Publish command, and DescribeAsync returns null once the workspace is gone), and the user is never told that the run's work on that file was discarded in favour of their edit.
- **Suggested fix as filed:** Surface the automatic path's conflict count the same way the manual one does: have SafePromote hand `result` to the panel (or persist it on the run's truncation/reason field) so `PublishNote` can render `Run_Publish_Conflicts` after an automatic promotion, and consider keeping the workspace when `Conflicts > 0` so the user can still recover the run's version before the retention rule sweeps it.

### LENS C (reviewer 3) · finding 1 — French run-panel string leaves the English word "branch" untranslated

- **Anchor:** `src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx` line 945
- **Severity as filed:** should-fix
- **Claim:** Premise, checked at HEAD (286ea09) in a clean worktree: the six new G4 keys are present in all three resx files with real DE/FR text, but the FR value for Run_Output_Branch is "Le résultat est sur la branch {0}" — "branch" is the English noun, with a French article in front of it. The French word is "branche". This is the one new string that carries an untranslated English word rather than a translation. (Secondary, weaker: the DE value at ViewStrings.de.resx:945 is "Das Ergebnis liegt auf Branch {0}", which drops the article German grammar wants — "auf dem Branch {0}". "Branch" itself is accepted German dev usage, so that half is style, not a missing translation.)
- **Failure scenario:** A French user runs an agent run in worktree mode (source root is a git repo, so RunWorkspaceService provisions a worktree and PromoteAsync copies nothing). RunProgressViewModel.OutputBranchNote formats Run_Output_Branch and RunProgressPanel.xaml:68 renders it, so the panel shows "Le résultat est sur la branch pia/run/9f2e…" — the one line the batch added specifically so the user knows where the deliverable went is half-English at the exact moment D5b says the panel must speak plainly.
- **Suggested fix as filed:** ViewStrings.fr.resx:945 → "Le résultat est sur la branche {0}". Optionally ViewStrings.de.resx:945 → "Das Ergebnis liegt auf dem Branch {0}". No code, no Designer.cs, no key change.

### LENS C (reviewer 3) · finding 2 — Git-parity test compares a raw GetTempPath expectation against a canonicalized path

- **Anchor:** `tests/Pia.Wpf.Tests/Services/GitToolHandlerWorkspaceRootTests.cs` line 85
- **Severity as filed:** nit
- **Claim:** Premise, verified at HEAD: GitToolHandler resolves the ambient workspace root through SafeFolderPath.NormalizeWorkspaceRoot (GitToolHandler.cs:142), which for an existing directory calls Canonicalize → GetFinalPathNameByHandleW with FILE_NAME_NORMALIZED|VOLUME_NAME_DOS, i.e. the long-form, junction-resolved, on-disk-cased path. The fixture's _runRoot (line 38) is built from Path.Combine(Path.GetTempPath(), "runs", guid) and is never canonicalized, yet lines 85, 86 and 176 assert equality against the recorded WorkingDirectory / CeilingDirectory. The same batch treats this as a live hazard everywhere else: HeadlessRunLauncherTests' G2 facts and LiveTurnExecutorPlannedRunTests both canonicalize the expectation with the comment "GetTempPath can carry an 8.3 or a link component, so a raw Path.Combine expectation would compare two spellings of the same directory". This one file does not.
- **Failure scenario:** On a machine whose %TEMP% resolves through an 8.3 short name or a junction/redirected folder (a corporate profile-redirection setup, or a service account), Path.GetTempPath() returns e.g. C:\Users\LONGNA~1\AppData\Local\Temp while the handler records C:\Users\LongName\AppData\Local\Temp\runs\<guid>. GitToolHandler_ResolvesTheRepoAgainstTheAmbientWorkspaceRoot and GitToolHandler_InAnIsolatedWorkspaceWithNoRepo_RoutesToInitInsideTheWorkspace then fail with two spellings of the same directory — a fixture-only red that reads as a G3 git-parity regression and sends the next builder hunting a non-existent bug.
- **Suggested fix as filed:** Canonicalize _runRoot once in the constructor (after CreateDirectory) with SafeFolderPath.Canonicalize, exactly as the launcher and live-executor fixtures do, so lines 85/86/176 and the ambient TaskContext all use the one spelling. Assertions and intent unchanged.

### LENS C (reviewer 3) · finding 3 — G1's runs-dir guard facts carry no REGRESSION/GUARD label, which 06 §9 requires in the test's own comment

- **Anchor:** `tests/Pia.Wpf.Tests/Services/FilesToolHandlerRunsDirGuardTests.cs` line 57
- **Severity as filed:** nit
- **Claim:** Premise: 06-run-workspace-isolation.impl.md §9's preamble states that every entry says REGRESSION or GUARD "and the distinction goes in the test's own comment, not only in this table". Neither AWriteInsideARealRunsWorkspace_Succeeds (line 58) nor EscapeVector_IsStillRejected_InsideARealRunsWorkspace (line 75) has a doc comment at all, and the class doc (lines 13-19) never uses either word — even though the spec classifies the first as the REGRESSION carrying R1's whole point and the second as the escape GUARD beside it. Every other new 06 test file complies (RunWorkspaceServiceTests 13/13, RunWorkspacePromotionTests 9/9, RunProgressPublishTests 9/9, RunWorkspaceRuleTests 2/2, SensitivePathGuardRunsCarveOutTests 2/2, AgentVerifierWorkspaceRootTests 2/2, ChatSessionWorkspaceIsolationTests 4/4, RunWorkspaceRedirectsTests 5/5), so this is one file out of step rather than a convention nobody followed. The 07 files (AgentPlannerRosterTests, StepPersonaResolverTests, AppSettingsAgentRosterTests) are NOT in violation: 07 §9 only says "every test is labelled" and does the labelling in its own T-id table, which those test names match.
- **Failure scenario:** A later fixer neutralizes the SensitivePathGuard carve-out to check something else, sees EscapeVector_IsStillRejected stay green and AWriteInsideARealRunsWorkspace go red, and has nothing at the test to tell which of the two was ever claimed to be demonstrably red — the exact distinction the guard carve-out's whole non-vacuity argument rests on (the escape assertions pass vacuously against an unwritable root). The class doc's R1 prose explains the fixture's location but never says which fact is the control.
- **Suggested fix as filed:** Add the two one-line labels the sibling files carry: REGRESSION on AWriteInsideARealRunsWorkspace_Succeeds ("revert the BuildAllowedExceptions runs entry → the write returns WriteResult.Failed with 'protected system or application data directory'") and GUARD on EscapeVector_IsStillRejected_InsideARealRunsWorkspace (containment is unchanged by G1/G2, so it cannot red on a revert). No assertion changes.

### LENS C (reviewer 3) · finding 4 — RunWorkspaceRedirectsTests' shared-collection premise is false — the class that drives a real Record is not in the collection

- **Anchor:** `tests/Pia.Wpf.Tests/Helpers/RunWorkspaceRedirectsTests.cs` line 20
- **Severity as filed:** nit
- **Claim:** Premise: the class doc (line 20) says it "Shares the RunWorkspaceRedirectsStatic collection with the tests that drive a real promotion", and line 25 applies [Collection("RunWorkspaceRedirectsStatic")]. A repo-wide grep finds exactly two members of that collection: this class and LiveTurnExecutorPlannedRunTests. RunWorkspacePromotionTests is not one of them, yet Promote_AtTheRealRunsRootShape_ActuallyCopiesFilesOut (RunWorkspacePromotionTests.cs:283) drives a real PromoteAsync whose CopyOut calls RunWorkspaceRedirects.Record with a workspace root under the real AssistantWorkspace.RunsRoot — i.e. a root the containment gate accepts, so it does mutate the process-global registry. The stated isolation is therefore absent for that class. I checked the arithmetic and it cannot fail today: Record_EvictsPastTheEntryCap asserts only Count <= MaxEntries (monotone-safe) and that the newest entry still resolves, and eviction always drops the oldest, so it would take 4+ foreign Record calls landing after the loop's last one to touch it, while only one is reachable. No failure scenario at this commit — hence nit.
- **Failure scenario:** No reproducible failure today. The concrete cost is a false premise in a comment that a later author will rely on: add one Resolve or Count assertion to RunWorkspacePromotionTests (or a second real-shape promotion fact), and it runs in parallel with Record_EvictsPastTheEntryCap deliberately overflowing the shared static registry — a fixture-only flake the comment claims is already prevented.
- **Suggested fix as filed:** Either put [Collection("RunWorkspaceRedirectsStatic")] on RunWorkspacePromotionTests so the comment becomes true, or narrow the comment to name LiveTurnExecutorPlannedRunTests as the only co-member and say explicitly why the promotion class does not need to join (its facts never read Resolve).

### LENS C (reviewer 3) · finding 5 — The G5 commit records an incomplete gate: no warning count and no Release rebuild

- **Anchor:** `docs/superpowers/specs/agent-roadmap/phase3-workflow-plan.md` line 399
- **Severity as filed:** nit
- **Claim:** Premise: §7 requires every builder to gate with `dotnet build -t:Rebuild -v:n` and again with `-c Release`, reading the count off MSBuild's `N Warning(s)` line, and to report it. Commit 695e123 (G5, interactive isolation + chip resolution) states only: "Measured here before committing, on this exact tree: `dotnet build -t:Rebuild` 0 Error(s); the suite at total 2505 / failed 0 / skipped 1" — no warning count and no Release configuration. Every other commit in the run states both configs with 0 Warning(s)/0 Error(s) and (from G2 on) the 4-CoreCompile sanity check. I re-measured HEAD myself in a clean detached worktree: Debug and Release `-t:Rebuild` are both 0 Warning(s) / 0 Error(s) with 4 CoreCompile invocations each, and the suite is 2571 total / 0 failed / 2570 passed / 1 skipped — matching b2f46a2's claim exactly. So nothing was actually shipped red; the record, not the tree, is the gap.
- **Failure scenario:** A resumed or bisected run trusts commit messages as the gate log (the workflow's own resumability design says a resume 'inherits real tree state' from what builders reported). At 695e123 the tree's Release-configuration warning count is unrecorded, so a Release-only analyzer warning introduced there — the exact class of warning that historically produced 186 of this repo's 194 warnings — would be invisible at that boundary and only surface later, attributed to whichever commit next ran a Release rebuild.
- **Suggested fix as filed:** No code change. When the roadmap/fix pass touches history notes, record the measured Debug+Release warning counts for the G5 boundary (0/0 as of HEAD), and keep the §7 wording that builders must state both configurations explicitly.

### LENS C (reviewer 3) · finding 6 — Architecture-rule failure message states a different threshold than the assertion

- **Anchor:** `tests/Pia.Wpf.Tests/Architecture/RunWorkspaceRuleTests.cs` line 42
- **Severity as filed:** nit
- **Claim:** Premise: line 41 asserts `teardownPathCalls >= 3` (the TearDownWorkspaceAsync declaration plus at least two call sites), while the failure message on line 42 says "expected the single teardown path to be declared and called at least twice". Declared + called-at-least-twice is three mentions, so the message is describing the intent in a way that reads as a different number than the code enforces.
- **Failure scenario:** Someone removes one of the two TearDownWorkspaceAsync call sites (say the chat-deleted handler) leaving 2 mentions. The test fails with "expected the single teardown path to be declared and called at least twice; found 2 mentions" — which reads as satisfied, so the reader's first hypothesis is that the rule is miscounting rather than that a workspace-removal site was just dropped, which is the failure the rule exists to catch (a worktree torn down without `git worktree remove` leaks a .git/worktrees entry).
- **Suggested fix as filed:** Reword to match the assertion, e.g. "expected TearDownWorkspaceAsync to be declared and called from at least two sites (>= 3 mentions); found {teardownPathCalls}".

---

## The one verdict that was returned

**refuted: False** · confidence: high

Verified byte-for-byte: src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx line 945 reads exactly `<data name="Run_Output_Branch" xml:space="preserve"><value>Le résultat est sur la branch {0}</value></data>` — the English noun 'branch' with a French article, not the correct French noun 'branche'. The DE line 945 is `Das Ergebnis liegt auf Branch {0}` as claimed (secondary, style-level point, correctly flagged as weaker by the finding). Reachability confirmed: RunProgressViewModel.OutputBranchNote (line 180-181) calls `_localization.Format("Run_Output_Branch", OutputBranchName!)` gated only by HasOutputBranch (line 175, true whenever OutputBranchName is non-empty, set from outcome?.BranchName at line 341 — i.e. whenever a run outcome carries a branch name, which is the worktree-mode deliverable path). RunProgressPanel.xaml:68 binds `Text="{Binding OutputBranchNote}"`, so the string is actually rendered in the UI, not dead code. Checked phase3-workflow-plan.md D5b (line 39) and G4 (line 325): the spec requires 'the panel must say plainly that the output is on a branch' but says nothing about tolerating an English loanword in French, and no glossary/deliberate-decision language appears anywhere near G4/D5b about keeping 'branch' untranslated. In fact the codebase's own French-localization convention cuts against a 'deliberate loanword' defense: ActionCard_Action_Commit is translated to 'Valider' (not left as 'commit'), showing the established practice is to translate git jargon into French rather than keep English terms — 'branch'/'branche' should follow the same pattern. No test enforces string content (only key-parity per the localization-drift memory note), so nothing would already have caught this. The claim holds and the scenario is reachable via ordinary worktree-mode completion.
