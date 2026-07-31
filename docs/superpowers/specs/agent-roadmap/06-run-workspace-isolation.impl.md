# Batch 06 — Run workspace isolation · IMPLEMENTATION SPEC

Executable spec derived from [`06-run-workspace-isolation.md`](06-run-workspace-isolation.md) and
[`phase3-workflow-plan.md`](phase3-workflow-plan.md), plus a full re-read of every seam it touches.
Branch: `feature/agent-run-spine`, authored against `53cd552`. **Design step only — no production code was
written for this document.**

This spec covers **work groups G1–G5** of the plan's §5, i.e. all of Batch 06. Batch 07 (G6–G10) is a
separate later invocation that reads this file off disk with no conversational context — §0.7 is the
inventory it needs.

> **RECONCILER STATUS — measured 2026-07-31, and the "no production code was written" line above is now
> HISTORY, not the tree.** Part of this spec has shipped:
> `70400aa` **G1** (guard carve-out + `RunContext.WorkspaceRoot` + the ctx-first verifier root),
> `4092765` **G2** (the `Initialize` flip at both launcher call sites),
> `00198f6` **G3** (the worktree | copy provisioner), `3c28e84` **G4** (promotion in the terminal settle + the
> publish affordance — committed while this reconcile pass was running). G4's builder appended its own
> *"ANNOTATED BY G4'S BUILDER"* block to §7 — read it, it corrects six things in §7/§9.4. **G5** had not
> started. `git log --oneline` is the current answer; this list is a floor, not a ceiling.
> Consequences for anyone reading this file cold:
> 1. **Every line number here is provenance, not an address — grep the symbol.** The five commits above
>    rewrote or created ~20 files, and `RunProgressViewModel.cs`, `RunProgressPanel.xaml` and all three
>    `ViewStrings*.resx` moved more than once.
> 2. **The gate's total is no longer 2424.** Each landed group added tests. The bar is `failed: 0`, measured
>    by stash → rerun on the tree you were handed, never read off a past count.
> 3. `07-subagents-multipersona.impl.md` **§0.10 is the anchor audit** for the same seams, re-measured against
>    this tree. Where a number here and a number there disagree, §0.10 is later.

The owner's nine decisions (plan §1, D1–D8 + D5b) are **settled inputs**. They are cited as "plan D5",
"plan D8" and never re-opened. Decisions this spec makes are numbered **B1…B17** so the two sets cannot
be confused.

Gate for every implementing agent:

```
dotnet build -t:Rebuild -v:n                 # 0 Error(s), 0 Warning(s)
dotnet build -t:Rebuild -c Release -v:n      # 0 Error(s), 0 Warning(s)
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"
                                             # failed: 0   (2424 total at df0841a)
```

Read the warning count off MSBuild's `N Warning(s)` summary line — at `-v:n` every warning prints twice, so
grepping the log double-counts. Sanity-check that the rebuild was genuine by counting `CoreCompile`/`Csc`
invocations (expect 4). This batch is test-heavy and **186 of the historical 194 warnings were xUnit
analyzer warnings in the test project**: use `Assert.Empty` not `Assert.Equal(0, x.Count)` (xUnit2013), and
never `.Result`/`.Wait()` in a test body (xUnit1031). New tests must add **zero** warnings.

Two known intermittents — re-run the class isolated before calling either a regression:
`TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock` and
`AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes`.

---

## 0. Corrections — read this first

### 0.0 The batch file's "Key seams" omits both ship-blockers, so the batch is FIVE groups, not two

`06-run-workspace-isolation.md:19-27` lists four seams and none of them is the guard or the verifier. Doing
what that file says — flip `Initialize(workspaceRoot: runRoot)` and add "a promotion step" — produces a run
in which **every file tool errors** (§0.2a) and, once that is fixed, a run that ends `Completed + unverified`
**on every single run** (§0.2b). The `Initialize` flip is therefore the **third** group, not the first.

### 0.1 Anchors re-measured at `53cd552` (the plan's §3.1 correction, re-verified)

| Claim in `06-…md` | Measured |
|---|---|
| `HeadlessTurnExecutor.Initialize` at `:91` | **`:104-116`** (signature at `:104`, body `:110-115`) |
| launch `workspaceRoot: null` at `HeadlessRunLauncher.cs:181` | **`:209`**. `:181` is `CancellationTokenSource.CreateLinkedTokenSource` |
| resume `workspaceRoot: null` at `:289` | **`:339`**. `:289` is a comment above the grant-envelope restore |
| — | workspace create: `HeadlessRunLauncher.cs:161-174`; idempotent resume re-create: `:313-315` |
| — | sweep predicate: `:451`; `OnChatsChanged`: `:480-498`; `TryDeleteDirectory`: `:514-525` |
| — | runs base default: `:107-108` |
| — | file-tool base-root dispatch: `FilesToolHandler.cs:170-171`, subpath narrowing `:182` |
| — | verifier root: `AgentVerifier.cs:210`; the now-false ownership comment: `:258-265` |
| — | interactive `Planned` create: `ChatSessionManager.cs:772-773`; `LiveTurnExecutor` construction `:788-790` |
| — | interactive per-step ambient + chip sink: `ChatSession.cs:661-668` |

**Do not grep `:181`, find a CTS, and conclude the spec is wrong about the whole batch.** The substance of
the batch file (both call sites pass null) is right.

### 0.2 The two ship-blockers, restated as prerequisites

**(a) The guard blocks the runs directory.** `SensitivePathGuard.BuildBlockedRoots` blocks
`%LOCALAPPDATA%\Pia` wholesale (`:74`, `AddEnv("LOCALAPPDATA", "Pia")`), and `BuildAllowedExceptions`
(`:103-107`) carves out exactly one island — `AssistantWorkspace.LegacyWorkdir`. `_runsBaseDir` defaults to
`%LOCALAPPDATA%\Pia\runs` (`HeadlessRunLauncher.cs:107-108`), i.e. inside the blocked root and not an
exception. `IsBlocked` runs **after** containment resolves, at eight sites in `FilesToolHandler`
(`:308`, `:477`, `:689`, `:803`, `:984`, `:1050`, `:1230`, `:1253` — the plan named six; the two
enumeration-time ones at `:308`/`:477` are the `list_files`/`search_files` walks), and carve-outs are
checked **before** the denylist (`:37-46`). So the flip without the carve-out gives a run whose every
read, write, delete, list and search is rejected with *"the path is inside a protected system or
application data directory"*. **G1 lands the carve-out before G2 flips anything.**

**(b) The verifier probes the wrong root.** `TaskAmbient.Current` is set per step and restored in the
step's `finally` (`HeadlessTurnExecutor.cs:287-291` set, `:312-316` restore; the comment at `:166-170`
says why it cannot be set in `BeginRunAsync`). Verify runs from the orchestrator **outside** any step flow
(`AgentRunOrchestrator.cs:211-212` → `SafeVerify` → `AgentVerifier.VerifyAsync`), so `TaskAmbient.Current`
is null there and `AgentVerifier.cs:210-211`'s `ambientRoot ?? settings.AssistantFilesFolder` silently
falls back to the settings folder. Once steps write into the workspace, every declared `ExpectedArtifact`
reports NOT FOUND, the verdict fails, the shared replan budget burns (`:219-235`), and the run terminates
`Completed` + `"unverified"`. **G1 adds `RunContext.WorkspaceRoot` and lands it before G2 too.**

### 0.3 CODE RIGHT, SPEC WRONG — `Provisioner` **is** an allowlisted suffix

Plan §4 R6 reads as if the naming space were hostile. Measured: `NamingConventionTests.cs:32-38`'s
`allowedSuffixes` contains `"Provisioner"`, `"Session"` and `"Resampler"` (promoted from name exemptions to
suffixes in an earlier pass). What is **absent** is `Promoter`. So plan R6 is true only about the literal
name `RunWorkspacePromoter`. This spec still uses `RunWorkspaceService` (B4) — one type, one lifecycle —
but nobody should relitigate the name on architecture-test grounds.

Also load-bearing and easy to trip: `NamingConventionTests.RecordTypes_MustNotLiveInTheServicesRootNamespace`
(`:86-99`). Every new record this batch adds lives in **`Pia.Services.Interfaces`**, never in `Pia.Services`.

### 0.4 CODE RIGHT, SPEC WRONG — `FileRef` chips are **not persisted**

Plan D8 says "no persisted chat content is rewritten", which reads as a constraint to honour. Measured: it
is **already trivially true**. `AssistantMessage.FileRefs` (`AssistantMessage.cs:46`) is an in-memory
`ObservableCollection` on a runtime view model; `git grep FileRef -- src/Pia.Wpf` returns only
`AssistantMessage`, `ChatMessageExtras`, `ChatSession.cs:308`/`:663`, `ChatSessionManager.cs:858`,
`PiaFileChip` and `PiaAssistantMessage.xaml:120`. There is **no** mapping in `AssistantChatService` or
`SyncMapper` — chips vanish on chat reload. Consequence, and it is the whole justification for B14: D8 is a
**live-session** concern only, so the redirect may be process-local state and needs no schema, no DTO and no
migration.

### 0.5 The R10 single-turn fallback is a SECOND terminal path, with the OPPOSITE order

`AgentRunOrchestrator.cs:95-121` returns early and **never reaches** the terminal-settle block at
`:239-250`. Worse, its success arm calls `SafeComplete` at `:117` **before** `SafeEndRun` at `:119` — the
reverse of the main path (`SafeEndRun` `:246` then `SafeComplete` `:247`). There is no verify on that arm at
all, so the plan's "drain → verify → promote → `CompleteAsync`" ordering does not map onto it.

This is the single most likely silent hole in G4, **and it is the well-trodden test path**: the
`FakePlanner` in both `HeadlessRunLauncherTests.cs:44-58` and `HeadlessTurnExecutorTests` returns
`PlanResult.Fallback`, so any promotion fact written against the launcher harness exercises exactly this
arm. G4 must insert promotion on **both** terminal paths — before `:117` on the fallback arm, and between
`:246` and `:247` on the main arm (B8).

### 0.6 The NON-SHIPPABLE window: green ≠ shippable between G2 and G3

Each group leaves the **test gate** green, but the product is not shippable at every boundary:

| After | State of an unattended run |
|---|---|
| **G1** | Unchanged (the root is still null). **Safe cut point.** |
| **G2** | Writes into an EMPTY `runs\<id>`: it can no longer read the user's existing files, and nothing promotes, so the deliverables sit in a directory the 30-day sweep deletes. **A functional regression. Do not ship here.** |
| **G3** | Can read (the workspace is provisioned from the source root) but still nothing promotes. **Do not ship here.** |
| **G4** | Complete for unattended runs. **Safe cut point.** |
| **G5** | Complete for both surfaces. **The intended stopping point.** |

If the loop stops between G2 and G3 inclusive, say so plainly in the handoff rather than reporting "gate
green".

### 0.7 Authoritative inventory of what Batch 06 changes UNDER Batch 07

Batch 07 (G6–G10) is written against the tree this batch leaves behind and both batches touch
`AgentRunOrchestrator`, `HeadlessRunLauncher`, `HeadlessTurnExecutor` and `LiveTurnExecutor`. What 06
changes under 07:

| Seam | Change | Why 07 cares |
|---|---|---|
| `RunContext` | new `WorkspaceRoot { get; set; }` (G1) | G8's parked-parent state and G10's child dispatch both build a `RunContext`; a child's context must carry the **parent's** root or the child writes outside the isolated workspace |
| `StepTurnSpec` | new **trailing defaulted** `string? WorkspaceRoot = null` (G5), appended after `Timeline` | G6 resolves persona/provider per step and touches `BuildSpec`; keep the member |
| `AgentRunOrchestrator` ctor | new **trailing defaulted** `IRunWorkspaceService? workspaces = null` | G6 changes `RunAsync`'s signature semantics (per-step persona); ~10 hand-constructed test call sites already exist — keep every new param trailing and defaulted |
| `HeadlessRunLauncher` ctor | new **trailing defaulted** `IRunWorkspaceService? workspaces = null`, **after** `runsBaseDirOverride` | G10 adds a child slot pool to the same type |
| `LiveTurnExecutor` ctor | new **trailing defaulted** `string? workspaceRoot = null` | G6/G7 touch it |
| `ChatSessionManager` ctor | new **trailing defaulted** `IRunWorkspaceService? workspaces = null`, **after** the existing `IAgentTimelineService? agentTimelineService = null` (G5) | G6 appends `StepPersonaResolver? stepPersonas = null` to the same ctor — it goes **after** this one. One hand-constructed test site (`ChatSessionManagerTests.cs:84`) passes positionally and omits trailing optionals |
| `RunProgressViewModel` ctor | new **trailing defaulted** `IRunWorkspaceService? workspaces = null` as the **7th** param, after `timelineService` (G4) | G7 appends `IPersonaService? personaService = null` — which is therefore the **8th** param, not the 7th |
| `RunProgressViewModel.RefreshAsync` | gains a **terminal-only** off-thread `DescribeAsync` block whose result is applied through `_uiContext.Post` (G4/B15) | G7 loads a persona map and G10 loads the children list in the same method — add beside it; do **not** fold the outcome read into the every-`RunChanged` path |
| `RunProgressPanel.xaml` | new Publish button beside Continue (near `:33-35`) + two note lines (G4) | G7 edits the avatar at `:66-68` and G10 appends an expander — 06 shifts those line numbers. **Locate by markup, not by line number.** |
| `HeadlessTurnExecutor` | `Initialize`'s `workspaceRoot` is now non-null in production; `BeginRunAsync` assigns `ctx.WorkspaceRoot` | G6 stops caching `_provider` in the same method |
| new `IRunWorkspaceService` / `RunWorkspaceService` | registered singleton | a child run must **inherit the parent's workspace root**, not provision its own (§13.4) |
| `AgentRunState` | **untouched.** No new ordinal. `Paused(4)` remains Batch 08's | G8 appends the parked-parent state; 06 takes nothing |
| `AgentRuns` schema / DTOs | **untouched.** No migration, no `PolicyJson` change, nothing crosses the sync wire | — |

---

## 1. Verified recon (re-read 2026-07-31; cite these, not the batch brief)

