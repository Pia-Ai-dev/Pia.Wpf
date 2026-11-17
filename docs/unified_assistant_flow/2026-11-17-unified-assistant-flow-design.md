# One assistant flow instead of a Chat/Agent lever — design

**Status:** proposed — owner decisions O1–O5 taken (section 12), design awaiting approval ·
**Owner:** Marco Altmann ·
**Written:** 2026-11-17
**Origin:** user feedback on agent mode in the assistant view (six points, section 1), a review of the
agent-run code on `develop` at `bd454448`, and one day of real runs from a release log (section 2).
**Tracking:** [2026-11-17-unified-assistant-flow-checklist.md](2026-11-17-unified-assistant-flow-checklist.md)

File references are relative to `src/Pia.Wpf/` and were read at `bd454448`. Line numbers drift; check
them before relying on one.

## 1. Why

Users reported six problems with agent mode:

1. Large tasks take too long, use too many tokens and feel static.
2. The note box in the plan header is one-way: it is sent only with Continue and never answered.
3. A run stopped by the credit limit or a network failure ends Failed, and "continue" starts over.
   This one is tracked separately as GitHub issue #95.
4. After the last box is ticked there is a further wait before the run completes.
5. The choices at the end are not understood: the lever flips back to Chat, and switching it to Agent
   again raises a banner asking how much of the conversation to send.
6. There is no natural way to ask a running plan how far it is, or to change it.

A seventh came up while discussing them: a running plan is a black box. Users want to look into a
step while they wait.

The owner's direction: drop the Chat/Agent lever and merge both into one flow. Small requests are
answered directly, as chat does today. Larger work is proposed as a plan, approved, and then worked as
a background job that belongs to the chat, while the chat stays a normal conversation.

## 2. What real runs showed

One release log of 16 October 2026 holds four interactive runs with plans of six to nine steps. None
delivered its result. The log is one day on one machine and carries no token counts.

| Job | Plan | Length | End | Where the time went |
|---|---|---|---|---|
| A | 6 steps | 32 min | paused by the user, 1 step done | 13 min waiting unnoticed for a tool approval (`git_add`, then `git_commit`). Each approval re-ran the step, so step 1 ran three times. Step 2 hit the 24-round ceiling and also ran three times. |
| B | 9 steps | 5 min | Completed, nothing published | Step 1 failed twice because an MCP tool was refused without a question. Two re-plans shrank the plan to two steps. The commit to the run branch failed, and so did Publish later. |
| C | 7 steps | 6 min | Failed | Step 2 failed three times, each after a refused MCP call. Publish failed twice. |
| D | 6 steps | 15 min | Failed at the credit limit | Two steps hit the 24-round ceiling (4 and 7 min) and then paid a 34–41 s wrap-up call. 63 model rounds were lost. This is issue #95. |

Measured across these and three small runs:

- **After the last tick:** 7.5 s in job B (verify 5.3 s, title call 2.0 s, publish 0.2 s), about 4 s in
  the small runs.
- **Before every tick:** 3–13 s, 8 s on average over 11 steps. This is the round after the step has
  already called `emit_step_result`.
- **Plan turn:** 7–17 s. Each re-plan: 6–13 s.
- **After that day** the logs hold no interactive run, only the daily routine.

Two things the table needs explained:

- **The refused MCP tool was not a defect.** The user had allowed those tools "for this session" on a
  chat card. `ToolAutonomy.Resolve` ignores a session grant for `ToolClass.External` on the unattended
  surface (`Services/ToolAutonomy.cs:114-124`) and never parks to ask about one (`:173-183`), so the
  call is refused. The user found the Tool access page, set twelve standing grants by hand, and the
  next job ran them. Every approved plan runs on the unattended surface today, so this reaches every
  user with an MCP server.
- **The tool approvals did park correctly**, but nothing told the user, and each answer restarts the
  step from its first round.

Conclusion for this design: the waiting that users feel comes mostly from a job that cannot ask in
place and cannot be seen into, not from the plan and verify turns. The end wait (point 4) is the
smallest item.

## 3. How it works today

