# The Agent System — Phase 1 Detail Plan (The Spine)

- **Date:** 2026-07-18
- **Status:** Detail planning. Builds on the north-star spec
  (`2026-07-18-agent-system-design.md`). Decisions here are resolved unless noted.
  **Revised 2026-07-18** after a 3-agent red-team + empirical verification — corrections logged in §16.
- **Scope:** Phase 1 only — `AgentRun` + `AgentPlan`/`AgentStep` + the plan→act→replan
  loop + the Chat/Agent lever + a live run-progress component. Verify/critic, budget
  *enforcement*, the MCP gate fix, sub-agents, and the out-of-folder temp workspace are
  Phase 2/3 and are only *seamed* here, not built.
- **Author:** Marco Altmann (with Claude Code)

---

## 0. Resolved open questions (the decisions Phase 1 is built on)

| # | Decision |
|---|----------|
| Q1 | Every execution is an `AgentRun`. The plan loop is a **shape** (`SingleTurn` \| `Planned`), exposed as a **UI lever** defaulting to Chat. Model may *suggest* switching to Agent; the user flips it. |
| Q2 | Plan is persisted data, rendered live in a first-class **run-progress component**, read-only in Phase 1. Mutation seam reserved for Phase 4 steering. |
| Q3 | `AgentStep` is a distinct type from `TodoItem`. Promotion to Kanban is a later opt-in one-way projection. |
| Q5 | Ephemeral scratch → `%LOCALAPPDATA%\Pia\runs\<runId>\` (outside the assistant folder). Results promoted into the assistant folder. Requires making the file-tool base root run-aware (Phase 3 workspace work; seamed here). |
| Q7 | **Generous** defaults, **transparent** UI ledger (tokens/steps/time live). Pausing-on-budget is Phase 2. |
| Q9 | Capability = persona→provider assignment (`PreferredProviderId`, already exists). Only a **non-blocking** check on the planner persona's provider. Specialists (incl. local) run on their assigned provider. |

Q4 static-first topology, Q6 approval-mode defaults, Q8 resumable-failure UX, Q10 timeline-as-team-UI are accepted but land in Phase 2/3.

---

## 1. Architectural crux: where the plan loop lives

The single most important structural decision. Today there are three execution paths:

- **Interactive** — `ChatSession.RunTurnAsync` (deeply UI-thread-affine by design: no
  `Task.Run`, no `ConfigureAwait(false)`; `ChatSessionManager` throws if constructed off
  the UI thread).
- **Headless** — `BackgroundAssistantTurnRunner.RunAsync` (fully off-UI-thread, DI-scope-clean,
  but **single-turn only**).
- **Scheduled** — `ScheduledJobBackgroundService` → `BackgroundAssistantTurnRunner`.

The plan→act→replan loop must be **shared** by interactive and headless, so it **cannot**
live in `ChatSession` (UI-affine) nor in `BackgroundAssistantTurnRunner` (single-turn).
It becomes a new **UI-thread-agnostic** service, mirroring `AiClientService`
(pure `IAsyncEnumerable`, already reused by both paths).

```
IAgentRunService          durable store + lifecycle for AgentRun/AgentStep (new)
AgentRunOrchestrator      the plan→act→replan loop; UI-agnostic (new)
  ├─ IAgentPlanner        planning/replan turns via emit_plan; environment-agnostic (new)
  └─ IAgentTurnExecutor   abstraction each *act* step-turn dispatches through (new)
       ├─ LiveTurnExecutor      drives a ChatSession (UI-affine; owns UI-thread marshaling)
       └─ HeadlessTurnExecutor  wraps BackgroundAssistantTurnRunner (off-UI-thread)
```

This is §13's "three views of one thing" made concrete: the orchestrator drives the loop;
the executor decides *how* a step-turn is run and rendered. Interactive attaches a
`LiveTurnExecutor` (so the UI streams as today); headless/scheduled attach a
`HeadlessTurnExecutor`.

`AgentRunOrchestrator` reuses `AiClientService.GetChatCompletionWithToolsAsync` per step
(the round loop is unchanged *inside* a step; the hard `maxToolRounds = 10` cap is now a
per-step bound, and the run-level bound is the plan + a generous step cap — see §5).

---

## 2. Data model

New durable tables, following the existing precedent in `SqliteContext`
(TEXT PK, ISO-8601 `"O"` timestamps, `ExtraJson` extension column, `SchemaVersion`).
`ChatState` stays runtime-only; `AgentRun.State` is the **persisted** superset.

### 2.1 `AgentRun`

```
Id                  Guid    PK
SchemaVersion       int
ChatId              Guid    FK → AssistantChats(Id)   -- the chat this run lives in (1 chat : 0..N runs); NOT unique
RunShape            int     -- SingleTurn | Planned
State               int     -- see §3
TriggerKind         int     -- User | Schedule | Event
TriggerRef          Guid?   -- e.g. ScheduledJob.Id when Schedule
ParentRunId         Guid?   -- reserved for Phase 3 sub-agents; null in Phase 1
OwnerDeviceId       Guid?   -- only owner fires/advances (mirrors ScheduledJob.OwnerDeviceId)
Goal                text    -- SENSITIVE (SensitiveDebug; E2EE-eligible on sync)
FirstMessageId      Guid?   -- run's transcript slice, by STABLE message Id (NOT positional ordinal — §16 R3)
LastMessageId       Guid?
PolicyJson          text?   -- reserved for Phase 2 autonomy policy
LedgerJson          text    -- tokens/cost/wall-clock, per step + total (Q7)
CreatedAt / UpdatedAt / StartedAt? / CompletedAt?
ExtraJson           text?
```

**Run ↔ chat is 1 : 0..N.** A run is scoped to a *goal* (§3: Goal + Plan), and one
conversation can carry many goals over its life — so a chat hosts zero or more runs, each
spanning a slice of the transcript delimited by **stable message `Id`s** (`[FirstMessageId,
LastMessageId]`, never positional ordinals — §16 R3). The chat
(`AssistantChats` + `AssistantChatMessages`) remains the message transcript and the thing
that syncs; a run adds plan/state/policy/ledger *over* its slice. This mirrors how
`ScheduledJob` already references a produced chat via `MarkRunCompleteAsync(job.Id, chatId)`
— a run now sits between job and chat. `ChatId` is indexed but **not unique**.

### 2.2 `AgentStep`

```
Id             Guid    PK
RunId          Guid    FK → AgentRuns(Id)
Ordinal        int
Title          string  -- SENSITIVE
Intent         text?   -- SENSITIVE (what this step should accomplish)
Status         int     -- Pending | Running | Done | Failed | Skipped
ExpectedArtifact string?
AssignedPersonaId Guid?  -- reserved for Phase 3; null in Phase 1 (single persona)
DependsOnJson  text?   -- reserved for DAG; Phase 1 is linear
ReRunnable     bool    -- idempotency hint for Phase 2 resume
FirstMessageId Guid? / LastMessageId Guid?  -- transcript slice by STABLE message Id (§16 R3)
CreatedAt / UpdatedAt
ExtraJson      text?
```

`AgentStep` is deliberately **not** a `TodoItem` (which is binary-status + positional, with
no deps/substeps/tool-state). Promotion to Kanban (Q3) is a later one-way projection into a
dedicated column via `ITodoService.CreateAsync`; no shared table.

### 2.3 In-memory / DTO

- `AgentPlan` = ordered `IReadOnlyList<AgentStep>` on the live `AgentRun` model.
- Sync: **not** synced in Phase 1 (runs are owner-device local; the *chat* still syncs).
  Reserve an `AgentRun` sync DTO shape for later, mirroring the E2EE plaintext/encrypted
  field split used by personas (`Goal`/step text encrypted; enums/ids plaintext).

### 2.4 Migration & the chat-without-run invariant

- **No removal.** Phase 1 is an *additive wrapping* refactor. `ChatSession`,
  `ChatSessionManager`, `BackgroundAssistantTurnRunner`, `AiClientService`, and the scheduler
  are all retained; the orchestrator sits *above* them and executors *delegate into* them.
- **A chat without a run row is valid** and renders exactly as today (absent run ≡
  `SingleTurn`, `Idle`). The run layer is **forward-only** — existing `AssistantChats` are
  **not backfilled**. No migration script; historical chats are untouched.
- **Persist predicate (Q11.4):** persist a run **iff it is `Planned` OR not driven by the live
  interactive `ChatSession` path.** (Trigger correlates but isn't the discriminator — an ad-hoc
  `User`-triggered *headless* call still persists.)
  - *Interactive `SingleTurn`* (the common case) → **not persisted**; the chat transcript is its
    durable record. Keeps the hot UI path write-free and the tables tiny.
  - *Headless / scheduled / event* → persisted even when `SingleTurn`, for trigger/linkage state
    absent from the chat. **No** `AgentStep` rows (one implicit step). *(§16 R14: the discriminator
    is the execution path, not the trigger — headless requests default `Trigger=User`.)*
  - *`Planned`* (any trigger) → persisted **with** `AgentStep` rows.
- **Retention:** all persisted runs use FK `ON DELETE CASCADE` from `AssistantChats`, and this is
  **actually enforced** — Microsoft.Data.Sqlite turns SQLite foreign keys ON by default
  (`PRAGMA foreign_keys=1`, verified empirically; `AssistantChatService.DeleteCoreAsync` already
  relies on cascade). Two consequences (§16 R1): (a) cascade genuinely fires — explicit chat
  deletion removes its runs, no manual cleanup; (b) a run row **cannot be inserted before its chat
  row exists**, which drives the 1.1/1.2 write-order fix (§12.4, §6.1). "`Planned` runs retained"
  is enforced by an **eviction-skip** — `EvictOlderThanAsync` skips chats bearing a `Planned` run —
  which lands in **1.2** (the first milestone that creates `Planned` runs), not 1.1 (§16 R17).
  Reachable via `FlowAction.OpenRun`. Net: run tables stay small.

---

## 3. State machine

Persisted superset of `ChatState`:

```
                 ┌────────────► Verifying ──(Phase 2)──┐
