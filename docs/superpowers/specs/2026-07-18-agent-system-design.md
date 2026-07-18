# The Agent System — Design Spec (North Star)

- **Date:** 2026-07-18
- **Status:** Brainstorming — exploratory. **Nothing is locked.** This is a north-star document to argue against, not an implementation plan.
- **Branch:** `feature/suggestions` (doc only)
- **Author:** Marco Altmann (with Claude Code)

---

## 1. Problem & goal

Pia today is a very capable **single-agent, single-turn** assistant. A user message (or a
scheduled job) triggers **one** turn with an implicit, bounded tool-calling loop
(`AiClientService.GetChatCompletionWithToolsAsync`, `maxToolRounds = 10`), driven by
**one** persona, and then it stops. Everything agent-like — memory, files, git, todos,
reminders, scheduled research, MCP — hangs off that one loop.

The gap between this and a "real agent system" (Claude Code, Codex, Hermes) is **not tools**
(we have a strong tool bus) and **not personas** (we have tool-scoped identities). It is the
**orchestration layer above the turn**: an agent that builds a plan, executes it across
multiple steps and possibly multiple specialist personas, verifies its own work, runs for a
long horizon while staying observable and steerable, and can be started by a trigger rather
than only by a keystroke.

**Goal:** promote the *turn* to a first-class, durable, observable, steerable **Agent Run**
— a goal + plan + workspace + one-or-more agent turns (possibly across personas) + a lifecycle
— unifying today's three separate execution paths under one runtime and adding the
plan → act → verify → replan loop that makes multi-step work feel agentic.

Non-goal: this is not "make Pia a coding IDE." The design is deliberately domain-neutral;
coding is one workload, "triage my inbox every morning" and "research X across these sources
and draft a brief" are equal-citizen workloads.

---

## 2. Where we are today (the honest baseline)

Three **separate** execution paths, each a single bounded turn:

| Path | Driver | Trigger | UI |
|------|--------|---------|-----|
| Interactive chat | `ChatSession.RunTurnAsync` + `ChatSessionManager` | user message | live, UI-thread-affine |
| Headless background turn | `BackgroundAssistantTurnRunner.RunAsync` | scheduler | none; result saved as a chat |
| Scheduled job | `ScheduledJobBackgroundService` (30s `PeriodicTimer`) | `RecurrenceCalculator` | Flow + toast |

Supporting substrate that the agent system will **reuse, not replace**:

- **Tool bus** — `PluginService.RouteToolCallAsync` over `IPluginToolHandler` handlers
  (memory, todo/kanban, reminder, scheduled-research, files, git, ingest) + remote MCP
  (`McpPluginToolHandler`, stdio + sse, server-pushed, CAB-delivered binaries).
- **Permission gate** — `ToolPermissionService` (deny-by-default, curated auto-approve
  allowlist, persisted per-(plugin,tool) grants) + `ActionCardBuilder` + `ToolDecision`.
- **Personas** — `Persona` / `PersonaService` / `PersonaToolScope` (system prompt + guardrails
  + output format + tool scope + preferred provider + reasoning effort). One per turn.
- **Flow** — `FlowService` durable cross-window attention inbox (`FlowItem`, `FlowSource`,
  typed `FlowAction` deep-links, dedup, auto-retract, SQLite persistence).
- **Memory** — markdown vault (`VaultStore`, `VaultIndexer`, `VaultWatcher`) + embeddings
  (`EmbeddingService`) + `ingest`; structured store in `SqliteContext`; server sync.
- **Resilience** — `RateLimitRetryHandler` (3 retries, honors `Retry-After`), per-provider
  timeouts, tool-capability fallback (`IsToolNotSupportedError`).

Known asymmetry to carry into the design: **MCP tools bypass the approval gate** —
`McpPluginToolHandler.HandleToolCallAsync` invokes inline with no `PendingAction`, so they
never reach `ToolPermissionService`/action cards. Tolerable for a watched chat; not for an
autonomous run.

**What does not exist today:** any persisted plan artifact, any sub-agent/delegation, any
self-verification, any durable/resumable run object, any event-based trigger, any mid-run
steering, and a web/shell/HTTP tool surface.

---

## 3. Concept: the Agent Run

The unifying abstraction. An **Agent Run** is a durable unit of work:

```
AgentRun
 ├─ Goal            (the user/trigger intent — sensitive text)
 ├─ Plan            (ordered AgentStep[]; adaptive — see §4)
 ├─ Workspace       (scoped artifact/scratchpad store — see §7)
 ├─ Turns[]         (each an AiClientService loop, possibly a different persona)
 ├─ Policy          (autonomy: budgets, approval mode, tool grants — see §8)
 ├─ Ledger          (tokens, cost, wall-clock, per step & total)
 ├─ Transcript      (the run timeline — see §11)
 ├─ Trigger         (User | Schedule | Event — see §10)
 ├─ OwnerDeviceId   (only owner fires/advances; synced read-only elsewhere)
 └─ State           (see machine below)
```

