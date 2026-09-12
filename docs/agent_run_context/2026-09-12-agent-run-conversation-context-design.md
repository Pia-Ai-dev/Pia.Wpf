# Agent runs started inside a conversation — design

**Status:** designed, not implemented · **Owner:** Marco Altmann · **Written:** 2026-09-12
**Origin:** a reported dead end — an agent run finished, the user typed a follow-up in the same chat,
and the new run had no idea what the conversation had been about. Root-caused in this document.

## The problem

A send with the composer lever on **Agent** does not start a chat turn. It creates a new run whose goal
is exactly the one sentence that was typed (`ChatSessionManager.cs:926`, `Goal: userText`). Nothing else
travels with it.

The plan turn that receives that goal is built as exactly `[System, User]`
(`AgentPlanner.BuildPlanMessages`, `AgentPlanner.cs:852`). The user message holds the goal, an optional
self-analysis, and a listing of the working folder. The chat transcript is not in it.

So a follow-up such as "die Datei liegt nicht im Arbeitsverzeichnis" reaches the planner as those eight
words plus a directory listing that does not contain the file. The planner cannot tell which file is
meant, correctly answers `cannotGround`, and the run parks `needs-goal` having done nothing.

## What already works

The **steps** of a run are not blind. They get the whole transcript, compacted to the provider's budget:

- live: `ChatSession.BuildStepChatMessagesAsync` (`ChatSession.cs:1001`) walks `Messages` and compacts
  through `AgentContextCompactor.CompactAsync`.
- headless: `HeadlessTurnExecutor` seeds `_messages` from the persisted chat rows
  (`HeadlessTurnExecutor.cs:326-339`).

Blind are only the **spine turns**, which run outside a session:

| Turn | What it sees today | Where |
|---|---|---|
| Reasoning | goal | `AgentPlanner.cs:806` |
| Plan | goal + analysis + working-folder listing | `AgentPlanner.cs:852` |
| Re-plan | goal + completed steps | `AgentPlanner.cs:903` |
| Verify | goal + executed steps | `AgentVerifier.cs:130` |

The reasoning turn is not a free summarisation slot: `ShouldReasonFirstAsync` (`AgentPlanner.cs:632`)
runs it only for providers that drop reasoning effort while tools are attached, which excludes Pia Cloud.
It is not repurposed here.

## Owner decisions

| # | Question | Answer |
|---|---|---|
| D1 | What is the context for? | Full continuity — which files exist, what was decided, what was already tried. Not just resolving "the file". |
| D2 | How is it produced? | Offered, not decided silently. Two modes: a deterministic verbatim excerpt, or an LLM summary. Scope is strictly this chat. |
| D3 | Where is the offer? | A banner above the composer, in the shape of the weak-provider banner (`AssistantView.xaml:426`). |
| D4 | What happens if it is ignored? | Nothing — the banner **blocks** sending. `Off` is a deliberate click, and the default only for paths with no human at the composer. |
| D5 | How long does a choice hold? | Per chat, persisted and synced. |
| D6 | Which turns get it? | Plan and re-plan. Verify stays on goal + executed steps so it checks artifacts, not conversation. |
| D7 | When does the banner appear? | Whenever agent mode is on, the chat is non-empty and no choice is recorded — on lever toggle **and** on chat load. |
| D8 | What about the model-offered agent chip? | Documented exception: it starts immediately with `Summary`. |

D7 exists because agent mode is frequently already on without a toggle: every user-initiated switch to
Agent persists `AssistantAgentModeDefault` (`AssistantViewModel.cs:922`), and `SeedAgentModeFromSettings`
re-applies it on load. A toggle-only trigger would never fire for a regular agent user.

D8's rationale: the chip is offered *because of* the conversation, so the conversation is its context by
construction. It is the one place where a paid summarisation turn happens unasked; the chip records
`Summary` on the chat so the banner does not then appear behind it.

## Design

### Data model

`AgentContextMode` — `Off` | `Verbatim` | `Summary`, nullable.

`null` is not `Off`. `null` means "never asked" and shows the banner; `Off` means "asked, declined" and
hides it. Without the distinction there is no way to choose "no context" and stay unasked.

Stored on the chat row as a nullable column, PRAGMA-detected exactly like `WorkingDirectory`
(`AssistantChatService.cs:215`), plus an additive field on `SyncAssistantChat` (`SyncAssistantChat.cs:31`
is the precedent). No schema version bump. Mirrored onto `ChatSession` so the view model can bind it.

### Trigger and banner

Visible when `AgentModeEnabled && Messages.Count > 0 && AgentContextMode is null`, evaluated on the lever
toggle and on chat load. Because the choice is per chat, the banner appears once per conversation.

Its own `Border` below the weak-provider banner — stacked, not merged; both can hold at once. Three
buttons with AutomationIds `Assistant_AgentContext_Summary`, `_Verbatim`, `_Off`, plus the matching
`ViewAutomationIdTests` row. After a choice the banner collapses to a one-line statement of what is
active, with a link to change it, so the state stays visible instead of acting invisibly.

The choice is written through the manager's existing `PersistAsync` path **before** any run is created.

### Blocking

`CanExecuteSendMessage` (`AssistantViewModel.cs:1165`) gains `&& !AgentContextChoicePending`, alongside
the `PlanApprovalParkActive` gate that already blocks sending. `CanExecuteRunInBackground`
(`AssistantViewModel.cs:1224`) builds on it and inherits the gate, so both buttons and the Enter key are
covered by one condition.

