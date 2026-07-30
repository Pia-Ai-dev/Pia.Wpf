# Batch 04 — Autonomy policy (`PolicyJson`) · IMPLEMENTATION SPEC

Executable spec derived from [`04-autonomy-policy.md`](04-autonomy-policy.md) plus a full re-read of the code
it touches. Branch: `feature/agent-run-spine`. **Design step only — no production code was written for this
document.**

Gate for the implementing agent (the bar moved; see §0.0):

```
dotnet build -t:Rebuild -v:n                 # 0 Error(s), 0 Warning(s)
dotnet build -t:Rebuild -c Release -v:n      # 0 Error(s), 0 Warning(s)
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"
                                             # failed: 0   (baseline 2232 total / 0 failed / 1 skipped)
```

Read the warning count off MSBuild's `N Warning(s)` summary line — at `-v:n` every warning prints twice, so
grepping the log double-counts. Never pass `--nologo` to `dotnet test`. Known flake, do not chase:
`TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock` (4/4 green in isolation; if it is the ONLY
failure, re-run its class isolated and move on).

**Sequencing: this batch lands BEFORE Batch 03** on the same working tree. 03's spec
([`03-audit-timeline.impl.md`](03-audit-timeline.impl.md)) is written against the tree this batch leaves
behind — **its §0.1 is the authoritative inventory of what this batch changed under it**, and D15 below settles
03's persisted decision vocabulary on purpose.

---

## 0. Corrections to the 04 spec (read this first)

### 0.0 The build bar is ABSOLUTE ZERO, not "194 pre-existing"

`00-OVERVIEW.md` still says "0 errors, 194 warnings, all pre-existing". That was true once; `6cdd4c9` took the
build to zero and CLAUDE.md now makes zero a commit-ready gate. Measured at `dda6703`, both configurations:
**0 Error(s), 0 Warning(s)**. **186 of the old 194 were xUnit analyzer warnings in `tests/Pia.Wpf.Tests`**
(xUnit2013, xUnit1031 and friends), and this batch adds ~50 tests. `Assert.Equal(0, x.Count)` → xUnit2013 (use
`Assert.Empty`); `.Result`/`.Wait()` in a test body → xUnit1031. New tests must add **zero**.

### 0.1 "bump `v` with a fallback that stays restrictive" is FALSE — and it silently ESCALATES authority

`04-autonomy-policy.md:15` instructs: *"add policy members alongside `grantedWrites`, bump `v` with a fallback
that stays restrictive"*. Verified against the code, that instruction is wrong in the dangerous direction.

The reader compares strictly: `if (envelope is null || envelope.V != GrantEnvelopeVersion || envelope.GrantedWrites is null) return null;`
(`HeadlessRunLauncher.cs:544`, `GrantEnvelopeVersion = 1` at `:46`). `null` means *"apply the resume floor"*,
and the floor is `ResumeFloorGrants = ["write_file"]` (`:43`), applied at the single resume site (`:269-274`).

So bumping to `v:2` makes **every envelope written before this batch unreadable**, and for the
**interactive-origin** envelope — which is deliberately `grantedWrites: []` (`ChatSessionManager.cs:749`) — the
"restrictive fallback" is **wider than the launch**: a run that launched with *"every write goes through a card
the user clicks"* would resume unattended with card-free `write_file`. That is precisely the escalation D1
closed. `HeadlessRunLauncherTests.cs:465` already pins `{"v":99,…}` → `null`, so the mechanism is not
hypothetical.

**The safe path is additive at `v:1`.** `GrantEnvelopeJsonOptions` (`:48-51`) sets **only**
`PropertyNamingPolicy = CamelCase` — no `UnmappedMemberHandling.Disallow`, no converters — so
`System.Text.Json` silently skips unknown members **in both directions**: an older build still restores grants
from a document carrying `policy`, and a newer build still reads a document without it. That is a stronger
compatibility guarantee than any version bump could give, and it is free. **Do not touch
`GrantEnvelopeVersion`.** (D1.)

### 0.2 "Both producers write one" is incomplete — there are THREE producers