Planning ──► Running ◄─────────────────────────────────┤
   │            │  ▲                                    ▼
   │            │  └──(replan)── Paused ◄──► WaitingForInput ──► Completed
   │            ▼
   └────► Failed / Cancelled          any state ──► Failed | Cancelled
```

- `SingleTurn` runs **skip Planning**: one implicit step, `Running → Completed` — byte-for-byte
  today's behavior, just wrapped in a run.
- `Planned` runs: `Planning` (orchestrator produces the plan) → per step `Running` →
  lightweight replan check → next step → `Completed`.
- `Verifying` exists in the enum but is a **no-op pass-through in Phase 1** (Phase 2 fills it).
- Mapping from today: `ChatState.Error` → `Failed`; `ChatState.Completed` → `Completed`.
- ⚠ `ChatState.WaitingForTool` does **not** map to `WaitingForInput` in Phase 1 (§16 R12): a
  write-tool action card flaps `WaitingForTool` *inside* a step, but a real `WaitingForInput` run
  state needs the Phase-2 pause machinery. In Phase 1 the run stays `Running` during a tool-approval
  wait, and the wait stays visible via the in-transcript action card — not the run state.

Two **silent** behaviors today become observable run states:
- `maxToolRounds` exhaustion (today: silently yields `Finished`) → the step ends and the run
  surfaces its state, not a silent stop.
- Headless write denial (today: returns `"Denied…Do not retry"` inline) → unchanged in
  Phase 1, but now attributable to a run/step in the timeline.

---

## 4. The plan → act → replan loop (Phase 1 form)

`AgentRunOrchestrator.RunAsync(AgentRun, IAgentTurnExecutor)`:

1. **Plan** (Planned only). One orchestrator-persona turn produces a structured plan via a
   **constrained `emit_plan(steps[])` tool call** (each step `{title, intent, expectedArtifact}`).
   Constrained is chosen over free-form JSON for reliability on weak/local providers.
   ⚠ **Reasoning/tools asymmetry** (`ReasoningEffortMapping`): because `emit_plan` is a tool,
   on **Chat-Completions** providers `ToOpenAi` omits the reasoning-effort param — the model
   still reasons at its *default*, but effort is **not boostable** during the plan turn. This
   is accepted for Phase 1 (structure reliability > boostable effort for decomposition).
   **Responses-API** providers get `emit_plan` *and* boosted reasoning together. A two-call
   *reason-then-emit* to recover boosted effort on Chat-Completions providers is a **Phase 2
   optimization**, not a Phase 1 default (doubles plan-turn cost — and note §16 R6: the constrained
   turn already pays one *extra* provider round because the tool loop has no early exit). Persist
   the plan + steps immediately (`Planning` state).
2. **Act.** For each `Pending` step in order: set `Running`, dispatch one step-turn via
   `executor.ExecuteStepAsync(run, step)`. The executor runs `AiClientService`
   (per-step tool loop unchanged) and streams into the chat. On completion, mark the step
   `Done` (or `Failed`) and record the transcript slice (by stable message `Id`, §16 R3) +
   per-step ledger delta (extended step API, §16 R16).
3. **Replan** (Planned only, lightweight in Phase 1). After each step, a cheap check: did the
   step fail, or did the model signal the remaining plan is wrong? If so, one orchestrator
   turn revises the *remaining* `Pending` steps (completed steps are immutable). Bounded by
   the generous step cap (§5). Phase 1 keeps replan minimal; Phase 2's verify feeds it.
4. **Complete.** No more `Pending` steps → `Verifying` (no-op Phase 1) → `Completed`.

`SingleTurn` short-circuits to a single `ExecuteStepAsync` with an implicit step = the user
turn, exactly reproducing today.

---

## 5. Budgets in Phase 1 (generous + transparent, no pausing yet)

- Replace the run-level meaning of `maxToolRounds`: it stays a **per-step** cap (unchanged
  at 10 internally); the **run** is bounded by the plan plus a generous step cap
  (default **~24 steps**) and a generous wall-clock (default **20 min interactive /
  45 min scheduled**).
- **No pausing/enforcement** in Phase 1 beyond the caps terminating the loop — but termination is
  **not silent** (§16 R5): step-budget or wall-clock exhaustion ends the run as `Completed` with a
  `truncated` marker in `ExtraJson` (`{truncated:true, reason}`), rendered distinctly by §15.1 and
  never presented as clean success. Budget-*pauses-into-`WaitingForInput`* is Phase 2.
- **Ledger is live and visible** (Q7): `LedgerJson` accrues per-step + total input/output
  tokens (we already aggregate `UsageDetails` across rounds), wall-clock, and cost where a
  price table exists. The run-progress component (§7) renders it as it grows.

---

## 6. Runtime refactor

### 6.1 Interactive path
- **Chat / `SingleTurn`** stays the **literal `RunTurnAsync` path with no run object** for all of
  Phase 1 (§16 R11) — not persisted, not wrapped in a `LiveTurnExecutor`. This is exactly what
  keeps the hot UI path untouched and the byte-for-byte regression guarantee (§13.5) meaningful.
- **`Planned`** (Agent mode): `ChatSessionManager.StartTurnAsync` creates an `AgentRun` and hands
  it to `AgentRunOrchestrator` with a `LiveTurnExecutor` bound to the session; per-step turns
  stream into the live chat via the existing events. `ChatSession` stays the UI view and gains a
  run reference for the progress component.
- ⚠ **Write order (§16 R1):** the first-turn chat `Id` is assigned synchronously, but the
  `AssistantChats` row is persisted only at end-of-turn — and FK enforcement is ON, so the run
  row (FK → chat) must be inserted **after / in the same transaction as** the chat row. See §12.4.

### 6.2 Headless / scheduled path
- `BackgroundAssistantTurnRunner` becomes the engine behind `HeadlessTurnExecutor`. Two
  changes: (a) it must become **step-callable** (invoked once per step by the orchestrator
  rather than one-shot), and (b) it must **set `TaskAmbient.Current`** — today it does not,
  so headless turns have **no per-run file scoping** (a latent gap even now; fixing it here
  is cheap and unblocks Phase 3 workspaces).
- `ScheduledJobBackgroundService.ExecuteResearchAsync` creates a `Schedule`-triggered
  `AgentRun` instead of calling the runner directly. `ScheduledJobKind` generalization is
  Phase 4, but note it already persists/syncs/round-trips — no migration needed later.

### 6.3 Provider / persona capability (Q9)
- Planner/orchestrator turns resolve a persona whose `PreferredProviderId` should be
  planning-capable. On flipping the lever to **Agent**, run the existing empirical tool probe
  (`AiClientService` capability test) against the resolved provider; if it fails
  `SupportsToolCalling` or the probe, show a **non-blocking** warning ("this provider may not
  plan well — continue, switch provider, or stay in Chat"). Never a hard gate. Specialists
  (Phase 3) honor their own persona's provider, local included.
- ⚠ The raw probe only proves the provider *accepts* a tools schema, not that it *emits* calls
  (§16 R10) — §14.4 strengthens it (assert an actual tool call + validate the plan) before this
  check is trustworthy.

---

## 7. UI: the Chat/Agent lever + run-progress component

### 7.1 The lever (Q1)
- A toggle in the assistant input area: **Chat** (default) ↔ **Agent**. Persisted as a **global
  last-used default in `AppSettings`** — *not* per-chat (§16 R15; §14.1 is authoritative). Drives
  `AgentRun.RunShape`.
- **Model-suggested switch:** in Chat mode, the model can surface a "Switch to Agent" chip. ⚠ This
  needs a **new typed suggestion** (§16 R8) — `AssistantMessage.Suggestions` is
  `ObservableCollection<string>` whose click merely pastes text, so it can't carry a
  `SwitchToAgent` kind/goal/reason. Mechanism: a `suggest_agent_mode` tool the model may emit
  (§14.3); clicking flips the lever and re-runs the goal as `Planned`. The user always decides.

### 7.2 The run-progress component (Q2/Q3)
A first-class control (design pass via the frontend-design skill when built). Responsibilities:
- Ordered **step list** with status glyphs (Pending/Running/Done/Failed), current step
  highlighted, and a live **current-activity** line (e.g. "reading notes.md…").
- **Ledger strip** (Q7 transparency): per-step + total tokens / cost / elapsed, updating live.
- **States:** Planning (spinner + "building a plan"), Running (step list + activity),
  Completed/Failed (summary + artifacts), and a **`truncated`-marked Completed** rendered
  distinctly ("stopped at budget", §16 R5). `WaitingForInput`/`Paused`/`Verifying` are **not**
  rendered in Phase 1 (§16 R12).
- **Attribution-ready:** each step block can host a `PiaPersonaAvatar` for Phase 3
  sub-agent attribution; Phase 1 shows the single active persona.
- Bound to the `AgentRun` via `IAgentRunService.RunChanged` (§15.2); read-only (mutation = Phase 4).

### 7.3 Flow deep-link (`FlowAction.OpenRun`)
Additive, no migration (per the Flow map):
1. `FlowActionKind.OpenRun` **appended** to the enum (ordinals are persisted — never reorder).
2. `sealed record OpenRunAction(Guid RunId, string Label)` with `EntityId => RunId`.
3. `FlowPersistenceStore.ReconstructAction` case; upsert path already generic.
4. `FlowItemViewModel.ExecuteAction` case → new `IWindowManagerService.ShowAgentRun(Guid)`
   (copy the `ShowAssistantChat` shape).
5. Optional `FlowSource.AgentRun` + glyph + a producer surface mirroring
   `ScheduledJobNotificationSurface`. Dedup/auto-retract/persistence inherited
   (`DedupKey = runId`, `RequestDurable = true`, `Retract(runId)` on open/finish).

---

## 8. Milestones (reviewable increments)

- **1.1 — Durable spine, zero behavior change.** `AgentRuns`/`AgentSteps` tables +
  `IAgentRunService` + wiring the **headless & scheduled** paths to create durable runs
  (state + ledger + trigger linkage), with the **chat-before-run write-order fix** (§16 R1). The
  **interactive path is untouched** — its runs arrive in 1.2 (when `Planned`). Run bookkeeping is
  failure-isolated (never breaks a turn). No new user-visible UI. *Full design in §12.*
- **1.2 — The orchestration loop.** `AgentRunOrchestrator` + `IAgentPlanner` + `IAgentTurnExecutor`
  (Live + Headless) + `emit_plan` + plan→act→replan for `Planned`. Interactive Agent-mode runs
  get their runs here; `BackgroundAssistantTurnRunner` refactored into the headless step engine
  and made `TaskAmbient`-establishing. Also lands the `Planned`-run **eviction-skip** retention
  policy (§16 R17). Triggered programmatically (the user-facing lever is 1.3);
  minimal/no progress UI yet. *Full design in §13.*
- **1.3 — The Chat/Agent lever + suggestion + capability check.** Toggle + `RunShape` wiring
  (replaces the 1.2 debug trigger), `suggest_agent_mode` chip, non-blocking
  `IProviderCapabilityService` probe. *Full design in §14.*
- **1.4 — Live progress component + Flow OpenRun.** Run-progress control (plan tracker + live
  ledger, Q7 transparency) + `IAgentRunService.RunChanged` consumers + `FlowAction.OpenRun`
  deep-link + run→Flow publishing (the *headless* producer has no caller until Phase 3/4 — only
  unfocused-window interactive Planned runs publish in Phase 1, §16 R18). *Full design in §15.*
  - *As-built follow-ups (post-1.4):* R17 deletion-side Flow retraction (chat delete → retract the
    durable `OpenRun` item); the §15.1 current-activity line; and user-configurable interactive
    budgets in an Assistant → **Agent runs** settings tab (`RunProfile.FromBudget`).
- **Milestone B — Headless / background Agent runs (the unattended producer).** Activates the
  headless path that shipped *seamed* in 1.2 (`HeadlessTurnExecutor`) and 1.4
  (`AgentRunNotificationSurface`'s headless branch, §16 R18) by giving it a real producer — a
  "Run in background" affordance + scheduler emission — plus the **out-of-folder per-run workspace**
  (§9, pulled forward because unattended writes require isolation) and a headless tool-consent model.
  The first milestone that runs an agent with **no human watching the turn**: safety-first, not
  UI-first. *Full design in §17.*

---

## 9. Explicitly deferred (seamed, not built)

- **Verify/critic pass** — `Verifying` is a no-op pass-through in Phase 1 (Phase 2).
- **Budget enforcement / pausing into `WaitingForInput`** — Phase 1 only *displays* the
  ledger and applies generous terminal caps (Phase 2).
- **MCP gate fix** — MCP still bypasses the gate in Phase 1 (unchanged). The fix
  (handler returns a deferred `PluginToolCall`) is a Phase 2 prerequisite for autonomy.
  ⚠ Note: MCP is **stdio-only** today (spec's "stdio+sse" is inaccurate — SSE is not wired).
- **Sub-agents / Council-for-work** — `ParentRunId`/`AssignedPersonaId` reserved; multi-persona
  resolve path (`ResolveActiveAsync` is single-persona-per-mode today) is Phase 3.
- **Out-of-folder temp workspace** (`%LOCALAPPDATA%\Pia\runs\<runId>`) — **now built in Milestone B**
  (§17.2), pulled forward because unattended headless writes make per-run isolation a *requirement*,
  not a nicety. Requires making the `FilesToolHandler` **base root** run-aware (ambient via
  `TaskContext`), not just the subpath, and still rejecting escapes. The per-run `TaskAmbient`
  established in 1.2 is the hook.
- **Plan editability / live steering (nudge/pause/resume)** — read-only plan in Phase 1;
  mutation API shape reserved (Phase 4).

---

## 10. Key seams (file anchors)

| Concern | Anchor |
|---------|--------|
| Per-step turn engine | `AiClientService.GetChatCompletionWithToolsAsync` (thread-agnostic; reuse verbatim) |
| Live executor | `ChatSession.RunTurnAsync` / `ChatSessionManager.StartTurnAsync` |
| Headless executor | `BackgroundAssistantTurnRunner.RunAsync` (make step-callable; set `TaskAmbient`) |
| Scheduler | `ScheduledJobBackgroundService.ExecuteResearchAsync` (emit an `AgentRun`) |
| Persistence precedent | `SqliteContext` (ExtraJson + SchemaVersion + `AssistantChats` FK pattern) |
| Ledger source | `UsageDetails` aggregation already in `AiClientService` |
| Progress attribution | `PiaPersonaAvatar` / `PersonaGlyph` / `PersonaAttribution` |
| Flow deep-link | `FlowAction` / `FlowPersistenceStore.ReconstructAction` / `FlowItemViewModel.ExecuteAction` |
| Capability probe | `AiClientService` empirical tool probe + `AiProvider.SupportsToolCalling` |
| Workspace scoping | `FilesToolHandler.ResolveEffectiveRoot` + `TaskAmbient` (base-root generalization pending) |

---

## 11. Phase 1 sub-questions — RESOLVED

1. **Plan format** → **constrained `emit_plan(steps[])` tool call** (reliability on weak/local
   providers). Reasoning/tools reconciliation baked into §4.
2. **Replan aggressiveness** → **failure-only** (keep cost down until verify exists in Phase 2).
3. **`suggest_agent_mode` trigger** → **model-emitted signal** (a lightweight tool the model
   may call in Chat mode; surfaces the "Switch to Agent" chip via the `Suggestions` surface).
   No heuristic classifier.
4. **Run retention** → **`SingleTurn` lifecycle-follows-chat (FK CASCADE, no `AgentStep` rows);
   `Planned` runs retained** as durable audit artifacts. See §2.4.

---

## 12. Milestone 1.1 — detailed design (the durable spine)

**Goal:** introduce the durable run substrate and make the **unattended** paths
(headless + scheduled) produce persisted runs, with **zero user-visible behavior change** and
**no change to the interactive/UI path**. Nothing here consumes a run yet beyond writing it;
1.2 builds the orchestrator on top.

### 12.1 Enums (persisted as `int`; append-only — never reorder)

```csharp
public enum RunShape       { SingleTurn = 0, Planned = 1 }
public enum AgentRunTrigger{ User = 0, Schedule = 1, Event = 2 }
public enum AgentRunState  { Planning = 0, Running = 1, Verifying = 2, WaitingForInput = 3,
                             Paused = 4, Completed = 5, Failed = 6, Cancelled = 7 }