- **Lever.** A send with the lever on Agent and a tool-capable persona is `planned`
  (`ViewModels/AssistantViewModel.cs:1354-1355`). `ChatSessionManager.StartTurnAsync`
  (`ViewModels/Models/ChatSessionManager.cs:714`) runs a triage call (`:848-858`), then creates a run
  with the typed sentence as its goal (`:930-1063`). The planner sees the goal plus an optional
  conversation digest whose mode the user picks in a banner that blocks Send
  (`docs/agent_run_context/2026-09-12-agent-run-conversation-context-design.md`, D4).
- **Plan and approval.** `AgentPlanner` makes one `emit_plan` call on the `plan` model tier. A first
  plan of three or more steps parks for approval when the executor supports it
  (`Services/AgentRunOrchestrator.cs:268`). The park blocks Send (`ChatSessionManager.cs:723-733`), and
  the plan cannot be edited there: edits need state `Paused` (`Services/AgentRunService.cs:1095-1096`).
- **After approval the run is a background job already.** Approve is `HeadlessRunLauncher.ResumeAsync`,
  and every resume runs on `HeadlessTurnExecutor`, which has no streaming and no cards. Its steps see
  the whole chat, because `BeginRunAsync` seeds its model context from the chat's stored messages
  (`Services/HeadlessTurnExecutor.cs:322-378`).
- **Send is locked while the job runs** (`ForeignRunActive`, `AssistantViewModel.cs:1260-1265`). The
  reason is storage: the live session saves the chat as a full replace built from its in-memory list
  (`ChatSessionManager.cs:1346-1391`), which deletes step replies it has not loaded yet. The job's own
  save is already non-destructive (`IAssistantChatService.SaveMergedAsync`,
  `Services/AssistantChatService.cs:147-190`).
- **What the user sees.** A panel pinned above the transcript (`Views/AssistantView.xaml:216-218`),
  a one-line activity strip (`:414-459`), and one chat message per finished step. Streamed text is
  received by `BackgroundAssistantTurnRunner` and kept in a local buffer until the step ends
  (`Services/BackgroundAssistantTurnRunner.cs:380-413`); reasoning text is dropped.
- **Step detail.** The timeline stores tool name, decision, outcome and duration per call, never
  arguments or results (`Models/AgentTimelineEvent.cs:38-44`). Arguments and results are stored in
  `AgentToolExchanges` for resuming and deleted when the run settles
  (`HeadlessTurnExecutor.cs:1097-1098`).
- **End.** Verify turn, then a title call that is awaited and cannot be cancelled (`:1086`), the final
  chat save, the purge, promotion of the workspace, `Completed`. The lever then flips to Chat. A
  finished run's panel is not shown again once the chat is reopened
  (`ChatSessionManager.cs:646-676`).

## 4. Goals, non-goals, anti-goals

**Goals**

- One composer with no mode. The model decides between answering and proposing a plan.
- Every plan is shown, can be edited, and needs approval before work starts.
- While a job works, the user sees what it is doing, can open any step, and can keep typing. A typed
  message is answered at once and can change the job.
- A job that needs the user asks where the user is looking and keeps the step's work.
- When a job ends, the conversation simply continues. No lever, no banner.
- A finished job stays readable in the chat, with its steps and their detail.

**Non-goals**

- Routines, scheduled jobs, "Run in background" and sub-agents keep their engine and behaviour.
- Cost and speed of the steps themselves (the 24-round ceiling, the round after `emit_step_result`,
  parallel sub-agents without a roster, cache misses at job start). They are listed in the checklist
  under "not yet planned" and get their own design after this one.
- Showing a job on a second device. Run data does not sync today and will not after this change.

**Anti-goals** — the change has failed if any of these happens, even when everything else works:

- A second execution engine for interactive work. Approved plans keep running on the run engine, with
  its parks, budget, workspace, publish step and resume.
- A job that can no longer stop to ask (the reason the single-turn shortcut was reverted,
  `docs/agent_mode_latency/2026-09-18-agent-mode-latency-checklist.md:132-139`).
- A message or a step reply lost because two parties saved the same chat.
- Tool arguments, results or streamed text in a log line outside `SensitiveDebug`.