**State machine** (superset of today's `ChatState`):

```
Planning → Running → Verifying → Completed
              ↕                     │
       WaitingForInput          (Verify fail)
              ↕                     ↓
           Paused ─────────────► Running (replan)
   any → Failed | Cancelled
```

The three current paths become **three views of one thing**:

- **Interactive chat** = an Agent Run with a live UI attached (`ChatSession` renders it).
- **Background turn** = a detached Agent Run with no UI, surfaced via Flow.
- **Scheduled job** = an Agent Run whose Trigger is a `Schedule`.

This is the single most important idea in the document. Everything below is a facet of the
Agent Run.

---

## 4. The plan → act → verify → replan loop

Today: `for (round = 0; round < 10; round++)` with no explicit plan and no exit criterion
beyond "the model stopped calling tools." The agent system replaces the *hard round cap* with
a *plan-bounded, adaptive* loop:

1. **Plan** — the orchestrator persona decomposes the goal into an explicit
   `AgentPlan` of `AgentStep`s (title, intent, assigned persona, expected artifact).
   The plan is **data**, persisted on the run, and **rendered live** (Flow + run view).
2. **Act** — execute the current step as one `AiClientService` turn (tool loop unchanged
   internally, but now scoped to a step, not the whole goal).
3. **Verify** — a verification pass checks the step/goal actually succeeded (§6). Failure
   does not end the run; it feeds (4).
4. **Replan** — the orchestrator revises remaining steps given what actually happened
   (a step failed, a tool returned surprising data, the user steered — §9).

`AgentStep` is **distinct from `TodoItem`.** Todos/Kanban are the *user's* board that the
agent CRUDs via tools; `AgentStep` is the *agent's own* execution state. Conflating them
would pollute the user's board with agent bookkeeping. (Open question §13: do we ever surface
plan steps *as* todos when the user asks the agent to "make this a project"?)

This directly answers "multi-step tasks" — the plan **is** the multi-step artifact, visible
and (stretch) editable by the user.

---

## 5. Orchestrator + specialist sub-agents (Council, realized for work)

Today one turn resolves exactly one persona. The design adds **delegation**: an orchestrator
persona that dispatches sub-goals to **specialist personas**, each a real sub-run.

- **`ISubAgentRunner`** — built on top of `BackgroundAssistantTurnRunner` (which already runs
  isolated, headless, tool-capable turns off the UI thread). A sub-agent = a scoped Agent Run
  nested under the parent, with its own persona, its own `PersonaToolScope`, and its own slice
  of the workspace.
- **Parallel fan-out** — independent sub-goals run concurrently (the headless runner is
  already thread-safe and DI-scoped per run); a **synthesizer** persona merges results.
- **Isolation** — each sub-agent sees only its slice of the workspace + a narrowed tool set,
  so a "researcher" persona cannot write files and a "writer" persona cannot run git.

This is the **Council** concept your persona schema already reserves fields for
(`Archetype`, `AccentColor`, and the "Council cards" doc-comments in `Persona.cs` /
`BuiltInPersona.cs`; see `docs/personas/TARGET/`). Council today is scoped as a *chat* feature
(multiple voices answering one question). The agent system generalizes it to *work*: multiple
specialists **doing** parts of one task, not just **opining** on one prompt.

Attribution reuse: sub-agent output in the transcript is attributed with the persona's
emoji/accent (the same `PiaPersonaAvatar` / `PersonaGlyph` surface built for persona
attribution in chat).

Open question (§13): orchestration topology — static (orchestrator plans the whole team up
front) vs. dynamic (orchestrator spawns sub-agents on demand as steps reveal the need). Start
static; design the run tree to allow dynamic.

---

## 6. Verification / critic pass

The loop never currently asks "did I succeed?" — it stops when the model emits no more tool
calls. The agent system adds a **verify gate** before `Completed`:

- **Deterministic verifiers** where available: run build/tests for code workloads, re-read a
  written file to confirm the edit, re-query an entity to confirm a mutation landed.
- **Critic persona** for open-ended workloads: a specialist persona reviews the artifact
  against the goal and either passes it or emits concrete deficiencies.
- **Failure → replan** (§4), bounded by the run's step/cost budget (§8), not an infinite loop.

This is what separates "looks done" from "is done" and is cheap relative to its reliability
payoff. It also gives autonomous/scheduled runs a self-check they currently lack entirely.

---

## 7. Shared workspace / blackboard

Parallel sub-agents and a multi-step plan need somewhere to share intermediate results.

- **Artifacts** — reuse the existing sandboxed assistant folder (`FilesToolHandler` containment,
  `.piaignore`, diff previews, `FileStalenessStore`). A run gets a workspace scope; sub-agents
  get sub-scopes.