public enum AgentStepStatus{ Pending = 0, Running = 1, Done = 2, Failed = 3, Skipped = 4 }
```

### 12.2 Schema (raw SQL in `SqliteContext`, matching existing conventions)

`CREATE TABLE IF NOT EXISTS` blocks added to `SqliteContext.EnsureSchema`. ⚠ There is **no** global
schema-version / `user_version` mechanism (§16 R19) — the real convention is idempotent
`CREATE TABLE IF NOT EXISTS` plus per-column `PRAGMA table_info` presence checks + `ALTER TABLE`
for later column additions. TEXT PKs, ISO-8601 `"O"` timestamps. The `ON DELETE CASCADE` below **is
enforced** (Microsoft.Data.Sqlite defaults foreign keys ON — §16 R1), so **insert order matters**:
a run row requires its `AssistantChats` parent to exist first.

```sql
CREATE TABLE IF NOT EXISTS AgentRuns (
    Id                  TEXT PRIMARY KEY,
    SchemaVersion       INTEGER NOT NULL DEFAULT 1,
    ChatId              TEXT    NOT NULL,
    RunShape            INTEGER NOT NULL,
    State               INTEGER NOT NULL,
    TriggerKind         INTEGER NOT NULL,
    TriggerRef          TEXT    NULL,
    ParentRunId         TEXT    NULL,
    OwnerDeviceId       TEXT    NULL,
    Goal                TEXT    NULL,     -- SENSITIVE
    FirstMessageId      TEXT    NULL,     -- stable message Id, NOT positional ordinal (§16 R3)
    LastMessageId       TEXT    NULL,
    PolicyJson          TEXT    NULL,     -- reserved Phase 2
    LedgerJson          TEXT    NULL,
    CreatedAt           TEXT    NOT NULL,
    UpdatedAt           TEXT    NOT NULL,
    StartedAt           TEXT    NULL,
    CompletedAt         TEXT    NULL,
    ExtraJson           TEXT    NULL,
    FOREIGN KEY (ChatId) REFERENCES AssistantChats(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_AgentRuns_ChatId     ON AgentRuns(ChatId);
CREATE INDEX IF NOT EXISTS IX_AgentRuns_State      ON AgentRuns(State);
CREATE INDEX IF NOT EXISTS IX_AgentRuns_UpdatedAt  ON AgentRuns(UpdatedAt);
CREATE INDEX IF NOT EXISTS IX_AgentRuns_TriggerRef ON AgentRuns(TriggerRef);

-- Table created now (schema-forward); NO rows written until 1.2 (Planned).
CREATE TABLE IF NOT EXISTS AgentSteps (
    Id                  TEXT PRIMARY KEY,
    RunId               TEXT    NOT NULL,
    Ordinal             INTEGER NOT NULL,
    Title               TEXT    NOT NULL, -- SENSITIVE
    Intent              TEXT    NULL,     -- SENSITIVE
    Status              INTEGER NOT NULL,
    ExpectedArtifact    TEXT    NULL,
    AssignedPersonaId   TEXT    NULL,     -- reserved Phase 3
    DependsOnJson       TEXT    NULL,     -- reserved (DAG)
    ReRunnable          INTEGER NOT NULL DEFAULT 1,
    FirstMessageId      TEXT    NULL,     -- stable message Id (§16 R3)
    LastMessageId       TEXT    NULL,
    CreatedAt           TEXT    NOT NULL,
    UpdatedAt           TEXT    NOT NULL,
    ExtraJson           TEXT    NULL,
    FOREIGN KEY (RunId) REFERENCES AgentRuns(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_AgentSteps_RunId ON AgentSteps(RunId, Ordinal);
```

### 12.3 `IAgentRunService` (singleton, thread-safe, non-UI — mirrors `AssistantChatService`)

Registered `AddSingleton` in `Bootstrapper` next to `IAssistantChatService`. Owns its DB access;
callable from the UI thread and background threads alike (no `SynchronizationContext` capture).

```csharp
public sealed record AgentRunCreateRequest(
    Guid ChatId, RunShape Shape, AgentRunTrigger Trigger,
    Guid? TriggerRef = null, Guid? OwnerDeviceId = null, string? Goal = null);

public interface IAgentRunService
{
    Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default);

    Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default);
    Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default); // run + per-step ledger (§16 R16)
    Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default); // stable Ids (§16 R3)
    Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default); // §16 R5
    Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default);

    Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default);
    Task<bool> ChatHasPlannedRunAsync(Guid chatId, CancellationToken ct = default); // eviction policy (wired in 1.2)

    // Steps: API present in 1.1, exercised in 1.2 (Planned). Records per-step ledger + transcript slice.
    Task ReplaceStepsAsync(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct = default);
    Task<AgentStep?> NextPendingStepAsync(Guid runId, CancellationToken ct = default); // re-query each loop iteration (§16 R2)
    Task SetStepStatusAsync(Guid stepId, AgentStepStatus status, CancellationToken ct = default); // e.g. → Running at step start
    Task RecordStepResultAsync(Guid stepId, AgentStepStatus status,
        Guid? firstMessageId, Guid? lastMessageId, UsageDetails? usage, CancellationToken ct = default); // terminal + ledger + slice — §16 R16, R3

    event EventHandler<AgentRunChangedEventArgs> RunChanged; // for 1.4 UI/Flow; no consumers in 1.1
}
```

`LedgerJson` shape: `{ inputTokens, outputTokens, costUsd?, wallClockMs, perStep: [...] }`.
`AddUsageAsync` accrues from the `Finished` stream item's `UsageDetails` (already aggregated
across tool rounds in `AiClientService`). One call per turn today; one per step under Planned.

### 12.4 Wiring (headless & scheduled only)

**`BackgroundTurnRequest`** — add two fields (default-`User` keeps existing callers unchanged):
```csharp
AgentRunTrigger Trigger = AgentRunTrigger.User;
Guid? TriggerRef = null;
Guid? OwnerDeviceId = null;
```

**`BackgroundAssistantTurnRunner.RunAsync`** — the run must be inserted **after its chat row
exists** (FK enforced — §16 R1). Today the chat is saved only on success at the very end, and the
empty/error paths `return` without saving *any* chat — so the run cannot be created up-front. Fix:
1. Persist the run in the **same transaction as the chat**, or write a **minimal `AssistantChats`
   row up front** (so the FK target exists) and finalize it at the end. Either way, the empty/error
   paths must persist a stub chat so a `Failed` run's `ChatId` is valid and `OpenRun` (§15.3)
   resolves rather than dereferencing a missing chat.
2. Set `Running` once the parent row exists; on the `Finished` item
   `AddUsageAsync(run.Id, stepId: null, usage)`.
3. On success: `SetRunMessageRangeAsync(run.Id, firstMsgId, lastMsgId)` + `CompleteAsync`. On
   empty/error: `FailAsync`. On `OperationCanceledException`: `FailAsync(cancelled: true)`, rethrow.

*(Persist discriminator — §16 R14: the rule is **path-based**. The interactive `ChatSession`
`SingleTurn` path is the only unpersisted case; everything reaching this runner persists,
regardless of `Trigger`, which is provenance metadata defaulting `User`.)*

**`ScheduledJobBackgroundService.ExecuteResearchAsync`** — pass trigger provenance into the
request: `Trigger = Schedule`, `TriggerRef = job.Id`, `OwnerDeviceId = job.OwnerDeviceId`.
`MarkRunCompleteAsync(job.Id, result.ChatId)` is unchanged (the job→chat link stays; the
run→job link is `TriggerRef`).

*(The runner creates the run in 1.1 for DRY/single-site; 1.2 hoists creation up to the
orchestrator when `BackgroundAssistantTurnRunner` becomes the `HeadlessTurnExecutor` engine.)*

### 12.5 Failure isolation (the "zero behavior change" guarantee)

Every `IAgentRunService` call from a turn path is wrapped so a run-bookkeeping failure **cannot**
fail the turn:
```csharp
try { await _runService.SetStateAsync(run.Id, Running, ct); }
catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping failed for {RunId}", run.Id); }
```
The turn's success is defined solely by the chat/AI pipeline, exactly as today. A run is
best-effort metadata layered beside the turn, never on its critical path.

### 12.6 State & ledger mapping

| Turn event | Run transition |
|---|---|
| Turn dispatched | `CreateAsync` → `Running`, `StartedAt` set (SingleTurn skips `Planning`) |
| `Finished` (usage) | `AddUsageAsync` |
| Success (non-empty) | `Completed`, `CompletedAt` |
| Empty / error | `Failed` |
| `OperationCanceledException` | `Cancelled` |

`ChatState` (interactive, runtime-only) is **not** touched in 1.1. The 1.2 `LiveTurnExecutor`
is what maps `ChatSession.StateChanged`/`TurnCompleted` onto `AgentRunState`.

### 12.7 Privacy / logging (per `CLAUDE.md`)

- `Goal`, `AgentStep.Title`/`Intent` are user content → `SensitiveDebug` only; never log the
  values at `Information`. Log `run.Id`/`State`/`TriggerKind` freely.
- `LedgerJson` token counts are safe to log. No URLs in this layer.
- `Goal`/step text are E2EE-eligible **if** runs are ever synced (not in Phase 1) — mirror the
  persona plaintext/encrypted field split then.

### 12.8 Tests (defer execution to Windows/CI per build-environment note)

- Schema creates idempotently; `CREATE TABLE IF NOT EXISTS` re-run is a no-op.
- `CreateAsync` → `Running`; `AddUsageAsync` accrues; `Complete`/`Fail`/`Cancelled` transitions.
- FK CASCADE (now known-enforced, §16 R1): deleting an `AssistantChats` row removes its `AgentRuns`
  (and `AgentSteps`).
- **Write order (§16 R1):** inserting a run before its chat row throws the FK constraint; the wiring
  persists chat-first (incl. a stub chat on empty/error paths).
- `ChatHasPlannedRunAsync` correctness for the eviction policy.
- Headless run persists with `TriggerKind`/`TriggerRef`/ledger correct.
- **Regression:** existing chat/scheduled-job tests stay green; injecting an `IAgentRunService`
  that throws does **not** fail a background turn (failure-isolation test).

### 12.9 Out of scope for 1.1 (deferred to later milestones)

Orchestrator / executors / `Planned` shape / `emit_plan` (1.2); interactive-path runs (1.2);
`TaskAmbient` in the headless runner (1.2 — it changes file scoping, so not "zero behavior");
the Chat/Agent lever (1.3); progress UI + `FlowAction.OpenRun` + `RunChanged` consumers (1.4);
verify, budget enforcement, MCP gate, sub-agents, out-of-folder workspace (Phase 2/3).

---

## 13. Milestone 1.2 — detailed design (the orchestration loop)

**Goal:** the `Planned` shape becomes real. A goal is decomposed by `emit_plan`, executed
step-by-step, and replanned on failure — the same loop for interactive (streams into a live
`ChatSession`) and headless (accumulates off-thread). This is where the interactive path
first gets a persisted run. Verify is a no-op pass-through (Phase 2); budgets are generous +
terminal (no pausing yet).

### 13.1 Component responsibilities

- **`AgentRunOrchestrator`** (new, UI-agnostic, no `SynchronizationContext` capture). Owns the
  loop: plan → act each step → failure-only replan → complete. Holds the run's
  `CancellationTokenSource` (one per run), the ledger accrual, and a `RunContext`
  (goal + completed-step summaries + scratchpad). Never touches the UI thread.
- **`IAgentPlanner`** (new, environment-agnostic). `PlanAsync` / `ReplanAsync` call
  `AiClientService.GetChatCompletionWithToolsAsync` with `tools = [emit_plan]` and an inline
  capture handler. Returns `AgentStep[]`. No user tools, no gate, no streaming — a plan is
  internal metadata rendered by the progress component (1.4), not chat text.
- **`IAgentTurnExecutor`** (new). Runs **one act step-turn** in its environment. Two impls:
  - **`LiveTurnExecutor`** — bound to a `ChatSession`; **owns UI-thread marshaling** (captures
    the UI `SynchronizationContext` at construction, `Post`s each step onto it and awaits).
    Streams into the transcript via the session's existing machinery (action cards + gate work
    exactly as today).
  - **`HeadlessTurnExecutor`** — off-thread; wraps the refactored `BackgroundAssistantTurnRunner`
    exchange engine (reads-always / writes-if-granted), accumulating into the chat.

**Thread rule:** the orchestrator is thread-agnostic; each executor is responsible for its own
threading. This is what lets one loop drive both the UI-affine `ChatSession` and off-thread work.

### 13.2 The loop (orchestrator pseudocode)

```csharp
await executor.BeginRunAsync(run, ct);                       // Live: session→Running; Headless: seed system+goal
await _runService.SetStateAsync(run.Id, Planning);
var plan = await _planner.PlanAsync(ctx.Goal, ctx, persona, provider, cts.Token);  // ≥1 provider round — §13.3/§16 R6
await _runService.ReplaceStepsAsync(run.Id, plan.Steps);