## 5. Approaches considered

| | A. One loop | B. Conversation on top of the run engine | C. Keep both, hide the lever |
|---|---|---|---|
| Idea | The chat turn becomes the agent: one thread with tools and its own checklist | The chat proposes the plan and stays a conversation; the run engine works the steps | Triage picks chat or plan for every message |
| Typing mid-run | answered between two tool rounds; work stops meanwhile | answered at once; work continues | queued as a note, no reply |
| Resume, budget, parks | lost for chat plans unless rebuilt | kept | kept |
| Files | written straight to the folder, one card per write | private workspace, published at the end | as today |
| Build | largest; two engines | large; ships in slices | smallest; point 6 half-solved |

**Chosen: B**, by the owner. It is the only one that answers at once and keeps what the run engine
already does.

Inside B one further choice mattered: where a job's step replies are stored.

- **In a hidden chat of the job's own**, as child runs and "Run in background" do. This removes the
  two-writer problem by construction. It was the first lean and was dropped after the check: the next
  conversation turn, a resumed job, a later job, history search and other devices all read a job's
  work from the messages of the user's chat, and stub chats are listed, synced, never evicted and not
  tied to a parent. Each of those would need new code.
- **In the user's chat, as today (chosen).** Nothing that reads step replies changes. The cost is that
  the chat's writers have to stop overwriting each other (section 8).

## 6. The flow

### 6.1 Sending

Every send is a conversation turn on the live session, as a Chat-mode send is today. There is no
`planned` flag, no triage call and no digest.

The turn gets one more tool, `propose_plan`, offered whenever the persona may use tools and the turn
has a tool list at all. It replaces `suggest_agent_mode` and its chip. `suggest_agent_mode` is offered
only on a provider rated `PlanningCapability.Capable` (`ChatSessionManager.cs:864-871`), but the lever
lets a user plan on any provider, with a warning (`AssistantViewModel.cs:2515-2527`). To keep that,
`propose_plan` is not tied to the rating; on a provider that is not rated Capable the plan block shows
the same warning.

- **Arguments:** `goal` (one or two sentences, self-contained) and `steps[]` with the fields of
  `AgentPlanner.PlanStepArg` (`Services/AgentPlanner.cs:157-171`): title, intent, expected artifact,
  and persona key and parallel group when a roster exists.
- **When to use it** (system prompt): answer directly, or do small tool work in the turn itself, when
  that finishes the request. Propose a plan when the work has several distinct parts, changes several
  files, or needs long research. A plan has at least two steps. The planner's existing rules for good
  steps (collapsing multi-file steps, naming the artifact) move into the tool description.
- **Handling:** intercepted before routing in `ChatSession.HandleToolCall`, the way `suggest_agent_mode`
  is (`ViewModels/Models/ChatSession.cs:1165-1181`). `ChatSession` has no run services, so the handler
  reaches them through the turn request or a delegate from the manager. It validates the steps with
  the planner's rules (`ValidatePlan` and `BuildSteps`, `AgentPlanner.cs:718-765`, need to become
  reusable), creates the job (6.2), and stops the tool loop, so the turn ends with the model's lead-in
  text and the plan below it. On a validation failure it returns the reason as the tool result and the
  model tries again or answers in text.
- **One job per chat.** While a chat has a job that is not finished, `propose_plan` returns an error
  telling the model to change that job (6.5) or wait.

Direct tool work in a conversation turn is unchanged: writes go to the user's folder behind an action
card each. A job writes into its private workspace and publishes at the end. Both models stay; the
plan is what moves work from the first to the second.

### 6.2 Creating the job

The recipe exists as a test seam
(`tests/Pia.Wpf.Tests/Services/AgentRunResumeNoRePlanPremiseTests.cs:263-282`) and becomes a product
path:

1. Save the chat, because the run row references it (`ChatSessionManager.cs:940`).
2. `IAgentRunService.CreateAsync`: shape `Planned`, trigger `User`, goal from the tool call, the
   interactive policy envelope, the persona of the turn. The envelope also records that this run is a
   *chat job* (6.8). The trigger cannot carry that: "Run in background" uses `User` too.