| # | Fact | Location |
|---|---|---|
| R1 | **One** dispatch point resolves the base root for all five file tools: `ambientRoot = TaskAmbient.Current?.WorkspaceRoot; baseRoot = ambientRoot is not null ? NormalizeWorkspaceRoot(ambientRoot) : _currentFolder`, then `root = ResolveEffectiveRoot(baseRoot, TaskAmbient.Current?.WorkingSubpath)`. Reads and writes share it — no read/write divergence. | `FilesToolHandler.cs:170-182` |
| R2 | `ResolveEffectiveRoot(baseRoot, subpath)` takes the base as a parameter and does not care where it came from; a subpath that escapes containment or does not exist **falls back to the base root** and never widens. | `FilesToolHandler.cs:203-216` |
| R3 | Two deliberate non-ambient consumers stay as they are: `ListRelativeFiles` (`@Files` autocomplete, `:319-328`, uses `_currentFolder` + `ActiveUiWorkingSubpath`) and `ReadPromptPreviewAsync` (`:787`). Both run outside any turn. | as cited |
| R4 | MCP/plugin-routed file calls wrap the same `IFilesToolHandler` singleton, so they inherit 06 for free — **no separate MCP work.** | `BuiltInPluginHandler.cs:159` |
| R5 | `TaskContext` is `readonly record struct(Guid? TaskId, string? WorkingSubpath, Action<FileTouch>? OnFileTouched = null, string? WorkspaceRoot = null)` on an `AsyncLocal<TaskContext?>`. Per-turn isolation is already correct. | `TaskAmbient.cs:34-57` |
| R6 | `WorkspaceRoot` has exactly **two** production readers today: `FilesToolHandler.cs:170` and `AgentVerifier.cs:210`. Exactly **one** writer: `HeadlessTurnExecutor.cs:291`. | `git grep WorkspaceRoot -- src/Pia.Wpf` |
| R7 | Carve-outs are checked **before** the denylist, and both sides are canonicalized through the same `SafeCanonical`. `SafeCanonical` canonicalizes only when `Directory.Exists(full)`; a missing root stays **lexical**. | `SensitivePathGuard.cs:37-46`, `:115-128` |
| R8 | `AssistantWorkspace` exposes `DefaultRoot`, `LegacyWorkdir`, `VaultSubfolderName`, `VaultRootFor`. `LegacyWorkdir` is built from `Environment.GetFolderPath(LocalApplicationData)` while `BuildBlockedRoots` uses `Environment.GetEnvironmentVariable("LOCALAPPDATA")` — a pre-existing asymmetry the OrdinalIgnoreCase prefix match absorbs. | `AssistantWorkspace.cs:33-35`, `SensitivePathGuard.cs:68-74` |
| R9 | `SensitivePathGuard`'s own doc comment says *"The vault gets no entry here — full file-tool access by design."* | `SensitivePathGuard.cs:16` |
| R10 | Workspace lifecycle today: created at launch (`Path.Combine(_runsBaseDir, run.Id.ToString())` + `CreateDirectory` + `Canonicalize`), **idempotently re-created on resume**, and a setup failure settles the run via `FailAsync` so it never dangles non-terminal. | `HeadlessRunLauncher.cs:161-174`, `:313-315` |
| R11 | The sweep predicate is exactly `remove = run is null \|\| Directory.GetLastWriteTimeUtc(dir) < UtcNow - 30d`. **Zero** `AgentRunState` awareness, zero promotion awareness. It enumerates `Directory.GetDirectories` only and `continue`s on any name that is not a parseable `Guid`. | `HeadlessRunLauncher.cs:440-457` |
| R12 | `OnChatsChanged` is a **synchronous** event handler that deletes a run's workspace inline, and only for run ids **this session** launched (`_runsByChat` is in-memory, never reloaded). | `HeadlessRunLauncher.cs:480-498` |
| R13 | `runsBaseDirOverride` is a trailing optional ctor param and `HeadlessRunLauncherTests` passes it **by name** (`:164-166`), with an explicit comment that it must never be the real `%LOCALAPPDATA%\Pia\runs` because `RunStartupSweepAsync` **deletes directories**. | `HeadlessRunLauncher.cs:97`, `HeadlessRunLauncherTests.cs:66-70`, `:164-166` |
| R14 | `FilesToolHandlerWorkspaceEscapeTests` roots `_interactiveRoot`/`_runRoot`/`_outside` under `Path.GetTempPath()`, which is outside every blocked root — **the existing regression suite structurally cannot see the guard collision** (plan R1). | `FilesToolHandlerWorkspaceEscapeTests.cs:137-141` |
| R15 | `GitToolHandler` carries its **own** copy of the resolution pattern and never reads `WorkspaceRoot`: `baseRoot = _currentFolder` (`:138`), `ResolveEffectiveRoot(baseRoot, TaskAmbient.Current?.WorkingSubpath)` (`:148`, own copy at `:675-688`). | as cited |
| R16 | `GitToolHandler` has **two more** `_currentFolder` readers that are not the dispatch point: `IsInsideSandbox` (`:598-613`, documented as the runtime-re-point **TOCTOU re-guard**, deliberately re-run inside the deferred `Execute` closure) and `GetCeilingDirectory` (`:662-670`, the parent of the sandbox, exported as `GIT_CEILING_DIRECTORIES`). | as cited |
| R17 | `GIT_CEILING_DIRECTORIES` stops upward `.git` discovery. With cwd under `%LOCALAPPDATA%\Pia\runs\<id>` and a ceiling of `Documents\`, the ceiling **does not apply at all** — discovery would walk `runs → Pia → Local → AppData → %USERPROFILE%` and could bind a repo the user keeps in their profile. | `GitProcessRunner.cs:123-126` |
| R18 | `ResolveContainedRepoAsync` already runs `git rev-parse --show-toplevel` on **every** call as its is-repo check (gate on exit code, never on the literal `false`), and `IGitProcessRunner.IsGitInstalled` is already the availability gate. There is **no `git worktree` in the agent tool surface** — provisioning is app-side and adds no agent capability. | `GitToolHandler.cs:527-582`, `:100-129` |
| R19 | The interactive `Planned` run is a bare `CreateAsync` — no directory is created anywhere on that path. One `GetSettingsAsync()` is already awaited at `:757` inside the same branch (Batch 04 D11). | `ChatSessionManager.cs:744-798` |
| R20 | `StepTurnSpec` is a positional record whose last two members (`Policy`, `Timeline`) are already appended-and-defaulted, and both construction sites use **named** arguments. A third appended defaulted member breaks nothing. | `IAgentTurnExecutor.cs:34-68`, `LiveTurnExecutor.cs:136-154` |
| R21 | The interactive per-step ambient is `new TaskContext(spec.RunId, WorkingDirectory, touch => …FileRef(touch.AbsolutePath, …))` — WorkspaceRoot defaulted to null. The ordinary (non-run) turn at `:307` is a separate construction and must stay untouched. | `ChatSession.cs:661-668`, `:303-313` |
| R22 | `LiveTurnExecutor.BeginRunAsync` assigns `ctx.WorkingSubpath = _session.WorkingDirectory` on the UI thread, for exactly the verifier reason `RunContext.WorkingSubpath` exists. | `LiveTurnExecutor.cs:54-70`, `RunContext.cs:50-58` |
| R23 | `RunProgressViewModel` is hand-constructed **positionally, outside DI** at `AssistantViewModel.cs:397`; its Batch-03 dependency was added as a **trailing** ctor param with a null default. It captures a raw `SynchronizationContext` (`:176`) and marshals every bound mutation through `_uiContext.Post` (G3). It has a precedent for a service-calling command: `ContinueCommand` → `IAgentRunResumeService`. | `RunProgressViewModel.cs:163-177`, `:260-277` |
| R24 | `PiaFileChip` is a `UserControl` with no DI whose two handlers call the static `ShellLauncher`. It is instantiated inside a **deferred `ItemsControl.ItemTemplate`** that no test materializes. | `PiaFileChip.xaml.cs:82-86`, `PiaAssistantMessage.xaml:118-125` |
| R25 | `SafeDirectoryMove` is the house precedent for copy → verify → delete with rollback (`File.Copy(overwrite: true)`, size+SHA256 verify, best-effort source delete). It has **no** ignore matcher and **no** cap. | `Infrastructure/Vault/SafeDirectoryMove.cs` |
| R26 | `SandboxIgnore.ForRoot(root)` yields the matcher `list_files`/`search_files` prune with (`.git`, `bin`, `obj`, `node_modules` + `.gitignore`/`.piaignore`). | `FilesToolHandler.cs:253`, `:335` |
| R27 | `ToolAutonomy.Resolve` is provably path-independent: `ToolGateInput` has no path/root field and `ToolClassifier` switches on plugin **name**. 06 changes *where* `write_file` lands, never *whether* it is gated — **no gate work in 06.** | `Services/ToolAutonomy.cs`, `Models/ToolGateEnums.cs` |
| R28 | `AgentRunBracketTests` (`:38`) scans types assignable to `IHeadlessRunLauncher`/`IBackgroundAssistantTurnRunner`, asserts ≥ 2 exist and that each injects `IExecutingRunStore`. 06 adds no executor type, so it is unaffected. | `AgentRunBracketTests.cs:38` |
| R29 | `ToolAutonomyRuleTests` (`:34`) pins the **exact count** of `ToolAutonomy.Resolve`/`IsMcpTool`/`IsAutoApproveEligible` calls per gate file. 06 adds none. | `ToolAutonomyRuleTests.cs:34` |
| R30 | There is no `SensitiveError`. `SafeLog` exposes `SensitiveTrace/Debug/Information/Warning` only — `SensitiveWarning` is the highest DEBUG-erased severity. | `src/Pia.Wpf/Logging/SafeLog.cs` |

---

## 2. Decisions

### B1 — The runs root becomes a shared constant on `AssistantWorkspace`, and the guard carves out the whole tree

```csharp
/// <summary>
/// Base directory for every per-run agent workspace: <c>%LOCALAPPDATA%\Pia\runs</c>. Lives here, beside
/// <see cref="LegacyWorkdir"/>, because it is the SECOND island <see cref="SensitivePathGuard"/> has to
/// carve out of the otherwise-blocked <c>%LOCALAPPDATA%\Pia</c> tree — the guard and the launcher must not
/// be able to disagree about where it is (Batch 06 B1). <c>HeadlessRunLauncher</c> uses this as its default
/// (an injected override keeps tests off the real user folder), and <c>RunWorkspaceService</c> uses it too.
/// </summary>
public static string RunsRoot { get; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pia", "runs");
```

`HeadlessRunLauncher.cs:107-108`'s inline `Path.Combine(...)` becomes `AssistantWorkspace.RunsRoot`.
`BuildAllowedExceptions` returns **both** islands.

Rejected: **a constant private to `SensitivePathGuard`.** Then the guard and the launcher each own a copy of
the same path and a relocation silently re-blocks every run. Rejected: **carving out only
`runs\<runId>` per run.** `BuildAllowedExceptions` is a `static readonly` array computed once per process;
a per-run entry would need mutable static state on a security primitive, and containment (base root =
this run's directory) already confines a run to its own workspace — the carve-out only removes the
denylist veto, it does not widen containment.

### B2 — Carve-outs canonicalize with a MISSING TAIL; blocked roots keep today's lexical fallback

`SafeCanonical` canonicalizes only when the directory exists (R7). `%LOCALAPPDATA%\Pia\runs` **does not
exist on a fresh install**, and `AllowedExceptions` is computed once at first `IsBlocked` — which in the
test process may fire from an unrelated test long before any runs directory exists. A lexical exception
compared against an already-canonicalized candidate is a **fail-CLOSED** mismatch: the carve-out misses and
every file tool in the run dead-ends. For a *blocked* root the same lexical fallback is fail-open and is
documented as correct (`SensitivePathGuard.cs:121-125`).

So `BuildAllowedExceptions` gets its own canonicalizer:

```csharp
/// <summary>
/// Canonicalizes an allowed island even when it does not exist yet: walks up to the deepest EXISTING
/// ancestor, canonicalizes THAT, and re-appends the missing tail. Deliberately NOT shared with
/// <see cref="SafeCanonical"/>, because the two have opposite failure directions — a lexical BLOCKED root
/// fails open (nothing resolves through a missing directory, so there is nothing to block), while a lexical
/// ALLOWED island fails CLOSED: the prefix match against the resolver's canonical candidate misses and the
/// island stays blocked, dead-ending every file tool in a run whose workspace has not been created yet
/// (Batch 06 B2). %LOCALAPPDATA%\Pia\runs does not exist on a fresh install and this array is built once
/// per process, so the missing-tail case is the NORMAL case, not an edge one.
/// </summary>
internal static string? CanonicalizeAllowedIsland(string path)
```

Contract: `CanonicalizeAllowedIsland(@"C:\ex\Pia\runs")` where only `C:\ex` exists returns
`SafeFolderPath.Canonicalize(@"C:\ex") + @"\Pia\runs"`; returns null only when nothing on the path can be
resolved. `internal` so a test can pin it order-independently (T-G1-3) — a test cannot control static-init
order within the process, so the *helper* is what gets the fact.

### B3 — `RunContext.WorkspaceRoot`, and the verifier prefers ctx over the ambient

Symmetric with `WorkingSubpath` (R22 / `RunContext.cs:50-58`):

```csharp
/// <summary>
/// The isolated per-run workspace root this run's file tools resolve against
/// (<c>TaskContext.WorkspaceRoot</c>), or null when the run writes at the configured assistant-files
/// folder. Set ONCE by the executor in <c>BeginRunAsync</c>, for the same reason
/// <see cref="WorkingSubpath"/> is: the per-step ambient that carries it is restored in the step's
/// <c>finally</c>, so by verify time — which runs on the ORCHESTRATOR thread, outside any step flow —
/// it is gone. Without this the artifact probe stats the settings folder for every declared artifact of
/// a run that wrote into its workspace, reports confident false NOT FOUNDs, burns the shared replan
/// budget and terminates the run Completed+"unverified" — on every run (Batch 06 B3).
/// It is ALSO the value the terminal-settle path promotes from (B8).
/// </summary>
public string? WorkspaceRoot { get; set; }
```

`AgentVerifier.cs:210` becomes:

```csharp
            // ctx FIRST: verify runs on the orchestrator thread where the per-step ambient is already
            // restored (B3). The ambient read is kept as the second choice for any caller that DOES verify
            // inside a step flow; the settings folder stays the last resort.
            var ambientRoot = ctx.WorkspaceRoot ?? TaskAmbient.Current?.WorkspaceRoot;
```

`AgentVerifier.cs:258-265`'s ownership comment is **now false** and is corrected in the same commit (plan
R3): it currently asserts *"unattended runs write there, so `WorkspaceRoot` is null in production and the
settings folder IS the root the step writes landed in."*

Both executors assign it: `HeadlessTurnExecutor.BeginRunAsync` sets `ctx.WorkspaceRoot = _workspaceRoot`
next to the existing `ctx.WorkingSubpath = null` (`:129`); `LiveTurnExecutor.BeginRunAsync` sets it in G5.
**Executor parity is a requirement, not a nicety** — a promotion that only fires for Headless is a defect.

### B4 — ONE interface, ONE implementation, TWO strategies

```
src/Pia.Wpf/Services/Interfaces/IRunWorkspaceService.cs   (interface + result records)
src/Pia.Wpf/Services/RunWorkspaceService.cs               (the one implementation)
```

```csharp
/// <summary>How a run's workspace was provisioned. Serialized by NAME into the workspace metadata
/// document, so this is APPEND-ONLY: never renumber, never rename a member. A name a build does not know
/// reads back as <see cref="None"/>, which means "no isolation" — the restrictive direction.</summary>
public enum RunWorkspaceMode { None = 0, Copy = 1, Worktree = 2 }

public sealed record RunWorkspace(Guid RunId, string Root, RunWorkspaceMode Mode, string SourceRoot, string? BranchName);
// Phase 3 fix pass: gained a trailing, defaulted `bool RetainWorkspace = false`. See the annotation on B8.
public sealed record RunPromotionResult(RunWorkspaceMode Mode, int Promoted, int Skipped, int Conflicts, string? BranchName);
public sealed record RunWorkspaceOutcome(RunWorkspaceMode Mode, string? BranchName, bool HasUnpublishedFiles);

public interface IRunWorkspaceService
{
    string RootFor(Guid runId);
    Task<RunWorkspace?> ProvisionAsync(Guid runId, string? workingSubpath, CancellationToken ct);
    Task<RunPromotionResult?> PromoteAsync(Guid runId, CancellationToken ct);
    Task<RunWorkspaceOutcome?> DescribeAsync(Guid runId, CancellationToken ct);
    Task TearDownAsync(Guid runId, CancellationToken ct);
    Task SweepOrphanMetadataAsync(CancellationToken ct);
}
```

Every method returns null / does nothing rather than throwing: this is bookkeeping and must never fail a
run (standing guardrail). `ProvisionAsync` returning `null` means **"no isolation — today's behaviour"**,
which every caller must handle by passing `workspaceRoot: null` onward.

Dependencies: `IGitProcessRunner`, `ISettingsService`, `ILogger<RunWorkspaceService>`, plus a trailing
`string? runsBaseDirOverride = null` mirroring the launcher's param name so a test can point both at the
same temp directory. Registered `services.AddSingleton<IRunWorkspaceService, RunWorkspaceService>();` next
to the launcher registrations (`Bootstrapper.cs:496-499`) or `DiRegistrationTests` fails.

Rejected: **two types behind one interface (a worktree provisioner and a copy provisioner).** Plan R16's
mitigation is *"the provisioner owns create AND teardown symmetrically"*; two types is exactly the shape
that lets a create and its teardown drift, and the teardown asymmetry (`git worktree remove` vs `rmdir`)
is the failure this batch most needs to prevent. Rejected: **splitting promotion into its own service.**
Promotion needs the mode, the source root and the provisioning instant — i.e. the provisioner's own
metadata; a second type would either duplicate the reader or read the other's file.

### B5 — Workspace metadata is a sibling JSON file OUTSIDE the sandbox, at `v:1`, additive

`<runsBase>\<runId>.workspace.json`:

```jsonc
{ "v": 1,
  "mode": "Copy",
  "sourceRoot": "C:\\Users\\me\\Documents\\Pia Assistant",
  "mainWorktree": null,
  "branch": null,
  "provisionedAtUtc": "2026-07-31T09:14:22.1234567Z",
  "degraded": false }
```

Written once by `ProvisionAsync`, read by `PromoteAsync` / `DescribeAsync` / `TearDownAsync` /
`SweepOrphanMetadataAsync`. camelCase, `v:1`, **additive** members only — the same discipline the grant
envelope documents (`HeadlessRunLauncher.cs:46-56`), for the same reason: this file is read by a build that
may be older or newer than the one that wrote it (a resume happens in a different process).

Why a **sibling** and not `<runRoot>\.pia-run\meta.json`: the run's file tools are contained to `<runRoot>`,
so a file inside it is model-writable — the agent could `write_file(".pia-run/meta.json", "junk")` and
steer its own promotion. A sibling in the runs base is unreachable from inside the sandbox, and the runs
base is not itself a sandbox root.

Why it exists at all, rather than in-memory state on the launcher: a **resume runs in a different process**
(`HeadlessRunLauncher.ResumeAsync` re-creates the workspace idempotently, R10), and the publish affordance
(plan D3) can be clicked days later. Promotion and teardown both need the mode, the destination and the
provisioning instant after a restart.

Restrictive degrades, all logged, none throwing:

| Metadata | `PromoteAsync` | `TearDownAsync` |
|---|---|---|
| absent / unparseable / `v != 1` | **skip promotion entirely**, keep the workspace, log a warning with the run id only | plain recursive `rmdir` + delete the metadata; **no** `worktree prune` (nothing says where the repo is) |
| `mode: Worktree` | no file copy at all (B10) | `git worktree remove --force` → fallback `rmdir` + `git worktree prune` |
| `mode: Copy` | the B9 diff-copy | plain recursive `rmdir` |

An unreadable metadata file must never make promotion copy *everything*: "promote nothing and keep the
files where they are" is recoverable (the user still has the publish offer and the folder); "overwrite the
user's assistant folder from a workspace we cannot reason about" is not.

### B6 — Copy mode copies the source tree IN. This is not optional.

The batch file's goal sentence ("a run writes into its isolated workspace") reads as if an empty directory
were enough. It is not: an unattended run today reads the user's existing files through the same tool set it
writes with, so an empty workspace silently breaks *"summarise notes.md"* — a Rank-1-visible functional
regression, and the reason plan D5 says "plain copy" rather than "fresh directory".

The **source root** is:

- headless: `settings.AssistantFilesFolder` (the run has no subpath — `BeginRunAsync` sets
  `ctx.WorkingSubpath = null` deliberately, `HeadlessTurnExecutor.cs:126-129`);
- interactive (G5): `settings.AssistantFilesFolder` narrowed by the chat's `WorkingDirectory` via
  `SafeFolderPath.TryResolveInsideAllowingAbsolute` + `Directory.Exists` — i.e. exactly `ResolveEffectiveRoot`'s
  rule, fail-safe to the base root (R2).

Consequence, stated so nobody re-derives it: because the workspace root corresponds 1:1 to the **narrowed**
source root, an isolated run's ambient `WorkingSubpath` must be **null** and its `ctx.WorkingSubpath` must
be **null** too. Narrowing twice would look for `<runRoot>\<subpath>`, which does not exist. This is the
same statement `HeadlessTurnExecutor` already makes; G5 makes it for the live path explicitly rather than
relying on `ResolveEffectiveRoot`'s fallback to do it by accident.

**What is excluded from the copy**, each for a stated reason:

1. **The memory vault** (`AssistantWorkspace.VaultSubfolderName`, i.e. `<source>\Vault`). `MemoryService`,
   the vault watcher and the ingest indexer own that tree and write to it through their own paths, not
   through the file tools. A copy-in/copy-back cycle would fight the indexer, and the run's copy would be
   stale the moment `MemoryService` writes. The run keeps full memory access through the memory tools
   (`recall`/`remember`/`browse_index`/`read_topic`/`read_source`), which do not read `WorkspaceRoot` and are
   untouched by this batch. **This narrows a documented deliberate property** (R9: *"The vault gets no entry
   here — full file-tool access by design"*), so `SensitivePathGuard`'s comment is amended in the same
   commit that lands the exclusion (R3-style), and `list_files` inside an isolated run simply will not show
   `Vault\`. Grep before you start: no test pins vault reachability through the file tools
   (`grep -rn "Vault" tests/…/FilesToolHandler*.cs` returns nothing) — if one appears, scope it to the
   non-isolated path, never delete it.
2. **Everything `SandboxIgnore.ForRoot(source)` prunes** (R26): `.git`, `bin`, `obj`, `node_modules` and any
   `.gitignore`/`.piaignore` entry. Using the same matcher `list_files` uses means what the run can see in
   its workspace is exactly what it would have listed in the real folder.

**Caps and the degrade.** `MaxProvisionedFiles = 2000`, `MaxProvisionedBytes = 256L * 1024 * 1024`,
both `internal const` on `RunWorkspaceService` so a test can reason about them. Exceeding either →
`ProvisionAsync` tears the partial workspace down and returns **null** (no isolation, today's behaviour),
logging `"Run {RunId} workspace provisioning skipped: source exceeds the isolation cap ({FileCount} files, {ByteCount} bytes)"`
at `Information` — counts and an id only. Running a partial tree is the one outcome that is worse than not
isolating: the agent would see a truncated folder, "recreate" the missing files, and promotion would then
write them over the originals.

Rejected: **promote everything back unconditionally.** It rewrites files the run never touched (mtime churn
that wakes the vault watcher and the sync delta) and, worse, silently reverts a user edit made **during** the
run. Rejected: **copy nothing, promote everything.** See the read regression above.

### B7 — The promote set is decided by mtime against `provisionedAtUtc`, with a byte-identity skip and a conflict rule

`File.Copy` preserves the source's `LastWriteTime`, so a copied-in file's mtime is **older** than
`provisionedAtUtc` and a file the agent wrote is **newer**. That single durable timestamp (B5) is the whole
change-detection mechanism — no manifest, no hash index, and it survives a resume in a new process.

For each file under `<runRoot>` (ignore-pruned, vault-excluded, capped by the same constants):

| Condition | Action |
|---|---|
| `LastWriteTimeUtc <= provisionedAtUtc` | **skip** — the run did not touch it |
| destination missing | **copy** (creating directories), count `Promoted` |
| destination exists and is byte-identical (size, then SHA256) | **skip**, count `Skipped` — no mtime churn |
| destination exists and `dest.LastWriteTimeUtc > provisionedAtUtc` | **CONFLICT: skip**, count `Conflicts` — the user (or another writer) changed it during the run, and a background run must not overwrite that |
| otherwise | **overwrite**, count `Promoted` |

Deletions inside the workspace are **never** propagated: a run cannot delete a user file by promotion.
Say it in the code comment — it is the difference between "promote" and "sync", and Batch 10 owns
arbitration.

If `provisionedAtUtc` is unusable (default/epoch, or the metadata's tail is missing), degrade to
**copy only files whose destination does not exist**. Restrictive, and it still delivers new deliverables.

**Why ONE timestamp is enough across a park → resume.** Promotion is **terminal-only** (B8): it happens once,
on the run's last dispatch, and nothing promotes mid-run. `provisionedAtUtc` is written once and a resume
reuses it (B11 step 2), so the promote set after a resume is *everything either segment wrote* — which is
correct, because nothing has been promoted yet. **Batch 07's builder must not break that invariant:** if a
parent and its children ever promote at different times, a single per-workspace timestamp stops being
sufficient and the second promotion re-copies the first one's output. State the invariant at the code line,
not just here (§13.4).

Windows note to state once, not to fix: NTFS *file-system tunneling* can preserve a creation time across a
delete+recreate within ~15 s. This design does not read `Directory.GetCreationTimeUtc` at all — the
timestamp is the one we wrote — precisely so that quirk is not in the trust chain.

### B8 — Ordering: drain → verify → **promote** → `CompleteAsync`, on BOTH terminal arms

Main arm, `AgentRunOrchestrator.cs:239-250`, insert between `SafeEndRun` (`:246`) and `SafeComplete`
(`:247`):

```csharp
                await SafeEndRun(executor, run, ctx, cancelled, failed).ConfigureAwait(false);
                // B8: promote BEFORE CompleteAsync. Verify has already run against the run root (B3), so the
                // artifacts it confirmed are the files being promoted; and no RunChanged consumer can observe
                // a Completed run whose deliverables are still only in a workspace the sweep may delete
                // (plan R4/R5). Failure-isolated: a promotion fault leaves the files in the workspace and the
                // publish affordance offers them (plan D3) — it never fails an otherwise-successful run.
                await SafePromote(run, ctx, cts.Token).ConfigureAwait(false);
                await SafeComplete(run.Id, cts.Token, truncated: unverifiedTruncated, …)
```

**Fallback arm (§0.5), `:113-118`** — this one is the trap. Its success branch calls `SafeComplete` at
`:117` **before** `SafeEndRun` at `:119`, and there is no verify:

```csharp
                    else
                    {
                        if (fr.FirstMessageId != Guid.Empty)
                            await SafeRange(run.Id, fr.FirstMessageId, fr.LastMessageId, cts.Token).ConfigureAwait(false);
                        // B8, second terminal path: the R10 degrade arm returns early and never reaches the
                        // terminal-settle block below, and it settles Complete BEFORE EndRun — the opposite
                        // order. Promotion still goes before CompleteAsync. There is no verify on this arm at
                        // all (the planner degraded), so "promote what the turn wrote" is the whole contract.
                        await SafePromote(run, ctx, cts.Token).ConfigureAwait(false);
                        await SafeComplete(run.Id, cts.Token).ConfigureAwait(false);
                    }
```

`SafePromote` is a `Safe*` wrapper in the house shape:

```csharp
    /// <summary>
    /// Promote the run's isolated workspace into its destination, then tear the workspace down. Only a
    /// CLEANLY drained run promotes automatically (plan D3): a cancelled or failed run keeps its workspace
    /// so the panel can offer to publish it. No-op when no workspace service was injected or the run has no
    /// workspace root — that is the pre-Batch-06 shape and every existing orchestrator test hits it.
    /// Failure-isolated (guardrail 1): a fault logs and returns; the files stay in the workspace.
    /// </summary>
    private async Task SafePromote(AgentRun run, RunContext ctx, CancellationToken ct)
```

It calls `PromoteAsync` and, **only on a non-null result**, `TearDownAsync`. Counts + the run id at
`Information`; never a path (R30).

Cancelled/failed runs are not promoted and their workspaces are **not** torn down here.

> **SUPERSEDED by the Phase 3 fix pass (`3b66603`), and this sentence was a data-loss path as written.**
> "Only on a non-null result" is no longer sufficient: `RunPromotionResult` gained a trailing, defaulted
> `RetainWorkspace`, and `SafePromote` tears down only when it is **false**. A non-null result can mean
> "promoted, and the workspace still holds work this promotion could not move" — a copy-mode CONFLICT whose
> resolution kept the user's newer file (B7), or a worktree whose run-branch commit did not take everything.
> Deleting the workspace on those results destroyed the only remaining copy, silently, on a run reporting
> success. See the Batch 06 review findings file (Lens A 5 / Lens B 3, and Lens A 1 / Lens B 2).

### B9 — Copy mode's promotion destination is the **source root** recorded at provisioning

Not "the assistant files folder" as a constant: the metadata's `sourceRoot` is the narrowed root the tree was
copied from (B6), so `runRoot\rel → sourceRoot\rel` is a pure inverse of provisioning, and it preserves
today's destination byte-for-byte for the headless case (where `sourceRoot` *is* `AssistantFilesFolder`,
plan §2 "settled"). It also makes the interactive case correct without a second rule.

A `sourceRoot` that no longer exists or no longer resolves inside the *current* `AssistantFilesFolder`
(the user relocated the folder mid-run, or edited the setting) → **skip promotion**, keep the workspace, log
a warning. Re-anchoring a promotion onto a folder the run never saw is not a repair.

### B10 — Worktree mode promotes NOTHING: the branch is the deliverable (plan D5b)

`PromoteAsync` on `mode: Worktree` copies no file and returns
`RunPromotionResult(Worktree, Promoted: 0, Skipped: 0, Conflicts: 0, BranchName: "pia/run/<runId>")`. There
is no merge, therefore no conflict handling on an unattended path. The workspace **is** still torn down on a
clean run (the worktree directory goes; the branch stays — that is the whole point), and `TearDownAsync`
does it through `git worktree remove` (B12).

Because the output is somewhere the user would not look, the panel must **say so** (B15): a run whose
outcome is `Worktree` renders *"Output is on branch pia/run/…"*. Without that line the honest user question
is "where is my file?".

> **CORRECTED by the Phase 3 fix pass (`3b66603`). Two halves of this section were wrong as built.**
>
> **(a) "Copies no file" was true and destroyed the deliverable.** Nothing in Batch 06 or 07 ever committed
> to the run branch, and an unattended run cannot: `DefaultGrantedWrites` is `{write_file}` and
> `RunAutonomyPolicy`'s presets exclude `ToolClass.Git`, so the model's own `git_commit` is refused as
> not-granted. Meanwhile teardown ran `git worktree remove --force`. So a clean worktree run reported
> success with a passing verdict, the branch stayed byte-identical to the base commit, and the file existed
> nowhere. `PromoteAsync`'s worktree arm now COMMITS the run's work app-side through the injected
> `IGitProcessRunner` (`status --porcelain --untracked-files=all` → `add -A` → `commit --no-verify` under
> explicit `-c user.name`/`user.email`/`commit.gpgsign=false`), reports the committed entry count as
> `Promoted`, and sets `RetainWorkspace` on any arm that leaves work outside the commit — a failed commit,
> or work the user's own `.gitignore` kept `add -A` from taking (caught by a post-commit
> `status --porcelain --untracked-files=all --ignored`). Still app-side, so plan R18 holds: no new agent
> capability.
>
> **(b) B15's branch line could only render for a FAILED worktree run.** The panel reads `DescribeAsync` in a
> TERMINAL-only branch, and promotion tears the workspace down BEFORE `CompleteAsync` (B8) — deleting the
> metadata document `DescribeAsync` reads. `TearDownAsync` now leaves a torn-down STUB for worktree mode
> (additive `tornDownAtUtc` on the same `v:1` document; `mainWorktree` retained so a failed
> `worktree remove` can still be pruned later) and `DescribeAsync` answers from it. The metadata sweep ages
> the stub out on the same seven-day window a settled run's workspace gets.
>
> **KNOWN, NOT CLOSED, and recorded rather than built** (the fix would need `DescribeAsync` to learn whether
> the branch actually received a commit, which is a redesign): on the commit-FAILURE arm the workspace is
> retained, so the document is intact and un-stamped, so `DescribeAsync` still answers
> `RunWorkspaceOutcome(Worktree, meta.Branch, HasUnpublishedFiles: false)` — the panel names a branch that
> received nothing, and worktree mode offers no publish button, so there is no recovery path in the UI. The
> files are in `%LOCALAPPDATA%\Pia\runs\<runId>` for seven days. See the live-items note in the Batch 06
> review findings file.
>
> **CLOSED by the consolidation pass of 2026-08-01 (`165486e`), and the redesign turned out to be a recorded
> fact rather than a new question.** `CommitToRunBranchAsync` stamps `branchCommittedAtUtc` on the metadata
> document on the two arms where the branch really carries the run's work — the commit succeeded, or
> `status --porcelain` found nothing to commit (a branch trivially carries a run that wrote nothing) — and
> BOTH of `DescribeAsync`'s worktree arms key on it. No stamp means no branch name **and**
> `HasUnpublishedFiles: true`, so the offer appears where the false claim used to be and publishing RETRIES this
> method. Additive `v:1` member, same shape as `tornDownAtUtc`; no git process is spawned in `DescribeAsync`,
> which matters because the panel calls it off-thread on every terminal `RunChanged`.
>
> **A second arm of the same lie, which no finding named**: a FAILED or CANCELLED worktree run never promotes at
> all (plan D3), so its branch is empty too — and the pre-fix describe named it just as confidently. Lens A 2
> read that arm as the *intended* one ("the branch line therefore only ever appears for a FAILED worktree run").
> It is the same empty branch, and it now gets the same answer: no name, and an offer that commits what the
> failed run did produce.

### B11 — Provisioning: worktree when the source root is a repo we may touch, else copy

`ProvisionAsync(runId, workingSubpath, ct)`:

1. `runRoot = RootFor(runId)`; `Directory.CreateDirectory(runRoot)`; canonicalize (`SafeFolderPath.Canonicalize`)
   — same three lines the launcher does today (R10).
2. **Idempotent reuse (resume).** If `<runsBase>\<runId>.workspace.json` reads back at `v:1`, return the
   workspace it describes without re-provisioning. A resume must land in the same workspace with the same
   `provisionedAtUtc`, or the promote set becomes "everything".
3. Resolve `sourceRoot` (B6). If it is missing or unusable → return **null** (no isolation).
4. **Worktree gate**, all four must hold, else copy mode:
   - `_runner.IsGitInstalled`;
   - `git -C <sourceRoot> rev-parse --show-toplevel` exits 0 with non-empty output (R18's exact is-repo
     check — gate on the exit code, never on a literal `false`);
   - the canonicalized toplevel is **inside the current `AssistantFilesFolder`** — the same absolute
     invariant `GitToolHandler.IsInsideSandbox` enforces (R16). A repo whose toplevel sits above the
     assistant folder is one the git tools already refuse to operate on, so provisioning must not open a
     side door to it;
   - `git -C <sourceRoot> rev-parse --verify HEAD` exits 0 (a repo with **no commits** has no HEAD and
     `worktree add` cannot start from a commit — R16).
5. Worktree mode: `git -C <toplevel> worktree add <runRoot> -b pia/run/<runId>`. The branch name uses
   `run.Id.ToString()` (hyphenated), matching the directory name; `git check-ref-format` accepts it.
   `worktree add` requires the target to be empty or absent — the directory we just created is empty; if it
   is not (a resume whose metadata was lost), fall through to copy mode rather than forcing.
6. Copy mode: the bounded ignore-pruned copy of B6.
7. Write the metadata (B5). **A metadata write failure is fatal to isolation**: tear the workspace down and
   return null. Running isolated with no metadata means nothing can promote or clean up correctly.

**Degrade-to-copy fault list — explicit, because "any fault" is not implementable** (plan R16). Every one of
these takes copy mode, none of them fails the run:

| # | Fault | Detection |
|---|---|---|
| F1 | git not installed | `IGitProcessRunner.IsGitInstalled` is false |
| F2 | source root is not a repo | `rev-parse --show-toplevel` exit ≠ 0, or empty stdout |
| F3 | git could not be launched at all | `rev-parse` exit `-1` (the runner's start-failure sentinel) |
| F4 | git timed out | `GitProcessResult.TimedOut` |
| F5 | the toplevel cannot be canonicalized | `SafeFolderPath.Canonicalize` throws |
| F6 | the toplevel is outside the assistant files folder | containment check |
| F7 | the repo has no commits (unborn HEAD) | `rev-parse --verify HEAD` exit ≠ 0 |
| F8 | `worktree add` failed for any reason (branch exists, locked index, target not empty, permissions) | exit ≠ 0 |
| F9 | any exception from the git path | `catch` |

Only **copy mode's own** failure (F10: the copy throws, or the caps in B6 are exceeded) degrades further, to
**no isolation** (null). A run must never be failed because its workspace could not be provisioned in the
fancy way — plan R16 says the degrade is the mitigation.

Two behaviours to put in the release notes rather than treat as bugs: a worktree starts from a **commit**,
so uncommitted and untracked files in the user's tree are invisible to the run; and worktree mode does
mutate the user's repo (`.git/worktrees/<id>` + a branch ref) even though the working tree is untouched.

### B12 — Teardown is symmetric with provisioning, and the sweep goes through it

`TearDownAsync(runId, ct)`:

- read the metadata; `mode: Worktree` →
  1. `mainWorktree` from the metadata (recorded at provisioning precisely so this works after the directory
     is gone);
  2. `git -C <mainWorktree> worktree remove --force <runRoot>`;
  3. on failure: recursive `rmdir <runRoot>`, then `git -C <mainWorktree> worktree prune`;
  4. **never** delete the branch — it is the deliverable (B10).
- `mode: Copy` or unreadable metadata → recursive `rmdir <runRoot>`.
- always: delete `<runsBase>\<runId>.workspace.json` last, so a crash between the two leaves a metadata file
  the orphan sweep can still act on.

`HeadlessRunLauncher.TryDeleteDirectory` (`:514-525`) is replaced at its three call sites by
`_workspaces?.TearDownAsync(runId, ct)`, with the existing `TryDeleteDirectory` retained as the fallback
when no service was injected (which is what keeps `HeadlessRunLauncherTests` unmodified).

`RunStartupSweepAsync` (`:424-459`) gains two things:

1. **A state-aware retention predicate** (this is plan D3's retention rule and plan R5's mitigation, in one
   place):

```csharp
                    var run = await _agentRunService.GetAsync(runId, ct).ConfigureAwait(false);
                    // Plan D3's retention rule: an unanswered publish offer must not pin a workspace
                    // forever. A run the DB no longer has goes immediately (unchanged). A run that has
                    // SETTLED keeps its workspace only long enough for the user to publish it — a clean
                    // run's workspace was already torn down at promotion (B8), so this window really only
                    // serves failed/cancelled runs. Anything non-terminal (or a state this build does not
                    // know) keeps the original 30-day floor: it may still be resumable.
                    var age = DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir);
                    var maxAge = run?.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled
                        ? _terminalWorkspaceMaxAge      // 7 days
                        : _workspaceMaxAge;             // 30 days (unchanged)
                    remove = run is null || age > maxAge;