int replans = 0;
// Re-read the persisted Pending list each iteration. A foreach over a snapshot would NOT pick up
// replanned steps (§16 R2) — query the next Pending step until none remain.
while (await _runService.NextPendingStepAsync(run.Id) is { } step) {
    if (StepBudgetExceeded() || WallClockExceeded()) {        // §16 R5: not silent
        await _runService.CompleteAsync(run.Id, truncated: true, truncationReason: "budget");
        await executor.EndRunAsync(run, ct); return;
    }
    await _runService.SetStateAsync(run.Id, Running);
    await _runService.SetStepStatusAsync(step.Id, Running);
    var r = await executor.ExecuteStepAsync(run, step, ctx, cts.Token);
    await _runService.RecordStepResultAsync(step.Id,          // per-step ledger + transcript slice — §16 R16/R3
        r.Succeeded ? Done : Failed, r.FirstMessageId, r.LastMessageId, r.Usage);
    ctx.RecordStep(step, r);
    if (r.Cancelled) { await _runService.FailAsync(run.Id, r.Error, cancelled: true); return; }
    if (!r.Succeeded) {
        if (replans++ < MaxReplans) {
            var revised = await _planner.ReplanAsync(ctx, failure: r.Error, cts.Token);
            await _runService.ReplaceStepsAsync(run.Id, KeepDone(run).Concat(revised)); // Done steps immutable
            continue;                                         // loop re-queries → picks up revised steps (§16 R2)
        }
        await _runService.FailAsync(run.Id, r.Error); return;
    }
}
await _runService.SetStateAsync(run.Id, Verifying);          // no-op pass-through in P1 (not rendered — §16 R12)
await executor.EndRunAsync(run, ct);                         // Live: session→Idle/Completed; Headless: persist chat
await _runService.CompleteAsync(run.Id);
```

### 13.3 `emit_plan` tool + planner degrade

```jsonc
{ "name": "emit_plan",
  "description": "Emit the ordered plan of steps to accomplish the goal.",
  "parameters": { "type": "object", "required": ["steps"], "properties": {
    "steps": { "type": "array", "items": { "type": "object",
      "required": ["title","intent"], "properties": {
        "title": {"type":"string"}, "intent": {"type":"string"},
        "expectedArtifact": {"type":"string"} } } } } } }
