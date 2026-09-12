# Conversation context for agent runs — checklist

**Status:** not started · **Owner:** Marco Altmann · **Written:** 2026-09-12
**Origin:** [2026-09-12-agent-run-conversation-context-design.md](2026-09-12-agent-run-conversation-context-design.md),
which root-causes a follow-up agent run that could not see the conversation it was sent from.

Tick each box in the commit that lands it.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Groups

| Group | Covers |
|---|---|
| A | The per-chat mode: type, column, DTO, session mirror |
| B | The blocking banner and the composer gate |
| C | Producing the digest inside the run |
| D | Feeding it to the plan and re-plan turns |
| E | The model-offered agent chip |
| F | Strings and verification |

## Decision gates

All eight owner decisions (D1–D8) are settled in the design document. No gate is open; nothing below
needs revisiting before it is built.

## Steps

- [ ] **A1 — The mode, end to end.** Add `AgentContextMode` (`Off` | `Verbatim` | `Summary`, nullable) as
  a PRAGMA-detected nullable column on the chat row, an additive field on `SyncAssistantChat`, and a
  mirrored property on `ChatSession`. `null` must survive a round trip distinct from `Off`.
  *Deps:* — · *Effort:* S · *Value:* Enabler

- [ ] **C1 — Carry it into the run.** Add `ConversationDigest` to `RunContext` beside `Clarifications`,
  and read the chat's mode in `AgentRunOrchestrator` immediately before `PlanAsync`. A run whose chat has
  no recorded mode takes `Off` — that is the rule that keeps routines and scheduled jobs from blocking.
  *Deps:* A1 · *Effort:* S · *Value:* Enabler

- [ ] **C2 — The verbatim renderer.** Map the chat rows to `ChatMessage`, drop the run's own goal row and
  its own clarification questions, compact through `AgentContextCompactor.CompactAsync` against
  `AgentContextBudget.From(provider)`, render the survivors to one text block. Character cap when the
  provider has no configured window.
  *Deps:* C1 · *Effort:* S · *Value:* High

- [ ] **D1 — Feed the planner.** Fold the digest into the user message of `BuildPlanMessages` and
  `BuildReplanMessages`, after the goal and before the grounding listing, in a delimited block. The
  request shape stays `[System, User]`; the digest never touches the system prompt.
  *Deps:* C2 · *Effort:* XS · *Value:* High

- [ ] **F1 — Planner and orchestrator tests.** Digest in the user message and not the system prompt ·
  absent at `null` and `Off` · present on a re-plan · a headless run with no recorded mode takes `Off`.
  *Deps:* D1 · *Effort:* S · *Value:* High

- [ ] **B1 — The banner.** A `Border` below the weak-provider banner with three buttons and the
  AutomationIds `Assistant_AgentContext_Summary` / `_Verbatim` / `_Off`, plus the `ViewAutomationIdTests`
  row in the same change. Writes the choice through the manager's existing `PersistAsync` path.
  *Deps:* A1 · *Effort:* S · *Value:* High

- [ ] **B2 — The gate.** `CanExecuteSendMessage` gains `&& !AgentContextChoicePending`; wire the notify
  sites so the buttons re-evaluate. `CanExecuteRunInBackground` inherits it. Switching the lever to Chat
  must free sending immediately.
  *Deps:* B1 · *Effort:* XS · *Value:* High

- [ ] **B3 — The trigger.** Evaluate `AgentModeEnabled && Messages.Count > 0 && AgentContextMode is null`
  on the lever toggle *and* on chat load, so an agent user who never toggles still sees the offer.
  *Deps:* B1 · *Effort:* XS · *Value:* High

- [ ] **B4 — The settled state.** After a choice, collapse the banner to one line naming what is active,
  with an affordance to change it.
  *Deps:* B3 · *Effort:* XS · *Value:* Med

- [ ] **B5 — View-model tests.** The trigger matrix (toggled vs. already on, empty vs. non-empty chat,
  choice recorded vs. not) and that a pending choice blocks send and run-in-background.
  *Deps:* B3 · *Effort:* S · *Value:* High

- [ ] **C3 — The summary turn.** One `GetChatResponseAsync` shaped like `ChatTitleService.GenerateAsync`
  on the fast model, transcript in the user message, output capped. Usage accrues run-level. A throw or
  empty text falls back to verbatim and logs — never fails the run.
  *Deps:* C2 · *Effort:* S · *Value:* High

- [ ] **E1 — The agent chip exception.** `SwitchToAgent` records `Summary` on the chat and starts without
  asking, so the banner does not appear behind it.
  *Deps:* C3, B3 · *Effort:* XS · *Value:* Med

- [ ] **F2 — Strings.** The banner body and three button labels in all three resx files (en/de/fr parity
  is enforced by test). Do not hand-edit `Designer.cs`.
  *Deps:* B1 · *Effort:* XS · *Value:* Enabler

- [ ] **F3 — Live verification.** One agent run on Pia Cloud in the conversation that produced the design
  document: verbatim once, summary once, and a confirmed block on an unanswered banner.
  *Deps:* C3, E1, F2 · *Effort:* XS · *Value:* High

## Suggested order

A1 → C1 → C2 → D1 → F1. That slice is decisive and needs no UI: at F1 the planner demonstrably sees the
conversation, and the whole design is either validated or dead before a pixel is drawn.

Then the UI slice B1 → B2 → B3 → B4 → B5, which is what makes the feature reachable at all.

Then C3 → E1, the paid path and its one exception, last because they cost tokens per run and should land
on a foundation that is already tested.

F2 alongside B1 (the banner cannot ship without strings), F3 at the end.

## Not yet planned

The four defects found during the same investigation, listed in the design document's closing section: the
lever's fall-back undone by the persona re-seed, the never-converging persona deletion in the sync loop,
orchestrator-posted messages carrying no token count or model, and Continue on a `needs-goal` park
re-planning with nothing new. None has a plan doc yet.