- **Structured scratchpad** — a small per-run key/value + note store (the "blackboard") for
  facts, decisions, and hand-offs that shouldn't be files. Persisted on the run so it survives
  restart and is visible in the timeline.
- **Memory promotion** — durable learnings graduate from the run scratchpad into the memory
  vault via the existing `remember` path, so knowledge outlives the run.

Open question (§13): is the workspace always the user's assistant folder, or do runs get
ephemeral per-run workspaces (cf. Claude Code worktrees) that merge on success?

---

## 8. Autonomy policy + uniform tool gating

A long-running or unattended agent needs a governance model richer than "show a card and
block." Introduce a per-run **autonomy policy**:

- **Approval mode** — `Interactive` (cards, today's behavior) | `AskOnRisk` (auto-approve
  safe/granted tools, card only for risky ones) | `Autonomous` (no cards; standing grants +
  budgets only; today's headless/voice path generalized).
- **Budgets** — hard caps on tokens, cost, steps, and wall-clock. Exceeding a budget pauses
  the run into `WaitingForInput` (surfaced via Flow) rather than silently continuing.
- **Standing grants** — reuse `ToolPermissionService.IsGranted` per-(plugin,tool), plus the
  per-run write-tool allowlist already on `ScheduledJob.GrantedTools` /
  `BackgroundTurnRequest.GrantedWriteTools`.

**Close the MCP gate bypass (prerequisite for autonomy).** Route `McpPluginToolHandler`
tool calls through the same `PendingAction` → `ToolPermissionService` gate as built-ins, so
every mutating tool — built-in *or* external process — obeys the run's approval mode and
grants. Server-pushed + cert-verified is not a substitute for user consent on writes.

Privacy note (per `CLAUDE.md`): run **Goal**, **Plan** step text, **workspace** contents, and
**scratchpad** notes are user content — log via `SensitiveDebug`, and treat as E2EE-eligible
where synced (mirror the persona-text E2EE split).

---

## 9. Live steering

Today the only in-flight human inputs are: approve/decline a tool card, or `Cancel()` the whole
turn. There is no way to nudge without killing the run. Add a **steering channel**:

- **Nudge** — inject a message into an in-flight run ("focus on X", "skip the tests") that the
  orchestrator folds into the next replan (§4) rather than interrupting the current step.
- **Answer** — a run in `WaitingForInput` (a genuine question, a budget pause, a risky
  approval) resolves when the user answers, via Flow deep-link.
- **Pause/Resume** — first-class, distinct from cancel; the durable run makes this possible.

This turns long runs from fire-and-pray into a collaboration.

---

## 10. Triggers: schedule + event

Today triggers are time-only (`RecurrenceCalculator`) and hardwired to a single workload
(`ScheduledJobKind { Research }`). Generalize:

- **Generalize the job kind** — `ScheduledJobKind { Research }` → arbitrary agent goals. The
  whole `ScheduledJobBackgroundService` / owner-device / grace-period / missed-run machinery
  already generalizes; only the payload is hardwired.
- **Event triggers** — start a run in response to an event, not just a clock. The event
  sources already exist and emit: `VaultWatcher` (a file landed / changed), `ITodoService`
  (a todo went overdue), `WindowTrackingService` (app/window events). Wire an event → run
  dispatcher next to the existing timer poller. This is the largest single lever for
  *autonomous* feel ("when a PDF lands in this folder, summarize it into memory").
- **Unify** — one `Trigger` abstraction on the run: `User | Schedule(recurrence) | Event(source, predicate)`.

---

## 11. Observability: the run timeline

A durable multi-agent run is only trustworthy if the user can see what it did. Add a
**run timeline** — a consolidated, reviewable trace:

- Ordered events: plan created/revised, step started/finished, tool call + decision + result,
  sub-agent spawned/returned, verify pass/fail, steering messages, budget events.
- Per-step and total **token/cost ledger**.
- Reachable live (watch it work) and after the fact (audit), via a Flow deep-link
  (`FlowAction.OpenRun(runId)`, a new sibling of `OpenChat`).

Much of the raw material exists (action cards are an audit trace of tool decisions; chats are
transcripts) — this consolidates it into one run-scoped view.

---

## 12. Context management / compaction

The `maxToolRounds = 10` ceiling is partly a symptom of having no way to compress a growing
transcript. For long horizons:

- **Tool-result summarization** — large tool outputs (file dumps, search results) get
  summarized/elided when the context grows, keeping references retrievable.
- **History compaction** — periodically summarize prior steps into a running brief; offload
  detail to the memory vault + embeddings (already present) for on-demand recall.
- Only with compaction does "run for 40 steps" become reachable without blowing context.

---

## 13. How it maps onto existing components

| Concern | Existing component | Change |
|---------|--------------------|--------|
| Turn execution | `AiClientService.GetChatCompletionWithToolsAsync` | reuse per-step; drop the hard round cap in favor of plan+budget |
| Foreground driver | `ChatSession` / `ChatSessionManager` | becomes the live view of an Agent Run |
| Headless driver | `BackgroundAssistantTurnRunner` | becomes the `ISubAgentRunner` substrate |
| Scheduler | `ScheduledJobBackgroundService` / `RecurrenceCalculator` | generalize `ScheduledJobKind`; add event dispatcher |
| Tool bus | `PluginService.RouteToolCallAsync` | unchanged; add web/shell/HTTP handlers |
| Permission gate | `ToolPermissionService` / `ActionCardBuilder` | add approval modes + budgets; **route MCP through it** |
| Personas | `Persona` / `PersonaService` / `PersonaToolScope` | orchestrator + specialists; realize Council for work |
| Surface | `FlowService` / `FlowAction` | add `OpenRun`; runs publish lifecycle to Flow |
| Memory | vault + `EmbeddingService` + `ingest` | scratchpad promotion + compaction offload |
| Attribution | `PiaPersonaAvatar` / `PersonaGlyph` | attribute sub-agent output in the timeline |
| **New** | `AgentRun`, `AgentPlan`/`AgentStep`, `ISubAgentRunner`, run timeline, autonomy policy, event trigger, steering channel | net-new |

---

## 14. Phased roadmap (strawman — for argument)

Ordered by "most changes the *character* of the system per unit of effort," reusing the most.

1. **Phase 1 — The spine.** `AgentRun` + `AgentPlan`/`AgentStep` + plan→act→replan loop +
   live plan rendering in Flow/run view. Refactor `ChatSession` and `BackgroundAssistantTurnRunner`
   to run *on top of* an Agent Run. No sub-agents yet. **This alone makes multi-step work feel agentic.**
2. **Phase 2 — Trust.** Verify/critic pass (§6) + autonomy policy & budgets (§8) +
   **fix the MCP gate bypass** + run timeline/observability (§11). Prerequisite for anything unattended.
3. **Phase 3 — The team.** `ISubAgentRunner` + orchestrator/specialist delegation + shared
   workspace/blackboard (§5, §7). Council realized for work.
4. **Phase 4 — Alive.** Event triggers + generalized job kinds (§10) + live steering (§9) +
   context compaction (§12).
5. **Phase 5 — Reach.** Broader tool surface: web search/fetch/browse, sandboxed shell/command,
   HTTP/API, calendar/email — all under the unified gate.

Phases 1–2 are the ones that convert "chat with tools" into "an agent system"; 3–5 are depth.

---

## 15. Open questions (the point of this doc)

1. **Run vs. chat identity.** Is every chat now an Agent Run, or only chats that cross a
   complexity threshold? (Cheap "what's 2+2" shouldn't spin up a plan.) Do we detect
   "this needs a plan" automatically, or is it a mode the user opts into?
2. **Plan visibility & editability.** Read-only live plan, or can the user edit/reorder/veto
   steps before/while it runs? How much ceremony before a run starts?
3. **AgentStep vs. TodoItem.** Kept strictly separate, or can a plan be "promoted" to the
   user's Kanban when they say "make this a project"?
4. **Orchestration topology.** Static up-front team vs. dynamic on-demand spawning (§5).
5. **Workspace model.** Always the shared assistant folder, or ephemeral per-run workspaces
   that merge on success (§7)?
6. **Autonomy defaults.** What's the default approval mode for user-started vs. scheduled vs.
   event-triggered runs? What are sane default budgets?
7. **Cost/latency.** Sub-agents + verify + replan multiply token spend. Where's the ceiling
   for a desktop app on user-supplied provider keys? Does the local/Ollama path change the calculus?
8. **Failure UX.** When a long autonomous run fails at step 7 of 12, what does the user see,
   and what can they resume vs. must redo?
9. **Provider capability floor.** Plan/verify/orchestrate lean on strong tool-use + reasoning.
   How does this degrade on weaker local models (`OllamaProviderHandler`, `VLlmProviderHandler`)?
   Is the agent system gated to capable providers, or does it degrade gracefully to the single-turn path?
10. **Scope of "team."** Is multi-persona delegation a headline feature or an internal
    implementation detail the user never names? (Affects how much UI Council needs.)

---

## 16. What this explicitly is *not* proposing

- Not replacing the tool bus, permission model, persona schema, Flow, or memory vault — all reused.
- Not an OS-level sandbox (isolation stays logical: path containment + tool scoping + gate).
- Not a coding-only agent — coding is one workload among general multi-step tasks.
- Not a locked plan — every decision in §14/§15 is open for the brainstorm this doc exists to seed.