```

- Reasoning/tools: because `emit_plan` is a tool, on Chat-Completions providers effort is not
  boostable during planning (default reasoning); Responses-API providers get both (§4).
- **Degrade (Q9, §16 R10):** if the model returns no `emit_plan` call, retry once with a firmer
  instruction; if still none — or if the emitted plan fails **semantic validation** (empty,
  duplicate, or implausible steps) — **fall back to the `SingleTurn` path** (run the goal as one
  ordinary turn) rather than recording a degenerate 1-step `Planned` run, and `log()` the degrade.
  Never hard-fail planning.
- **Loop termination (§16 R6):** `GetChatCompletionWithToolsAsync` has **no** handler-driven early
  exit — after the `emit_plan` call is captured it `continue`s into one more provider round before
  the loop ends on a no-tool-call round. So a plan turn costs **≥1 extra round** (this is why §4's
  "two-call doubles cost" is a Phase-2 *optimization*, not a cost the design avoids). Contract: the
  capture handler returns a short ack; repeated `emit_plan` calls are **last-write-wins**; the
  planner takes the final captured plan. An optional `stop-after-tool` hook on
  `GetChatCompletionWithToolsAsync` to drop the extra round is a Phase-2 efficiency.

### 13.4 `IAgentTurnExecutor`

```csharp
public interface IAgentTurnExecutor
{
    Task BeginRunAsync(AgentRun run, CancellationToken ct);
    Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct);
    Task EndRunAsync(AgentRun run, CancellationToken ct);
}
public sealed record StepTurnResult(
    bool Succeeded, bool Cancelled, string? Error,
    string VisibleText, UsageDetails? Usage, Guid FirstMessageId, Guid LastMessageId);