```

`private static readonly TimeSpan _terminalWorkspaceMaxAge = TimeSpan.FromDays(7);` beside `_workspaceMaxAge`
(`:37`).

2. **A second enumeration pass** for orphaned metadata: `SweepOrphanMetadataAsync`. `RunStartupSweepAsync`
   enumerates `GetDirectories` only (R11), so `<runId>.workspace.json` files are invisible to it and would
   accumulate forever — including exactly the orphans that carry the `worktree prune` information for a
   directory that is already gone. The pass: for each `*.workspace.json` in the runs base whose sibling
   directory does not exist, `worktree prune` against the recorded `mainWorktree` when the mode is
   `Worktree`, then delete the file.

### B13 — `OnChatsChanged` cancels first and does its delete off the handler

R12: it is a **synchronous** event handler, and teardown now spawns a git process. Plan R4 also notes that
after 06 that directory is the only copy of un-promoted work.

```csharp
        foreach (var runId in runIds)
        {
            // Plan R4: after Batch 06 this directory is the only copy of a non-promoted run's work, so never
            // delete it under a LIVE writer — cancel the dispatch first and let it unwind. Deleting the chat
            // is an explicit user act that cascades the run row away, so the files go with it by design.
            if (_inflight.TryGetValue(runId, out var entry))
            {
                try { entry.Cts.Cancel(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to cancel run {RunId} before workspace delete", runId); }
            }
            // Teardown may now spawn git (worktree remove), which must not run inline on a synchronous
            // event handler. A failed delete self-heals: the run row is gone by FK cascade, so the next
            // startup sweep sees `run is null` and removes the directory unconditionally.
            TearDownWorkspaceAsync(runId).SafeFireAndForget(_logger);
        }
```

### B14 — D8: chip opening redirects through a bounded, process-local registry

`src/Pia.Wpf/Helpers/RunWorkspaceRedirects.cs` (Helpers, so a ViewModel or a control may call it without
breaking the layer rule — the same reason `ShellLauncher` lives there):

```csharp
/// <summary>
/// Maps a file path recorded inside a run's isolated workspace to where that file ended up after promotion,
/// so an open-file chip still opens the right file once the workspace is gone (plan D8, "resolve on open").
/// <para>
/// PROCESS-LOCAL and bounded on purpose. FileRef chips are NOT persisted — they live only on the in-memory
/// AssistantMessage and vanish on chat reload (Batch 06 §0.4) — so a redirect that outlives the process
/// would have no chip left to serve. Nothing here is user-authored: both roots are app-derived, and Record
/// REFUSES a workspace root that is not under AssistantWorkspace.RunsRoot, so no model-supplied string can
/// install a redirect. Resolve does one File.Exists on the recorded path first, so during the run the chip
/// opens the workspace copy exactly as it does today.
/// </para>
/// </summary>
public static class RunWorkspaceRedirects
{
    internal const int MaxEntries = 16;

    /// <summary>Records a promotion. No-op unless <paramref name="workspaceRoot"/> is under the runs root.</summary>
    public static void Record(string workspaceRoot, string destinationRoot);

    /// <summary>The path to open: the recorded one when it still exists, else the same relative path under
    /// the destination the workspace promoted to when THAT exists, else the input unchanged.</summary>
    public static string? Resolve(string? recordedPath);
}
```

A `ConcurrentDictionary<string,string>` keyed OrdinalIgnoreCase on the canonicalized workspace root, with
the oldest entry evicted past `MaxEntries` (16 runs of chips is far more than a session shows). `Resolve`
never throws.

`RunWorkspaceService.PromoteAsync` calls `Record(runRoot, sourceRoot)` on a successful copy-mode promotion.
Worktree mode records nothing — the file is on a branch, not at a path.

Wiring, two one-line edits in `PiaFileChip.xaml.cs:82-84`:

```csharp
    private void OnOpenClick(object sender, RoutedEventArgs e)
        => ShellLauncher.OpenFile(RunWorkspaceRedirects.Resolve(AbsolutePath));

    private void OnRevealClick(object sender, RoutedEventArgs e)
        => ShellLauncher.RevealInExplorer(RunWorkspaceRedirects.Resolve(AbsolutePath));
```

Deliberately **not** inside `ShellLauncher` itself: the two ViewModel call sites (`AssistantViewModel.cs:919`,
`AssistantHistoryViewModel.cs:471`) open export paths that are never under the runs root, and narrowing the
change to the chip keeps `ShellLauncher`'s "no state, no logging" contract intact.

Rejected: **a trailing `PromotedPath` member on `FileRef` + a new `PiaFileChip` DP + a binding.** The chip
lives in a deferred `ItemsControl.ItemTemplate` no test materializes (R24), so all three moving parts would
be unverifiable at once, and the promotion happens *after* the chip is built — the sink would have to
predict the destination. Rejected: **an `IRunWorkspaceService` injected into the chip.** A `UserControl` in
`Controls/` has no DI, and giving it one to open a file is a layering cost with no payer. Rejected:
**resolving in the ViewModel at bind time.** The whole point of D8 is that the answer changes between
"during the run" and "after promotion".

### B15 — G4's publish affordance on `RunProgressViewModel`

New members, following the `ContinueCommand` precedent exactly (R23):

```csharp
[ObservableProperty] private bool _isPublishing;
[ObservableProperty] private string? _publishNote;        // localized result line, null when nothing to say
[ObservableProperty] private string? _outputBranchName;   // worktree mode only
public bool HasOutputBranch => !string.IsNullOrEmpty(OutputBranchName);
public bool CanPublish => _hasUnpublishedFiles && !IsPublishing;
[RelayCommand(CanExecute = nameof(CanPublish))] private async Task Publish() { … }
```

- The workspace service arrives as a **trailing ctor param with a null default**
  (`IRunWorkspaceService? workspaces = null`), after `timelineService`. R23/plan R12: this type is
  hand-constructed positionally in production and in tests, and its own ctor comment flags that as a
  break-everything-silently hazard. Update the single production call site
  (`AssistantViewModel.cs:397`). **Do not introduce a `System.Windows` reference while in there** — the
  ViewModel ratchet exempts only `AssistantViewModel`, and `RunProgressViewModel` is not exempt.
- The outcome is resolved **off-thread, only when the run reaches a terminal state**, and applied through
  `_uiContext.Post` — the same mechanism `LoadTimelineAsync`/`ApplyTimelineAsync` already use (`:283-345`).
  `DescribeAsync` does a small file read plus one directory enumeration; that must not land on the
  dispatcher, and it must not run on every `RunChanged`.
- `Publish()` → `PromoteAsync` → on a non-null result `TearDownAsync` → `_hasUnpublishedFiles = false`,
  `PublishNote` = `Run_Publish_Done` formatted with `result.Promoted`, plus `Run_Publish_Conflicts` formatted
  with `result.Conflicts` when that is non-zero. `result.Skipped` is deliberately **not** surfaced (B7: it is
  the byte-identical no-op case — there is nothing to tell the user about a file that was already correct).
  A fault logs a warning (run id only) and sets the failed note. Declining is doing nothing: the workspace is
  retained and then swept by B12's 7-day terminal rule.
  > **STILL TRUE, and now an asymmetry worth naming (Phase 3 fix pass).** `Publish()` ignores
  > `RetainWorkspace` on purpose — this path is user-initiated and INFORMED (the note it renders carries the
  > conflict count) — so a manual publish still tears the workspace down and deletes the run's version of a
  > conflicted file. That is the inverse of the loss Lens A 5 / Lens B 3 filed against the AUTOMATIC path,
  > and it was left alone deliberately rather than overlooked: retaining here would leave an offer standing
  > that the user has just answered. Recorded as a live item in the review findings file.
  >
  > **NO LONGER TRUE — reversed by the consolidation pass of 2026-08-01 (`165486e`).** `Publish()` obeys
  > `RetainWorkspace`, so the manual and automatic paths are symmetric on purpose. The "offer standing that the
  > user has just answered" objection does not survive reading the XAML: the offer line (bound to `CanPublish`)
  > and `PublishNote` are SEPARATE stacked `TextBlock`s, so a retaining publish shows both at once and both are
  > true — N published, M left alone, and the M really are still in the workspace. The standing offer is also
  > actionable rather than stale: a user who moves their own copy aside turns the conflict into "destination
  > missing" and the next click carries the run's version out. Being user-initiated does not make a deleted file
  > recoverable, which is what the objection assumed.
  >
  > Two further changes in the same commit. `Run_Publish_Failed` now also covers the arm where a promotion
  > retained the workspace and moved nothing at all (a worktree whose run-branch commit is still failing) —
  > "Published 0 file(s)" would read as success there. And **"worktree mode never reaches `Publish()`" is no
  > longer absolute**: a worktree run whose branch never received a commit describes with
  > `HasUnpublishedFiles: true`, so the button appears for it and publishing retries the commit. That is the one
  > case, and it exists because the alternative was a panel with no recovery path at all (see B10's note).
  >
  > `HasUnpublishedFiles` is also no longer "a FAILED or CANCELLED run" only: a clean COPY-mode run whose
  > promotion hit a conflict now keeps its workspace, so a Completed run can legitimately raise the offer.
  > T-G4-16 (`ACompletedRun_OffersNothing`) is still correct — it drives a run whose promotion moved
  > everything — but its title reads wider than what it asserts.

**Six** new loc keys in **all three** resx files, inserted after `Run_Action_Continue`
(en `:914`, de `:939`, fr `:939` — anchor on the **key name**, not the line: G4 already moved these files).
This table said *five* while the bullet above already spoke of a "failed note"; the reconciler measured the
G4 working tree and the sixth key (`Run_Publish_Failed`) is really there, in all three files. Six is the
number, and Batch 07's G7 note cites six.

| Key | en | Fed by |
|---|---|---|
| `Run_Action_Publish` | `Publish files` | the button |
| `Run_Publish_Pending` | `This run's files are still in its workspace.` | the offer line, `HasUnpublishedFiles` |
| `Run_Publish_Done` | `Published {0} file(s).` | `RunPromotionResult.Promoted` |
| `Run_Publish_Failed` | `Nothing could be published — the files are still in the run's workspace.` | a null `PromoteAsync` result or a fault; the workspace is deliberately kept |
| `Run_Publish_Conflicts` | `{0} file(s) were left alone because they changed while the run was working.` | `RunPromotionResult.Conflicts` — **not** `Skipped`, which by B7 is the byte-identical no-op case and is deliberately never surfaced (there is nothing to tell the user about a file that was already correct) |
| `Run_Output_Branch` | `Output is on branch {0}` | plan D5b — the panel must say so |

DE/FR use the established
terminology from these files: de **Ausführung** for a run, fr **exécution**; de **Zweig**/**Branch** — use
**Branch** (the term the git tool strings already use). Do **not** hand-edit `ViewStrings.Designer.cs`;
`loc:Str` resolves through `ResourceManager` and the Designer has drifted.

XAML (`Controls/Assistant/RunProgressPanel.xaml`): a Publish button beside the Continue button at `:33-35`
with the identical visibility/CanExecute shape, and a muted note line under the truncation chip for
`PublishNote` / `Run_Output_Branch`. This is **manual-smoke debt** — no test parses this file (plan R11 /
§10.2).

### B16 — Provisioning failure no longer settles the run, and that is deliberate

Today `HeadlessRunLauncher.cs:161-174` wraps the three workspace-creation lines in a `try/catch` that calls
`FailAsync(run.Id, "workspace setup failed")` — the run row already exists (`Planning`), so a throw here would
otherwise leave it dangling non-terminal until the next startup sweep (G-4).

B11 moves `Directory.CreateDirectory` **inside** `ProvisionAsync`, which by B4 never throws and returns
`null` for "no isolation". So on the provisioner path that `catch` becomes **unreachable**, and a run whose
directory cannot be created now proceeds *unisolated* where today it fails.

**That is the intended outcome, not a refactoring accident.** Plan R16's whole mitigation shape is
"degrade rather than fail the run", and by G2 the alternative is worse in both directions: an unattended run
that fails because a scratch directory could not be created delivers nothing, while the same run writing to
the assistant folder delivers exactly what it delivered before Batch 06. The G-4 property the old `catch`
protected — *never leave a run dangling non-terminal* — is preserved by construction, because there is no
longer a throw to escape.

What stays: the `try/catch → FailAsync` block is **kept**, guarding the legacy `_workspaces is null` branch
(the shape `HeadlessRunLauncherTests` exercises). Do not delete it, and do not "restore" a `FailAsync` on the
provisioner path — pinned by T-G3-14.

### B17 — 06 changes no gate, no enum ordinal, no schema

Stated so a reviewer does not go looking: `ToolAutonomy.Resolve` is path-independent (R27), so isolation
changes *where* `write_file` lands and never *whether* it is gated. No `AgentRunState` member is added
(`Paused(4)` stays Batch 08's). `RunWorkspaceMode` is a **new** enum serialized by NAME, so it is
append-only on the same terms as the envelope's class names. `AgentRuns`/`AgentSteps` DDL, `PolicyJson`,
`GrantEnvelopeVersion` and every sync DTO are untouched.

---

## 3. Files to touch

| File | Change | Group |
|---|---|---|
| `src/Pia.Wpf/Infrastructure/AssistantWorkspace.cs` | `RunsRoot` (B1) | G1 |
| `src/Pia.Wpf/Infrastructure/SensitivePathGuard.cs` | second allowed island + `CanonicalizeAllowedIsland` (B1/B2); amend the class comment for the two islands and (in G3) the vault exclusion | G1, G3 |
| `src/Pia.Wpf/Services/RunContext.cs` | `WorkspaceRoot { get; set; }` (B3) | G1 |
| `src/Pia.Wpf/Services/AgentVerifier.cs` | `ctx.WorkspaceRoot ?? ambient ?? settings` at `:210`; correct the false ownership comment at `:258-265` | G1 |
| `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs` | assign `ctx.WorkspaceRoot` in `BeginRunAsync`; rewrite `Initialize`'s doc comment (null is no longer the intended production value) | G1, G2 |
| `src/Pia.Wpf/Services/HeadlessRunLauncher.cs` | `AssistantWorkspace.RunsRoot`; `workspaceRoot: runRoot` at `:209` and `:339`; trailing `IRunWorkspaceService? workspaces`; provisioning at launch + resume; state-aware sweep + orphan pass; `OnChatsChanged` cancel-then-async-teardown | G1–G4 |
| `src/Pia.Wpf/Services/Interfaces/IRunWorkspaceService.cs` | **new (CRLF)** — interface, `RunWorkspaceMode`, three result records | G3 |
| `src/Pia.Wpf/Services/RunWorkspaceService.cs` | **new (CRLF)** — both strategies, metadata, promotion, teardown, orphan sweep | G3, G4 |
| `src/Pia.Wpf/Services/GitToolHandler.cs` | ambient-aware effective root captured once per call and threaded into `IsInsideSandbox`/`GetCeilingDirectory`/the deferred closures (B-git, §6.2) | G3 |
| `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` | trailing `IRunWorkspaceService? workspaces`; `SafePromote` on **both** terminal arms (B8) | G4 |
| `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs` | publish affordance + branch line (B15) | G4 |
| `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` | the one `RunProgressViewModel` construction at `:397` | G4 |
| `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml` | Publish button + note lines | G4 |
| `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | 6 keys each (B15, corrected) | G4 |
| `src/Pia.Wpf/Bootstrapper.cs` | `AddSingleton<IRunWorkspaceService, RunWorkspaceService>()` | G3 |
| `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` | provision for the interactive `Planned` run; pass the root to `LiveTurnExecutor` | G5 |
| `src/Pia.Wpf/ViewModels/Models/LiveTurnExecutor.cs` | trailing `string? workspaceRoot = null`; assign `ctx.WorkspaceRoot`/`ctx.WorkingSubpath`; `BuildSpec` sets `WorkspaceRoot` | G5 |
| `src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs` | `StepTurnSpec` gains trailing `string? WorkspaceRoot = null` | G5 |
| `src/Pia.Wpf/ViewModels/Models/ChatSession.cs` | `RunStepTurnAsync`'s `TaskContext` carries `spec.WorkspaceRoot` and nulls the subpath when it is set (`:662`). `:307` (the ordinary turn) **untouched** | G5 |
| `src/Pia.Wpf/Helpers/RunWorkspaceRedirects.cs` | **new (CRLF)** — B14 | G5 |
| `src/Pia.Wpf/Controls/Chat/PiaFileChip.xaml.cs` | two one-line redirects | G5 |

**Every new parameter in this batch is trailing and defaulted** — on `HeadlessRunLauncher`,
`AgentRunOrchestrator`, `RunProgressViewModel`, `LiveTurnExecutor` and `StepTurnSpec`. That is not
tidiness: it is what makes each group's "the existing suite passes unmodified" claim true. Every one of
those types is hand-constructed **positionally** somewhere in the test project.

---

## 4. G1 — guard carve-out + verifier root  *(no behaviour change)*

**Does:** B1, B2, B3. The runs root becomes `AssistantWorkspace.RunsRoot`; the guard carves it out with
missing-tail canonicalization; `RunContext.WorkspaceRoot` exists, is assigned by
`HeadlessTurnExecutor.BeginRunAsync`, and `AgentVerifier` prefers it; the false ownership comment is
corrected.

**Does NOT:** flip either `Initialize` call site. `_workspaceRoot` is still null in production, so
`ctx.WorkspaceRoot` is still null and the verifier still resolves the settings folder. **Zero behaviour
change** — which is exactly why the G1 tests must root at the real shape (T-G1-2) rather than proving
anything through a run.

**Safe cut point.** A tree that stops here is shippable and strictly no worse than `53cd552`.

> **BUILDER NOTE (G1) — from the reconciler.**
> `RunContext.WorkspaceRoot` is the **single seam two later groups in another batch depend on**, so add it
> exactly as B3 writes it — `public string? WorkspaceRoot { get; set; }`, settable, symmetric with
> `WorkingSubpath`. Batch 07's G10 reads `ctx.WorkspaceRoot` in `AgentRunOrchestrator` to hand a **child**
> run its parent's workspace root (a child must never provision its own workspace: 06 B7 allows exactly one
> promotion per workspace, decided by a single `provisionedAtUtc`). If this member ends up `init`-only, or
> named differently, or assigned anywhere other than `BeginRunAsync`, G10 has no seam and will invent a
> worse one.
> You are editing `HeadlessTurnExecutor.BeginRunAsync`, which Batch 07's G6 also edits (it repurposes the
> `_persona`/`_provider`/`_setup` caching into a run-default triple). **Do not restructure that method** —
> add your one `ctx.WorkspaceRoot = _workspaceRoot;` line beside the existing `ctx.WorkingSubpath = null;`
> and leave the persona/provider resolution untouched, or G6 arrives at a method it cannot recognize.
> Add **no ctor parameter** to `HeadlessTurnExecutor` in this batch (none is needed); G6 appends
> `StepPersonaResolver? stepPersonas = null` after `timelineService` and its spec says to confirm
> `timelineService` is still last.

---

## 5. G2 — flip both `Initialize` call sites  *(first behaviour change)*

**Does:** pass `runRoot` instead of `null` at `HeadlessRunLauncher.cs:209` (launch) and `:339` (resume).
Rewrite the two doc comments that currently describe null as the intended production value:
`HeadlessTurnExecutor.Initialize`'s `<para>` at `:92-99` and the inline comment at `:205-208`. Re-root
`FilesToolHandlerWorkspaceEscapeTests` (§9.2).

**Does NOT:** provision (the workspace is an **empty** directory), promote, tear down differently, or touch
git. So after G2 an unattended run:

- **cannot read the user's existing files** — `list_files` returns "No files found";
- writes its deliverables into `runs\<id>`, where nothing promotes them and the 30-day sweep eventually
  deletes them.

That is a functional regression, and it is bounded by G3+G4 landing in the same session. **Do not ship a
tree that stops at G2.** State it in the handoff if the loop halts here.

> **BUILDER NOTE (G2) — from the reconciler.**
> Two-line group, one cross-batch obligation: the doc comment you rewrite on
> `HeadlessTurnExecutor.Initialize` is read by Batch 07's G6 builder as the statement of what `workspaceRoot`
> now means in production. Say plainly that it is **non-null for an isolated run and null only for the
> no-isolation degrade**, because G6 must not assume either value, and Batch 07's G10 passes a *parent's*
> root through this same parameter for a child run. Keep the parameter name `workspaceRoot` — G10's
> `LaunchCoreAsync` threads a `workspaceRootOverride` into this exact call.

---

## 6. G3 — provisioning: worktree | copy, with symmetric teardown and git parity

### 6.1 The provisioner

B4, B5, B6, B11, B12, plus `SensitivePathGuard`'s comment amendment for the vault exclusion. Both call
sites in `HeadlessRunLauncher` change from *"create a directory"* to *"ask the provisioner"*:

```csharp
        // Launch (in front of :161-174). A provisioning failure is NOT a run failure: the service degrades
        // worktree→copy→no-isolation on its own (B11 F1-F10) and returns null for "no isolation", which is
        // exactly the pre-Batch-06 behaviour. It NEVER throws, so the FailAsync settle below is unreachable on
        // this path — see B16 for why that is the intended outcome and why the block stays anyway.
        string? runRoot = null;
        if (_workspaces is not null)
        {
            runRoot = (await _workspaces.ProvisionAsync(run.Id, workingSubpath: null, ct))?.Root;
        }
        else
        {
            // Legacy path (no service injected — the shape HeadlessRunLauncherTests exercises): the original
            // create + canonicalize, still guarded by the try/catch → FailAsync at :161-174 (G-4).
            …
        }
```

The legacy `Directory.CreateDirectory` + `try/catch → FailAsync` block is **kept verbatim** for the
`_workspaces is null` case, so `HeadlessRunLauncherTests` compiles and passes unmodified (R13) and the G-4
never-dangle property keeps its guard where a throw is still possible. The resume path (`:313-315`) calls the
same `ProvisionAsync`, which is idempotent by B11 step 2.

**Does NOT:** promote anything (still G4), touch the interactive path (still G5), or add any `git worktree`
tool to the agent surface (R18 — provisioning is app-side).

> **BUILDER NOTE (G3) — from the reconciler.**
> Batch 07's G10 **extracts the launch dispatch you are editing into one private `LaunchCoreAsync`** and
> gives it a `string? workspaceRootOverride`: when that override is non-null the child **skips
> `ProvisionAsync` entirely** and passes the value straight to `executor.Initialize(workspaceRoot: …)`. Two
> consequences for how you write this group:
> 1. Keep the provisioning decision as **one contiguous block** at the top of the launch dispatch (the
>    `if (_workspaces is not null) … else legacy …` shape B4/§6.1 specifies), not sprinkled through the
>    dispatch. A single block is what makes G10's override a two-line change instead of a rewrite.
> 2. Write the **same block shape on the resume path** (`:313-315`). G10 must add a rule there too — a
>    resumed **child** must not provision at its own run id — and it can only do that cheaply if launch and
>    resume look alike.
> `IRunWorkspaceService` stays a **singleton** with the trailing `runsBaseDirOverride`: G10's child dispatch
> resolves it from the same singleton, and a scoped registration would give parent and child different
> metadata readers for one workspace.

### 6.2 GitToolHandler parity — capture the root, do not read the ambient in the closure

Without this, 06 creates an incoherence the tree does not have today: the agent writes into the workspace and
commits the interactive folder's stale tree. Three readers must change, and **the third one is the trap**:

1. `HandleToolCallAsync:138` — `baseRoot` becomes ambient-aware, mirroring `FilesToolHandler.cs:170-171`
   including the `NormalizeWorkspaceRoot` canonicalization:

```csharp
        // Batch 06: an isolated run supplies its own workspace root, so git resolves the repo THERE — or
        // files and git disagree and the agent commits the interactive folder's stale tree. Same one-line
        // shape as FilesToolHandler's dispatch point, canonicalization included.
        var ambientRoot = TaskAmbient.Current?.WorkspaceRoot;
        var sandboxRoot = ambientRoot is not null ? NormalizeWorkspaceRoot(ambientRoot) : _currentFolder;
```

2. `GetCeilingDirectory` must take the **effective** sandbox root as a parameter, not read `_currentFolder`.
   R17: with cwd under `runs\<id>` and a ceiling of `Documents\`, `GIT_CEILING_DIRECTORIES` **does not
   apply at all**, and upward `.git` discovery would walk `runs → Pia → Local → AppData → %USERPROFILE%` and
   could bind a repo the user keeps in their profile. The ceiling must be the parent of the effective root.
   `RunGitAsync` therefore threads the effective root through instead of calling the parameterless helper.

3. `IsInsideSandbox` must compare against a **captured** effective root, not re-read the ambient. It is
   deliberately re-run inside the deferred `GitToolCall.Execute` closure as the runtime-re-point TOCTOU
   re-guard (R16), and `FilesToolHandler` states the governing rule at `:179-182`: *ambient flow is not
   guaranteed inside the deferred execute closure*. If `IsInsideSandbox` read the ambient, a mutating git
   tool in worktree mode would pass prepare and then **refuse after the user approves it** — null ambient →
   `_currentFolder` → toplevel is `<runRoot>` → `OutsideSandboxRefusal`. So: resolve the effective root
   ONCE in `HandleToolCallAsync`, capture it into every prepare closure, and re-guard against the captured
   value. The TOCTOU property is preserved for the interactive case (the captured value is still
   `_currentFolder`, and `_currentFolder` is re-read for the *interactive* comparison), and it gains a
   stronger one for the isolated case: the run cannot escape its workspace even if the user re-points the
   folder mid-run.

Note the invariant this preserves: in worktree mode `rev-parse --show-toplevel` returns `<runRoot>` itself,
which is inside the effective sandbox → allowed. In copy mode the workspace has no `.git` (B6 excludes it),
so `rev-parse` fails and the model gets `FreshFolderHint` — it may `git_init` inside its own workspace,
which is contained and harmless, and whose result is not promoted (`.git` is ignore-pruned on the way out
too). Release-note item, not a bug.

---

## 7. G4 — promotion, publish affordance, retention

**Does:** B7, B8, B9, B10, B15; the state-aware sweep and orphan-metadata pass from B12; the loc keys and
the panel XAML.

**Does NOT:** merge a worktree branch (plan D5b — there is deliberately no conflict handling on an
unattended path); promote a cancelled or failed run automatically; rewrite any persisted chat content; touch
the interactive path (G5).

Two things a builder will get wrong if they skim:

- **The R10 fallback arm** (§0.5 / B8). It is the arm every launcher-harness test exercises.
- **`AgentRunOrchestrator`'s workspace dependency is trailing-optional-null, so NO existing orchestrator
  test covers promotion.** Do not assume inherited coverage; the promotion facts must construct the
  orchestrator **with** the service supplied.

Logging discipline for this group specifically (plan R7 / R30 — 06 logs a lot of paths):

```csharp
// Information and above: counts, booleans, ids. Never a path, never a filename — this lands in a
// support-attachable release log and there is NO SensitiveError helper; SensitiveWarning is the highest
// DEBUG-erased severity available.
_logger.LogInformation("Run {RunId} promoted {PromotedCount} file(s), skipped {SkippedCount}, {ConflictCount} conflict(s)",
    runId, result.Promoted, result.Skipped, result.Conflicts);
_logger.SensitiveWarning("Run {RunId} promotion conflict on {Path}", runId, rel);
```

`SafeUrl` does **not** apply — it is scheme+host shaped and says nothing about a filesystem path.

> **BUILDER NOTE (G4) — from the reconciler.**
> Three things Batch 07 will edit inside what you write here. Get the *shape* right and each of them is one
> line for its builder; get it wrong and they are rewrites.
> 1. **`SafePromote` must be a single statement at each of its two call sites, and all of the promote/teardown
>    logic must live inside the method.** *(Measured in `3c28e84`: **satisfied** — the committed body opens
>    `private async Task SafePromote(AgentRun run, RunContext ctx, CancellationToken ct)` followed by
>    `if (_workspaces is null || string.IsNullOrEmpty(ctx.WorkspaceRoot)) return;`, and both call sites are a
>    single awaited statement. This fact is now a property of the tree, not a request.)*
>    Batch 07's G10 adds **one** early return inside it —
>    `if (run.ParentRunId is not null) return;`, beside your `_workspaces is null ||
>    string.IsNullOrEmpty(ctx.WorkspaceRoot)` return — because a child run is a full run with its own
>    orchestrator, would otherwise reach your promote line, consume the workspace's one allowed promotion (B7)
>    out from under its parent, **and then tear the shared workspace down while the parent's other children are
>    still writing into it**. That guard is one line *only because* `SafePromote` takes `run` and is the single
>    funnel for both `PromoteAsync` and `TearDownAsync`. So: keep `run` as a parameter, keep both calls inside
>    the method, and do not inline promotion logic into either terminal arm.
> 2. **`AgentRunOrchestrator`'s new ctor param is trailing and defaulted** (`IRunWorkspaceService? workspaces
>    = null`). Batch 07's G10 appends `IHeadlessRunLauncher? childLauncher = null` **after** it. There are 13
>    positional `new AgentRunOrchestrator(...)` constructions in tests; a required parameter breaks all of
>    them and both batches' "existing suite passes unmodified" claims at once.
> 3. **`RunProgressViewModel`: your `IRunWorkspaceService? workspaces = null` is the 7th ctor param**, after
>    `timelineService`. Batch 07's G7 appends `IPersonaService? personaService = null` as the 8th (its own
>    §4.4 still says "7th" — that ordinal was written before this batch landed; the *position*, last, is what
>    matters). In `RefreshAsync`, keep the `DescribeAsync` outcome read in its own **terminal-only**,
>    off-thread branch applied via `_uiContext.Post`: G7 adds a persona-map load and G10 adds a children-list
>    load to the same method, and folding your read into the unconditional path would put a file read plus a
>    directory enumeration on every `RunChanged`.
> In `RunProgressPanel.xaml`, your Publish button and note lines shift the line numbers Batch 07 cites
> (`:66-68` for the step-row avatar, the timeline expander for G10's children list). That is expected and
> handled — 07's builders are told to locate by markup.

**ANNOTATED BY G4'S BUILDER (2026-07-31), measured against the tree. Six places this section and §9.4 are
wrong or incomplete; the tree does the second thing in each case.**

1. **B15's key table is one key short of B15's own prose.** The table lists five keys, but the bullet above it
   requires *"a fault logs a warning (run id only) and sets the failed note"* and T-G4-18 asserts
   `PublishNote` names a failure. There is no key for that. G4 added a **sixth**, `Run_Publish_Failed`, in all
   three resx files. It also covers the non-fault "nothing was promoted" degrade (a relocated assistant
   folder), which must not clear the offer and must not claim "published 0 files".
2. **T-G4-5's redirect clause and B14's `Record` call are G5 work and were NOT done here.**
   `Helpers/RunWorkspaceRedirects.cs` does not exist until G5, so `PromoteAsync` records no redirect and
   T-G4-5 asserts everything except the redirect. **G5's insertion point:** in
   `RunWorkspaceService.CopyOut`, after the per-file loop and before the `LogInformation` line, on a
   successful copy-mode promotion only — `RunWorkspaceRedirects.Record(runRoot, destination);`.
3. **`HeadlessRunLauncherWorkspaceTests.cs` does not exist and was not created.** T-G4-13/14 need
   `BuildLauncher`, which is `private` to `HeadlessRunLauncherTests`, so they live there (as does T-G3-14,
   which G3 never wrote). A second harness would have been a copy of that file's ten dependencies.
4. **T-G4-21's non-vacuity threshold does not describe the tree G3 left.** The spec expected *"every
   workspace-removal site references `TearDownAsync`"* and `count >= 3`. G3 centralized removal into ONE
   private `TearDownWorkspaceAsync` with a single `_workspaces.TearDownAsync` inside it, called from two
   sites. That is a strictly better shape, so the rule pins THAT: one `Directory.Delete(` call in the whole
   file, inside the documented `TryDeleteDirectory` fallback, and `>= 3` mentions of the single teardown path
   (its declaration plus its callers).
5. **T-G4-1 as specified does not discriminate the rule it exists for.** With the mtime skip neutralized, an
   untouched copied-in file is still protected by the byte-identity skip, so the fact stayed green. The
   measured discriminator is a copied-in file the USER DELETED at the destination during the run: without
   the mtime rule, promotion "creates a missing file" and **resurrects** it. That case is now the third file
   in T-G4-1, and neutralizing the mtime skip reds it.
6. **T-G4-10's `fallback-fail` row does not red under the "promote unconditionally" neutralization** the spec
   names, because the fallback arm's failure path returns before the terminal-settle block it neutralizes.
   The `cancel` and `step-fail` rows do (both measured). The row is kept: it guards the *other* arm.

---

## 8. G5 — interactive isolation (plan D4) and chip resolve-on-open (plan D8)

**Does:**

1. `ChatSessionManager`'s `Planned` branch (`:744-798`): after `CreateAsync` and before constructing
   `LiveTurnExecutor`, provision a workspace with `workingSubpath: session.WorkingDirectory`. Uses the one
   `settings` instance already read at `:757` (Batch 04 D11's single-read rule — do not add a second read).
   A null result means no isolation and the turn proceeds exactly as today; **a provisioning fault must never
   fail the turn** (there is a user watching, and this is bookkeeping).
   The service reaches this type as a **trailing defaulted ctor param** `IRunWorkspaceService? workspaces = null`,
   appended **after** the existing `IAgentTimelineService? agentTimelineService = null` (`ChatSessionManager.cs:122`)
   and following its comment discipline verbatim. `ChatSessionManagerTests.cs:84` is the one hand-constructed
   site: it passes positionally and omits the trailing optional, so it compiles unmodified. Batch 07's G6
   appends `StepPersonaResolver? stepPersonas = null` to the same ctor, after yours.
2. `LiveTurnExecutor`: trailing `string? workspaceRoot = null`; in `BeginRunAsync`, on the UI thread, next
   to the existing `ctx.WorkingSubpath` assignment (`:66`):

```csharp
            ctx.WorkspaceRoot = _workspaceRoot;
            // An isolated run's workspace root IS the already-narrowed source root (B6), so narrowing a
            // SECOND time would probe <runRoot>\<subpath>, which does not exist. Stated as an explicit
            // assignment rather than left to ResolveEffectiveRoot's fail-safe fallback — the same shape
            // HeadlessTurnExecutor uses at its own BeginRunAsync.
            ctx.WorkingSubpath = _workspaceRoot is null ? _session.WorkingDirectory : null;
```

3. `StepTurnSpec` gains trailing `string? WorkspaceRoot = null` (R20's precedent, verbatim); `BuildSpec`
   sets it; `ChatSession.RunStepTurnAsync:662` becomes

```csharp
        TaskAmbient.Current = new TaskContext(
            spec.RunId,
            // Same one-narrowing rule as the run context (B6): the workspace root already IS the narrowed
            // root. The ORDINARY interactive turn (RunTurnAsync) is a separate construction and keeps
            // passing WorkingDirectory — only a Planned run's steps isolate.
            spec.WorkspaceRoot is null ? WorkingDirectory : null,
            touch => assistantMessage.AddOrUpgradeFileRef(new FileRef(touch.AbsolutePath, …)),
            spec.WorkspaceRoot);
```

4. B14's redirect registry and the two `PiaFileChip` handler edits.

Promotion needs **no** interactive-specific work: it lives in the executor-agnostic orchestrator (B8) and
reads `ctx.WorkspaceRoot`, so the live path inherits it. That is the parity requirement satisfied by
construction, and §9.5 tests both halves anyway.

> **BUILDER NOTE (G5) — from the reconciler.**
> Two ctors you touch are appended to again by Batch 07's G6, so **trailing-and-defaulted is a cross-batch
> contract, not a style choice**:
> `LiveTurnExecutor` — your `string? workspaceRoot = null` goes last **now**; G6 appends
> `StepPersonaResolver? stepPersonas = null` after it (07's own §3.5 says to count the parameters in the file
> rather than trust its table, precisely because of your edit).
> `ChatSessionManager` — your `IRunWorkspaceService? workspaces = null` goes after `agentTimelineService`;
> G6 appends the resolver after that.
> `StepTurnSpec.WorkspaceRoot` is the member G6 must **keep passing** from `BuildSpec` while it rewrites the
> persona-derived members around it. Dropping it would compile (trailing + defaulted) and silently
> un-isolate every interactive step, so make the member's doc comment say what it is for and that
> `BuildSpec` is its only producer.
> Leave `ChatSession.RunStepTurnAsync` in the shape §8.3 specifies: 07 explicitly plans **no change** to that
> method because `spec.Persona`/`spec.Provider` already carry everything a per-step persona needs.
> **Three additions, measured 2026-07-31 while G4 was still landing (it committed as `3c28e84` during this
> reconcile pass) — so verify each against the tree before you write, not against this note:**
> 1. **Two of your members are the only unverified predictions left in Batch 07's spec.** Its §0.10.2 records
>    `LiveTurnExecutor`'s trailing `string? workspaceRoot = null` and `StepTurnSpec`'s trailing
>    `string? WorkspaceRoot = null` as **absent** at audit time (you had not run yet), and tells its G6 builder
>    to `grep -n "WorkspaceRoot" src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs` and *"not invent the
>    member"* if it is missing. **You are what makes those two predictions true.** Land them with exactly
>    those names, in exactly those positions (last on the ctor; after `Timeline` on the record). Rename or
>    relocate either one and G6's grep finds nothing, concludes G5 was cut, and every interactive step ships
>    un-isolated with no test to catch it.
> 2. **B14's `Record` call is G5 work, and G4's builder already picked the line for you** — see annotation 2
>    on §7: in `RunWorkspaceService.CopyOut`, after the per-file loop and before the `LogInformation`, on a
>    successful **copy-mode** promotion only. `Helpers/RunWorkspaceRedirects.cs` does not exist before your
>    commit, so T-G4-5 asserts everything except the redirect; your commit is where that clause is closed.
> 3. **`ctx.WorkspaceRoot` on the live path is what makes promotion executor-agnostic.** `SafePromote` already
>    early-returns on `string.IsNullOrEmpty(ctx.WorkspaceRoot)`, so until your `LiveTurnExecutor.BeginRunAsync`
>    assignment lands, promotion is a **no-op for interactive runs** and `SafePromote`'s doc comment ("which
>    BOTH executors assign") is written for the tree you leave. Batch 07's §0.10.2 B3 tells its builder not to
>    "fix" that comment; make it true instead.

**Does NOT:** isolate an ordinary (non-`Planned`) interactive turn — `ChatSession.cs:307` is untouched, and
a plain chat still writes straight to the assistant folder. Does not change `@Files` autocomplete or
`ReadPromptPreviewAsync` (R3): during an isolated interactive run the picker still lists the **real** folder
while the run writes into the workspace. That inconsistency is real and is recorded in §13.2 rather than
fixed — the picker runs outside any turn and has no run to key off.

**ANNOTATED BY G5'S BUILDER (2026-07-31), measured against the tree. Six places this section, B14 and §9.5 are
wrong or incomplete; the tree does the second thing in each case.**

1. **B14's "two one-line edits" in `PiaFileChip.xaml.cs` are THREE.** The chip has a third open path,
   `OnOpenInVsCodeClick` → `VsCodeLauncher.Open`, which B14's enumeration simply missed. Leaving it out would
   have left one of three buttons dead after promotion, which is the exact failure D8 exists to prevent.
   `VsCodeLauncher.IsSupportedFile` is extension-based, so click-time resolution needs no DP or binding change.
2. **`Record` MUST canonicalize its key, and B14 does not say so — this is the difference between D8 working
   and D8 being a silent no-op.** `PromoteAsync` holds the raw `RootFor(runId)` (`Path.Combine`, uncanonicalized),
   while the path a chip carries is built from `FilesToolHandler.NormalizeWorkspaceRoot(ambientRoot)`, i.e.
   `GetFullPath` + a real-path resolve — the spelling `ProvisionAsync` returned. A key in the other spelling
   misses the prefix match, `Resolve` returns its input, and every post-promotion chip is dead with a green
   gate. `Record` therefore normalizes exactly the way `FilesToolHandler` does, and `Resolve` normalizes its
   input through `SensitivePathGuard.CanonicalizeAllowedIsland` (deepest existing ancestor + missing tail),
   because the leaf being gone is the only case that reaches the lookup. The ordering that makes this possible
   is already right: `CopyOut` records BEFORE the caller's `TearDownAsync`, so the directory still exists.
3. **T-G5-7 as specified cannot go red.** Its neutralization is "pass `WorkingDirectory` through as the ambient
   subpath", but `ResolveEffectiveRoot` FALLS BACK to the base root when the subpath does not resolve
   (`FilesToolHandler.cs:203`), so a doubly-narrowed isolated step still writes to `<runRoot>\a.md` and the file
   location is identical. The one-narrowing rule is asserted on the **ambient itself** instead
   (`WorkingSubpath is null` beside `WorkspaceRoot == <root>`), which does discriminate it — measured red both
   in `ChatSession` and in `LiveTurnExecutor.BeginRunAsync`.
4. **T-G5-6/7/8/11 are not all in the files §9.5 names.** `LiveTurnExecutorPlannedRunTests` gets the two facts
   that need the real orchestrator (the isolated write, and promotion + chip resolution), because only those
   exercise `BuildSpec`. The rest live in a new `ChatSessionWorkspaceIsolationTests`, which drives
   `RunStepTurnAsync` directly: `ChatSession`'s two arguments and `BuildSpec`'s are separate call sites, and one
   fact covering both would let either be deleted (G2's "one fact per call site" precedent). The ordinary-turn
   pin (T-G5-8) needs `RunTurnAsync`, which that harness has and the planned-run harness does not.
5. **T-G5-10 needs the MANAGER, so it lives in `ChatSessionManagerTests`** — the provisioning call is the
   manager's, and `LiveTurnExecutorPlannedRunTests` never constructs one. Three facts landed there (provision
   with the chat subpath + hand the root to the executor; degrade on null; degrade on a throw), and the
   positive one is load-bearing: the degrade facts assert a NULL root, so they stay green when the provisioning
   call is cut entirely. `CreateSut` is deliberately left untouched — a second builder carries the two new
   dependencies, so the file keeps one positional construction that omits every trailing optional, which is
   what proves the new ctor parameter is source-compatible.
6. **The promote fact backdates the recorded `provisionedAtUtc`.** B7 decides the promote set by
   `mtime > provisionedAtUtc`, and a file written milliseconds after provisioning can TIE that timestamp — which
   would make an end-to-end promotion fact flake rather than fail. The fixture rewrites the metadata document's
   instant five minutes back; WHAT the mtime rule promotes is `RunWorkspacePromotionTests`' subject (T-G4-1),
   not this fact's.

**Interactive teardown needs no new plumbing, and that is a decision, not an omission.** `RunStartupSweepAsync`
enumerates `<runsBase>\<runId>` directories and keys purely on the run row it reads back — it never consults
`HeadlessRunLauncher._runsByChat`, so an interactive run's workspace is already in scope for the same
state-aware predicate (B12) as a headless one's. A clean interactive run is promoted and torn down by the
orchestrator; a failed one keeps its workspace for the publish offer; a chat deleted mid-run cascades the run
row away, so the next sweep sees `run is null` and removes the workspace unconditionally — the identical
self-healing path `OnChatsChanged`'s own comment already documents for a failed delete. Registering interactive
runs into `_runsByChat` would mean a new `IHeadlessRunLauncher` member for both batches to carry, in exchange
for a worse failure direction (R4's hazard is deleting under a LIVE writer, and the interactive path is the one
with a user watching).

---

## 9. Test plan

Every entry says **REGRESSION** (demonstrably red before the change) or **GUARD** (pins a premise; cannot go
red on a revert of the behaviour it accompanies) — and the distinction goes in the test's **own comment**, not
only in this table. Neutralize by editing the source and restoring with `git checkout --`, never by copying a
backup: a preserved older mtime makes MSBuild skip the recompile and the "restored" run exercises the mutated
binary.

Namespaces mirror the folder (`Pia.Tests.Services`, `Pia.Tests.Infrastructure`, `Pia.Tests.ViewModels`,
`Pia.Tests.Helpers`, `Pia.Tests.Architecture`). Every new `.cs` file is **CRLF**.

### 9.1 G1

**`tests/Pia.Wpf.Tests/Infrastructure/SensitivePathGuardRunsCarveOutTests.cs` — NEW**

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G1-1 | `RunsRoot_IsCarvedOut_WhileTheDataRootAndDbStayBlocked` | **REGRESSION** | mirrors the existing `Workdir_IsCarvedOut_…` shape (`SensitivePathGuardTests.cs:19-40`): `Directory.CreateDirectory(AssistantWorkspace.RunsRoot)`, then `IsBlocked` is **false** for the canonicalized runs root, for `<runs>\<guid>`, and for `<runs>\<guid>\nested\a.md`; still **true** for `%LOCALAPPDATA%\Pia` and `%LOCALAPPDATA%\Pia\history.db`. The "siblings stay blocked" half is the non-vacuity control — a carve-out that accidentally covered `%LOCALAPPDATA%\Pia` would turn only the first half green. | remove the second entry from `BuildAllowedExceptions` → the first three assertions red |
| T-G1-2 | `AWriteInsideARealRunsWorkspace_Succeeds` | **REGRESSION** | THE R1 fact. `FilesToolHandler` level, **not** launcher level: create `Path.Combine(AssistantWorkspace.RunsRoot, Guid.NewGuid().ToString())` — the REAL shape, because `BuildBlockedRoots` reads the real `LOCALAPPDATA` and a `GetTempPath()` fixture is outside every blocked root (R14). Set `TaskAmbient.Current = new TaskContext(runId, null, null, thatDir)`, call `write_file("out.md", "hi")`, execute the returned pending action, and assert **`File.Exists` + the content** — a successful write, not merely that an escape was rejected. Then assert an escape (`../secret.txt`) is still rejected. Dispose deletes the directory. Lives in `FilesToolHandlerRunsDirGuardTests.cs`. | revert the carve-out → the write returns the `WriteResult.Failed` record with *"protected system or application data directory"* |
| — | — | — | **Use a Guid-shaped directory name.** `RunStartupSweepAsync` `continue`s on any name that is not a parseable Guid (R11), so a leaked non-Guid fixture would live in the developer's real runs folder forever; a Guid-named one is swept as `run is null` on the next app start. And do **not** point a launcher test at the real runs base — `HeadlessRunLauncherTests.cs:66-70` says why (the sweep **deletes**). Launcher tests keep `runsBaseDirOverride` under `GetTempPath()` and deliberately do not cover the guard. | | |
| T-G1-3 | `CanonicalizeAllowedIsland_ResolvesAnIslandWhoseTailDoesNotExist` | **GUARD** | `internal` helper, called directly: for `<existingTemp>\nope\deeper`, the result equals `SafeFolderPath.Canonicalize(<existingTemp>) + "\nope\deeper"`. Order-independent by construction — a test cannot control when `SensitivePathGuard`'s statics initialize, which is exactly the hazard B2 exists for (the runs root does not exist on a fresh install). | — (it pins a premise; the behaviour it protects is only observable via static-init order) |

**`tests/Pia.Wpf.Tests/Services/AgentVerifierWorkspaceRootTests.cs` — NEW**

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G1-4 | `ArtifactProbe_ResolvesAgainstTheContextWorkspaceRoot_NotTheSettingsFolder` | **REGRESSION** | Build a `RunContext` with `WorkspaceRoot = <tempWorkspace>` and one completed step declaring `report.md`; write `report.md` into `<tempWorkspace>` and **not** into the settings folder; `TaskAmbient.Current` is null (the production shape at verify time). The verify prompt's artifact block reports the artifact **found**. Follow `AgentVerifierTests.cs:184-230`'s existing probe harness. | drop `ctx.WorkspaceRoot ??` from `:210` → red (reports NOT FOUND) |
| T-G1-5 | `ArtifactProbe_StillUsesTheSettingsFolder_WhenNoWorkspaceRootIsSet` | **GUARD** | `WorkspaceRoot` null → the settings folder is probed, byte-for-byte today's behaviour. The pin that G1 is a no-op for every existing run. | — |
| T-G1-6 | `BeginRunAsync_PublishesTheWorkspaceRootOntoTheRunContext` | **REGRESSION** | `HeadlessTurnExecutor.Initialize(workspaceRoot: X, …)` then `BeginRunAsync` → `ctx.WorkspaceRoot == X` **and** `ctx.WorkingSubpath is null`. Add to `HeadlessTurnExecutorTests`. | delete the assignment → red |

### 9.2 G2

**Scoping note, read before writing these two.** Do **not** try to prove G2 by driving a real `write_file`
through `LaunchAsync`. At G2 there is no provisioner, so the workspace is empty and the run has no files to
work with; and the existing launcher harness (`FakePlanner` → `PlanResult.Fallback`, `HeadlessRunLauncherTests.cs:44-58`)
gives you no provider that emits a tool call — you would need a fake `IAiClientService` returning a
`FunctionCallContent`, a real `FilesToolHandler` in the scope and the grant set to contain `write_file`. That
is a disproportionate fixture, and it is not where the change is. Assert at the **seam that changed**: what
value reaches `HeadlessTurnExecutor.Initialize`.

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G2-1 | `Launch_InitializesTheExecutorWithTheRunWorkspaceRoot` | **REGRESSION** | In `HeadlessRunLauncherWorkspaceTests.cs` (NEW, `runsBaseDirOverride` under `GetTempPath()`): give the launcher a stub `IServiceScopeFactory` whose scope hands back a `HeadlessTurnExecutor` the test holds a reference to, drive `LaunchAsync` with the existing `FakePlanner`, await the handle's completion, and assert the executor's `ctx.WorkspaceRoot` after `BeginRunAsync` equals `<runsBase>\<runId>` — reading it off the `RunContext` the orchestrator built, which is the value G1 made observable (T-G1-6). If wiring a stub scope factory proves awkward, the acceptable fallback is a source scan of `HeadlessRunLauncher.cs` asserting **zero** `workspaceRoot: null` occurrences remain, with `Assert.NotEmpty(source)` as the non-vacuity control — say in the commit message which form you used. | revert `:209` to `null` → red |
| T-G2-2 | `Resume_InitializesTheExecutorWithTheSameRunWorkspaceRoot` | **REGRESSION** | park a run, `ResumeAsync`, assert the same root reaches the resumed dispatch's executor. Covers `:339`, which is a **separate literal** from `:209` and has drifted from it before — one fact per call site, never one fact for both. | revert `:339` → red while T-G2-1 stays green |
| T-G2-3 | `FilesToolHandlerWorkspaceEscapeTests` re-rooted | **GUARD** | `_runRoot` moves from `Path.GetTempPath()` to a Guid-named directory under `AssistantWorkspace.RunsRoot` (the real shape), so the whole existing escape matrix now runs where the guard actually applies. All five write vectors, three delete vectors, two read vectors and the symlink case keep their assertions **unchanged**; the class doc comment gains one sentence saying the fixture is deliberately at the real shape because a `GetTempPath()` root cannot see the guard (R14). Add one positive control to the class: `Write_InsideTheRunRoot_Succeeds` — without it, a fixture whose root is silently un-writable would make every escape assertion pass vacuously. | — (the escape assertions cannot go red on a revert of G2; the positive control can and does) |

> **G2 AS BUILT (annotations by the G2 builder).** Three corrections to §9.2, all measured.
> 1. **`HeadlessRunLauncherWorkspaceTests.cs` was NOT created.** T-G2-1/2 live in the existing
>    `HeadlessRunLauncherTests`, which already owns the whole DI harness a launch and a park+resume need;
>    a second copy of it would be ~200 duplicated lines that drift. The stub `IServiceScopeFactory` §9.2
>    suggests is unnecessary: the `RunContext` is reachable through two doubles the harness already
>    registers. **Launch** — `FakePlanner.PlanAsync` receives the `ctx` (orchestrator calls
>    `BeginRunAsync` at `:73`, `PlanAsync` at `:91`), so a `PlanContext` capture reads
>    `ctx.WorkspaceRoot` directly. **Resume** — a resume does not plan (D1), so `FakeVerifier` gained
>    `SeenWorkspaceRoots` beside its existing `SeenCompletedSteps`, and `BuildLauncher` gained a
>    trailing-defaulted `FakeVerifier? verifier = null` (the precedent that file documents for
>    `appSettings`). Verify **is** reachable on the resumed fallback harness — measured, one entry.
>    Neither the source-scan fallback nor any weakened form was used.
> 2. **The resume site had to be re-shaped, not just re-argued.** `:315-316` DISCARDED its canonical path
>    (`_ = SafeFolderPath.Canonicalize(...)`). It is now assigned to a local and passed, so both sites hand
>    the executor the same spelling of the same directory. Recomputing it at the call would have drifted
>    on any link or 8.3 component in the base dir.
> 3. **Four doc comments were falsified, not two.** Besides `Initialize`'s `<para>` and the inline at
>    `:206-209`: the workspace-creation comment at `:157-161` said *"Real deliverables go to the assistant
>    files folder (see the Initialize call below), so this directory holds only ephemeral run temp"*, and
>    `BeginRunAsync`'s B3 comment said *"Still null today — `_workspaceRoot` is only ever set by
>    Initialize's reserved (currently unused) parameter."* Both rewritten. `HeadlessTurnExecutor.cs:55`,
>    `RunContext.WorkspaceRoot` and `TaskAmbient`'s `<param>` are still TRUE and were left alone.
>
> Minor: the "a `GetTempPath()` root cannot see the guard" citation is plan **R1**, not R14 (R14 is the
> timeline-ordering row). Same mis-citation in T-G1-2 above. The test comments cite R1.

### 9.3 G3

**`tests/Pia.Wpf.Tests/Services/RunWorkspaceServiceTests.cs` — NEW.** Uses `FakeGitProcessRunner`
(`tests/Pia.Wpf.Tests/Services/FakeGitProcessRunner.cs`) and a temp `runsBaseDirOverride`.

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G3-1 | `CopyMode_CopiesTheSourceTree_SoTheRunCanReadExistingFiles` | **REGRESSION** | `IsGitInstalled = false` (F1): source has `a.md` + `sub\b.md` → both exist in the workspace, mode is `Copy`, metadata `sourceRoot` == the source. **This is B6's whole justification as a fact.** | make copy mode create an empty directory → red |
| T-G3-2 | `CopyMode_ExcludesTheVaultAndIgnoredTrees` | **REGRESSION** | source has `Vault\memory\m.md`, `.git\config`, `bin\x.dll`, `node_modules\p\i.js`, `keep.md` → only `keep.md` is in the workspace. `Assert.False(File.Exists(...))` per exclusion (never `Assert.Equal(0, count)` — xUnit2013). | drop the vault exclusion → the Vault assertion reds |
| T-G3-3 | `CopyMode_OverTheFileCap_ReturnsNull_AndLeavesNoWorkspace` | **REGRESSION** | write `MaxProvisionedFiles + 1` tiny files → `ProvisionAsync` returns **null** and `<runsBase>\<runId>` does not exist. Pins B6's "a partial tree is worse than no isolation". | remove the cap check → returns a workspace |
| T-G3-4 | `WorktreeMode_AddsAWorktreeOnTheRunBranch` | **REGRESSION** | `FakeGitProcessRunner.RepoAt(source)` + `rev-parse --verify HEAD` exit 0 → mode `Worktree`, `BranchName == $"pia/run/{runId}"`, and the recorded calls contain a `worktree` request whose arguments are `["worktree","add",<runRoot>,"-b",$"pia/run/{runId}"]`. Assert the argument **list**, not a substring. | flip the gate to always-copy → red |
| T-G3-5 | `WorktreeGate_DegradesToCopy_OnEveryFaultInTheList` | **REGRESSION** | `[Theory]` over **F1–F9** of B11's table, one row each, driven by the fake's `Responder`: every row yields mode `Copy` and a usable workspace, and **no** row throws or returns null. This is the executable form of plan R16's "degrade to copy on any fault rather than failing the run". | make any single gate throw instead of degrading → that row reds |
| T-G3-6 | `Provision_IsIdempotent_SoAResumeLandsInTheSameWorkspaceWithTheSameTimestamp` | **REGRESSION** | provision, read `provisionedAtUtc`, provision again → same root, same mode, **same timestamp**, and (worktree mode) **no second `worktree add`** in the recorded calls. Without this a resume's promote set becomes "everything". | drop the metadata-reuse short-circuit → the timestamp changes |
| T-G3-7 | `Metadata_RoundTripsAtV1_AndAnUnknownVersionReadsAsNoWorkspace` | **GUARD** | serialize/read-back; then overwrite the file with `{"v":99,…}` → `DescribeAsync` is null and `PromoteAsync` returns null **without deleting anything**. The B5 restrictive degrade. | — |
| T-G3-8 | `TearDown_WorktreeMode_RemovesTheWorktree_AndNeverDeletesTheBranch` | **REGRESSION** | recorded calls contain `["worktree","remove","--force",<runRoot>]` against `mainWorktree`; contain **no** `branch -D` / `branch -d`; the metadata file is gone. | replace teardown with `rmdir` → the `worktree remove` assertion reds |
| T-G3-9 | `TearDown_WhenWorktreeRemoveFails_FallsBackToRmdirThenPrune` | **REGRESSION** | fake returns exit 1 for `worktree remove` → the directory is gone **and** a `["worktree","prune"]` call was made. Closes R5's stale-registration half. | drop the fallback → the directory survives |
| T-G3-10 | `SweepOrphanMetadata_PrunesAndDeletesAMetadataFileWhoseDirectoryIsGone` | **REGRESSION** | write a `Worktree` metadata file with no sibling directory → after the sweep the file is gone and a `prune` was issued. Add a **positive control**: a metadata file whose directory **does** exist is left alone. Without the control, a sweep that deleted everything would pass. | delete the orphan pass → the file survives |
| T-G3-11 | `GitToolHandler_ResolvesTheRepoAgainstTheAmbientWorkspaceRoot` | **REGRESSION** | in `GitToolHandlerWorkspaceRootTests.cs` (NEW): ambient `WorkspaceRoot = <runRoot>`, fake `RepoAt(<runRoot>)` → `git_status` succeeds, and the recorded request's `WorkingDirectory` is `<runRoot>`, and its `CeilingDirectory` is the **parent of `<runRoot>`** (R17 — a ceiling pointing at the assistant folder's parent does not constrain a cwd under `%LOCALAPPDATA%`). | revert `baseRoot` to `_currentFolder` → the working directory assertion reds; revert the ceiling → the ceiling assertion reds |
| T-G3-12 | `GitToolHandler_MutatingTool_StillPassesContainment_AfterTheApprovalAwait` | **REGRESSION** | the §6.2 trap, as a fact: ambient `WorkspaceRoot = <runRoot>`, prepare `git_commit`, then **clear `TaskAmbient.Current`** (simulating the deferred closure's lost ambient flow) and `await pending.Execute()` → it does **not** return `OutsideSandboxRefusal`. | make `IsInsideSandbox` read the ambient instead of the captured root → red |
| T-G3-13 | `GitToolHandler_WithNoAmbient_IsByteIdenticalToTodaysContainment` | **GUARD** | the existing `GitToolHandlerContainmentTests` must pass **unmodified**; add one explicit row asserting the interactive TOCTOU re-guard still refuses after a runtime re-point of `AssistantFilesFolder`. If any existing containment test needs editing, the change altered semantics and is wrong. | — |
| T-G3-14 | `ProvisioningFailure_DoesNotFailTheRun_AndTheLegacySettlePathIsIntact` | **REGRESSION** | B16, both halves. (a) a fake `IRunWorkspaceService` returning null → `LaunchAsync` completes and the run settles `Completed`, **not** `Failed` with `"workspace setup failed"`. (b) with `workspaces: null` and an unwritable `runsBaseDirOverride` (point it at a file, not a directory), the original `try/catch → FailAsync` still fires and the run is `Failed` — the non-vacuity control that keeps (a) from passing on a launcher whose settle path was simply deleted. | make `ProvisionAsync`'s null result call `FailAsync` → (a) reds; delete the legacy `try/catch` → (b) reds |

### 9.4 G4

**`tests/Pia.Wpf.Tests/Services/RunWorkspacePromotionTests.cs` — NEW**

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G4-1 | `Promote_CopiesOnlyWhatTheRunWrote` | **REGRESSION** | copy-mode workspace with an untouched copied-in `a.md` (mtime < `provisionedAtUtc`) and a run-written `new.md`: destination gains `new.md`; `a.md`'s destination `LastWriteTimeUtc` is **unchanged**; `Promoted == 1`. | promote everything → `a.md`'s mtime moves |
| T-G4-2 | `Promote_SkipsAByteIdenticalDestination` | **REGRESSION** | workspace file newer than `provisionedAtUtc` but byte-identical to the destination → `Skipped == 1`, `Promoted == 0`, destination mtime unchanged. | drop the identity check → `Promoted == 1` |
| T-G4-3 | `Promote_NeverOverwritesAFileTheUserChangedDuringTheRun` | **REGRESSION** | destination file's mtime > `provisionedAtUtc` **and** the workspace copy differs → destination content is **unchanged**, `Conflicts == 1`. The B7 conflict rule; the one that protects a real user edit. | drop the conflict branch → the destination is overwritten |
| T-G4-4 | `Promote_NeverDeletesAtTheDestination` | **GUARD** | delete `a.md` inside the workspace → after promotion `a.md` still exists at the destination. Pins "promote is not sync". | — |
| T-G4-5 | `Promote_WorktreeMode_CopiesNothing_AndReportsTheBranch` | **REGRESSION** | `Promoted == 0`, `BranchName == "pia/run/<id>"`, destination unchanged, and `RunWorkspaceRedirects.Resolve` records **no** redirect for it. Plan D5b. | make worktree mode fall through to the copy path → the destination changes |
| T-G4-6 | `Promote_WithUnreadableMetadata_PromotesNothing_AndKeepsTheWorkspace` | **GUARD** | garbage in the metadata file → null result, workspace intact, destination untouched. B5's restrictive degrade. | — |
| T-G4-7 | `Promote_WhenTheSourceRootNoLongerResolvesInsideTheAssistantFolder_IsSkipped` | **REGRESSION** | relocate `AssistantFilesFolder` between provision and promote → null result, nothing written. B9. | drop the containment re-check → files land in a folder the run never saw |

**`tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs` — extend (all existing facts unmodified)**

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G4-8 | `CleanRun_Promotes_AfterVerify_AndBeforeCompleteAsync` | **REGRESSION** | a recording fake `IRunWorkspaceService` + a recording run service: the observed call order is `VerifyAsync` → `PromoteAsync` → `CompleteAsync`. Assert the **order**, not just that each happened. Construct the orchestrator **with** the service — no existing orchestrator test supplies it, so there is no inherited coverage. | move `SafePromote` after `SafeComplete` → red |
| T-G4-9 | `TheSingleTurnFallbackArm_AlsoPromotes` | **REGRESSION** | `PlanResult.Fallback` → `PromoteAsync` was called, before `CompleteAsync`. **§0.5: this arm returns early at `:120` and never reaches the terminal-settle block, and it settles Complete BEFORE EndRun.** Omitting it is the batch's most likely silent hole and every launcher-harness test rides this path. | **Two neutralizations, run both.** (i) `SafePromote` absent from **both** arms → this fact must fail; that is its red-before-green. (ii) The call present on the main arm **only** → it must *still* fail while T-G4-8 stays green; that is its discrimination property, and it is what proves the fact catches the omission it exists for instead of riding on T-G4-8's coverage. |
| T-G4-10 | `ACancelledOrFailedRun_DoesNotPromote_AndKeepsItsWorkspace` | **REGRESSION** | `[Theory]` over cancelled / step-failed / verify-degraded-to-failed → `PromoteAsync` never called, `TearDownAsync` never called. Plan D3's "completed auto, else offer". | promote unconditionally → red |
| T-G4-11 | `APromotionFault_DoesNotFailTheRun` | **GUARD** | the fake throws from `PromoteAsync` → the run still settles `Completed`. Failure-isolated bookkeeping. | — |
| T-G4-12 | `WithNoWorkspaceService_TheLoopIsByteIdenticalToToday` | **GUARD** | orchestrator built with `workspaces: null` → every existing assertion holds. The pin that the trailing-optional param changed nothing. | — |

**`tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherWorkspaceTests.cs` — extend**

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G4-13 | `Sweep_KeepsANonTerminalRunsWorkspace_ButRemovesASettledOneAfterTheTerminalWindow` | **REGRESSION** | four directories: (a) no run row → removed; (b) `Failed` run, `LastWriteTimeUtc` 8 days old → removed; (c) `Failed` run, 1 day old → **kept** (the publish offer is still live); (d) `WaitingForInput` run, 8 days old → **kept** (30-day floor, still resumable). (c) and (d) are the non-vacuity controls — a sweep that deleted everything would pass on (a)+(b) alone. | revert to the single 30-day predicate → (b) reds |
| T-G4-14 | `ChatDeleted_CancelsAnInFlightRunBeforeTearingDownItsWorkspace` | **REGRESSION** | hold a run inside the orchestrator, delete its chat → the run's CTS is cancelled and teardown is requested. Await the fire-and-forget through a completion the fake service signals; **no `.Result`/`.Wait()`** in the body (xUnit1031). B13 / plan R4. | remove the cancel → the assertion on cancellation reds |

**`tests/Pia.Wpf.Tests/ViewModels/RunProgressPublishTests.cs` — NEW** (ViewModel level only; plan R11 —
**do not** add a frame-pushing View test to the `WpfApplicationStatic` collection)

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G4-15 | `AFailedRunWithUnpublishedFiles_OffersPublish` | **REGRESSION** | fake service reports `HasUnpublishedFiles: true` → `CanPublish` true, `PublishCommand.CanExecute()` true. | hardcode `CanPublish => false` → red |
| T-G4-16 | `ACompletedRun_OffersNothing` | **GUARD** | a promoted run has no workspace → `HasUnpublishedFiles` false → `CanPublish` false. | — |
| T-G4-17 | `Publish_PromotesThenTearsDown_AndClearsTheOffer` | **REGRESSION** | call order `PromoteAsync` → `TearDownAsync`; afterwards `CanPublish` is false and `PublishNote` is the localized count line. | drop the teardown → the order assertion reds |
| T-G4-18 | `Publish_Fault_DoesNotThrow_AndLeavesTheOfferStanding` | **GUARD** | the fake throws → no exception escapes, `CanPublish` stays true, `PublishNote` names a failure. | — |
| T-G4-19 | `WorktreeOutcome_SurfacesTheBranchName` | **REGRESSION** | outcome `Worktree` + branch → `OutputBranchName` set, `HasOutputBranch` true. Plan D5b's "the panel must say so", at the only level a test can reach. | drop the projection → red |
| T-G4-20 | `WithNoWorkspaceService_ThePanelIsUnchanged` | **GUARD** | `workspaces: null` → `CanPublish` false, `OutputBranchName` null, and every existing `RunProgressViewModel` fact passes unmodified. | — |

**Localization / architecture**

| # | Test | Kind | Asserts |
|---|---|---|---|
| — | `LocalizationTests` (existing) | **GUARD** | catches a missing `loc:Str` key and en/de/fr parity for all five new keys. No new test needed. |
| — | `DiRegistrationTests` (existing) | **GUARD** | fails unless `IRunWorkspaceService` is registered in `Bootstrapper`. |
| — | `NamingConventionTests` (existing) | **GUARD** | `RunWorkspaceService` ends with an allowlisted suffix; the three new records must live in `Pia.Services.Interfaces`, not `Pia.Services` (§0.3). |
| T-G4-21 | `RunWorkspaceRuleTests.TheLauncherTearsDownThroughTheWorkspaceService` | **GUARD** | source-scan `HeadlessRunLauncher.cs`: every workspace-removal site references `TearDownAsync`, and `Directory.Delete` appears only inside the documented `_workspaces is null` fallback. **Non-vacuity: assert the scan found at least the expected number of `TearDownAsync` occurrences (`Assert.True(count >= 3)`) and that the file was actually read (`Assert.NotEmpty(source)`)** — otherwise renaming the method or moving the file turns the rule green. Resolve the path from the repo root the way the existing localization rule does. |
| T-G4-22 | `RunWorkspaceRuleTests.RunWorkspaceModeStartsAtNoneZero` | **GUARD** | reflect `RunWorkspaceMode`: a member named `None` with value 0 exists and no two members share a value. Mechanizes the append-only rule for the one new enum (the same shape as `ToolAutonomyRuleTests.EveryPersistedGateEnumStartsAtUnknownZero`). |

### 9.5 G5

**`tests/Pia.Wpf.Tests/Helpers/RunWorkspaceRedirectsTests.cs` — NEW**

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G5-1 | `Resolve_ReturnsTheRecordedPath_WhileItStillExists` | **REGRESSION** | phase 1 of plan D8: file present in the workspace → the input path comes back unchanged even with a redirect recorded. | resolve unconditionally → red |
| T-G5-2 | `Resolve_RedirectsToThePromotedCopy_OnceTheWorkspaceIsGone` | **REGRESSION** | phase 2: record the redirect, delete the workspace file, create `<dest>\sub\a.md` → `Resolve` returns the destination path. | remove the redirect lookup → red |
| T-G5-3 | `Resolve_ReturnsTheInput_WhenNeitherPathExists` | **GUARD** | a since-deleted file with no redirect → the input, unchanged; `ShellLauncher` then no-ops as it does today. | — |
| T-G5-4 | `Record_RefusesAWorkspaceRootOutsideTheRunsRoot` | **REGRESSION** | `Record(Path.GetTempPath(), someDest)` installs nothing → a path under that temp root resolves unchanged. The containment gate on the registry. | drop the gate → red |
| T-G5-5 | `Record_EvictsPastTheEntryCap` | **GUARD** | `MaxEntries + 4` records → the dictionary never exceeds `MaxEntries`, and the newest is still resolvable. Bounds process-local state. | — |

**Interactive isolation**

| # | Test | Kind | Asserts | Neutralize |
|---|---|---|---|---|
| T-G5-6 | `PlannedRun_WritesIntoItsWorkspace_NotTheAssistantFolder` | **REGRESSION** | in `LiveTurnExecutorPlannedRunTests` (real orchestrator + real `LiveTurnExecutor` + real `ChatSession`, the harness Batch 04 already built there): a step's `write_file` lands under the provisioned workspace and **not** in the settings folder. Bound it with `Task.WhenAny` + a timeout the way that file's existing end-to-end fact is — a gate that prompts would otherwise hang the suite instead of failing it. | drop `BuildSpec`'s `WorkspaceRoot:` → red |
| T-G5-7 | `PlannedRun_WithAWorkingDirectory_DoesNotNarrowTwice` | **REGRESSION** | session `WorkingDirectory = "sub"`, workspace provisioned from `<folder>\sub` → a step writing `a.md` lands at `<workspace>\a.md`, not `<workspace>\sub\a.md`; and `ctx.WorkingSubpath` is null. B6's one-narrowing rule. | pass `WorkingDirectory` through as the ambient subpath → red |
| T-G5-8 | `AnOrdinaryChatTurn_StillWritesToTheAssistantFolder` | **GUARD** | the non-`Planned` path (`ChatSession.cs:307`) is untouched: a plain turn's `write_file` lands in the settings folder. The "no interactive regression" pin. | — |
| T-G5-9 | `PlannedRun_PromotesOnCleanCompletion` | **REGRESSION** | the same live harness: after a clean run the file exists at `<folder>\sub\a.md` and the workspace is gone. **Executor parity with T-G4-8** — a promotion that only fires for Headless is a defect. | make `SafePromote` bail when the executor is not `HeadlessTurnExecutor` → red |
| T-G5-10 | `AProvisioningFailure_DoesNotFailTheTurn` | **GUARD** | fake `ProvisionAsync` returns null → the run proceeds and writes to the assistant folder (today's behaviour). Bookkeeping must never fail a turn with a user watching. | — |
| T-G5-11 | `StepTurnSpec_WorkspaceRoot_DefaultsToNull` | **GUARD** | a `StepTurnSpec` built with named arguments and no `WorkspaceRoot` has `WorkspaceRoot is null`. Trivial, and it is the pin that every existing spec construction still means "no isolation" (R20). | — |

---

## 10. Manual-smoke debt (no automated coverage exists)

Fold these into `00-OVERVIEW.md`'s Rank-1 list in the roadmap pass — Phase 3 **lengthens** that list.

1. **A real headless run writing into an isolated workspace and promoting on success.** The whole point of
   06, and the one item no unit test substitutes for.
2. **Worktree mode against a real repo**: the run branch exists, the agent's commits are on it, the working
   tree is untouched, and `git worktree list` after the run shows **no** stale registration.
3. **Copy mode against a non-repo folder** (the ordinary case), plus the degrade path with git absent.
4. **A failed run's publish offer** — decline it, confirm the workspace is retained; accept it, confirm the
   files land at the right paths.
5. **An interactive run's file chips** — clicked *during* the run and again *after* promotion (plan D8's two
   phases). The chip is inside a deferred `ItemTemplate` no test materializes (R24).
6. **The panel's new lines** — the Publish button and the "Output is on branch …" line. `RunProgressPanel.xaml`
   is parsed by nothing.
7. **DE/FR without clipping** for all five new strings.
8. **A run that reads an existing file** — the B6 copy-in, end to end. `list_files` inside a run must show
   the user's real files and must **not** show `Vault\`.
9. **The memory tools during an isolated run** — `recall`/`remember` still work (they do not read
   `WorkspaceRoot`), which is what makes the vault exclusion acceptable.

---

## 11. Guardrails, instantiated for this batch

- **Failure-isolated bookkeeping.** Every new failure site swallows: `ProvisionAsync` degrades
  worktree→copy→null (B11 F1–F10); `PromoteAsync`/`TearDownAsync`/`DescribeAsync` return null or no-op and
  never throw; `SafePromote` is a `Safe*` wrapper; `RunWorkspaceRedirects.Resolve` never throws; an
  interactive provisioning fault leaves the turn on today's path (B15/§8). Emitting, reading or cleaning a
  workspace must never fail a run.
- **No interactive regression.** `ChatSession.cs:307` (the ordinary turn) is untouched; the
  `SetState(WaitingForTool)` → `finally` → `Running` bracket is untouched; the card gate is untouched
  (R27); `@Files` and the prompt preview keep `_currentFolder` (R3). T-G5-8 is the pin.
- **Executor parity.** Promotion lives in the executor-agnostic orchestrator and reads
  `ctx.WorkspaceRoot`, which **both** executors assign (B3/§8). Tested on both paths: T-G4-8 headless,
  T-G5-9 live.
- **Off-thread `RunChanged` stays marshaled.** `RunProgressViewModel`'s new outcome read runs off-thread and
  applies through `_uiContext.Post`, the same mechanism `ApplyTimelineAsync` uses. No `System.Windows`
  reference is added to any ViewModel — the ratchet exempts only `AssistantViewModel`.
- **Append-only persisted enums and ordinals.** No `AgentRunState` member is added; `Paused(4)` stays Batch
  08's. `RunWorkspaceMode` is new, starts at `None = 0` and is serialized by NAME. `GrantEnvelopeVersion`
  and the envelope shape are untouched.
- **Privacy-first logging.** `Information`-and-above carries counts, booleans, run ids and enum values only.
  Paths and filenames go through `SensitiveWarning` (the highest DEBUG-erased severity — there is **no**
  `SensitiveError`, R30) or a scoped `#if DEBUG`. Never log the metadata document's contents, the branch
  name (it embeds nothing sensitive, but the surrounding lines do carry paths — keep the whole family at
  counts), or a promoted file's name at `Information`. `SafeUrl` does not apply to paths.
- **Three resx files.** Six keys × 3 (B15, corrected — `Run_Publish_Failed` is the sixth), real DE and FR.
  `ViewStrings.Designer.cs` untouched.
- **Code style.** 4-space C#, 2-space XAML, `_camelCase` fields, `var` for apparent types,
  `[ObservableProperty]`/`[RelayCommand]`, namespaces `Pia.*` (not `Pia.Wpf.*`). Every new `.cs` and `.md`
  file **CRLF**.
- **Do not push, merge or rebase.** Commit locally; the branch is unpushed by owner decision and ~49
  commits ahead of `origin`.

---

## 12. Commit plan

One commit per group, each independently buildable and gate-green. §0.6 says which boundaries are also
*shippable*.

| # | Group | Commit subject | Contents | Green means |
|---|---|---|---|---|
| 1 | G1 | `Runs: carve the run workspace out of the guard and carry its root to verify` | `AssistantWorkspace.RunsRoot`, the second allowed island + `CanonicalizeAllowedIsland`, `RunContext.WorkspaceRoot`, `AgentVerifier`'s ctx-first root + the corrected ownership comment, `HeadlessTurnExecutor.BeginRunAsync`'s assignment; T-G1-1…6 | the whole existing suite is untouched — the root is still null in production, so this commit changes no behaviour |
| 2 | G2 | `Runs: unattended runs write into their isolated workspace` | `workspaceRoot: runRoot` at `:209` and `:339`, the two rewritten doc comments, the re-rooted escape suite; T-G2-1…3 | **first behaviour change.** `FilesToolHandlerWorkspaceEscapeTests`' assertions are unmodified — only its fixture root moves. If an assertion needs editing, containment changed and the commit is wrong. **Not shippable alone (§0.6).** |
| 3 | G3 | `Runs: provision a workspace as a git worktree or a bounded copy` | `IRunWorkspaceService` + `RunWorkspaceService` (provision + teardown + orphan sweep), the launcher's two provisioning sites, `GitToolHandler`'s ambient-aware root, the `SensitivePathGuard` comment amendment, the `Bootstrapper` registration; T-G3-1…13 | `HeadlessRunLauncherTests` and `GitToolHandlerContainmentTests` pass **unmodified**. Still not shippable — nothing promotes. |
| 4 | G4 | `Runs: promote a completed run's work, and offer to publish a failed one's` | `PromoteAsync` + `DescribeAsync`, `SafePromote` on **both** orchestrator terminal arms, the state-aware sweep, `OnChatsChanged`'s cancel-first teardown, the panel affordance + 6 loc keys ×3 + XAML; T-G4-1…22 | `AgentRunOrchestratorTests` and the existing `RunProgressViewModel` facts pass unmodified — which holds **only because** the new ctor params are trailing and defaulted. If one of those files needs an edit, a parameter was made required; fix the parameter, not the test. **Shippable.** |
| 5 | G5 | `Runs: isolate interactive planned runs and resolve file chips on open` | `ChatSessionManager` provisioning, `LiveTurnExecutor`'s root, `StepTurnSpec.WorkspaceRoot`, `ChatSession`'s per-step ambient, `RunWorkspaceRedirects`, the two `PiaFileChip` edits; T-G5-1…11 | `ChatSessionManagerTests`, `ChatSessionStepTurnTests`, `ChatSessionStateMachineTests` and `LiveTurnExecutorPlannedRunTests` pass unmodified. **The intended stopping point for Batch 06.** |

---

## 13. Open questions (none blocking)

1. **The no-isolation degrade is silent to the user.** B6's cap and B11's F10 both fall back to writing
   straight into the assistant folder — today's behaviour, so not a regression, but the run's own record does
   not say which mode it ran in and the panel cannot tell the user. The metadata document already carries
   `degraded`; surfacing it needs a sixth loc key and a panel line, which is more UI than this batch's
   affordance budget. Whoever takes it should surface `RunWorkspaceMode.None` in the same place B15 renders
   the branch line.
2. **`@Files` autocomplete lists the real folder during an isolated interactive run** (R3/§8). The picker
   runs outside any turn, so there is no ambient to read and no run to key off; wiring it would mean
   pushing the active run's workspace into `IFilesToolHandler.ActiveUiWorkingSubpath`'s sibling, i.e. a
   second piece of view-driven handler state. Named, not fixed.
3. **Copy mode against a repo (the git-absent degrade) leaves the workspace without `.git`**, so the model
   sees `FreshFolderHint` and may `git_init` inside its own workspace. Contained and harmless, and the
   resulting `.git` is ignore-pruned on the way out — but the agent's commits then live in a directory that
   teardown deletes. Release-note item.
4. **A child run (Batch 07 G10) must INHERIT the parent's workspace, not provision its own.** Nothing in 06
   prevents `ProvisionAsync(childRunId, …)` from creating a second workspace whose promotion would race the
   parent's. `RunContext.WorkspaceRoot` is the seam: a child's context takes the parent's value and the
   child never calls the provisioner. The other half of the same constraint is B7's invariant —
   **promotion is terminal-only, once per workspace**, which is what lets a single `provisionedAtUtc` decide
   the promote set even across a park → resume. A child that promoted the shared workspace before its parent
   did would make the parent's later promotion re-copy the child's output over anything the destination has
   accumulated since. Recorded here because G10's builder will not otherwise think of either.
5. **Worktree mode never merges** (plan D5b), so a user who never looks at `git branch` never sees the
   output. The publish affordance says *where* it is; it does not offer to merge, and it should not from an
   unattended path. A "review / merge this branch" flow is its own batch, with conflict UI.
6. **The 7-day terminal retention is a judgement call, not a measurement.** It bounds plan D3's unanswered
   offer (B12), and it is the only number in this batch nobody has data for. If it turns out to be short,
   it is one constant.