3. `ReplaceStepsAsync` with the validated steps.
4. Set the run's `FirstMessageId` to the assistant message that proposed the plan
   (`SetRunMessageRangeAsync`). The field exists, is written today and has no reader besides the
   orchestrator's own resume; it becomes the anchor that places the plan block (6.3).
5. `PauseAsync` with `AgentRunOrchestrator.PlanApprovalReason`.

No workspace is provisioned and no executor is created until approval. The planner is not called.
`AgentPlanner` stays for re-plans inside a job and for every run that does not start from a chat plan.

### 6.3 The plan block and approval

The plan is shown as a block under the assistant message that proposed it, in the transcript, not
pinned above it. It is the existing `RunProgressViewModel` in a new place. It must not be an entry of
`session.Messages`: anything there is saved, sent to the model and windowed with the transcript
(`docs/agent_run_visibility/2026-09-12-agent-run-visibility.md:112-119`).

```
Pia   I'd split this into four steps:

      ┌ Plan · waiting for your approval ───────────────────┐
      │ ☐ 1 Collect the operators                    ✎  ✕   │
      │ ☐ 2 Compare prices                           ✎  ✕   │
      │ ☐ 3 Write the summary                        ✎  ✕   │
      │ ☐ Check the result                                  │
      │ + Add step                    [Start]  [Discard]    │
      └──────────────────────────────────────────────────────┘
```

- **Every proposed plan needs approval.** The fixed threshold of three steps goes: what is small
  enough to skip approval is done in the turn and never becomes a plan (decision O2).
- **Editable at approval.** Edit, insert, move and skip work while the job waits for approval. Today
  they need `Paused`; the gate widens to the plan-approval park, where nothing is draining the plan.
- **Typing is allowed.** The send lock at this park goes. "Drop step 3" is a conversation turn that
  changes the waiting plan through the job tools (6.5).
- **Start** is the existing `ResumeAsync`; **Discard** the existing `RejectPlanAsync`.
- **Approval is a click.** No tool lets the model start a plan or answer a tool approval. A typed "go
  ahead" gets a reply that points at Start.
- **The check is a row.** "Check the result" is the last row of every plan that will be verified, so
  the wait after the last step has a visible owner.
- **Where the block sits.** After the message that proposed the plan. A run without one (a lever run,
  a routine's or a background run's chat) has its block before the first message the job wrote, or,
  while it has written none, after the last message older than the run.

A slim line above the composer stays visible while a job is active ("Step 2 of 4 · Compare prices ·
1:12") and scrolls to the block when clicked. The existing activity strip is that line.

### 6.4 The job at work

Approval starts the job on the existing headless path. What changes is what reaches the screen.

```
      │ ☑ 1 Collect the operators                    0:41   │
      │ ◐ 2 Compare prices                           1:12   │
      │     Reading the tariff page of the first operator…  │
      │     ▸ web_search   "tarif 2026"         2.1 s  ok   │
      │     ▾ web_fetch    example.org/…        3.4 s  ok   │
      │         Input    { "url": … }                       │
      │         Result   4,812 characters …                 │
      │ ☐ 3 Write the summary                               │
      │ ☐ Check the result                                  │
      │                              [Pause]  [Stop]        │
```

- **Live text.** `BackgroundAssistantTurnRunner.RunExchangeAsync` gets a callback for `TextDelta` and
  `ReasoningDelta` (reasoning has no case today). A new singleton keyed by run id, shaped like
  `ITimelineWatcher`, carries the text to `RunProgressViewModel`, which already marshals to the UI
  thread and coalesces. The text is shown in the running step's row. It is never stored by this path
  and never logged.
- **Step replies stay chat messages**, saved when the step ends, as today. On the device that ran the
  job they are shown inside their step's row and not as loose bubbles. Elsewhere they stay ordinary
  messages.
- **The job records which messages it wrote.** The step rows' `FirstMessageId` and `LastMessageId` are
  not enough: a re-plan keeps the finished steps and replaces every other row
  (`AgentRunOrchestrator.KeepDoneAsync`, `AgentRunService.ReplaceStepsAsync`), so the reply of a failed
  attempt stays in the chat with no step that owns it. Three of the four logged jobs re-planned. A
  message row cannot carry a marker, so the job keeps a local record of every message it writes: run,
  the step at that time, message id. Replies whose step was replaced are shown in the block under a
  collapsed "Earlier attempts" row.
- **Tool rows.** Each call is a row under its step: name, a short argument line, duration, outcome.
  The data is the timeline, which is live already.
- **Input and result of a call** open on demand from `AgentToolExchanges`. Retention is in section 9.
- **Pause** is today's pause: it cancels the running step. **Stop** cancels the job and is new: the
  composer's cancel reaches only a step on the live session, and no command cancels a headless run
  from the UI today. Both buttons are on the block.

The job's budget is the one every resume uses today (the `Scheduled*` values,
`Services/HeadlessRunLauncher.cs:714-718`).