```

- **`LiveTurnExecutor.ExecuteStepAsync`** → `Post` to the UI context → `session.RunStepTurnAsync(spec)`
  (§13.5). Context for the exchange = system prompt + visible transcript so far + an **ephemeral**
  "Execute step K: {intent}. Expected: {expectedArtifact}" message (never added to `Messages`,
  never persisted — §13.7). Assistant output streams into a new visible `AssistantMessage`
  attributed to the persona; tool calls use the session's existing `HandleToolCall` gate.
- **`HeadlessTurnExecutor.ExecuteStepAsync`** → the refactored exchange engine (§13.6) with the
  same ephemeral step instruction appended to its accumulating message list; `grantedWrites`
  handler; no streaming.

### 13.5 `ChatSession` changes (the riskiest part — UI-affine hot path)

Behavior-preserving refactor + one additive method. **Guard:** the existing single-turn path
must be byte-for-byte unchanged (golden-transcript test, §13.12).

1. **Extract** the *exchange body* of `RunTurnAsync` (`ChatSession.cs:323–365` — stream consumption
   + tool loop) into `private async Task RunModelExchangeAsync(...)`. ⚠ The catch handlers
   (`:367–439`) and the `finally` (`:440–495`) are **not** part of the body — the earlier
   ':323–386' boundary was wrong, it bisected the truncation catch (§16 R4).
2. **Split the `finally`** (§16 R4): it does more than the terminal-state decision — it also clears
   `IsStreaming`, runs the **safety-net PII detokenization**, restores the ambients, and
   synthesizes the empty-response placeholder. Factor a **per-exchange cleanup** helper
   (`IsStreaming=false` + detokenize + ambient restore + empty synthesis) that **both**
   `RunTurnAsync` and `RunStepTurnAsync` run — omitting it in the step path would leak tokenized
   PII into the (syncing) transcript and stick `IsStreaming` true. The **per-run terminal finalize**
   (the `Idle`/`Completed`/`Error` state decision) runs only in `RunTurnAsync` (single-turn) and
   the orchestrator's `EndRunAsync`.
3. **Exceptions → `StepTurnResult`** (§16 R4): today's catch handlers set `ChatState.Error` + raise
   a `RunFailed` snackbar. In a `Planned` step that must instead surface as
   `StepTurnResult(Succeeded=false, Error=…)` for the orchestrator to replan — **no**
   `ChatState.Error`, **no** per-step failure snackbar. `RunModelExchangeAsync` throws;
   `RunTurnAsync` keeps today's catches verbatim; `RunStepTurnAsync` converts them to a failed result.
4. **Add** `internal async Task<StepTurnResult> RunStepTurnAsync(StepTurnSpec spec, CancellationToken ct)`:
   builds `context` from `Messages` + the ephemeral instruction, creates a persona-attributed target
   `AssistantMessage`, runs `RunModelExchangeAsync` + the per-exchange cleanup, returns the result.
   No per-run finalize.
5. **Run bracketing:** the session enters `Running` at run start and stays `Running` across all
   steps (`ChatState` does not flap per step); a mid-step tool-approval `WaitingForTool` is the one
   exception and stays visible via the action card, not the run state (§16 R12).
   `BeginRunAsync`/`EndRunAsync` on the live executor own the run-level bracketing.
6. **`TaskAmbient`:** `TaskId = run.Id` is **run-stable** (unifies file-staleness keys across steps),
   but the `TaskContext` **object is re-set per step** (§16 R9) — it bundles `TaskId` +
   `OnFileTouched`, and the touch sink must target the *current* step's `AssistantMessage`, so a
   single run-scoped context would misattribute file chips. Cheap: an AsyncLocal assignment inside
   each UI-posted step continuation. ⚠ Staleness keys shift from chat-Id (prior single-turns) to
   `run.Id` within a Planned run — reads recorded before the run won't match; acceptable in Phase 1.

### 13.6 `BackgroundAssistantTurnRunner` refactor + `TaskAmbient`

- **Extract** the single exchange (build messages → `GetChatCompletionWithToolsAsync` →
  post-process) into a reusable `RunExchangeAsync(messages, setup, grantedWrites, ct)` the
  `HeadlessTurnExecutor` calls **per step**, accumulating messages across steps and persisting
  the `SyncAssistantChat` once at `EndRunAsync` (title precedence unchanged).
- **Set `TaskAmbient.Current`** = `new TaskContext(run.Id, workingSubpath, onFileTouched)` around
  the run (the deferred-from-1.1 fix). Phase-1 `workingSubpath` = the existing default (out-of-folder
  temp workspace is Phase 3); this is a *correctness* change — headless writes were previously
  scoped to `Guid.Empty`. The single-turn `RunAsync` (still used by scheduled SingleTurn) keeps
  working via a one-step call into the same engine.

### 13.7 Message model (transcript stays clean)

- The visible transcript = `[user: goal]` + one `[assistant]` message per step. **Step
  instructions are ephemeral** model context, appended only for that exchange.
- Resume (Phase 2) re-derives instructions from the persisted `AgentStep.Intent`, so the clean
  transcript loses nothing. `AgentStep.First/LastMessageId` map each step to its transcript slice.
- Step instructions injected as a `User`-role context message (avoids multi-`System`-message
  incompatibilities across providers); it is simply never mirrored into `Messages`.

### 13.8 Budgets, replan, cancellation

- **Generous terminal caps** (§5): `MaxSteps ≈ 24`, `MaxReplans ≈ 2`, wall-clock — **both** the
  step and wall-clock checks live in the §13.2 loop. Exceeding ends the run as `Completed`+`truncated`
  (§16 R5), surfaced not silent. Per-step tool rounds keep the internal cap of 10.
- **Replan is failure-only** (Q11.2): triggered by a failed step, never speculatively.
- **Cancellation (§16 R13):** for an interactive `Planned` run the run's `CancellationTokenSource`
  is **linked from the session's** (`CreateLinkedTokenSource(session.Cts.Token)`), so
  `ChatSession.Cancel()` (which cancels `session.Cts`) propagates to the whole run + the in-flight
  step. The `Planned` branch does **not** call `BeginTurn` per step (it dispose/recreates
  `session.Cts` and is built for single turns); the run CTS is created once at run start, disposed
  at run end. Headless runs own the run CTS outright.

### 13.9 Provider / persona (single persona in Phase 1)

The **active persona** (resolved as today via `ResolveActiveAsync`) is *both* the orchestrator/
planner and the actor for every step. `AgentStep.AssignedPersonaId` stays null (multi-persona
delegation is Phase 3). The non-blocking planner-provider capability check is **1.3** (the lever);
1.2 uses whatever the active persona resolves.

### 13.10 DI + failure isolation

- `AgentRunOrchestrator` + `IAgentPlanner`: transient. `HeadlessTurnExecutor`: created in a fresh
  DI scope per run (as `ScheduledJobBackgroundService` already does). `LiveTurnExecutor`:
  constructed on the UI thread, bound to the session.
- Run **state/ledger** writes remain failure-isolated (§12.5). But a **planner** failure (can't
  produce a plan even after degrade) or an **executor** failure *does* fail the step/run — those
  are on the run's critical path, unlike bookkeeping.

### 13.11 Triggering Agent mode in 1.2 (before the lever)

`ChatSessionManager.StartTurnAsync` branches: `Planned` → create run + hand to the orchestrator
with a `LiveTurnExecutor`; else today's `RunTurnAsync` path (unchanged). In 1.2 the `Planned`
branch is reachable **programmatically** (a debug command / integration harness / a temporary
flag), so the loop is testable before 1.3 wires the user-facing Chat/Agent toggle.

### 13.12 Tests

- Planner: `emit_plan` parsed into ordered steps; no-call/invalid-plan → retry → **SingleTurn
  fallback** (not a 1-step Planned run — §16 R10).
- Loop: N-step plan runs in order; per-step ledger accrues; `Completed` on success.
- **Replan re-query (§16 R2):** after `ReplaceStepsAsync`, the loop executes the *revised* steps and
  skips dropped ones (guards against the foreach-snapshot bug).
- Replan bound: a failing step triggers ≤ `MaxReplans` replans; `Done` steps preserved; then `Failed`.
- **Budget (§16 R5):** step/wall-clock exhaustion → `Completed`+`truncated`, never a clean `Completed`.
- **Cleanup split (§16 R4):** a step-turn clears `IsStreaming` and detokenizes PII — no tokenized
  PII persists to the transcript.
- **Cancellation (§16 R13):** `ChatSession.Cancel()` during a Planned run stops the in-flight step
  (linked CTS); mid-step → `Cancelled`, no further steps.
- **Interactive regression:** Chat-mode single-turn output/state/persistence byte-for-byte
  unchanged after the `RunModelExchangeAsync` extraction (golden-transcript test).
- Headless: multi-step run accumulates one chat; `TaskAmbient.TaskId == run.Id` during the run.

### 13.13 Out of scope for 1.2

Chat/Agent lever + `suggest_agent_mode` + capability probe (1.3); progress UI + `FlowAction.OpenRun`
+ `RunChanged` consumers (1.4); verify/critic, budget *pausing*, MCP gate, sub-agents/multi-persona,
out-of-folder workspace (Phase 2/3).

---

## 14. Milestone 1.3 — detailed design (the lever + suggestion + capability)

**Goal:** make Agent mode a user affordance and let the model *suggest* it. Turns the 1.2
programmatic trigger into a real toggle; adds the escalation chip and a non-blocking provider check.

### 14.1 The Chat/Agent lever

- **State:** a per-session UI property on `AssistantViewModel`
  (`[ObservableProperty] bool _agentModeEnabled`, default `false` = Chat). Not a chat column —
  the chosen shape is captured on the `AgentRun` when a `Planned` run is created (1.1/1.2).
- **Default persistence:** last-used mode stored as a window/mode default in `AppSettings`
  (mirrors how the active persona selection persists via `OnActivePersonaChanged`,
  `AssistantViewModel.cs:450`). Reopening a chat starts from that default.
- **UI:** a two-state segmented toggle in the input area near the persona picker
  (`AssistantView.xaml:370`) — "💬 Chat" / "🤖 Agent". Visual design pass via the
  **frontend-design** skill at build time; this spec fixes only behavior + bindings.
- **Disabled state:** if the active persona's `ToolScope == None` (e.g. *ExplainItSimply*),
  Agent mode is unavailable (a no-tool persona can't do multi-step tool work) — toggle disabled
  with a tooltip.

### 14.2 `RunShape` wiring

`ChatSessionManager.StartTurnAsync` (`:322`) reads `AgentModeEnabled` → sets
`request.RunShape`. The `Planned` branch (from §13.11) now fires from the toggle instead of the
debug hook; the `SingleTurn`/Chat branch is today's unchanged path. On flipping to Agent with a
non-`Capable` provider, route through the §14.4 warning first.

### 14.3 `suggest_agent_mode`

- **Injection:** `AssistantPromptComposer.PrepareTurn` adds a `suggest_agent_mode(reason)` tool
  **only when** mode == Chat **and** `ToolScope != None` **and** the provider supports tools — no
  reasoning regression (those turns already carry plugin tools), no-tool personas never see it.
  ⚠ `PrepareTurn` has **no mode parameter today** (§16 R7): the "Chat only" condition means a
  signature change threaded through its **three call sites** (`ChatSessionManager`,
  `AssistantViewModel`, `BackgroundAssistantTurnRunner`), explicitly **excluding** the headless
  path (a user-affordance tool in a headless turn has no one to click it).
- **Handler:** ⚠ **not** "inline like `emit_plan`" (§16 R7) — `emit_plan` works only because the
  planner is a dedicated call site with its own handler. A normal Chat turn's handler is
  `ChatSession.HandleToolCall`, which routes every call to `PluginService` (unknown →
  "Unknown tool."). The interception point is a **pre-route special-case** of the tool name in
  `ChatSession.HandleToolCall` (least leaky) that records the `reason` and returns a short ack —
  not gated.
- **Surface:** ⚠ the chip needs a **new typed suggestion** (§16 R8) — `AssistantMessage.Suggestions`
  is `ObservableCollection<string>` whose click merely pastes text. Add an
  `AgentModeSuggestion { Goal, Reason }` (+ DataTemplate + command) alongside the string
  collection; this is net-new 1.3 UI work, not "reuse". The model still answers in Chat; the chip
  is offered *alongside*, non-disruptive.
- **Action:** clicking → `AssistantViewModel.SwitchToAgentCommand(goal)` sets
  `AgentModeEnabled = true` and re-dispatches the goal as a `Planned` turn (reuses the
  regeneration path; the prior Chat answer stays in the transcript).

### 14.4 `IProviderCapabilityService` (non-blocking, cached)

```csharp
public enum PlanningCapability { Capable, Weak, Unknown }
public interface IProviderCapabilityService {
    Task<PlanningCapability> GetPlanningCapabilityAsync(AiProvider provider, CancellationToken ct = default);
    void Invalidate(Guid providerId);   // on provider config change
}
```
- `Capable` = `provider.SupportsToolCalling` **and** a **strengthened** probe passes. ⚠ The
  existing probe (`AiClientService:756`) only checks the provider *accepts* a tools schema (no
  `RequireAny`; success = no 400/404) — it does **not** verify a tool call is emitted (§16 R10).
  Strengthen it to assert an actual tool call (or an `emit_plan`-shaped probe); `Weak` = it fails
  or `!SupportsToolCalling`. Pair with **semantic validation** of `emit_plan` output before
  recording a `Planned` run (§13.3 degrade → SingleTurn on failure). Probe-once + cache per
  `providerId` (mirrors `CloudCapabilityService`).
- **UX:** flipping to Agent (or first planning turn) with `!= Capable` shows a **non-blocking**
  banner: *"This provider may not plan reliably."* → **[Continue] [Choose provider…] [Stay in Chat]**.
  Optional per-provider "don't warn again". A subtle ⚠ adorner sits on the Agent toggle for a
  `Weak` provider. Never hard-blocks — local providers remain usable (Q9).

### 14.5 Tests

- Toggle drives `RunShape`; disabled for `ToolScope == None`; default persists across reopen.
- `suggest_agent_mode` injected only in Chat + `ToolScope != None` + tool-capable provider.
- Clicking the chip flips the lever and re-runs the goal as `Planned`.
- `GetPlanningCapabilityAsync` caches; `Invalidate` re-probes; `Weak` surfaces the banner but
  never blocks.

### 14.6 Out of scope for 1.3

The progress component + Flow (1.4); auto-detection *without* a model signal (Phase 4); any
persona→provider *reassignment* UI beyond linking to existing provider settings.

---

## 15. Milestone 1.4 — detailed design (progress UI + Flow OpenRun)

**Goal:** make a `Planned` run *visible while it works* (Q2/Q3) and *reachable after the fact*
via Flow. This is the live **plan tracker + ledger**, not the full audit timeline (that richer
trace is Phase 2 §11).

### 15.1 The run-progress component

`RunProgressPanel` (WPF control; visual design via **frontend-design** at build time) bound to a
`RunProgressViewModel`. Responsibilities:
- **Step list:** ordered steps with status glyphs (Pending/Running/Done/Failed), the `Running`
  step highlighted, each row carrying a `PiaPersonaAvatar` (single active persona in Phase 1).
- **Current-activity line:** active step title + optional in-flight tool status (reuses the
  existing per-message `StatusText` surface); a spinner while `Planning`.
- **Ledger strip (Q7 transparency):** per-step + total input/output tokens, cost where a price
  table exists, elapsed — updating live.
- **States:** `Planning` (spinner + "building a plan"), `Running` (list + activity),
  `Completed`/`Failed` (summary), and a **`truncated`-marked `Completed`** rendered distinctly
  ("stopped at budget", §16 R5). `Verifying` (pass-through), `WaitingForInput`, `Paused` are
  **not** rendered in Phase 1 (§16 R12).
- **Read-only** (mutation = Phase 4 steering). Embedded in `AssistantView` above/beside the
  transcript when the active chat has a live or selected `Planned` run; the transcript keeps
  streaming as today (the panel is *additive*, not a replacement).

### 15.2 `RunChanged` consumers / the ViewModel

`RunProgressViewModel` subscribes to `IAgentRunService.RunChanged` (from §12.3) filtered to its
`runId`, **marshals to the UI thread** (the singleton service may raise off-thread for headless
runs), and projects `AgentRun.State` + `AgentSteps` + `LedgerJson` into observable properties.
The orchestrator's existing `SetStateAsync`/`SetStepStatusAsync`/`AddUsageAsync` writes are the
event source — no new orchestrator hooks needed. This is the **first consumer** of `RunChanged`
(dormant since 1.1).

### 15.3 `FlowAction.OpenRun` (additive; no migration)

Per the Flow map, five localized touch-points:
1. `FlowActionKind.OpenRun` **appended** to the enum (ordinals are persisted — never reorder).
2. `sealed record OpenRunAction(Guid RunId, string Label) : FlowAction(Label)` with
   `Kind => OpenRun`, `EntityId => RunId` (⇒ `IsReDerivable == true`).
3. `FlowPersistenceStore.ReconstructAction` (`:191`): `OpenRun => new OpenRunAction(entityId, label)`.
4. `FlowItemViewModel.ExecuteAction` (`:167`): `case OpenRunAction run:` →
   `IWindowManagerService.ShowAgentRun(run.RunId)` then `RetractByKey`.
5. `IWindowManagerService.ShowAgentRun(Guid)` — new, mirroring `ShowAssistantChat`; resolves the
   run → `ShowAssistantChat(run.ChatId)` and focuses that run (no dedicated run window — a run is a
   slice of a chat). ⚠ **Missing-run handling (§16 R17):** a durable `OpenRun` item can outlive its
   run (chat deleted → run cascaded away), so `ShowAgentRun` must handle a null run by retracting
   the stale Flow item + a brief toast — never dereference a missing `ChatId`.

### 15.4 Run → Flow publishing

`AgentRunNotificationSurface` (new; mirrors `ScheduledJobNotificationSurface`) subscribes to
`IAgentRunService.RunChanged` and publishes for **non-foreground** `Planned` runs
(headless/scheduled, or any whose window isn't focused):
- `Completed` → `FlowSeverity.Success` item, `Action = OpenRunAction`.
- `Failed` → `FlowSeverity.Error` item, `Action = OpenRunAction`.
- `DedupKey = runId`, `Lifetime.Persistent`, `RequestDurable = true`; `Retract(runId)` on open
  **and on run/chat deletion** (§16 R17) so no durable item dangles.
- `WaitingForInput`/`ActionRequired` publishing is **Phase 2** (needs the pause machinery).
- Optional: add `FlowSource.AgentRun` + a `FlowSourceToSymbolConverter` glyph; otherwise reuse
  `BackgroundChat`. Foreground interactive runs publish nothing (the user is watching the panel).
- ⚠ **Phase-1 reachability (§16 R18):** no Phase-1 trigger creates a *headless* `Planned` run
  (scheduler emits `SingleTurn`; the lever + 1.2 trigger are interactive), so this headless branch
  + `HeadlessTurnExecutor` have **no production caller** until Phase 3/4 — the only reachable case
  in Phase 1 is an *unfocused-window interactive* Planned run.

### 15.5 Tests

- Panel reflects `Planning → Running(step i) → Completed/Failed` from `RunChanged`; ledger
  accrues live; off-thread `RunChanged` is marshaled without cross-thread exceptions.
- `OpenRunAction` round-trips through `FlowPersistenceStore` (persist → reconstruct) and
  `ExecuteAction` opens the run's chat + retracts.
- A non-foreground (unfocused-window) `Planned` `Failed` run publishes exactly one durable, deduped
  Flow item; opening it retracts. *(A true headless producer arrives Phase 3/4 — §16 R18.)*

### 15.6 Out of scope for 1.4

The full audit timeline with per-tool decisions (Phase 2 §11); `AccentColor` multi-persona
differentiation (Phase 3); `WaitingForInput`/budget-pause Flow items (Phase 2); plan
editing/steering (Phase 4).

---

## 16. Red-team corrections (changelog)

A 3-agent adversarial review (opus architecture + fable consistency + fable grounding) plus
empirical verification produced the corrections below. **Headline:** the review's top finding —
"SQLite foreign keys are OFF, so `ON DELETE CASCADE` is a dead no-op" — is **empirically false**.
Microsoft.Data.Sqlite 10.0.9 turns FK enforcement **ON** by default (`PRAGMA foreign_keys=1`;
cascade verified against a bare `Data Source=` connection). The finding was *inverted*, not
discarded: because FK **is** enforced, the real defect is the run-before-chat **insert order** (R1),
and the §2.4 CASCADE retention design is sound. See memory `sqlite-fk-enforcement`.

Legend: severity is post-verification; ✓ = claim checked against source this session.

| R | Sev | Defect | Resolution | Sections |
|---|-----|--------|------------|----------|
| R1 | **crit**✓ | FK is enforced (not off); run row inserted before its chat row → FK throw → 1.1 silently persists 0 headless runs; failed headless turns never save a chat | Insert run in the chat's transaction / write a stub chat first (incl. failure paths); keep CASCADE | §2.4, §6.1, §12.2, §12.4, §12.8 |
| R2 | high✓ | `foreach` over a step snapshot never runs replanned steps | `while` + `NextPendingStepAsync` re-query per iteration | §12.3, §13.2, §13.12 |
| R3 | high✓ | Transcript slices keyed by positional `Ordinal` (reassigned every save) | Reference by stable message `Id` (`First/LastMessageId`) | §2.1, §2.2, §4, §12.3 |
| R4 | high✓ | `RunStepTurnAsync` skipping `finally` drops per-exchange cleanup incl. PII detokenize; extraction boundary bisected the catches | Split per-exchange cleanup (both paths) vs per-run finalize; exception→`StepTurnResult` | §13.5, §13.12 |
| R5 | high | Budget exhaustion completes as `Completed` — the silent truncation the plan claims to kill | `Completed`+`truncated` marker + wall-clock check in loop; rendered distinctly | §5, §7.2, §13.2, §13.8, §15.1 |
| R6 | med✓ | `emit_plan` can't end the tool loop → ≥1 extra discarded round (undercuts §4 cost claim) | Termination contract (sunk round, last-write-wins); optional stop-after-tool hook | §4, §13.3 |
| R7 | med✓ | `suggest_agent_mode` has no inline hook; `PrepareTurn` has no mode param | Pre-route special-case in `HandleToolCall`; `PrepareTurn` mode flag across 3 sites (exclude headless) | §14.3 |
| R8 | med✓ | Chip can't ride `Suggestions` (`ObservableCollection<string>`) | New typed `AgentModeSuggestion` + template + command | §7.1, §14.3 |
| R9 | med✓ | Run-scope `TaskContext` breaks per-step file chips; staleness-key divergence | Per-step `TaskContext` re-set with run-stable `TaskId` | §13.5 |
| R10 | med✓ | Probe measures schema acceptance, not tool emission → degenerate Planned runs | Strengthen probe (assert a call) + semantic plan validation; degrade to SingleTurn | §6.3, §13.3, §14.4 |
| R11 | high✓ | §6.1 (run+executor for interactive SingleTurn) contradicts the milestones | §6.1 rewritten: interactive SingleTurn = literal `RunTurnAsync`, no run | §6.1 |
| R12 | med✓ | `WaitingForTool→WaitingForInput` mapping vs "ChatState doesn't flap" vs Phase-2 defer | Drop the Phase-1 mapping; tool-approval waits stay on the action card | §3, §7.2, §13.5, §15.1 |
| R13 | med✓ | Dual cancellation (session CTS vs run CTS) unreconciled | Run CTS linked from `session.Cts`; Planned skips per-step `BeginTurn` | §13.8, §13.12 |
| R14 | low✓ | Persist predicate: "keys on trigger" vs "path is the discriminator" | State the path-based rule identically; drop "(non-User)" label | §2.4, §12.4 |
| R15 | low✓ | Lever persistence: §7.1 per-chat vs §14.1 global | Global last-used default (§14.1 authoritative) | §7.1 |
| R16 | med✓ | 1.1 `IAgentRunService` can't record the per-step ledger/ranges §4/§13/§15 need | Extend API now (`AddUsageAsync(stepId)`, `RecordStepResultAsync`, `NextPendingStepAsync`) | §12.3, §13.2 |
| R17 | med✓ | Eviction-skip wiring in no milestone; durable `OpenRun` can dangle | Assign eviction-skip to 1.2; `ShowAgentRun` missing-run handling + retract-on-delete | §2.4, §8, §15.3, §15.4 |
| R18 | low✓ | §15 headless Planned publishing has no Phase-1 producer | Reworded to unfocused interactive; true headless producer is Phase 3/4 | §8, §15.4, §15.5 |
| R19 | low✓ | "bump the schema version per convention" — no such mechanism | Real convention: `CREATE TABLE IF NOT EXISTS` + `table_info`/`ALTER` | §12.2 |

Two agent claims were themselves wrong and are **not** actioned: *FK-is-off* (R1, inverted above),
and *"`ChatMessageExtras.cs` does not exist"* (it does — but R8's core, `Suggestions` being a
`string` collection, is verified true regardless).

---

## 17. Milestone B — Headless / background Agent runs (the unattended producer)

**Goal:** activate the headless execution path that shipped *seamed* in 1.2 (`HeadlessTurnExecutor`)
and 1.4 (`AgentRunNotificationSurface`'s headless branch, §16 R18) by giving it a **real producer**, so
a `Planned` run can execute **unattended** — no interactive `ChatSession` driving it — and surface in
**Flow** on completion/failure. This is the first milestone that runs an agent with **no human watching
the turn**, so its center of gravity is **safety** (an isolated workspace + a tool-consent model), not
new UI. It pulls the Phase-3 **out-of-folder workspace** (§9) forward because unattended writes make
per-run isolation a requirement, not a nicety.

**Depends on (all already built):** 1.2 `AgentRunOrchestrator` + `HeadlessTurnExecutor` + per-run
`TaskAmbient` (`TaskId = run.Id`); 1.4 `AgentRunNotificationSurface` (the Flow producer — publishes for
non-foreground Planned runs; R17 deletion-side retraction already covers durable items); the 1.2c
Assistant → Agent runs budget settings (extended to scheduled here). The Flow side is done; this
milestone is the **producer** + the **workspace substrate** + the **headless consent model**.

### 17.1 The producer(s)

Two triggers create a headless `Planned` run and hand it to `AgentRunOrchestrator` with a
`HeadlessTurnExecutor` in a **fresh DI scope** (as `ScheduledJobBackgroundService` already scopes runs):

1. **"Run in background" (primary, user-facing).** A send-time choice in Agent mode: instead of the live
   `LiveTurnExecutor` (§14.2), dispatch the goal as a **headless** Planned run so the user can close /
   navigate away and get a Flow item when it finishes. Reuses the 1.4 `OpenRun` deep-link (the Flow item
   opens the run's chat). Small net-new UI: an Agent-mode "run detached" affordance (e.g. a split
   "Send ▸ Run in background") — design pass via the **frontend-design** skill.
2. **Scheduler (secondary).** `ScheduledJobBackgroundService` emits a `Schedule`-triggered `Planned` run
   for an "agent job" kind, generalizing `ScheduledJobKind` (it already persists / syncs / round-trips,
   §6.2). The full create/edit/list **scheduler UI** for agent jobs stays deferred (Phase 4); this
   milestone wires the *execution* + a minimal/programmatic trigger.

Both are **non-foreground by definition**, so `AgentRunNotificationSurface` publishes for them with no
change (Completed→Success, Failed→Error; `DedupKey = runId`; durable; retract on open + on chat delete).

### 17.2 Workspace isolation (the safety prerequisite)

> **Amendment (post-build, owner decision):** unattended runs write their **real deliverables to the
> assistant files folder** with full read/write/**delete**, contained (no escape, no system paths) exactly
> like an interactive chat — only MCP stays withheld. `runs\<runId>` is retained as the run's **ephemeral
> scratch/temp** directory (auto-cleaned), not the deliverable root. The per-run base-root redirect below is
> kept as a *reserved seam* (`TaskContext.WorkspaceRoot` + `HeadlessTurnExecutor.Initialize(workspaceRoot)`)
> for a future **opt-in per-run sandbox**, but is not engaged by default. The containment machinery and its
> escape-fuzz tests still apply verbatim to whichever root is active (the assistant folder by default).

Unattended multi-step runs that **write files** must not share the interactive default folder. Give each
headless run an isolated workspace `%LOCALAPPDATA%\Pia\runs\<runId>`:

- Make `FilesToolHandler` **base-root** run-aware. Today `ResolveEffectiveRoot(baseRoot, workingSubpath)`
  (`FilesToolHandler.cs:185`) varies only the *subpath* under a fixed base (the interactive files
  folder). Add an **ambient base root** carried on `TaskContext` so a headless run resolves against its
  per-run directory. `HeadlessTurnExecutor.cs:143` already sets
  `TaskAmbient.Current = new TaskContext(_runId, WorkingSubpath: null, …)` per exchange — extend
  `TaskContext` with the run workspace root and populate it there (per exchange — the AsyncLocal does not
  flow back out of `BeginRunAsync`, §16 R9 reasoning).
- Still **reject escapes** (`..`, absolute paths, symlinks) against the *new* base — the containment
  checks must run against the run root, not the interactive root. This is a security boundary; fuzz it.
- **Cleanup / artifacts:** `runs\<runId>` accumulates. Add a retention policy (delete on run/chat delete
  via the FK-cascade hook, or an age sweep). Decide the artifact story — anything the run produced that
  the chat references must either outlive the workspace or be copied into the vault.

### 17.3 `HeadlessTurnExecutor` activation

The executor is built (`BeginRunAsync:66` / `ExecuteStepAsync:122` / `EndRunAsync:193`) and drives
`BackgroundAssistantTurnRunner.RunExchangeAsync` per step, accumulating messages and persisting the chat
once at end (title precedence unchanged). Activation work: give it a production caller (§17.1), point its
`TaskContext` at the run workspace (§17.2), and confirm the reads-always / **writes-if-granted** policy
end-to-end (the denial path returns `"Denied: … Do not retry."` — `BackgroundAssistantTurnRunner.cs:350`).

### 17.4 Tool consent for unattended writes (the core risk)

Interactive runs gate writes via the action-card approval; a headless run has **no one to approve**.
Decide the headless consent model:

- **Pre-granted scope:** the run declares up front which write scopes it may use (file write *within its
  workspace*, git, etc.); anything outside is denied inline (current behavior). Writes are confined to the
  run workspace (§17.2).
- **MCP stays denied headless** until the Phase-2 MCP gate fix lands (MCP is stdio-only and bypasses the
  gate, §9 — an unattended, unreviewed MCP call is unacceptable). Simplest this milestone: disable MCP
  tools for headless runs.
- Record every headless tool decision to the run/step timeline (privacy: tool args are SENSITIVE →
  `SensitiveDebug`).

### 17.5 Budgets, lifecycle, concurrency

- **Profile:** headless/scheduled runs use `RunProfile.Scheduled` (45 min; defined, currently unused).
  Surface scheduled budgets by mirroring the interactive knobs from the 1.2c "Agent runs" settings tab.
- **Cancellation:** a headless run owns its own CTS (no session). App shutdown mid-run must settle it
  cleanly (persist `Cancelled`/`Failed`; never a dangling `Running`).
- **Concurrency:** cap simultaneous headless runs (a small queue) so N scheduled jobs don't stampede the
  provider / disk.
- **Missed runs:** decide catch-up for scheduled agent jobs missed while the app was closed (reuse the
  existing `MissedRun` dialog pattern, or auto-run).

### 17.6 Red-team / things that bite

- **FK write-order (R1):** a headless run needs its `AssistantChats` parent row first — reuse the
  stub-chat-first pattern from 1.1 (failure paths must still persist a chat).
- **Base-root escape:** the workspace change is a security boundary; a bug lets an unattended agent write
  outside `runs\<runId>`. Fuzz containment on the *new* base.
- **Eviction:** confirm the 1.2 `Planned`-run eviction-skip covers headless run chats (retained as audit
  artifacts).
- **`TaskAmbient` flow:** set the workspace base root **per exchange** (`HeadlessTurnExecutor.cs:138`),
  not in `BeginRunAsync` — the AsyncLocal doesn't propagate back out (§16 R9).
- **Off-thread `RunChanged`:** the singleton raises off the UI thread for headless runs — the 1.4
  consumers already marshal (G3); a headless run with no open panel is fine (only the surface consumes it).
- **Unfocused/always-publish (R18):** a headless run is never foreground, so it always publishes — even
  while the user is on a *different* chat. That's correct (they didn't start it).

### 17.7 Tests

- Programmatic "run in background" → a headless `Planned` run completes off-thread, accumulates one chat,
  publishes exactly one durable Flow item; opening it opens the chat.
- Workspace isolation: a headless file write lands under `runs\<runId>`; an escape (`../`, absolute) is
  rejected against the run root.
- MCP denied headless; a non-granted write returns the inline denial.
- App-shutdown mid-run settles the run to a terminal state (no dangling `Running`).
- `RunProfile.Scheduled` (45 min) applied; scheduled budget setting flows through.
- Cleanup: deleting the run/chat removes its workspace.

### 17.8 Out of scope (this milestone)

- Full scheduler UI to create/edit/list agent jobs (Phase 4) — a minimal/programmatic trigger suffices.
- Sub-agents / multi-persona (Phase 3, separate).
- Verify/critic + budget pausing into `WaitingForInput` (Phase 2).
- The Phase-2 MCP gate fix (this milestone just *disables* MCP headless).

---

## 18. Parallel job execution — plan (post–Milestone B)

**Status:** planned, not built. Milestone B introduced the first real concurrency knob — the
`HeadlessRunLauncher`'s shared `SemaphoreSlim(2)` — but the **scheduler** still runs jobs strictly
serially: `ScheduledJobBackgroundService` holds a single `_runLock` across each job (research and agent
alike) and, for agent jobs, additionally `await`s the run's `Completion` before releasing it. So today at
most one *scheduled* job progresses at a time, even though the launcher pool could admit two. This section
is the design for lifting that to controlled parallelism.

### 18.1 Current state (as-built)
- **Headless launcher:** up to 2 concurrent runs (`_slots`), shared by the "Run in background" producer and
  the scheduler; a 3rd queues on the slot. Own linked CTS per run; shutdown cancels + bounded-awaits all.
- **Scheduler:** `ExecuteOnceAsync` iterates due jobs sequentially; `_runLock` serializes execution; agent
  jobs block on `handle.Completion` before releasing the lock. Net **scheduled** parallelism = 1.
- No per-provider / per-resource throttle beyond the global cap; no explicit fairness/ordering policy.

### 18.2 Goals
- Run N scheduled + detached jobs concurrently up to a **user-configurable** cap (default 2), with the
  launcher pool as the single source of truth for concurrency (no second, contradictory gate).
- Never stampede a provider or the disk; keep shutdown bounded and crash recovery intact (G-4).
- Fairness: a wedged job must not starve the queue (a per-job wall-clock already bounds it).

### 18.3 Design
1. **Single concurrency authority.** Drop the scheduler's serializing `_runLock` for agent jobs and stop
   blocking on `handle.Completion`; let the launcher semaphore be the only gate. Dispatch fire-and-track:
   the scheduler records the `runId` and reconciles job status from the terminal run state on completion
   (reuse today's `ExecuteAgentTaskAsync` success/failure tail, moved to a completion continuation).
2. **Configurable cap.** Promote the launcher's `SemaphoreSlim(2)` to a setting
   (`MaxParallelBackgroundRuns`, default 2, clamped 1..8) surfaced beside the scheduled-budget knobs.
3. **Per-provider throttle.** Optional per-provider concurrency limit (a keyed semaphore) so several jobs on
   the same provider don't exceed its rate limits; jobs on different providers run fully parallel.
4. **Fairness / ordering.** Admit due jobs oldest-`NextFireAt`-first; a job that can't get a slot waits its
   turn rather than being skipped. The per-run wall-clock bounds a wedged job so it releases its slot.
5. **Research jobs.** Either move `Research` (SingleTurn) behind the same pool, or keep its own small cap —
   decided when built; the SingleTurn path must first gain workspace handling consistent with §17.2.

### 18.4 Safety / lifecycle
- **Shutdown:** `StopAsync` already cancels + bounded-awaits the whole pool — unchanged.
- **Crash recovery:** `FailInterruptedRunsAsync` already settles any non-terminal run — unchanged.
- The global cap + per-provider throttle are what prevent the provider/disk stampede §17.5 warned about,
  so raising parallelism above 1 is only safe *with* the throttle in place.

### 18.5 Tests
- N due agent jobs with cap=2 → at most 2 concurrent (mirror `ConcurrencyCap_NeverExceedsTwoConcurrentRuns`).
- Per-provider throttle honored across jobs on the same provider; different providers run parallel.
- A wedged job (holds its slot to the wall-clock) does not block a different-provider job.
- Shutdown mid-parallel-batch settles every run terminal (no dangling `Running`).

### 18.6 Out of scope
- Cross-device distributed scheduling; priority classes; dynamic autoscaling.