The pending flag includes `AgentModeEnabled`, so switching the lever back to **Chat** clears it and frees
sending immediately. That is why the banner needs no "stay in chat" button — the lever is one.

### Producing the digest

In `AgentRunOrchestrator`, immediately before the `PlanAsync` call (`AgentRunOrchestrator.cs:230`). That
single site covers a live run, a headless dispatch and a `needs-goal` resume, because all three reach it —
no new plumbing through `HeadlessRunLauncher`.

Read the chat with `_chats.GetAsync(run.ChatId)`, read the mode off the row, then:

- **Verbatim** — map the rows to `ChatMessage`, drop the run's own goal row and its own clarification
  questions, compact through `AgentContextCompactor.CompactAsync` against
  `AgentContextBudget.From(provider)`, render the survivors into one text block. No extra provider round.
- **Summary** — one `GetChatResponseAsync` shaped like `ChatTitleService.GenerateAsync`
  (`ChatTitleService.cs:32`): `tools: null`, `mode: AgentTurnRouting.Mode`, `personaModelType:
  AgentTurnRouting.ModelType` (`"fast"`), transcript in the user message. Output capped like
  `MaxAnalysisChars`. Usage accrues run-level via `SafeAddUsage(run.Id, …, stepId: null)`, the same call
  the plan turn's own usage uses (`AgentRunOrchestrator.cs:234`), so the round is never paid invisibly.

The result lands on `RunContext` as `ConversationDigest`, next to `Clarifications`
(`RunContext.cs:206`) — same construction, same lifetime, same fold-into-the-goal pattern.

### Consuming it

`BuildPlanMessages` and `BuildReplanMessages` fold the digest into the **user** message, after the goal
and before the grounding listing, in a delimited block like the analysis. The request shape stays
`[System, User]`.

User content must ride on the user message: `TokenizingAiClientService.TokenizeMessages` rewrites only
`ChatRole.User` text to PII placeholders, so a digest in the system prompt would ship a transcript past
the tokenizer verbatim (`AgentPlanner.cs:880` states the same rule for the analysis).

Verify and the reasoning turn are untouched.

### Paths with no human at the composer

Routines, scheduled jobs and background assignments never block. A run whose chat carries no recorded
mode takes `Off`. This is a rule in the orchestrator, not a setting.

`SwitchToAgent` (`AssistantViewModel.cs:2320`) is the documented exception: it records `Summary` on the
chat and starts, per D8.

### Failure handling

A summary turn that throws or returns empty text **falls back to Verbatim** and logs it; it never fails
the run. A missing chat row yields no digest and no error. A provider with no configured context window
(`AgentContextBudget.From` returns null) falls back to a character cap instead of compaction.

### Privacy

The digest is user content: user role only, never the system prompt. Logs carry lengths and counts only;
the text goes through `SensitiveDebug`.

## Tests

- Planner: the digest lands in the user message and not the system prompt · absent when the mode is
  `null` or `Off` · present on a re-plan.
- Orchestrator: a failing summary turn falls back to verbatim and still accrues the usage · a headless
  run with no recorded mode takes `Off` and does not block.
- View model: the trigger matrix — toggled vs. already on, empty vs. non-empty chat, choice recorded vs.
  not — and that a pending choice blocks both send and run-in-background while switching to Chat frees
  them.
- One `ViewAutomationIdTests` row for the three buttons.
- One live run on Pia Cloud in the conversation that produced this document.

## Out of scope

No vault or cross-chat context. No global setting. No per-message selection. Verify stays blind.

## Found alongside, not part of this design

The same investigation turned up four unrelated defects, recorded here so they are not lost:

1. **The lever's fall-back to Chat is undone every few minutes.** `OnRunProgressSettled`
   (`AssistantViewModel.cs:633`) flips the lever to Chat when a run settles, deliberately without
   persisting. But `PersonasChanged` → `LoadPersonasAsync` → `SeedAgentModeFromSettings`
   (`AssistantViewModel.cs:800, 838`) re-reads the still-true setting, and the sync pull loop raises
   `PersonasChanged` on every cycle.
2. **The same persona deletion is re-applied on every sync cycle** and never converges — visible in
   `pia-2026-09-12.log` at 12:05, 12:11, 12:15. It is what makes defect 1 fire so often.
3. **Orchestrator-posted messages carry no token count and no model.**
   `SafePostClarificationQuestionAsync` (`AgentRunOrchestrator.cs:1701`) and the live mirror
   (`LiveTurnExecutor.cs:207`) write no `Tokens`/`ModelName`/`ProviderName`, so `AnswerStats` stays null
   and the bubble shows neither the cost nor who answered. The tokens are on the run ledger, just not on
   the message. The "plan rejected" notice (`AgentRunOrchestrator.cs:689`) is app text with no model
   behind it and must not be stamped — it wants a notice style instead.
4. **Continue on a `needs-goal` park re-plans with nothing new.** Measured: a resume at 12:28:33 with
   `0 recorded clarification answer(s)` re-ran the identical plan turn and declined identically. Once
   this design lands the second attempt at least carries a digest, but the button is still misleading
   without an answer.

Also visible in the log: a plan turn spends a second provider round after `emit_plan` whose text is
discarded ("Round 2: no tool calls, completing", 249 chars).