### 6.5 Talking to a running job

The composer is never locked by a job. A message typed while a job works is a normal conversation
turn. It runs next to the job; the provider throttle allows four requests at once by default
(`Models/AppSettings.cs:377-417`).

- **What the turn knows.** A short job block is appended to the turn's last user message: state, the
  steps with their status, the running step and how long it has run, and what the job is waiting for
  if it waits. It is built per turn and not stored. Step replies of finished steps are in the context
  anyway, because they are chat messages.
- **What the turn can do** — tools offered only while the chat has an unfinished job:

  | Tool | Effect |
  |---|---|
  | `job_step_details` | returns one step's reply and its tool calls, for questions the job block does not answer |
  | `job_update_plan` | edits, inserts, removes or reorders steps that have not started |
  | `job_note` | a steering note for all remaining steps (the existing note text, `Services/RunContext.cs:103-136`, now kept for the rest of the job) |
  | `job_pause` | stop after the current step, or now |
  | `job_restart_step` | cancel the running step and run it again with a note |
  | `job_continue` | resume a job that is paused, parked at its budget, or stopped by an interruption (#95) |
  | `job_answer` | answer a question the job asked |
  | `job_stop` | cancel the job |

  The tools act without a card: the user asked for the change in their own words, the model says what
  it did, and the block marks every step it added or changed until that step starts. Tool approvals
  inside the job are unaffected by a changed plan.
- **Plan changes never race the job.** A change to steps that have not started must appear in the
  block at once and must not interleave with the loop that picks the next step. The recommended way:
  queue the change in the steering store and let the orchestrator apply it at the top of its loop,
  where it is the only actor (`AgentRunOrchestrator.cs:321-325`), with the block overlaying queued
  changes until then. That keeps the reason for the `Paused`-only rule
  (`Services/Interfaces/IAgentRunService.cs:409-415`) intact.
- **"Stop after this step"** is new. A pause request recorded without cancelling would be consumed
  before the finished step is recorded and would throw that step away (`:463-468`, `:515`), so it
  needs its own flag, checked at the top of the loop.
- **The job does not hear the conversation.** Its model context is fixed when it starts. What the user
  says reaches it only through these tools. After a park and resume the job re-reads the chat and then
  sees the side conversation too; that is accepted.
- **The note box goes.** It existed because the composer was locked.

### 6.6 When the job needs the user

This section changes who may authorise a tool call, so all of it is decision O3. It applies to chat
jobs only (6.8).

- **Session grants count**, MCP tools included. The user gave them in this session, and the job is
  their own, approved by them. This holds whether or not the chat is on screen. Two classes stay
  outside, as the gate has them today: a session grant for a screen tool or a knowledge-base tool does
  not count in a job. Those calls are asked on the block unless "always allow" is set.
- **A call that needs a decision is asked on the block**, on the step that made it, with its
  arguments. The choices are those of a chat card: allow once, allow for this session, always allow,
  deny. They mean what they mean on a card. This is not today's park answer, which grants or denies
  the tool for the whole run (`Services/HeadlessRunLauncher.cs:883-969`).
- **The step waits in place** when the job is *attended*: its chat is the one displayed in a window.
  That is checked for each call that needs a decision. After the answer the step carries on in the
  same round; nothing is re-run. While it waits, the job's clock is stopped, the line above the
  composer says so, and a Flow item is raised, because job A waited 13 minutes unseen. Switching to
  another chat during a wait does not end it.
- **Otherwise the job parks.** If the chat is not on screen when the call arrives, or nobody answers
  within ten minutes, the job parks with the question open, MCP calls included. Today an MCP call is
  refused instead (`Services/ToolAutonomy.cs:177-183`). The stored call is shown with its arguments,
  the same four choices apply, and the answer resumes the job, which re-runs that step and replays the
  call.

A question the job asks through `request_user_input` appears on the block with an answer field. Typing
the answer in the composer works as well: the conversation turn passes it on with `job_answer`.

Sub-agent child runs of a chat job keep today's rules: they cannot ask, and session grants do not
reach them.

### 6.7 The end

- "Check the result" runs as the last row. Its outcome, and what was published where, are shown on the
  block.
- The title call leaves the path to `Completed`. A chat that has a title keeps it. Otherwise the title
  is generated after completion, without anyone waiting for it.
- The job's last step reply is a chat message, as today, and is shown below the block as the result.
- Nothing flips and no banner appears. The next message is a conversation turn that sees the job's
  step replies in its context.
- A finished block stays in the transcript and is restored when the chat is reopened: the chat's runs
  are looked up including finished ones and placed by their anchor message. Today only unfinished runs
  are restored.

### 6.8 Which runs get what

A *chat job* is a planned run started from a plan in the user's chat: by `propose_plan`, or by the
lever for as long as it exists. The run's policy envelope records it at creation. Routines, scheduled
jobs and "Run in background" runs are not chat jobs, even while their chat is open.

| | Chat job | Any other planned run shown in a chat |
|---|---|---|
| Plan block in the transcript, tool rows, kept step detail | yes | yes |
| Live text and reasoning | yes, on the headless path | yes, on the headless path |
| Composer unlocked, job block, job tools | yes | no; Send stays locked as today |
| Session grants, asking in place (6.6) | yes | no; today's unattended rules |
| Starts without a background slot (O4) | yes | no |

Steps that still run on the live session (lever plans under three steps, until the lever goes) stream
into their own bubbles and ask with cards already. They get the block and nothing else new.

## 7. What goes away

- The Chat/Agent lever, its per-chat state and the "new chats start in Agent mode" setting.
- The triage call and the "Answered directly" chip on the interactive path.
- The conversation-context banner and the digest call for jobs started from a chat. The stored
  per-chat mode stays in the data model for sync compatibility and is no longer read here.
- `suggest_agent_mode` and the "Try this in Agent mode" chip.
- The send locks at the plan-approval park and during a running job.
- The note box on the run panel, and the panel's pinned place above the transcript.
- The interactive use of `LiveTurnExecutor` for plan steps. Whether the class can be deleted depends on
  what else uses it; the builder decides.

Regenerate removes the selected answer and everything after it, so it needs a rule for each state of
a job whose block lies at or below that answer:

- **Running:** blocked, as today. The job is still appending.
- **Waiting for Start:** the waiting plan is discarded first, the way Discard does it. Regenerating the
  proposal is how a user asks for a different plan.
- **Paused or parked after work was done:** blocked until the job is stopped. Otherwise a job with no
  transcript would remain and, with one job per chat, block every new plan.
- **Finished:** allowed. Its replies go with the transcript, and a job with no messages left in the
  chat is not shown.

Answers below a job's last message can be regenerated in every state but Running.

"Run in background" stays as a feature. Its button is visible only with the lever on Agent today
(`Views/AssistantView.xaml:1007-1016`). It moves into a menu on the Send button (decision O5).

### Rollout

- **Groups A to D of the checklist improve today's flow and ship as they land**, under the lever, for
  the runs section 6.8 names.
- **Groups E and F sit behind a hidden setting, off by default.** With it on, the lever is hidden,
  `propose_plan` replaces `suggest_agent_mode`, every plan needs approval and the composer is unlocked
  during a chat job. With it off, nothing of E and F is reachable. Both flows exist side by side only
  between E and G.
- **Group G turns the setting on for everyone and deletes the old path.** It needs gate G3 closed.

## 8. Two writers on one chat

The job writes step replies into the chat while the live session writes conversation turns. Today the
live save is a full replace and would delete the job's rows; `ForeignRunActive` prevents that by
locking Send. The lock goes, so the storage has to hold on its own.

Rules the chat store must guarantee:

1. No writer removes a message it did not explicitly delete.
2. No writer rewrites a message it did not author. The job currently re-asserts every message it
   loaded at its start on each save; a row the live session changed since would flip back.
3. The chat's header (title, provider, working folder, extra data) is written only by the party that
   changes one of those fields. The job writes a header captured at its start today.
4. Messages are ordered by timestamp, however many writers there were.

The recommended shape: a message-level upsert on `IAssistantChatService` (insert or update by id,
header untouched, search row rebuilt) used by the live session, the job and the one-message appends,
plus an explicit delete-by-ids for the two places that remove messages today, Regenerate
(`AssistantViewModel.cs:1674-1698`) and the dropped pair after a vision rejection
(`ChatSession.cs:509-510`). `SaveFromRemoteAsync` stays a full replace: it is the sync pull, which runs
once at start.

The live window pulls rows the job wrote only when it is not streaming (`ChatSessionManager.cs:1170`).
With rule 1 in place a missed pull no longer loses anything; it has to be repeated when the turn ends.

This reverses the choice of "one effective writer"
(`docs/superpowers/specs/agent-roadmap/10-durability-and-lifecycle.md:99-143`), which rejected an
incremental write because it needs deletion to be modelled. Two deletion sites exist today.

`AssistantChatConcurrencyTests` is the place for the tests: a live turn and a job saving the same chat
in every interleaving lose nothing, duplicate nothing and keep the order.

## 9. Keeping step detail

The owner decided: inputs and results of tool calls stay viewable as long as the chat is kept on this
PC, and the approval records that hold real file contents are still deleted when the job ends.

- **Storage.** The purge at settle and the finished-run clause of `PruneAsync` are narrowed to the
  parked and withheld rows (`Services/AgentToolExchangeStore.cs:410-458`,
  `HeadlessTurnExecutor.cs:1097-1098`). Call and result rows then age out on the chat-retention
  cutoff, the rule the timeline already follows
  (`Services/AssistantChatRetentionService.cs:119-138`), and go with the chat when it is deleted.
- **What that means for the user.** A chat that holds a planned run is never removed by retention, so
  it can outlive its detail. After the chat-history period (180 days unless changed) the tool rows and
  their inputs and results are gone, while the chat, the plan and the step replies stay. Tool rows and
  detail leave together, so no row is left that cannot be opened. Keeping both for the chat's whole
  life would mean changing the timeline's retention as well and was not chosen.
- **Names.** Call and result rows hold what the model saw: placeholders when name protection is on.
  The table that maps them back lives in memory for one dispatch and is never saved, so a stored
  placeholder cannot be translated later. To show real names, a readable copy has to be written when
  the call is recorded, while the map is alive. That stores real names past the run, which the rule
  for this table forbade until now. The owner allowed it (decision O1).
- **Limits the view has to show.** A stored string result is cut at about 4,000 characters, arguments
  over 1 MB are dropped, and recording stops after 500 rows or four million characters per run. Each
  needs a visible "not kept" state.
- **Durations.** No per-step duration is stored. It can be derived from the step's messages or its
  timeline rows, or stored; the builder decides.
- **Where.** `history.db`, encrypted at rest, device-local, never synced. The comments and tests that
  promise "purged with the run" change with the code.

The timeline's rule stays as it is: metadata only.

## 10. Prerequisites

- **Issue #95, resumable interruption.** Planned in
  `docs/agent_run_interruption/2026-11-17-resumable-interruption-plan.md` and being built separately.
  It owns the changes to `TryAnswerParkedRunAsync` and the new park reasons. This design builds on its
  parks: the block shows the reason, and `job_continue` resumes. Its rule "a message typed in Agent
  mode continues the run" is replaced here by `job_continue`, once the lever is gone.
- **Publish fails in a git folder (gate G3).** Both jobs that reached promotion logged "run-branch
  commit failed (1)" (`Services/RunWorkspaceService.cs:302-340`), and the work stayed in the run's
  workspace. Known: worktree mode, two and three changed files, `git add -A` succeeded, the commit
  exited 1. The commit already sets an identity, turns signing off and skips hooks (`:386-392`), so
  those causes are out. A release log holds only the exit code. The defect exists today with or
  without this change, so it does not hold back building groups A to F. It does hold back group G:
  the new flow must not become the default while finished work can stay unpublished.
- **The conversation model as planner (gate G2).** The largest unmeasured assumption here. It drops
  the triage that was validated live, the `plan` model tier, the folder listing the planner grounds on
  and its "cannot ground this goal" answer.
  - **Test:** the four prompts of the latency validation
    (`docs/agent_mode_latency/2026-09-18-agent-mode-latency-checklist.md:100-116`) plus four
    multi-step goals, on Pia Cloud with the default persona. A second provider is useful evidence,
    not part of the bar.
  - **Pass:** no small request gets a plan, every multi-step goal gets one that passes the planner's
    validation, and the owner, comparing each with the planner's plan for the same goal, finds it no
    worse.
  - **If it fails:** `propose_plan` carries only the goal. `AgentPlanner` then makes the steps on the
    `plan` tier, with the conversation as its verbatim digest, and the result is shown for approval
    the same way. The triage call and the banner still go; the plan turn stays.

## 11. Constraints

- **Privacy-first logging.** Streamed text, tool arguments and results are payloads. New log lines
  carry ids, counts and reason tokens only.
- **Server calls.** None is added or changed.
- **Localization.** New strings in `ViewStrings.resx`, `.de.resx` and `.fr.resx`.
- **UI automation.** Every new control gets an `AutomationId`, per-item inside the block, and a row in
  `ViewAutomationIdTests`. The playbook and the scripts that drive the lever change with it.
- **Help.** The desktop guide in Pia.Docs describes the lever and the banner; the release that ships
  this needs it updated before the help corpus is refreshed.
- **Zero warnings, test gate from the built exe**, as for every change.

## 12. Owner decisions

All five were taken on 2026-11-17.

| # | Question | Decided |
|---|---|---|
| O1 | May step detail keep real names past the run when name protection is on? | Yes. The chat text in the same encrypted database already holds them. |
| O2 | Does every proposed plan need approval by a click, with editing there, and no step threshold? | Yes. Small work is done in the turn and never becomes a plan. |
| O3 | May a chat job use session grants, MCP tools included, and ask on the block with the four card choices, waiting in place or parking (6.6)? | Yes. Routines and "Run in background" keep today's rules. |
| O4 | Does a job approved in a chat start at once, without waiting for one of the two background slots? | Yes. Routines keep queueing. |
| O5 | Where does "Run in background" go when the lever is gone? | Into a menu on the Send button, as "Run as a background task in a new chat". |

Not a decision, because nothing changes: another device shows a job's step replies as ordinary
messages and no plan block, as today.

## 13. Left to the builder

- The names and placement of the new services, and whether `propose_plan` and the job tools share one
  handler.
- How the block is hosted in the transcript, as long as it is not a message.
- How queued plan changes are stored and overlaid.
- How a waiting tool call is represented between the background runner and the block, and how the
  ten-minute fallback is timed.
- Whether per-step durations are derived or stored.
- The wording of the prompts and of every new string.
- The order of work inside each checklist step.

## 14. Found alongside, not part of this design

- A resumed run builds a new name map, while the tool rounds it carries over still hold the previous
  dispatch's placeholders (`HeadlessTurnExecutor.cs:315-320`, `:381-386`). A carried placeholder can
  then stand for a different value. Not verified beyond reading the code.
- `docs/agent_mode_latency/2026-09-18-agent-mode-latency-checklist.md:86-88` says the plan turn runs on
  the `fast` tier. The code uses `plan` (`Services/AgentTurnRouting.cs`).
- The headless save writes a chat header captured at the run's start, which resets the chat's extra
  data. It is harmless today only because the chat cannot change while a run works.