`00-OVERVIEW.md:185-187` says both producers write an envelope. There is a third:
`BackgroundAssistantTurnRunner.cs:89-91` creates `RunShape.SingleTurn` rows with **no** `PolicyJson` → NULL
(pinned by `AgentRunServiceTests.CreateAsync_WithoutPolicyJson_StaysNull`). Those runs are safe only
incidentally — `TryBeginResumeAsync` filters on `State = WaitingForInput` and **not** on shape
(`AgentRunService.cs:309-326`), and only `AgentRunOrchestrator.cs:403-407` ever calls `PauseAsync`, which a
SingleTurn run never reaches. This batch does **not** give SingleTurn runs a policy (they have no plan, no
steps and no resume path); it records the fact so a later batch that makes them parkable knows it inherits a
NULL-policy row. (D10 makes NULL mean *today's behaviour*, which is the right answer for them.)

### 0.3 There is no single approval gate — so "the M3 floor is a hard floor" is only *currently* true

`04-autonomy-policy.md:27` speaks of *"the approval gate"* singular, and `:42` guarantees *"no policy can
auto-approve a destructive MCP call"*. There are **two disjoint permission models** sharing exactly two
`public static` helpers:

| | Interactive | Unattended |
|---|---|---|
| gate | `ChatSession.HandleToolCall` (`ChatSession.cs:824`) | `BackgroundAssistantTurnRunner.HandleToolCallAsync` (`:364`) |
| vocabulary | per-`(PluginId, ToolName)` persisted grants + action cards | a flat `HashSet<string>` of tool names |
| the M3 floor | `!ToolPermissionService.IsDeleteLike(tool)` inside the `eligible` expression (`:883`) — suppresses AUTO-approval only; the user may still click *Allow once* | `IsDeleteLike && IsExternalTool` → refuse outright, inside the granted branch (`:391`) |
| `IToolPermissionService` injected? | yes (`ChatSession.cs:32`) | **no** — nowhere in either headless file |

The floor is therefore **two independent expressions over the same static name heuristic**, with no shared
`CanAutoApprove()`. It is structural *against grant-widening* (`:391` sits inside the `grantedWrites.Contains`
branch with no override flag) and **not** structural in general: a policy branch added anywhere else bypasses
both, and `IsDeleteLike`/`IsPresumedExternalDeleteLike` are `static` on the **concrete**
`ToolPermissionService`, absent from `IToolPermissionService` (`:9-29`), so nothing can substitute or tighten
them via DI. **Making that guarantee structural is part of this batch** (D5, D6, §9 T-FLOOR-*).

### 0.4 The interactive-origin resume is WIDER than its launch when the serializer faults

`ChatSessionManager.cs:748-750` must swallow a serializer fault (bookkeeping guardrail) and degrades to
`policyJson = null`; `HeadlessRunLauncher.cs:269-273` maps `null` → `["write_file"]`. So the **only** thing
standing between an interactive-origin parked run and card-free unattended writes is one
`JsonSerializer.Serialize` call succeeding — and this batch makes that document richer, i.e. more likely to
fault. Fixed here by D12 (a hardcoded fallback literal instead of `null`).

### 0.5 Voice mode is an ungated write path — this batch closes it

`AssistantViewModel.cs:1481-1509`: route, return reads, then `await pendingAction.Execute()` at `:1496` with
no eligibility check, no grant check, no card and no destructive floor. Comment: *"Auto-approve write
operations in voice mode (no dialog)"*. `write_file`, `delete_file`, `forget` and every destructive MCP tool
execute silently. Nothing in the suite pins this behaviour either way
(`git grep -n 'HandleVoiceModeToolCall\|VoiceMode' -- tests/` returns only DI and dispatcher comments), so it
can be fixed without breaking a test — and until it is fixed, *"the policy governs all writes"* is false.
**Closed by D13**, as the last commit group so it is droppable without stranding the rest.

### 0.6 `scheduled-research` is a built-in plugin whose card claims to be an external tool

`ActionCardBuilder.cs:31-40` derives the category from `PluginName` with `_ => ActionCardCategory.Mcp`.
`"scheduled-research"` is a **built-in** plugin (`BuiltInPluginDefaults.cs:73`) missing from that switch, and
`create_scheduled_research` / `update_scheduled_research` / `delete_scheduled_research` **do** return pending
actions (`ScheduledJobToolHandler.cs:69-73`). Consequences, all verified from source:

- the card is titled *"External tool"* (`ActionCard_Category_Mcp`, `ActionCardBuilder.cs:161-162`);
- `IsAutoApprovable` is `true` (`:102-103`, `category == Mcp && !isDestructive`) → the triad renders an
  **"Always allow"** button on a built-in scheduling tool;
- `Details` is parsed with `JsonHelper.ParseToDetails` (`:75-77`) although
  `ScheduledJobToolHandler.cs:185-197` builds `"Label: value"` **text** — so the detail rows render wrong too;
- the gate computes `eligible = false` and silently degrades AlwaysAllow → AllowOnce (`ChatSession.cs:952`).

The gate defends; **the UI lies**. Nothing pins the categorization, so it is invisible to the suite. Fixed
here because a policy that keys on a tool *class* cannot have two contradictory answers for what class a tool
is (D4). This is scope this batch **found**, not scope it invented.

---

## 1. Verified recon (re-read 2026-07-30; cite these, not the batch brief)

| # | Fact | Location |
|---|---|---|
| R1 | `GrantEnvelopeJsonOptions` sets only `PropertyNamingPolicy = CamelCase`. Unknown members are silently skipped both ways. | `HeadlessRunLauncher.cs:48-51` |
| R2 | `GrantEnvelope` is a `private sealed class` with `int V`, `List<string>? GrantedWrites`, `string? Trigger`. `Trigger` is documented *"diagnostics only; never consulted to widen a grant"*. | `HeadlessRunLauncher.cs:564-574` |
| R3 | `SerializeGrantEnvelope(grants, trigger)` is `internal static` and shared by BOTH producers (the launcher via `TrySerializeGrantEnvelope`, and `ChatSessionManager` directly). `InternalsVisibleTo Pia.Wpf.Tests` (`Pia.Wpf.csproj:69`) lets tests call it. | `:519-527`, `ChatSessionManager.cs:749` |
| R4 | `TryRestoreGrantEnvelope` returns `null` for blank/unparseable/`V != 1`/`GrantedWrites is null`, honours a present-but-EMPTY list, trims + drops blanks + dedupes `OrdinalIgnoreCase`, and never throws. | `:536-558` |
| R5 | Resume: `grants = TryRestoreGrantEnvelope(run.PolicyJson); if (grants is null) { log; grants = ResumeFloorGrants; }` → `executor.Initialize(workspaceRoot: null, grants, provider)`. ONE resume site; both resume entry points funnel through `ResumeAsync`. | `:268-274`, `:308` |
| R6 | Launch: `grants = req.GrantedWrites ?? HeadlessRunRequest.DefaultGrantedWrites` (`["write_file"]`), persisted via `TrySerializeGrantEnvelope`, then `Initialize(workspaceRoot: null, grants, provider)`. The launch **never reads** the envelope back. | `:124-128`, `:184` |
| R7 | `ResumeFloorGrants` (`:43`) and `IHeadlessRunLauncher.DefaultGrantedWrites` (`:38`) are two INDEPENDENT literals that happen to be equal. Changing one does not change the other. | as cited |
| R8 | `AgentRunService` stores `PolicyJson` verbatim on the INSERT only, reads it back at index 12, logs `policy={HasPolicy}` presence only, never parses it. There is **no UPDATE path** anywhere. | `AgentRunService.cs:92`, `:128`, `:139-140`, `:604` |
| R9 | `ExtraJson` is clobbered wholesale by `CompleteAsync`/`FailAsync`/`PauseAsync` and set to NULL by `TryBeginResumeAsync`. Not a viable home for anything that must survive a park. | `AgentRunService.cs:238`, `:268`, `:295`, `:321` |
| R10 | `AutoApproveAllowlist` = `{create_object, create_todo, create_reminder, append_to_list}`, `StringComparer.Ordinal` (case-SENSITIVE). `_grantedKeys` is a default-comparer tuple set (case-sensitive). `grantedWrites` is `OrdinalIgnoreCase`. `IsDeleteLike`/`BuiltInDestructiveTools` are `OrdinalIgnoreCase`. | `ToolPermissionService.cs:24-30`, `:34`, `BackgroundAssistantTurnRunner.cs:142`, `HeadlessTurnExecutor.cs:36` |
| R11 | `IsDeleteLike` = literal `forget` (ci) OR any `DestructiveStems` **substring** (delete, remove, purge, drop, wipe, erase, destroy, truncate), ci. Self-documented as *"a NAME HEURISTIC, not a boundary"*. | `ToolPermissionService.cs:58-59`, `:96-99` |
| R12 | `IsPresumedExternalDeleteLike` = `IsDeleteLike && !BuiltInDestructiveTools.Contains`. Only consumer: `ScheduledJobToolHandler.ParseGrantedTools` (`:372-388`), which strips such names at job-create time and appends an English note via `DescribeRejectedGrants`. | as cited |
| R13 | Interactive eligibility, one expression: `_permissions.IsAutoApproveEligible(tool) \|\| (_pluginService.IsMcpTool(tool) && !ToolPermissionService.IsDeleteLike(tool))`. `IsMcpTool` is called **bare** — no try/catch, unlike the headless twin. | `ChatSession.cs:882-883` |
| R14 | Auto-approve bypass: `if (eligible && _permissions.IsGranted(pluginId, tool))` → build a **pre-resolved** card, add it to `message.ActionCards` **BEFORE** executing (audit trace, never silent), log name + plugin id only, then `ExecuteAndReport()`. | `ChatSession.cs:912-919` |
| R15 | `decision = await card.WaitForUserDecisionAsync()` bracketed by `SetState(WaitingForTool)` / `finally` → `Running`; `catch (TaskCanceledException)` maps a cancelled card to `Decline`. `AlwaysAllow` on an INELIGIBLE tool executes once and persists NO grant. | `ChatSession.cs:924-962` |
| R16 | The card's own eligibility copy: `IsAutoApprovable = _permissions.IsAutoApproveEligible(ToolName) \|\| (category == Mcp && !isDestructive)`. `IsAutoApprovable` is `init`-only and drives the triad-vs-pair button set (`ActionCardInfo.cs:151-165`), never `IsDestructive`. | `ActionCardBuilder.cs:102-103` |
| R17 | A THIRD notion of destructive, UI-only, enforced nowhere: `isDestructive = IsDeleteLike \|\| ToolName is "git_switch" or "git_restore" or "git_stash"`. | `ActionCardBuilder.cs:45-47` |
| R18 | `IPluginService.IsMcpTool(name)` = `_toolNameRoutes.TryGetValue(name, out var h) && h is McpPluginToolHandler`, under `lock`. Returns **false** for an unrouted name (i.e. "unknown" reads as built-in). | `PluginService.cs:290-294` |
| R19 | `RegisterHandler` is LAST-REGISTRATION-WINS with no collision detection; `UnregisterHandler` removes routes BY NAME. An MCP server can shadow a built-in tool name and, on unregister, delete the built-in's route as collateral. | `PluginService.cs:200-224`, `:651-662` |
| R20 | MCP is 100% pending: `McpPluginToolHandler.HandleToolCallAsync` returns `(null, pending)` with `Details` = the raw serialized tool arguments, so every MCP call flows one of the two gates. | `McpPluginToolHandler.cs:96-140` |
| R21 | Unattended gate: reads always run; `grantedWrites.Contains(name)` → the B2 floor check → execute; else the deny string *"…is a write action not granted to this background job. Do not retry."* | `BackgroundAssistantTurnRunner.cs:377-403` |
| R22 | `IsExternalTool` wraps `IsMcpTool` in try/catch and returns **true** (external) on any exception — fail-closed. Pinned by `BackgroundAssistantTurnRunnerTests.cs:306`. | `BackgroundAssistantTurnRunner.cs:415-426` |
| R23 | Executor parity is STRUCTURAL on the unattended side: `HeadlessTurnExecutor.Initialize(workspaceRoot, grantedWrites, providerOverride)` fills `_grantedWrites` and delegates to the same `_engine.RunExchangeAsync(..., _grantedWrites, ...)`. A change in `BackgroundAssistantTurnRunner` covers Headless AND SingleTurn for free. | `HeadlessTurnExecutor.cs:36`, `:91-98`, `:275` |
| R24 | `StepTurnSpec` is a positional record whose last member `UseGoalVerbatim = false` is already defaulted; `LiveTurnExecutor.BuildSpec` and the one test factory (`ChatSessionStepTurnTests.cs:39`) both construct it with **named** arguments — so a defaulted appended member breaks nothing. | `IAgentTurnExecutor.cs:34-46`, `LiveTurnExecutor.cs:121-134` |
| R25 | `ChatSessionManager` already `await`s `GetSettingsAsync()` at `:772` inside the same Planned branch that writes the envelope (`:748-753`) and constructs `LiveTurnExecutor` (`:768`). One settings read serves all three. | as cited |
| R26 | `SyncScheduledJob.GrantedTools` crosses the sync wire and is stored unvalidated (`GrantedTools = sync.GrantedTools ?? []`); a scheduled AgentTask job's list becomes the launch grant set. `AgentRuns` themselves never sync (no `SyncAgentRun` DTO). | `SyncMapper.cs:1013`, `ScheduledJobBackgroundService.cs:198` |
| R27 | Settings precedent (Batch 05): plain `bool` on `AppSettings` (`:180`) + `[ObservableProperty]` + `OnXChanged` autosave guarded by `_isLoading` + load + save (`AssistantSettingsViewModel.cs:303-309`, `:332`, `:480`) + CheckBox (`Views/SettingsViews/AssistantView.xaml:411-422`) + 3 resx keys in all three locales. Agent knobs are deliberately **absent** from `SyncSettings`. | as cited |
| R28 | `ActionCardCategory` is a UI-only enum (`ActionCardInfo.cs:16-26`), never persisted; its only non-builder consumers are four `DataTrigger`s in `ActionCardControl.xaml:64-73` (Memory has none — it is the default). Adding a member is safe. | as cited |
| R29 | Three handlers convert a pending action into an immediate result on their **error** paths (`return (await pending.Execute(), null)`), so `Execute()` can run upstream of every gate. Constrained to error paths today. | `TodoToolHandler.cs:84`, `ReminderToolHandler.cs:73`, `ScheduledJobToolHandler.cs:83` |
| R30 | `ExecutePendingActionAsync` is declared on all seven handler interfaces, implemented seven times, and **called from nowhere**. Both gates invoke `pendingAction.Execute()` directly. It is NOT a chokepoint. | e.g. `FilesToolHandler.cs:218`, `McpPluginToolHandler.cs:145` |

---

## 2. Decisions

### D1 — The envelope stays at `v:1`; the policy is an **additive member**

`GrantEnvelope` gains one member: `public RunAutonomyPolicyDto? Policy { get; set; }` → serialized as
`"policy"`. `GrantEnvelopeVersion` is **not** touched. Justification: §0.1 + R1.

Rejected: **bump to `v:2` with a restrictive fallback** (the batch file's instruction). The reader's `!=`
equality makes the "fallback" the `{write_file}` floor, which is *wider* than the interactive-origin launch —
a silent escalation of every in-flight interactive run. Rejected: **version the reader to accept `V <= Current`**.
It works, but it buys nothing over an additive member and it re-opens the question of what a v2 document means
to a v1 reader on another device — a question `System.Text.Json`'s unknown-member skipping already answers
correctly.

Rejected: **a second column / a second blob.** `PolicyJson` is the only durable per-run TEXT (R9 rules out
`ExtraJson`), the DDL is bare `TEXT NULL` with no CHECK (`SqliteContext.cs:296`), and `AgentRuns` has no
ALTER-based migration block — so one string it is.

### D2 — Policy document shape: exactly ONE member

```jsonc
{ "v": 1,
  "grantedWrites": ["write_file"],
  "trigger": "Schedule",
  "policy": { "autoApproveClasses": ["Memory", "Todo", "Reminder", "Scheduling", "Files"] } }
```

`autoApproveClasses` is the **only** member. Deliberately **no** `alwaysPrompt` and **no** `neverAuto`, which
the batch file suggests (`:34`):

- `alwaysPrompt` is redundant — it is the **default** for everything not named in `autoApproveClasses`. A
  second list that can only restate the default is a second thing to keep consistent and a second thing to get
  wrong.
- `neverAuto` in the document would be a **config-expressed floor**, and a floor a document can express is a
  floor a document can shrink. The floor stays in code (D5/D6) precisely so no value of the document can
  loosen it. This is the concrete mechanism behind guardrail `:42`.

Reader contract, mirroring R4 exactly:

- absent / `null` / `[]` → **today's behaviour, byte for byte** (no class is auto-approved);
- an entry that does not parse to a known `ToolClass` name → **dropped** (restrictive), logged as a count;
- comparison is `OrdinalIgnoreCase` against the enum member names;
- never throws — any exception yields `null`, which is *today's behaviour*, not the grant floor. **The policy
  reader's failure mode and the grant reader's failure mode are deliberately different**: an unreadable grant
  list must fall back to *something the run can work with*; an unreadable policy must fall back to *nothing*.

### D3 — The policy keys on a derived **class**, never on tool names

Rejected: **name lists.** Against the actual inventory that is a footgun three ways. (a) Names are the unit the
**existing** grant list already uses (R21), so a name-keyed policy would be a second, subtly-different name
list over the same vocabulary with a different comparer (R10) — `Create_Todo` is eligible unattended and
ineligible interactively **today**, and a third name set would add a third answer. (b) Tool-name routes are
last-wins with no collision detection (R19): an MCP server that registers `create_todo` inherits every
name-keyed authority granted to the built-in. (c) A name list authored at job-create time cannot know the MCP
tool set at fire time — the exact reason `IsPresumedExternalDeleteLike` exists (R12).

Rejected: **both** (classes plus a name escape hatch). The escape hatch is the whole attack surface and the
class list already expresses everything the settings surface can author (D9). A per-run editor (a later batch)
may add names; it must then answer (a)–(c).

### D4 — ONE classifier, used by both gates **and** the card

New `ToolClassifier.Classify(string? pluginName, bool isExternalRoute)` → `ToolClass`. **The route wins over
the name**: `isExternalRoute` (from `IPluginService.IsMcpTool`, R18) short-circuits to
`ToolClass.External`; otherwise the plugin name maps, with `_ => ToolClass.Unknown` — **not** `External`. That
single change is what fixes §0.6, because `"scheduled-research"` becomes a named class instead of falling into
the MCP bucket.

`ActionCardBuilder` has no `IPluginService` and must keep working when the gate does not tell it the class, so
`IActionCardBuilder.Build` gains a trailing **optional** `ToolClass? toolClass = null`; when null the builder
classifies from `PluginName` alone (`isExternalRoute: false` → an unrecognised name yields `Unknown`, which
renders as the external-tool card, i.e. today's shape for a genuinely external plugin). Both gates pass the
authoritative value. Optional-trailing keeps every existing `Build(...)` call site and every
`ActionCardBuilderTests` construction compiling.

Rejected: **`ActionCardBuilder` takes `IPluginService`.** It would give the card a route lookup it does not
need, add a DI edge from a presentation-shaped service into the plugin subsystem, and still be the *second*
classifier in the codebase whenever a gate forgot to pass its own.

### D5 — ONE shared resolver — this is the mechanism that makes the M3 floor **structural**

New `static class ToolAutonomy` (pure, no DI, no state) with exactly two public members:

```csharp
public static bool IsStandingGrantOfferable(ToolClass toolClass, string toolName, bool isAllowlisted);
public static ToolGateVerdict Resolve(in ToolGateInput input);
```

`ToolGateInput` is a `readonly record struct`:

```csharp
public readonly record struct ToolGateInput(
    ToolGateSurface Surface,
    string ToolName,
    ToolClass ToolClass,
    bool IsAllowlisted,        // ToolPermissionService.IsAutoApproveEligible(name)      — computed by its owner
    bool HasStandingGrant,     // IToolPermissionService.IsGranted(pluginId, name)       — computed by its owner
    bool IsNamedGrant,         // grantedWrites.Contains(name)                           — computed by its owner
    RunAutonomyPolicy? Policy);
```

`ToolGateVerdict(ToolGateOutcome Outcome, ToolGateDecision Decision)`, with
`ToolGateOutcome { AutoRun, Prompt, Refuse }`.

The floor lives in exactly one place, and the policy is an **input** to the function that applies it:

```
Resolve(input):
    // FLOOR (M3) — evaluated FIRST and unconditionally. No branch below can reach AutoRun past it.
    if (IsDeleteLike(ToolName) && ToolClass == External)
        return Surface == Interactive ? (Prompt, Unknown)            // today's interactive semantics: still promptable
                                     : (Refuse, DeniedDestructiveFloor);

    // POLICY (D6) — additive over classes, and NEVER over a delete-like name.
    if (Policy.Covers(ToolClass) && !IsDeleteLike(ToolName))
        return (AutoRun, AutoApprovedPolicy);

    // EXISTING AUTHORITY — unchanged semantics, per surface.
    if (Surface == Interactive && IsStandingGrantOfferable(...) && HasStandingGrant)
        return (AutoRun, AutoApprovedStandingGrant);
    if (Surface != Interactive && IsNamedGrant)
        return (AutoRun, GrantedByName);

    return Surface == Interactive ? (Prompt, Unknown) : (Refuse, DeniedNotGranted);
```

Why this is structural and not merely current:

1. **One function.** Both gates' only auto-approve path is one `Resolve` call. The grep rule in §9 T-ARCH-1
   keeps it that way: `ChatSession.cs` and `BackgroundAssistantTurnRunner.cs` must contain **zero** references
   to `IsAutoApproveEligible`, `IsDeleteLike` or `IsMcpTool` outside the two lines that feed `ToolGateInput`.
   (`ActionCardBuilder.cs:45` legitimately calls `IsDeleteLike` for warning text and is **out of scope** for
   that rule — a blanket "nobody calls `IsDeleteLike`" rule would be wrong, and a rule scoped to the two gate
   files is checkable by source scan.)
2. **The floor is first, not last.** A reviewer can see in three lines that no policy value reaches past it.
   Ordering it first (rather than ANDing it into each branch) is deliberate: an added branch below inherits the
   floor by construction, whereas an added branch *beside* an ANDed floor would not.
3. **The policy cannot express the floor.** D2 gives it one additive member over classes; there is no member a
   document can set that appears anywhere near the floor's condition.
4. **Exhaustive test.** T-FLOOR-1 enumerates *every* `ToolGateSurface` × *every* `ToolClass` × *every*
   `DestructiveStems` member × `{policy covering that class, policy covering all classes, no policy}` ×
   `{granted, not granted}` and asserts `Outcome != AutoRun` for every external delete-like name. That is the
   whole policy value space, not a sample.

`Resolve` also keeps **surface asymmetry honest**: `Refuse` is unreachable on the `Interactive` surface
(T-FLOOR-2 asserts it). That is not a shortcoming — it is today's semantics
(`ChatSessionStateMachineTests.cs:575` pins *"GrantedDestructiveMcpTool_IsNotAutoApproved_StillPrompts"*), and
this batch is not in the business of tightening the path where a human is looking at the card.

Rejected: **an `IToolAutonomyService` on the DI graph.** The decision is a pure function of its inputs; a
service adds a mock in every gate test, an interface a future policy could substitute (defeating the point),
and a null-reference path where today there is none. Rejected: **methods on `IToolPermissionService`.** That
interface owns *persisted grants*; the resolver owns *a decision*, and giving the grant store a decision method
would put the floor back behind a substitutable interface (§0.3's exact problem).

### D6 — A class grant NEVER covers a delete-like tool

`Policy.Covers(class) && !IsDeleteLike(name)`. So the policy can never be the reason a destructive tool ran, on
either surface — a strictly stronger statement than the M3 floor, which is external-only.

This is load-bearing. `ToolClass.Files` contains both `write_file` and `delete_file`; without this rule a
preset that says "let the agent write files" would hand an unattended run card-free `delete_file`, and the M3
floor would **not** stop it (it is external-only by design — `BackgroundAssistantTurnRunner.cs:388-390`
explains why, and `BackgroundAssistantTurnRunnerTests.cs:259` pins it).

**Every pinned test survives, and the builder must not "fix" them:**

- `GrantedBuiltInDeleteFile_StillExecutes_TheFloorIsExternalOnly` (`:259`) is a **named** grant
  (`IsNamedGrant`), not a policy class grant — untouched, still executes.
- `GrantedDestructiveMcpTool_IsNotAutoApproved_StillPrompts` (`ChatSessionStateMachineTests.cs:575`) and
  `ForgedGrant_OnIneligibleTool_StillPrompts_AndDoesNotGrant` (`:627`) stay `Prompt` because the interactive
  surface has no `Refuse`.
- `ToolPermissionServiceTests` / `ToolPermissionServiceDeleteLikeTests` are untouched: **no comparer, no
  allowlist and no stem list changes anywhere in this batch.**

Rejected: **an `autoApproveDestructive` member.** That is literally the policy loosening the floor.

### D7 — The resolver takes grant lookups as **booleans**, computed by their existing owners

`IsAllowlisted`, `HasStandingGrant` and `IsNamedGrant` are `bool` inputs, never lookups the resolver performs.
This sidesteps the comparer asymmetry (R10) entirely: the resolver never re-implements a set membership test,
so no comparer changes and no behaviour drift. **Do not move those lookups inside `ToolAutonomy`** — doing so
would force one comparer on three sets that today disagree, silently changing which tools are eligible.

The resolver's *own* comparisons — the class-name match in `Policy.Covers` and `IsDeleteLike` — are
`OrdinalIgnoreCase` (`IsDeleteLike` already is, R11). State that in the code comment.

### D8 — Interactive `IsMcpTool` gets the fail-closed guard its headless twin already has

`ChatSession.cs:883` calls `_pluginService.IsMcpTool(tool)` **bare** while
`BackgroundAssistantTurnRunner.IsExternalTool` (`:415-426`) wraps it and returns `true` on any exception. A
throw at `:883` propagates out of `HandleToolCall` through `AiClientService`'s tool loop (which only filters
`IsToolNotSupportedError`) and fails the whole turn — safe-direction but not graceful, and **untested**. Since
this batch adds a classifier call to the same expression, the guard becomes mandatory: the classifier's input
is resolved through one private `IsExternalTool(toolName)` helper on `ChatSession` with the same try/catch and
the same `return true`. A policy read that faults must never fail a turn (failure-isolated bookkeeping).

### D9 — The default is authored as a **preset**; the envelope stores the resolved **class list**

Settings gets **one CheckBox**, following R27's shape exactly:
`AppSettings.AgentRunAutoApproveBuiltInWrites` (bool, default **false**).

`RunAutonomyPolicy.FromSettings(settings)`:

- `false` → `null` (no policy member is written; the document is byte-identical to today's).
- `true` → `autoApproveClasses = [Memory, Todo, Reminder, Scheduling, Files]`.

Excluded from the preset, each for a stated reason:

- **`Git`** — `git_switch` / `git_restore` / `git_stash` shed uncommitted work and are **not** delete-like by
  name, so neither the floor (D5) nor D6 would stop them. They are the one family where the card's own
  `isDestructive` (R17) is wider than the enforced rule, and a preset must not be the thing that closes that
  gap silently.
- **`External`** — a policy must never auto-approve server-defined tools *as a class*. Today an external tool
  is grantable one at a time by an informed click (R16); a class grant would make an MCP server's next tool
  addition auto-approved retroactively.
- **`Unknown`** — by construction: `Covers(Unknown)` is hardcoded `false`, so an unrecognised class name in a
  document (D2) cannot become authority.

**The envelope stores the resolved list, not the preset name.** Consequences, all wanted: a later per-run
editor can express any class set with no document change; an older build reading a newer document simply drops
class names it does not know (D2); and flipping the setting can never retroactively change a run that is
already parked (D10).

Rejected: **a multi-select class list in settings.** A checkbox-per-class needs 8 label strings ×3 locales, and
five of the eight are traps (see the exclusions). Rejected: **an ordered `AutonomyLevel` enum in the
document.** An ordinal ladder invites `>=` comparisons, which is exactly how a floor gets loosened by a
value that did not exist when the comparison was written; a set of named classes has no ordering to abuse.
Rejected: **per-provider or per-persona.** Autonomy is about the *user's* tolerance, not the model's
competence, and every existing `Agent*`/`Scheduled*` knob is global (R27).

### D10 — The resume **never** consults settings; the envelope is the run's authority of record

At launch the policy is resolved from settings (there is nothing else — R6: the launch never reads the
envelope). At resume it comes **only** from `run.PolicyJson`. A settings flip between park and *Continue* must
not widen a parked run — that is the same escalation class D1 closed, arriving by a different door.

`TryRestoreGrantEnvelope` keeps its exact signature and contract (R4) and gains a sibling:
`internal static RunAutonomyPolicy? TryRestorePolicy(string? policyJson)` — same options object, same
never-throws discipline, returns `null` for absent/unreadable **and** for a `policy` member with no usable
class. So:

| `PolicyJson` | grants | policy |
|---|---|---|
| absent / garbage / `v:99` | `ResumeFloorGrants` (unchanged) | `null` → today's behaviour |
| `v:1`, no `policy` member (**every run written before this batch**) | restored (unchanged) | `null` → today's behaviour |
| `v:1` + `policy` | restored (unchanged) | restored |

Note the asymmetry is deliberate and is the whole backward-compatibility guarantee: **an unreadable envelope
loses the policy before it loses the grant list**, and losing the policy is always the restrictive direction.
Pinned by T-ENV-3.

### D11 — The interactive producer resolves the policy ONCE

`ChatSessionManager`'s Planned branch already awaits `GetSettingsAsync()` at `:772` (R25). Move that read
**above** the envelope write at `:748` and use the one `AppSettings` instance for all three consumers: the
envelope, the `LiveTurnExecutor` constructed at `:768`, and the `RunProfile` at `:773`. Two reads could
straddle a settings save and give the persisted envelope and the live executor different policies — a run whose
record disagrees with what it actually did.

### D12 — The interactive envelope's fault fallback is a hardcoded literal, not `null`

```csharp
/// The exact document SerializeGrantEnvelope([], AgentRunTrigger.User) produces with no policy. Used when
/// serialization FAULTS: `null` would make the resume fall back to ResumeFloorGrants ({write_file}), which is
/// WIDER than what this launch granted (nothing). Pinned byte-for-byte by T-ENV-5.
internal const string InteractiveEmptyEnvelopeJson = """{"v":1,"grantedWrites":[],"trigger":"User"}""";
```

`ChatSessionManager.cs:748-750` becomes `catch { log; policyJson = HeadlessRunLauncher.InteractiveEmptyEnvelopeJson; }`.
Two tests, not one (the advisor's point, and it is the right one):

- **shape pin** — `Assert.Equal(InteractiveEmptyEnvelopeJson, SerializeGrantEnvelope([], AgentRunTrigger.User))`.
  Without this, a later member addition rots the literal silently while the round-trip test still passes.
- **round-trip pin** — `TryRestoreGrantEnvelope(InteractiveEmptyEnvelopeJson)` is non-null and **empty**, and
  `TryRestorePolicy` of it is `null`.

Note the literal must carry **no** `policy` member: an interactive run's *fault* fallback grants nothing and
auto-approves nothing, which is strictly narrower than its success path when the setting is on. Narrower on
fault is the only acceptable direction.

`ChatSessionManagerTests.cs:152-183` (`StartPlannedTurn_PersistsAnEmptyGrantEnvelope_SoParkingCannotWidenAuthority`)
keeps passing unchanged — the success path still writes `SerializeGrantEnvelope([], User, policy)`, whose grant
list is still empty.

### D13 — Voice mode is routed through the same resolver (a stated behaviour change)

`AssistantViewModel.HandleVoiceModeToolCall` (`:1481`) gains the gate it never had, on
`ToolGateSurface.Voice`:

- reads (non-null `result`) — unchanged, always run;
- writes — `ToolAutonomy.Resolve` with `Surface = Voice`, `IsAllowlisted` from `IToolPermissionService`,
  `HasStandingGrant` from `IsGranted(pluginId, name)`, `IsNamedGrant: false`, `Policy` = the settings preset
  (there is no run, so there is no envelope — **stated, not implied**);
  > **AS BUILT — the voice allowlist branch additionally requires `ToolClass != External`, and that was a
  > must-fix, not a polish.** `IsAutoApproveEligible` is `AutoApproveAllowlist.Contains(toolName)` with **no
  > `PluginId`**, and tool-name routes are last-wins (R19), so an MCP server exposing a tool named exactly
  > `create_todo` — a plausible name for a task-tracker server — would have auto-run on the **one surface with no
  > card and no transcript entry**, sending the user's spoken content to a third party with only a log line
  > behind it. The interactive gate was never exposed, because it additionally requires a standing grant, i.e. a
  > card the user clicked. This narrows nothing intended: all four allowlisted names are built-ins, and a
  > *renamed* built-in classifies as `Unknown`, not `External`. Pinned at both levels — a resolver theory over
  > all four names asserting the same name on a **built-in** route still auto-runs (so the discriminator is
  > provably the class, not the name), plus a ViewModel-level fact driving `HandleVoiceModeToolCall`.
- `AutoRun` → execute exactly as today (same token-map re-init);
- `Prompt` → *refuse*, because voice has no card surface: `"Denied: '{tool}' needs your confirmation and voice
  mode cannot show an approval card. Ask me again in the chat window."` — an English literal, like both
  existing gate refusals; it goes to the **model**, not the UI, so it needs no resx key;
- `Refuse` → the destructive-floor refusal string.

**What changes for a user:** `create_todo` / `create_reminder` / `create_object` / `append_to_list` still run
(allowlist) — **as built, only when they route to a built-in**; the same name coming from an MCP server refuses,
per the note above. Anything the user has already "always allowed" still runs (standing grant). With the new setting
on, the preset classes still run. `write_file` with the setting off, and **every** `delete_*` / `forget` /
destructive MCP tool, now refuse instead of executing silently. That is a tightening, it is the documented
intent of the whole gate, and no test pins the old behaviour (§0.5).

`ToolGateSurface.Voice` exists rather than reusing `Unattended` so Batch 03 can tell a voice refusal from a
scheduled one without a second column, and so this refusal's *string* can differ (it names a remedy).

Rejected: **leave it out of scope.** Then the batch's own guardrail (*"no policy can auto-approve a destructive
MCP call"*) would be false in the app's most surprising surface, and the roadmap would have to say so.
Rejected: **surface a card in voice mode.** That is a UX feature (a spoken confirmation flow), not a policy
layer, and it needs `ActionCardInfo` to reach a view that does not exist.

### D14 — This batch does **not** change the create-time grant filter, the comparers, or the allowlist

`ScheduledJobToolHandler.ParseGrantedTools` (R12) keeps stripping presumed-external delete-like **names** at
job-create time. It gains **nothing**: the policy is not authored in the scheduled-job tool this batch (a job
carries `GrantedTools` names, and the launcher resolves the policy from settings at fire time, D9/D10).
Recorded as an open item (§13.2) because a later per-run editor that lets the model author a policy must give
the class list the identical treatment or it becomes the new escalation route.

Likewise untouched: `AutoApproveAllowlist` and its `Ordinal` comparer, `_grantedKeys`, `grantedWrites`'
`OrdinalIgnoreCase`, `DestructiveStems`, `BuiltInDestructiveTools`. Zero behaviour change to any of them, which
is why every existing permission test passes unmodified.

### D15 — The decision vocabulary is defined HERE, for Batch 03

`ToolGateDecision` is **append-only, never renumbered, never reused** and is persisted by Batch 03 as an
INTEGER column. It is defined in this batch so 03's enum is complete on the first try:

| Ordinal | Member | Produced by |
|---|---|---|
| 0 | `Unknown` | never written by this build; the render value for an ordinal an older/newer DB carries |
| 1 | `AutoApprovedStandingGrant` | interactive: offerable + `IsGranted` (`ChatSession.cs:912`) |
| 2 | `AutoApprovedPolicy` | either surface: the run policy covers the class |
| 3 | `GrantedByName` | unattended/voice: the name is in `grantedWrites` |
| 4 | `ApprovedOnce` | user clicked *Allow once* |
| 5 | `ApprovedAlways` | user clicked *Always allow* (grant persisted) |
| 6 | `DeclinedByUser` | user clicked *Decline* |
| 7 | `CardCancelled` | `TaskCanceledException` from the card (new chat / retry / scope dispose) — **not** a user denial |
| 8 | `DeniedNotGranted` | no user present and nothing authorized it |
| 9 | `DeniedDestructiveFloor` | the M3 floor |
| 10 | `UnknownTool` | `RouteToolCallAsync` returned null |
| 11 | `AutoApprovedAllowlist` | voice: the curated additive allowlist authorized the call. Voice-only — interactive needs a standing grant as well, unattended has no allowlist. **Batch 03 must carry 0–11, not 0–10.** |

`ToolGateSurface { Unknown = 0, Interactive = 1, Unattended = 2, Voice = 3 }` and `ToolClass`
(`Unknown = 0, Memory = 1, Todo = 2, Reminder = 3, Files = 4, Git = 5, Scheduling = 6, External = 7,
Ingest = 8`) are append-only on the same terms. `Ingest` maps the SEVENTH built-in plugin name — `ingest` is in
`BuiltInPluginDefaults` too — and is deliberately absent from `PresetClasses`. It is unreachable today
(`IngestToolHandler` runs inline and returns no pending action, so it never reaches a gate or a card) and is
kept, not dropped, for two reasons: the enum is append-only, so removing the member would be a renumber; and
`ToolClassifier.Classify` must map every built-in name, or ingest recreates exactly the
`scheduled-research`-as-external bug of §0.6 the day it starts gating. Batch 03 therefore needs a label for a
class it will never observe. `ToolGateOutcome` is **not** persisted (it is a control-flow value) and needs no
ordinal discipline — say so in its doc comment so nobody "fixes" it into the persisted set.

Note `Unknown = 0` on all three persisted enums is the append-only guardrail's other half: an unknown ordinal
must render as *unknown*, never throw and never be re-mapped.

---

## 3. Files to touch

| File | Change |
|---|---|
| `src/Pia.Wpf/Models/ToolGateEnums.cs` | **new (CRLF)** — `ToolClass`, `ToolGateDecision`, `ToolGateSurface`, `ToolGateOutcome` |
| `src/Pia.Wpf/Models/RunAutonomyPolicy.cs` | **new (CRLF)** — the policy record + `Covers` + `FromSettings` + the DTO the envelope serializes |
| `src/Pia.Wpf/Services/ToolClassifier.cs` | **new (CRLF)** — D4 |
| `src/Pia.Wpf/Services/ToolAutonomy.cs` | **new (CRLF)** — D5, the one resolver |
| `src/Pia.Wpf/ViewModels/Models/ChatSession.cs` | gate → one `Resolve` call; private fail-closed `IsExternalTool` (D8); pass `spec.Policy` down `RunModelExchangeAsync` |
| `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs` | gate → one `Resolve` call; `RunExchangeAsync` gains a trailing optional `RunAutonomyPolicy? policy = null` |
| `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs` | `Initialize` gains a trailing optional `RunAutonomyPolicy? policy = null`; relays it into `RunExchangeAsync` |
| `src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs` | `StepTurnSpec` gains a trailing `RunAutonomyPolicy? Policy = null` (R24) |
| `src/Pia.Wpf/ViewModels/Models/LiveTurnExecutor.cs` | ctor gains a **trailing optional** `RunAutonomyPolicy? policy = null`; `BuildSpec` sets it |
| `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` | one settings read (D11); policy into the envelope + into `LiveTurnExecutor`; literal fallback (D12) |
| `src/Pia.Wpf/Services/HeadlessRunLauncher.cs` | `GrantEnvelope.Policy`; `SerializeGrantEnvelope` overload; `TryRestorePolicy`; `InteractiveEmptyEnvelopeJson`; launch + resume wiring |
| `src/Pia.Wpf/Services/ActionCardBuilder.cs` | classify via `ToolClassifier`; `IsAutoApprovable` via `ToolAutonomy`; `Scheduling` category + title/verb/details fixes |
| `src/Pia.Wpf/Services/Interfaces/IActionCardBuilder.cs` | `Build` gains a trailing optional `ToolClass? toolClass = null` |
| `src/Pia.Wpf/Models/ActionCardInfo.cs` | `ActionCardCategory.Scheduled` (appended — R28) |
| `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` | `HandleVoiceModeToolCall` → the resolver (D13) |
| `src/Pia.Wpf/Models/AppSettings.cs` | `AgentRunAutoApproveBuiltInWrites = false` |
| `src/Pia.Wpf/ViewModels/AssistantSettingsViewModel.cs` | declare + autosave hook + load + save (R27) |
| `src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml` | "Autonomy" section: header + CheckBox + description |
| `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | 4 keys each (3 settings + 1 card category) |

Do **not** hand-edit `ViewStrings.Designer.cs` (it has drifted; `loc:Str` resolves via `ResourceManager`).
Every new `.cs` file must be **CRLF**.

**Every new parameter in this batch is trailing and defaulted** — on `Build`, on `Initialize`, on
`RunExchangeAsync`, on `RunModelExchangeAsync`, on `StepTurnSpec`, **and on `LiveTurnExecutor`'s constructor**
(`RunAutonomyPolicy? policy = null`, documented as *"null ⇒ no per-run policy; today's behaviour"*). That is
not tidiness: it is what makes commit 2's and commit 4's "the existing suite passes unmodified" claim true, and
`LiveTurnExecutor` is hand-constructed with a **positional** argument list at `ChatSessionManager.cs:768` and in
`LiveTurnExecutorPlannedRunTests`, so a non-defaulted parameter there would force test edits into the middle of
a refactor whose whole proof is that no test needed editing.

**Correction, post-review.** This section originally claimed *"a forgotten argument at the one production call
site is caught by T-GATE-1, which drives the policy end-to-end through the live executor."* **That was false as
built.** T-GATE-1 lives in `ChatSessionPolicyGateTests` and constructs `StepTurnSpec(Policy: …)` by hand, so it
never reaches `ChatSessionManager`'s `new LiveTurnExecutor(…, policy)` or `LiveTurnExecutor.BuildSpec`'s
`Policy: _policy`. Both are trailing and defaulted, so dropping either **compiles** — and the run would then
revert to carding every write while its persisted envelope still recorded the preset classes, i.e. the
record-disagrees-with-behaviour case D11 exists to prevent. The headless twin was covered end to end
(`HeadlessRunLauncherTests.Launch_WithTheSettingOn_AutoApprovesAWriteWithNoNamedGrant`), so this was an
executor-PARITY gap in the coverage, not merely a thin spot.

Closed by two facts added in the review-close commit:

- `ChatSessionManagerTests.StartPlannedTurn_PersistsTheSettingsPolicyInTheEnvelope` — pins the manager's
  serialize argument (verified red by dropping it).
- `LiveTurnExecutorPlannedRunTests.PlannedRun_CarriesTheRunPolicyIntoTheGate_SoACoveredWriteAutoRuns` — a real
  orchestrator + real `LiveTurnExecutor` + real `ChatSession` gate; pins `BuildSpec`'s `Policy: _policy`
  (verified red by dropping it). It is deliberately **bounded** with a `Task.WhenAny` timeout: without the
  policy the gate prompts and the run blocks on a card nobody clicks, so the naive shape hangs the suite
  instead of failing it.

---

## 4. `ToolGateEnums.cs` — the shared vocabulary

```csharp
namespace Pia.Models;

/// <summary>
/// The family a tool belongs to, for autonomy-policy purposes. PERSISTED (Batch 03's timeline column and the
/// PolicyJson envelope's class names) → APPEND-ONLY: never renumber, never reuse, never rename a member
/// (the envelope stores member NAMES). An ordinal or a name this build does not know reads back as
/// <see cref="Unknown"/>, which <c>RunAutonomyPolicy.Covers</c> hardcodes to false.
/// </summary>
public enum ToolClass
{
    Unknown = 0,
    Memory = 1,
    Todo = 2,
    Reminder = 3,
    Files = 4,
    Git = 5,
    /// <summary>The built-in scheduled-job tools (plugin "scheduled-research").</summary>
    Scheduling = 6,
    /// <summary>An external, server-defined MCP tool. Derived from the ROUTE, never from a name.</summary>
    External = 7,
    /// <summary>The built-in ingest tool (plugin "ingest"). Runs inline, returns no pending action, so it
    /// never reaches a gate or a card today — the class exists so it cannot be silently treated as external
    /// the way scheduled-research was (§0.6). Excluded from PresetClasses.</summary>
    Ingest = 8,
}

/// <summary>Which gate asked. PERSISTED by Batch 03 → APPEND-ONLY.</summary>
public enum ToolGateSurface { Unknown = 0, Interactive = 1, Unattended = 2, Voice = 3 }

/// <summary>Why a tool ran or did not. PERSISTED by Batch 03 → APPEND-ONLY. See 04's D15 table.</summary>
public enum ToolGateDecision
{
    Unknown = 0,
    AutoApprovedStandingGrant = 1,
    AutoApprovedPolicy = 2,
    GrantedByName = 3,
    ApprovedOnce = 4,
    ApprovedAlways = 5,
    DeclinedByUser = 6,
    CardCancelled = 7,
    DeniedNotGranted = 8,
    DeniedDestructiveFloor = 9,
    UnknownTool = 10,
    /// <summary>The curated additive allowlist authorized the call. Voice-mode only (D13).</summary>
    AutoApprovedAllowlist = 11,
}

/// <summary>
/// What the caller must DO. Control flow only — NOT persisted and NOT append-only-constrained; adding or
/// reordering members here is safe. Kept separate from <see cref="ToolGateDecision"/> so the persisted audit
/// vocabulary is not hostage to a control-flow refactor.
/// </summary>
public enum ToolGateOutcome { AutoRun, Prompt, Refuse }
```

## 5. `RunAutonomyPolicy` + the envelope

```csharp
/// <summary>
/// A run's autonomy policy: the tool CLASSES this run may auto-approve without a card (interactive) or
/// without a named grant (unattended). Purely ADDITIVE — there is deliberately no "never" list, because a
/// floor a document can express is a floor a document can shrink; the floor lives in ToolAutonomy.Resolve.
/// A null policy, an empty class set, and a policy naming only classes this build does not know are all
/// exactly TODAY'S behaviour (04 D2).
/// </summary>
public sealed record RunAutonomyPolicy(IReadOnlyCollection<ToolClass> AutoApproveClasses)
{
    /// <summary>Unknown is hardcoded false: an unrecognised class NAME in a document must never become authority.</summary>
    public bool Covers(ToolClass toolClass)
        => toolClass != ToolClass.Unknown && AutoApproveClasses.Contains(toolClass);

    /// <summary>Null when the setting is off — so the envelope stays byte-identical to a pre-04 document.</summary>
    public static RunAutonomyPolicy? FromSettings(AppSettings settings) =>
        settings.AgentRunAutoApproveBuiltInWrites
            ? new RunAutonomyPolicy([ToolClass.Memory, ToolClass.Todo, ToolClass.Reminder,
                                     ToolClass.Scheduling, ToolClass.Files])
            : null;
}
```

`HeadlessRunLauncher`, additions only:

```csharp
    private sealed class GrantEnvelope
    {
        public int V { get; set; }
        public List<string>? GrantedWrites { get; set; }
        public string? Trigger { get; set; }

        /// <summary>Batch 04 autonomy policy. ADDITIVE at v:1 — V is deliberately NOT bumped (04 D1): the
        /// reader's `V != 1` equality would turn a bump into "every existing envelope unreadable → the
        /// {write_file} resume floor", which for an interactive-origin envelope (grantedWrites: []) is a
        /// WIDENING. GrantEnvelopeJsonOptions sets no UnmappedMemberHandling, so an older build skips this
        /// member and still restores the grants. WhenWritingNull is on THIS member only, so a policy-less
        /// document stays byte-identical to a pre-04 one (T-ENV-5/6).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PolicyDto? Policy { get; set; }
    }

    /// <summary>Wire shape of the policy. Class NAMES, not ordinals: a name an older build cannot parse is
    /// dropped (restrictive) instead of silently colliding with a member it does know.</summary>
    private sealed class PolicyDto { public List<string>? AutoApproveClasses { get; set; } }

    // Overload — the 3-arg form stays, so ChatSessionManagerTests and HeadlessRunLauncherTests compile.
    internal static string SerializeGrantEnvelope(
        IReadOnlyCollection<string> grants, AgentRunTrigger trigger, RunAutonomyPolicy? policy);

    /// <summary>Read the run's autonomy policy. Returns null — meaning "today's behaviour", NOT the grant
    /// floor — for an absent/unreadable envelope, an absent `policy` member, or a member whose class names
    /// this build does not recognise. Never throws. Unrecognised names are DROPPED and their COUNT logged
    /// (never the names: an MCP-adjacent string is not ours to log).</summary>
    internal static RunAutonomyPolicy? TryRestorePolicy(string? policyJson);

    internal const string InteractiveEmptyEnvelopeJson = """{"v":1,"grantedWrites":[],"trigger":"User"}""";
```

`SerializeGrantEnvelope` with `policy: null` must **omit** the member, not emit `"policy":null` — otherwise
T-ENV-5's byte pin and the "pre-04 document is byte-identical" claim both break. Achieve it with a
**per-member** `[JsonIgnore(Condition = WhenWritingNull)]` on `GrantEnvelope.Policy`, as shown above — **not**
with `DefaultIgnoreCondition` on `GrantEnvelopeJsonOptions`. The options object is shared by the serializer
*and* the deserializer and governs all four members; scoping the change to the one new member means nothing has
to be argued about `V` / `GrantedWrites` / `Trigger` at all. Either way, run
`HeadlessRunLauncherTests.GrantEnvelope_IsVersionedCamelCase_AndCarriesTheOriginTrigger` (`:484`) — it asserts
the literal substrings `"v":1`, `"grantedWrites"` and `Schedule`, and it is the tripwire if the emitted shape
moves.

## 6. The two gates

**Interactive** (`ChatSession.cs`, replacing `:882-883` and `:912`):

```csharp
            // ONE resolver for both gates (04 D5): the M3 floor lives inside Resolve and is evaluated
            // before any policy or grant branch, so no policy value can reach an auto-approval past it.
            // Grant lookups stay with their owners and arrive as bools (D7) — the three sets involved use
            // three different comparers today and this batch changes none of them.
            var toolClass = ToolClassifier.Classify(pendingAction.PluginName, IsExternalTool(tool));
            var offerable = ToolAutonomy.IsStandingGrantOfferable(
                toolClass, tool, _permissions.IsAutoApproveEligible(tool));
            var verdict = ToolAutonomy.Resolve(new ToolGateInput(
                ToolGateSurface.Interactive, tool, toolClass,
                IsAllowlisted: _permissions.IsAutoApproveEligible(tool),
                HasStandingGrant: _permissions.IsGranted(pluginId, tool),
                IsNamedGrant: false,
                Policy: policy));

            if (verdict.Outcome == ToolGateOutcome.AutoRun)
            {
                var autoCard = _actionCardBuilder.Build(pendingAction, tokenizationEnabled, autoApproved: true, toolClass);
                message.ActionCards.Add(autoCard);          // card BEFORE execute — audit trace, never silent
                _logger.LogInformation("Auto-approved {ToolName} ({Decision}, plugin {PluginId})",
                    tool, verdict.Decision, pluginId);
                return await ExecuteAndReport();
            }
```

`offerable` replaces `eligible` verbatim at the `AlwaysAllow` branch (`:952`) — the defensive
`if (eligible) GrantAsync(...)` keeps its exact meaning, so `ForgedGrant_OnIneligibleTool_…` (`:627`) stays
green. **`verdict.Outcome == Refuse` is unreachable here (T-FLOOR-2)**; write the `Refuse` arm anyway as
`throw new InvalidOperationException(...)`? **No** — a throw in the tool loop fails the turn. Write it as the
same code path as `Prompt` with a comment saying the interactive surface never produces `Refuse` and T-FLOOR-2
pins it. Degrading toward the card is the safe direction if that ever changes.

The card-before-execute ordering (`ChatSessionStateMachineTests.cs:511`) and the
`SetState(WaitingForTool)` → `finally` → `Running` bracket (`:276`) are untouched.

**Unattended** (`BackgroundAssistantTurnRunner.cs`, replacing `:382-403`):

```csharp
            var toolClass = ToolClassifier.Classify(pending.PluginName, IsExternalTool(pending.ToolName));
            var verdict = ToolAutonomy.Resolve(new ToolGateInput(
                surface, pending.ToolName, toolClass,
                IsAllowlisted: false,        // no allowlist unattended: there is no user to have curated it
                HasStandingGrant: false,     // persisted grants are an INTERACTIVE concept (see §0.3)
                IsNamedGrant: grantedWrites.Contains(pending.ToolName),
                Policy: policy));

            switch (verdict.Outcome)
            {
                case ToolGateOutcome.AutoRun:
                    _logger.LogInformation("Background turn executing {ToolName} ({Decision})", pending.ToolName, verdict.Decision);
                    return await pending.Execute();
                case ToolGateOutcome.Refuse:
                    _logger.LogWarning("Background turn refused destructive external tool {ToolName}", pending.ToolName);
                    return $"Denied: '{pending.ToolName}' is a destructive external (MCP) tool and never runs unattended, "
                           + "even when granted. Do not retry.";
                default:
                    _logger.LogInformation("Background turn denied ungranted write tool {ToolName}", pending.ToolName);
                    return $"Denied: '{pending.ToolName}' is a write action not granted to this background job. Do not retry.";
            }
```

**Both refusal strings are byte-identical to today's** (`:394-395`, `:403`), so
`BackgroundAssistantTurnRunnerTests`' 10 facts pass **unmodified**. That is the proof that commit 2 is a pure
refactor: *if any of those tests needs editing, the refactor is wrong.*

`IsAllowlisted: false` unattended is not a regression — it is today's behaviour restated
(`IToolPermissionService` is injected nowhere in either headless file, §0.3), and it is the honest input: the
allowlist is a *curated interactive convenience*, and silently granting it unattended would widen four tools'
authority on every scheduled job. Record it as an open question (§13.3), not as a fix.

## 7. `ActionCardBuilder` — one class truth (fixes §0.6)

- `category` derived from `ToolClassifier.Classify(pendingAction.PluginName, isExternalRoute: false)` when the
  caller passed no class, else from the passed class; mapped to `ActionCardCategory` 1:1 with
  `ToolClass.Unknown → ActionCardCategory.Mcp` (an unrecognised plugin name still renders as an external tool,
  i.e. today's shape) and `ToolClass.Scheduling → ActionCardCategory.Scheduled` (**new**).
- `IsAutoApprovable = ToolAutonomy.IsStandingGrantOfferable(toolClass, ToolName, _permissions.IsAutoApproveEligible(ToolName))`
  — the card and the gate now compute eligibility with the **same function**, so the divergence between
  `:102` and `ChatSession.cs:882` cannot recur.
  > **AS BUILT: `&& !isDestructive` as well** — this line as written above **silently dropped the git-verb
  > exclusion**, which was caught at review. R17's `isDestructive` is wider than the enforced rule by exactly
  > `git_switch` / `git_restore` / `git_stash`, none of which is `IsDeleteLike`, so the unified expression alone
  > would have started offering "Always allow" on an MCP git server's `git_switch` — after which every later call
  > auto-runs and can discard uncommitted work in the user's repo unattended. D9 explicitly says a change must not
  > close that gap **silently**; unifying on the shared helper closed it from the other side. The card is therefore
  > deliberately **narrower** than the gate here, and the asymmetry is stated at the code line, where the next
  > person editing `IsAutoApprovable` will read it. Note that **T-CARD-5 cannot cover this**: it compares the card
  > against the same helper production calls, so it is tautological on the shared half — the new row asserts
  > `IsAutoApprovable == false` **directly**, and was proved load-bearing by neutering the guard (all three rows
  > red).
- Details parsing: JSON for `memory` and `ActionCardCategory.Mcp` only. `Scheduled` joins the key/value branch,
  which is what `ScheduledJobToolHandler` actually produces (`:185-197`).
- Title: `ActionCard_Category_Scheduled` (new key) + two existing verb keys wired up —
  `"update_scheduled_research" => "ActionCard_Action_Update"`, `"delete_scheduled_research" => "ActionCard_Action_Delete"`.
  `create_scheduled_research` already falls to `ActionCard_Action_Create`.
- `isDestructive` (R17) is **unchanged** — `delete_scheduled_research` is delete-like, so its warning already
  resolves via the `isDelete` branch to `Msg_Assistant_PermanentDeleteExternal`; leave it (a scheduling-specific
  warning string is a nicety, not a defect, and it would be a fourth locale triple).

`GitActionCardTests.cs:120` (git tools are not auto-approve-eligible) stays green: `Git` is not `External`, so
`IsStandingGrantOfferable` returns the allowlist answer, which is false.

## 8. Settings surface

**`AppSettings.cs`**, directly under `AgentPlanReasoningTurnEnabled` (`:180`):

```csharp
    // Batch 04 — per-run autonomy policy default. When true, the preset auto-approves Pia's OWN write tools by
    // CLASS — memory, todo, reminder, scheduling and files. Never covers a delete-like tool (04 D6), never Git
    // (its destructive tools are not delete-like by name), never an external/MCP tool.
    //
    // FOUR consumers: (1) an interactive Planned run, (2) a "Run in background" detach, (3) a scheduled
    // AgentTask, and (4) VOICE MODE (D13) — where there is no run and no envelope, so the policy is read
    // straight from settings. A RESUME is deliberately NOT one: it reads the parked run's envelope (D10).
    // Default OFF: with it on, an unattended run can overwrite files in the assistant folder with nobody
    // watching. Global, like every other Agent*/Scheduled* knob, and local-only (absent from SyncSettings).
    public bool AgentRunAutoApproveBuiltInWrites { get; set; } = false;
```

**The user-visible copy must name voice mode**, and does. The first draft said *"During an agent run …
Deleting anything, Git commands and external (MCP) tools always ask"*, which was wrong twice over: the setting
also governs voice writes (consumer 4 above — a surface with no card at all), and "always ask" is false on the
two surfaces that cannot ask. Both reviewers raised it independently. The fix is the STRINGS, not the gate —
D13's voice behaviour is deliberate — so all three locales now read *"During an agent run and in voice mode …
Deleting anything, Git commands and external (MCP) tools are never covered by this permission."* Unattended, a
scheduled job whose `GrantedTools` name `delete_file` still executes it with no ask (pre-existing, pinned by
`GrantedBuiltInDeleteFile_StillExecutes_TheFloorIsExternalOnly`), which is why the copy no longer promises an ask.

**`AssistantSettingsViewModel`** — the four R27 touch points, `OnSuggestionsEnabledChanged` shape (no
`…Display`, no clamp, no `Format` call):

```csharp
    [ObservableProperty]
    private bool _agentRunAutoApproveBuiltInWrites;

    partial void OnAgentRunAutoApproveBuiltInWritesChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }
```

plus `AgentRunAutoApproveBuiltInWrites = settings.AgentRunAutoApproveBuiltInWrites;` in `InitializeAsync`
(under the `_isLoading` guard) and the mirror in `SaveSettingsAsync`.

**`Views/SettingsViews/AssistantView.xaml`** — after the Planning block (which ends at `:422`) and **before**
the `<!-- Scheduled / background-run budget -->` comment, so the scheduled section stays contiguous. This is
the correction Batch 05's polish pass had to make (`05-…impl.md` §7.2): a *global* toggle placed after the
scheduled knobs reads as a fourth unattended option.

```xml
            <!-- Autonomy (Batch 04): global; applies to interactive AND unattended agent runs. -->
            <TextBlock Text="{loc:Str Settings_Agent_Autonomy_Section_Header}"
                       Style="{StaticResource PiaSettingsSectionLabelStyle}"
                       Margin="0,12,0,0"/>
            <StackPanel Margin="0,0,0,20">
              <CheckBox Content="{loc:Str Settings_Agent_AutoApproveBuiltInWrites}"
                        IsChecked="{Binding AgentRunAutoApproveBuiltInWrites}"
                        Margin="0,8,0,0"/>
              <TextBlock Text="{loc:Str Settings_Agent_AutoApproveBuiltInWrites_Description}"
                         Style="{StaticResource PiaSettingsDescriptionStyle}"
                         TextWrapping="Wrap"
                         Margin="22,4,0,0"/>
            </StackPanel>
```

### 8.1 resx — 4 keys, all three files

Settings keys go after `Settings_Agent_PlanReasoningTurn_Description` (en `:927`, de `:114`, fr `:114`);
`ActionCard_Category_Scheduled` goes after `ActionCard_Category_Mcp` (en `:769`, de `:791`, fr `:791`).

`ViewStrings.resx` (en):
```xml
  <data name="Settings_Agent_Autonomy_Section_Header" xml:space="preserve"><value>Autonomy</value></data>
  <data name="Settings_Agent_AutoApproveBuiltInWrites" xml:space="preserve"><value>Auto-approve Pia's own write tools during agent runs</value></data>
  <data name="Settings_Agent_AutoApproveBuiltInWrites_Description" xml:space="preserve"><value>During an agent run, let Pia create and change files, notes, tasks, reminders and scheduled jobs without asking each time. Deleting anything, Git commands and external (MCP) tools always ask. Off by default: a run with this permission can overwrite files in your assistant folder unattended.</value></data>
  <data name="ActionCard_Category_Scheduled" xml:space="preserve"><value>Scheduled job</value></data>
```

`ViewStrings.de.resx`:
```xml
  <data name="Settings_Agent_Autonomy_Section_Header" xml:space="preserve"><value>Autonomie</value></data>
  <data name="Settings_Agent_AutoApproveBuiltInWrites" xml:space="preserve"><value>Pias eigene Schreibwerkzeuge während einer Ausführung automatisch freigeben</value></data>
  <data name="Settings_Agent_AutoApproveBuiltInWrites_Description" xml:space="preserve"><value>Erlaubt Pia während einer Agenten-Ausführung, Dateien, Notizen, Aufgaben, Erinnerungen und geplante Aufträge anzulegen und zu ändern, ohne jedes Mal zu fragen. Löschvorgänge, Git-Befehle und externe (MCP-)Werkzeuge fragen weiterhin immer nach. Standardmäßig aus: eine Ausführung mit dieser Freigabe kann Dateien in deinem Assistenzordner unbeaufsichtigt überschreiben.</value></data>
  <data name="ActionCard_Category_Scheduled" xml:space="preserve"><value>Geplanter Auftrag</value></data>
```

`ViewStrings.fr.resx`:
```xml
  <data name="Settings_Agent_Autonomy_Section_Header" xml:space="preserve"><value>Autonomie</value></data>
  <data name="Settings_Agent_AutoApproveBuiltInWrites" xml:space="preserve"><value>Approuver automatiquement les outils d'écriture de Pia pendant une exécution d'agent</value></data>
  <data name="Settings_Agent_AutoApproveBuiltInWrites_Description" xml:space="preserve"><value>Pendant une exécution d'agent, autorise Pia à créer et modifier des fichiers, des notes, des tâches, des rappels et des travaux planifiés sans demander chaque fois. Les suppressions, les commandes Git et les outils externes (MCP) demandent toujours confirmation. Désactivé par défaut : une exécution disposant de cette autorisation peut écraser des fichiers de votre dossier d'assistant sans surveillance.</value></data>
  <data name="ActionCard_Category_Scheduled" xml:space="preserve"><value>Tâche planifiée</value></data>
```

Terminology checked against the existing files: de uses **Ausführung** for a run
(`Settings_Agent_MaxReplans_Description`), fr uses **exécution**; de/fr already say **Externes Tool** /
**Outil externe** for `ActionCard_Category_Mcp`. No `&`, `<` or `>` in any value → no XML escaping needed.

---

## 9. Test plan

Every behavioural change carries a **neutralization** (how to make it go red). Run each, restore by
`git checkout --` (not by copying a backup — a preserved older mtime makes MSBuild skip the recompile and the
"restored" run silently exercises the mutated binary; `05-…impl.md` §12 records that trap).

### 9.1 `tests/Pia.Wpf.Tests/Services/ToolAutonomyTests.cs` — NEW (CRLF), namespace `Pia.Tests.Services`

| # | Test | Asserts | Neutralize |
|---|---|---|---|
| T-FLOOR-1 | `DestructiveExternalTool_IsNeverAutoRun_AcrossTheEntirePolicySpace` | `[Theory]` over the cross product: every `ToolGateSurface` × every `ToolClass` × every `DestructiveStems` member (built as `$"{stem}_thing"`) + `"forget"` × `{null policy, policy covering that class, policy covering EVERY class}` × `{granted, ungranted}` × `{allowlisted, not}`. For `ToolClass.External`: `Outcome != AutoRun` **always**. This is the whole policy value space, not a sample. | move the floor block below the policy block → red on the "policy covering every class" rows |
| T-FLOOR-2 | `InteractiveSurface_NeverRefuses` | over the same space with `Surface = Interactive`: `Outcome != Refuse`. Pins that this batch does not tighten the path where a human sees the card. | make the floor return `Refuse` unconditionally → red |
| T-FLOOR-3 | `PolicyNeverCoversADeleteLikeTool_EvenABuiltInOne` | `Policy = [Files]`, tool `delete_file`, class `Files`, `IsNamedGrant: false` → not `AutoRun`; with `IsNamedGrant: true` → `AutoRun` + `GrantedByName` (D6's exact boundary: a NAMED grant still runs, a POLICY grant never does) | drop `&& !IsDeleteLike(...)` from the policy branch → the first half reds |
| T-FLOOR-4 | `UnknownClass_IsNeverCovered` | `Policy = [Unknown]` (constructed directly), class `Unknown` → not `AutoRun` | remove the `!= Unknown` guard in `Covers` → red |
| T-RES-1 | `Interactive_OfferableAndGranted_AutoRuns_WithStandingGrantDecision` | `AutoApprovedStandingGrant` | — |
| T-RES-2 | `Interactive_PolicyCoveredClass_AutoRuns_WithoutAnyGrant` | `Policy = [Todo]`, `create_todo`, `HasStandingGrant: false` → `AutoRun` + `AutoApprovedPolicy` | delete the policy branch → red |
| T-RES-3 | `Unattended_Ungranted_Refuses_WithNotGrantedDecision` | `Refuse` + `DeniedNotGranted` | — |
| T-RES-4 | `Unattended_NamedGrant_AutoRuns_WithGrantedByNameDecision` | `AutoRun` + `GrantedByName` | — |
| T-RES-5 | `Resolve_IsCaseInsensitiveOnTheDeleteLikeName` | `"DELETE_thing"` + `External` → not `AutoRun` (D7's stated comparer) | — |
| T-OFF-1 | `IsStandingGrantOfferable_MatchesTheHistoricEligibleExpression` | `[Theory]`: allowlisted → true for every class; `External` non-delete-like → true; `External` delete-like → false; every other class not allowlisted → false. This is the *executable form* of "the refactor changed no semantics". | — |

### 9.2 `tests/Pia.Wpf.Tests/Services/ToolClassifierTests.cs` — NEW (CRLF)

| # | Test | Asserts |
|---|---|---|
| T-CLS-1 | `RouteWinsOverName` | `Classify("files", isExternalRoute: true)` → `External` |
| T-CLS-2 | `EveryBuiltInPluginNameMapsToANamedClass` | `[Theory]` over the six built-in plugin names incl. **`"scheduled-research"` → `Scheduling`** — the §0.6 fix, red before it |
| T-CLS-3 | `AnUnknownNameIsUnknown_NotExternal` | `Classify("something-else", false)` → `Unknown` (the old `_ => Mcp` is what produced the lie) |

### 9.3 `tests/Pia.Wpf.Tests/Services/ActionCardBuilderScheduledCategoryTests.cs` — NEW (CRLF)

| # | Test | Asserts | Neutralize |
|---|---|---|---|
| T-CARD-1 | `ScheduledResearchCard_IsNotAnExternalToolCard` | `Build(create_scheduled_research pending)` → `Category == Scheduled`, `Title` is not the `ActionCard_Category_Mcp` string | revert the classifier fallback to `Mcp` → red |
| T-CARD-2 | `ScheduledResearchCard_OffersNoAlwaysAllowButton` | `IsAutoApprovable == false` → `Decisions` is the **pair**, not the triad. This is the user-visible half of §0.6. | revert `IsAutoApprovable` to the old expression → red |
| T-CARD-3 | `ScheduledResearchCard_ParsesItsKeyValueDetails` | a `"Name: x\nKind: Agent task"` `Details` yields ≥2 `ActionCardDetail` rows with those labels (today the JSON parser yields none) | route `Scheduled` back through `ParseToDetails` → red |
| T-CARD-4 | `UpdateAndDeleteScheduledResearch_UseTheirOwnVerbs` | titles start with the Update / Delete verb strings, not "Create" | — |
| T-CARD-5 | `CardAndGate_AgreeOnEligibility` | `[Theory]` over ~10 (pluginName, toolName) pairs: `Build(...).IsAutoApprovable == ToolAutonomy.IsStandingGrantOfferable(...)`. The regression guard for R16's divergence. | — |

### 9.4 `tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherPolicyTests.cs` — NEW (CRLF)

| # | Test | Asserts | Neutralize |
|---|---|---|---|
| T-ENV-1 | `PolicyRoundTripsInsideTheV1Envelope_WithoutBumpingV` | serialize with a 2-class policy → the JSON contains `"v":1` **and** `"policy"` **and** `"autoApproveClasses"`; `TryRestorePolicy` returns those two classes; `TryRestoreGrantEnvelope` **still** returns the grants. The executable form of D1. | bump `GrantEnvelopeVersion` to 2 → the grant-restore half reds (and so does `HeadlessRunLauncherTests.cs:484`) |
| T-ENV-2 | `APreBatch04Envelope_HasNoPolicy_AndItsGrantsStillRestore` | feed the literal `{"v":1,"grantedWrites":["write_file"],"trigger":"Schedule"}`: `TryRestorePolicy` is `null`, `TryRestoreGrantEnvelope` is `["write_file"]`. "Null policy == current behavior", from the batch file's own test list. | — |
| T-ENV-3 | `AnUnreadableEnvelopeLosesThePolicyBeforeItLosesTheGrantFloor` | `[Theory]` over `null`/`""`/`"{not json"`/`"{}"`/`{"v":99,…}`/`{"somethingElse":true}`: `TryRestorePolicy` is `null` in every case. The D10 asymmetry. | make `TryRestorePolicy` fall back to `FromSettings` → red |
| T-ENV-4 | `UnknownClassNamesAreDropped_NotHonoured` | `{"policy":{"autoApproveClasses":["Files","Warp","",null]}}` → exactly `[Files]`; and `["Warp"]` alone → `null` (no usable class ⇒ no policy) | — |
| T-ENV-5 | `TheInteractiveFallbackLiteralIsTheDocumentTheSerializerProduces` | `Assert.Equal(InteractiveEmptyEnvelopeJson, SerializeGrantEnvelope([], AgentRunTrigger.User, policy: null))`, **and** `TryRestoreGrantEnvelope(literal)` is non-null + empty, **and** `TryRestorePolicy(literal)` is null. Shape pin + round-trip pin (D12). | add a member without `WhenWritingNull`, or emit `"policy":null` → the shape half reds |
| T-ENV-6 | `SerializeWithNoPolicy_OmitsTheMember` | the JSON does **not** contain `"policy"` — so a pre-04 reader sees a pre-04 document | drop `WhenWritingNull` → red |
| T-ENV-7 | `Resume_RestoresThePolicyFromTheEnvelope_NotFromSettings` | park a run whose envelope has **no** policy; set `AgentRunAutoApproveBuiltInWrites = true`; resume; assert a `write_file` call is still **gated** (denied, not granted) at the unattended gate. **The D10 red-before-green.** | make `ResumeAsync` call `FromSettings` instead of `TryRestorePolicy` → red |
| T-ENV-8 | `Launch_WithTheSettingOn_PersistsThePresetClasses` | `LaunchAsync` with the setting on → `TryRestorePolicy(captured.PolicyJson)` covers `Files` and does **not** cover `Git` or `External` (D9's exclusions, as a test) | add `Git` to the preset → red |

`HeadlessRunLauncherTests`' existing 7 envelope facts must pass **unmodified**.

### 9.5 Gate integration — extend the two existing gate suites (do not fork them)

`tests/Pia.Wpf.Tests/ViewModels/ChatSessionStateMachineTests.cs` (all 8 existing facts unmodified):

| # | Test | Asserts | Neutralize |
|---|---|---|---|
| T-GATE-1 | `PolicyCoveredClass_AutoApproves_WithoutAStandingGrant_CardStillAddedFirst` | policy `[Todo]`, ungranted `create_todo`: never enters `WaitingForTool`, the pre-resolved card is in `message.ActionCards` **before** the execute, and the tool ran. **The batch file's "a run whose policy auto-approves class X does not prompt for X".** | drop the policy from `StepTurnSpec` → red |
| T-GATE-2 | `PolicyCoveredClass_StillPromptsForAnUncoveredClass` | same policy, a `files` `write_file` call → the card is added and the turn waits. The "…but still prompts/denies for Y" half. | make `Covers` return true → red |
| T-GATE-3 | `PolicyCanNotAutoApproveADestructiveMcpTool` | policy covering **every** class, a delete-like MCP tool → still prompts, still no standing grant on AlwaysAllow. | — |
| T-GATE-4 | `NoPolicy_IsByteIdenticalToTodaysBehaviour` | `Policy: null` over 4 representative tools → the same decisions the pre-batch suite asserts | — |
| T-GATE-5 | `WhenMcpDerivationThrows_TheTurnSurvives_AndTheToolIsTreatedAsExternal` | `IsMcpTool` faulted: the turn completes (no propagating exception) and a delete-like tool is not auto-approved. **The absence §0.3/D8 names — nothing pins this today.** | remove the try/catch → red (the test throws out of the tool loop) |

`tests/Pia.Wpf.Tests/Unit/BackgroundAssistantTurnRunnerTests.cs` (all 10 existing facts unmodified):

| # | Test | Asserts |
|---|---|---|
| T-GATE-6 | `PolicyCoveredClass_ExecutesWithoutANamedGrant` | policy `[Todo]`, empty `grantedWrites`, `create_todo` → executed |
| T-GATE-7 | `PolicyCoveredClass_DoesNotCoverItsDeleteLikeSibling` | policy `[Files]`, `delete_file`, empty `grantedWrites` → the "not granted" deny string (D6 unattended) |
| T-GATE-8 | `PolicyOverEveryClass_StillCannotRunADestructiveExternalTool` | the destructive-floor string, byte-identical |
| T-GATE-9 | `NullPolicy_LeavesTheGrantGateExactlyAsItWas` | `[Theory]` mirroring the four existing grant cases with `policy: null` |

### 9.6 Voice mode — `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelVoiceGateTests.cs` NEW (CRLF)

There is **no** existing voice-mode test, so this file also establishes the harness. `HandleVoiceModeToolCall`
is private → drive it through the public seam the production code uses (`StreamVoiceModeResponse`'s tool
handler) or make the method `internal` and cover it with the existing `InternalsVisibleTo` — **prefer
`internal`**: the alternative needs a full voice turn stood up, which is a disproportionate fixture for four
facts. Record the choice in the commit message.

| # | Test | Asserts | Neutralize |
|---|---|---|---|
| T-VOICE-1 | `AllowlistedWriteStillRuns` | `create_todo` executes (no behaviour change for the common case) | — |
| T-VOICE-2 | `UngrantedWriteFileIsRefused_NotExecuted` | `Execute` never called; the result names *voice mode cannot show an approval card* | revert to the bare `await pendingAction.Execute()` → red |
| T-VOICE-3 | `DeleteLikeToolIsRefused_EvenWithTheSettingOn` | `delete_file` and a destructive MCP tool both refuse | — |
| T-VOICE-4 | `StandingGrantIsHonoured` | `IsGranted` true for an offerable tool → executes | — |
| T-VOICE-5 | `ReadsAreUnaffected` | a non-null `result` route returns it without touching the gate | — |

### 9.7 Settings — `tests/Pia.Wpf.Tests/Models/AppSettingsAgentAutonomyTests.cs` NEW (CRLF)

Mirroring `AppSettingsAgentPlanningTests` exactly (namespace `Pia.Tests.Models`):

| # | Test | Asserts |
|---|---|---|
| T-SET-1 | `AgentRunAutoApproveBuiltInWrites_DefaultsOff` | `Assert.False(new AppSettings().AgentRunAutoApproveBuiltInWrites)` — D9's default is a decision, not an accident |
| T-SET-2 | `AgentRunAutoApproveBuiltInWrites_RoundTripsThroughCamelCaseJson` | `[Theory]` over `true`/`false`. The **only** automated proof the CheckBox *can* persist — §10.1 rules out an `AssistantSettingsViewModel` test |
| T-SET-3 | `FromSettings_OffYieldsNull_OnYieldsThePresetClasses` | off → `null` (so the envelope stays pre-04-shaped); on → exactly the five preset classes, and **not** `Git`, `External` or `Unknown` |

### 9.8 Architecture — extend `tests/Pia.Wpf.Tests/Architecture/`

| # | Test | Asserts |
|---|---|---|
| T-ARCH-1 | `ToolAutonomyRuleTests.TheTwoGateFilesDeriveNoAutonomyDecisionOfTheirOwn` | source-scan `ChatSession.cs` + `BackgroundAssistantTurnRunner.cs`: at most **one** occurrence each of `IsAutoApproveEligible`, `IsDeleteLike` and `IsMcpTool`, and each occurrence must be on a line that also mentions `ToolGateInput` or `IsExternalTool`. Scoped to the two gate files **by design** — `ActionCardBuilder.cs:45` legitimately calls `IsDeleteLike` for warning text, and a blanket ban would be wrong (D5.1). Resolve paths from the repo root the way the existing localization rule does. |
| T-ARCH-2 | `ToolAutonomyRuleTests.EveryPersistedGateEnumStartsAtUnknownZero` | reflect `ToolClass`/`ToolGateDecision`/`ToolGateSurface`: member `Unknown` exists with value `0`, and no two members share a value (the append-only guardrail, mechanized) |
| — | `LocalizationTests` (existing) | catches a missing `loc:Str` key **and** en/de/fr parity for all four new keys — no new test needed |
| — | `DiRegistrationTests` (existing) | unaffected: this batch adds **no** new service interface (D5 makes the resolver static) |

---

## 10. Manual-smoke debt (no automated coverage exists)

1. **The CheckBox's `Binding` path.** `AgentRunAutoApproveBuiltInWrites` resolves at runtime only; a typo
   renders a checkbox that silently never persists. No test parses **`Pia.Views.SettingsViews.AssistantView`**
   (Batch 12's `AssistantViewParseTests` parses the same-named *chat* view, a different type in a different
   namespace). No `AssistantSettingsViewModel` test exists either (four concrete sub-VM deps, disproportionate
   for a checkbox). **Check:** Settings → Assistant → Agent runs shows an "Autonomy" section; toggle it,
   restart, still on.
2. **A real interactive Planned run with the setting ON.** Confirm a `write_file` step shows a
   **pre-resolved, already-accepted** card (never nothing at all — silent execution would mean §6's
   card-before-execute ordering was dropped), and that a `delete_file` step **still** shows a live card with a
   Decline / Allow-once pair and no Always-allow button.
3. **The scheduled-research card.** Ask Pia in chat to create a scheduled job. Confirm the card is titled
   *"Create Scheduled job"* (not *"Create External tool"*), that it offers **two** buttons not three, and that
   the detail rows show `Name` / `Kind` / `Query` / `Recurrence` as label/value pairs rather than nothing.
4. **Park → flip → resume (D10).** Start a Planned run with the setting OFF, let it hit its step cap, turn the
   setting ON, click *Continue*. The resumed run must still card every write. T-ENV-7 pins the mechanism; only
   a live round proves the whole chain (envelope → `TryRestorePolicy` → `Initialize` → the gate).
5. **Voice mode.** With the setting off, ask voice-mode Pia to write a file: it must decline and say to use the
   chat window. Then ask it to add a todo: it must still work. This is the batch's one user-facing *removal*
   of a capability.
6. **DE/FR** render without clipping — the description is the longest agent-settings string in the pane.
7. **An actual MCP server.** Nothing in the suite exercises a real `McpPluginToolHandler` route, so
   `IsMcpTool`-derived `ToolClass.External` is only ever faked. Confirm one real external tool still prompts
   with the triad and that "always allow" still persists.

---

## 11. Guardrails, instantiated for this batch

- **Failure-isolated bookkeeping.** Three new failure sites, all swallowed: `TryRestorePolicy` never throws
  (D10); the interactive envelope write falls back to a **literal**, not `null` (D12 — the pre-existing
  `null` path was itself an escalation); and the classifier's route lookup goes through a fail-closed
  `IsExternalTool` on **both** gates (D8). Emitting or reading a policy must never fail a step.
- **No interactive regression.** `SetState(WaitingForTool)` → `finally` → `Running` is untouched; the card is
  still added **before** the execute on the auto path; `WaitForUserDecisionAsync` is never left orphaned (the
  auto path returns before the card is ever awaited, exactly as today); the composer gains no dead state.
  T-GATE-4 and the 8 unmodified `ChatSessionStateMachineTests` facts are the proof.
- **Executor parity.** The policy reaches the unattended gate through `RunExchangeAsync`, which
  `HeadlessTurnExecutor` and the SingleTurn `RunAsync` both call (R23) — so Headless is covered by
  construction. Live gets it through `StepTurnSpec.Policy`. **Both** paths are tested (T-GATE-1 live,
  T-GATE-6 headless). A feature on one executor only would be a defect.
- **Off-thread `RunChanged` stays marshaled (G3).** This batch raises no `RunChanged` and touches no
  ViewModel threading. `AssistantSettingsViewModel` gains no dispatcher use;
  `git grep "Application\.Current" -- src/Pia.Wpf/ViewModels/` must keep returning nothing.
- **Append-only persisted enums and ordinals.** `ToolClass`, `ToolGateDecision`, `ToolGateSurface` all start at
  `Unknown = 0` and are never renumbered (D15, T-ARCH-2). `ActionCardCategory.Scheduled` is **appended** (it is
  not persisted, R28, but appending costs nothing and keeps the habit). `AgentRunTrigger` is **not** touched —
  the origin-blind resume floor is out of scope (§13.1).
- **Privacy-first logging.** New lines log the tool **name**, the plugin **id**, the `ToolGateDecision` and
  `ToolClass` **enum values** — all already Information-safe in both gates today
  (`ChatSession.cs:917`, `BackgroundAssistantTurnRunner.cs:398`). **Never** logged: the policy's class list
  content beyond a count, a rejected class **name** (an MCP-adjacent string is not ours to log — precedent:
  refused destructive grant names moved from `LogWarning` to `SensitiveDebug` in the fix-up pass), tool args
  (`SensitiveDebug` only), `PolicyJson` content (`AgentRunService.cs:139-140` logs presence only — keep it).
- **A new user-visible string lands in all three resx files** — 4 keys × 3 files, real DE and FR (§8.1).
  `ViewStrings.Designer.cs` stays untouched.
- **Code style.** 4-space C#, `_camelCase` fields, `var` for apparent types, `[ObservableProperty]`, namespaces
  `Pia.*`. New `.cs` files **CRLF**.

---

## 12. Commit plan (each independently buildable and green)

| # | Commit | Contents | Green means |
|---|---|---|---|
| 1 | `Autonomy: one classifier and one resolver for the tool gates` | `ToolGateEnums.cs`, `RunAutonomyPolicy.cs`, `ToolClassifier.cs`, `ToolAutonomy.cs`; T-FLOOR-*, T-RES-*, T-OFF-*, T-CLS-*, T-ARCH-2 | new code, no call sites → the whole existing suite is untouched |
| 2 | `Autonomy: route both gates through the shared resolver` | both gates rewired with `policy: null`; the fail-closed `IsExternalTool` on `ChatSession`; T-GATE-5, T-ARCH-1 | **all 18 existing gate facts pass UNMODIFIED.** If any needs editing, the refactor changed semantics and is wrong. |
| 3 | `Cards: scheduled-research is a built-in, not an external tool` | `ActionCardBuilder`, `IActionCardBuilder.Build`'s optional param, `ActionCardCategory.Scheduled`, `ActionCard_Category_Scheduled` ×3, the two verb keys; T-CARD-* | existing `ActionCardBuilderTests` / `…FilesDiffTests` / `GitActionCardTests` unmodified |
| 4 | `Autonomy: persist a per-run policy in the existing v1 envelope` | `GrantEnvelope.Policy`, the serialize overload, `TryRestorePolicy`, `InteractiveEmptyEnvelopeJson`, launch + resume + `ChatSessionManager` (D11/D12), `HeadlessTurnExecutor.Initialize`, `RunExchangeAsync`, `StepTurnSpec.Policy`, `LiveTurnExecutor`; T-ENV-*, T-GATE-1/2/3/4/6/7/8/9 | existing `HeadlessRunLauncherTests`, `ChatSessionManagerTests`, `HeadlessTurnExecutorTests`, `LiveTurnExecutorPlannedRunTests`, `ChatSessionStepTurnTests`, `AgentRunServiceTests` unmodified — which holds **only because** every new parameter, `LiveTurnExecutor`'s ctor argument included, is trailing and defaulted (§3). If one of those files needs an edit, a parameter was made required; fix the parameter, not the test. |
| 5 | `Autonomy: a settings default for built-in writes in agent runs` | `AppSettings`, VM, XAML, 3 resx keys ×3; T-SET-* | `LocalizationTests` green |
| 6 | `Voice mode: writes go through the tool gate` | `AssistantViewModel`; T-VOICE-* | droppable without stranding 1–5 — but then §11's *"the policy governs all writes"* must be narrowed to *"all writes on the two run gates"* in the roadmap note |

---

## 13. Open questions (none blocking)

1. **The resume floor is still origin-blind.** `Trigger` is carried in the envelope and explicitly never
   consulted (`HeadlessRunLauncher.cs:572-573`), so an envelope **loss** still hands `{write_file}` to a run
   that was granted nothing. D12 removes the only *reachable* loss path for the interactive origin, but a
   pre-D1 row or a corrupted column still takes the floor. The obvious fix — derive the floor from the origin —
   is **not implementable from today's signals**: the interactive Planned create (`ChatSessionManager.cs:753`)
   and the "Run in background" detach (`:1010`) both write `TriggerKind = User` + `RunShape.Planned`.
   Distinguishing them needs a **new append-only `AgentRunTrigger` ordinal** (`AgentEnums.cs:19-24` is
   User=0/Schedule=1/Event=2), and an older peer may store an unknown ordinal unvalidated. Deliberately out of
   scope; whoever takes it should also make `ResumeFloorGrants` reference `DefaultGrantedWrites` instead of
   duplicating its value (R7).
2. **A model-authored policy would need `ParseGrantedTools`' treatment.** Today the class list is authored only
   from settings (D9), so nothing untrusted reaches it. A per-run editor — or a `create_scheduled_research`
   parameter — must filter the class list the way `ParseGrantedTools` filters names (R12), and must reckon with
   R26: `SyncScheduledJob.GrantedTools` is peer-writable and stored unvalidated.
3. **The allowlist is interactive-only, unattended.** §6 passes `IsAllowlisted: false` on the unattended
   surface because that is today's behaviour. Whether the four additive tools *should* be free unattended is a
   real question; answering it either way is a behaviour change and belongs in its own batch with its own test.
4. **`ExecutePendingActionAsync` is dead surface** (R30) and `Execute()` can run inside a handler's error path
   (R29). Neither is a hole today, but "a pending action implies a gated call" is not universally true, and a
   future batch that assumes it would be wrong.
5. **Tool-name route collisions are undetected** (R19). A class-keyed policy is *less* exposed than a
   name-keyed one, but `IsAutoApproveEligible` is still name-only with no `PluginId` restriction, so a
   shadowing MCP server still inherits the allowlist. Unchanged by this batch, and worth a `RegisterHandler`
   collision warning some day. **Partly closed at the review**: the voice branch now also requires
   `ToolClass != External`, so a shadowing server no longer inherits the allowlist on the surface that has no
   card. Interactive is contained by its standing-grant requirement. The underlying collision is still silent.

### Deferred at the review close, with reasons

6. **`IsExternalTool`'s fault path is fail-closed for the FLOOR and fail-OPEN for grantability.** Mapping a
   route-lookup fault to `External` makes a non-delete-like BUILT-IN pass `IsStandingGrantOfferable`, so a fault
   on `write_file` would let the card offer *Always allow* and let the gate persist a grant the allowlist
   deliberately excludes. Not fixed, for two reasons. It is **unreachable**: every `_toolNameRoutes` mutation in
   `PluginService` is inside `lock (_handlers)` and `IsMcpTool` is a locked `TryGetValue`, so the only throw is a
   null tool name, which cannot arrive from a pending action. And the fix — `offerable = routeKnown && …` — would
   put a **second expression that gates an auto-approval** in the very file T-ARCH-1 guards, to close a path that
   cannot fire. Both doc comments now state the true direction instead of claiming fail-closed.
7. **The SingleTurn scheduled path takes no policy.** `BackgroundAssistantTurnRunner.RunAsync` →
   `RunExchangeAsync` passes no policy, so `AgentRunAutoApproveBuiltInWrites` behaves differently for a scheduled
   AgentTask job (auto-approves the preset) and a scheduled Research job (still refuses). Recorded as a decision
   at the call site rather than relayed: the direction is restrictive, and widening an unattended surface is not
   a change to make off a reviewer nit. The comment names the fix for whoever wants parity.
8. **`TryRestorePolicy` does not intersect against `PresetClasses`.** Suggested at review and declined:
   `PresetClasses` is the *settings preset*, not the envelope's legal vocabulary, so pinning the reader to it
   would silently narrow the first per-run policy a later batch authors, with nothing failing to explain why.
   §13.2's filtering belongs where a policy is AUTHORED from untrusted input. The reader's *readability* test was
   fixed instead — it now requires `grantedWrites` to be present, like the grant half, so the two cannot disagree
   about whether a document is readable.
