# What an approval park does to an agent run — four defects, one spine

**Status:** executable · **Owner:** Marco Altmann · **Written:** 2026-08-31
**Origin:** `artifacts/Agent_Run/agent_run_issues.md` — four problems observed in run
`9c942a8e-ec2e-49d2-ba9d-079404f2bbf8` (goal: read `20260831_Fehlzeitenübersicht.xlsx`, write a
per-employee holiday summary into the vault), with the screenshots and `pia-2026-08-31.log` beside it.

Sequel to [`docs/agent_run_e2e/2026-08-27-cross-step-tool-context.md`](../agent_run_e2e/2026-08-27-cross-step-tool-context.md),
which fixed the *in-process* step boundary. This one is about the boundary that fix does not cover:
the **park/resume** boundary, where a fresh executor rebuilds its context from persistence.

---

## The observed run

One run, three planned steps, two tool-approval parks and one clarification park.

| Time | Event |
|---|---|
| 14:06:28 | Run created, `Planned` shape, Copy workspace seeded with 1 file (the xlsx) |
| 14:06:33 | Plan parked for approval (3 steps) |
| 14:06:51 | Resumed → step 0 runs `list_files`, `read_file`, `emit_step_result` |
| 14:07:04 | Step 0 Done, step 1 starts, two more `read_file` calls succeed |
| **14:07:16** | **`write_file` PARKED for human approval** (`.scratch/fehlzeiten_raw.txt`, the raw extract) |
| 14:07:45 | Round 4: another `write_file` — rejected pre-gate, no gate row |
| 14:07:55 | Round 5: `create_source` — **withheld** ("already parked on write_file") |
| **14:07:57** | **Run → `WaitingForInput`; the approval dialog finally appears** |
| 14:08:12 | Granted `write_file` **by name**; step 1 restarts from Round 1 |
| 14:08:18 | `search_files path not found under sandbox` — the parked file was never written |
| 14:08:23 | `request_user_input`: *"Die Datei liegt im Binärformat vor und lässt sich nicht auslesen"* |
| 14:09:16 | User: *"Du hast die Daten in Schritt 1 doch schon gelesen. Also arbeite damit"* |
| 14:09:47 | `remember` parked; 14:09:55 `write_file` withheld; 14:09:59 → `WaitingForInput` |
| 14:10:21 – 14:11:09 | Four `write_file` executions, `GrantedByName` |
| 14:11:19 | Promoted **2 files**, run `Completed` |

---

## The spine: a park keeps the tool NAME and throws the call away

`ToolApprovalStore` records one string — the tool name. `ToolApprovalArguments.Describe` builds a
**display** string beside it, capped at 120 chars per value and 400 chars total with an ellipsis
(`src/Pia.Wpf/Services/ToolApprovalArguments.cs:13-14`). `RunPauseEnvelope` persists exactly those two
members (`tool`, `args`) into `AgentRuns.ExtraJson`. On resume the launcher logs
`granted approved tool write_file` and the gate decides `GrantedByName`.

Nothing replayable survives. Three of the four reported defects fall out of that single fact:

- **Issue 2** — the parked `write_file` carried the extracted Excel data *in its arguments*. Approval
  discarded them. The step re-ran from the top against a context with no tool exchanges in it, so the
  model had neither the file it wrote nor the data it read, and concluded it could not read the xlsx.
- **Issue 3 (first half)** — `create_source` at 14:07:55 was the run's only genuine vault write. It was
  withheld because the run was already parked, and discarded with the same envelope. The resumed step
  never reached it again and fell back to `write_file`.
- **Issue 1.1** — the ellipsis in `wait_for_approval.png` is **source-side** truncation, not UI trimming.
  The full text exists nowhere in the process, so a tooltip or an expander alone fixes nothing.

Fix the park so it holds the actual call, and those three stop being independent problems.

---

## Issue 1 — the approval dialog appears 30–60 s after the badge

**Not a rendering delay.** The badge and the dialog are driven by two different events, ~41 s apart:

```
14:07:16.249  Background turn parked write_file for human approval (first=True)   ← badge appears
14:07:45.827  Round 4: 1 tool call(s) detected: write_file
14:07:55.568  Round 5: 1 tool call(s) detected: create_source  → withheld
14:07:57.852  Headless run ... parked step for approval of write_file (2 parked call(s))
14:07:57.867  Run ... → WaitingForInput (paused)                                  ← dialog appears
```

### Root cause

`AiClientService`'s round loop has **no way for a tool handler to stop it**. After every tool round it
unconditionally `continue`s (`src/Pia.Wpf/Services/AiClientService.cs:391-406`). The park arm in
`BackgroundAssistantTurnRunner.DispatchGateVerdictAsync` returns only a *string* asking the model to
stop (`:625-627`), and the withhold arm does the same (`:481-484`). Both are advisory. The run does not
reach `WaitingForInput` — the state that raises the dialog — until the whole tool-aware completion
unwinds back through `HeadlessTurnExecutor`.

The delay is therefore **unbounded, not 30–60 s**: it is (rounds the model still spends) × (provider
round-trip), capped only by `AssistantSettingsViewModel.MaxToolRoundsPerStep`, default **10**. The
second park cost 11.7 s; the first cost 41.6 s and 19 208 input tokens for work that was thrown away.

### The change

Give the dispatch a real stop signal and honour it.

1. `ToolDispatchContext` gains a settable stop flag (a `RequestStop()` the handler calls), so no new
   plumbing reaches the interactive path.
2. The loop must be able to *read* it. Today the context is constructed **inside** the per-call
   `foreach` (`AiClientService.cs:625`) and `DispatchToolCallsAsync` returns a bare `Task`, so a flag
   set on that object has no route back to the round loop at `:391-406`. Hoist the context out of the
   per-call loop, or have `DispatchToolCallsAsync` return the stop decision — pick one before writing
   the change. Then the loop finishes the exchange instead of `continue`-ing.
3. `BackgroundAssistantTurnRunner` sets it in three arms: `Park`, withheld-because-parked, and
   withheld-because-asking. The advisory strings stay — a model that sees the result still reads a
   coherent reason.

**Do not also raise the approval early from the gate.** Once the loop short-circuits, park →
`WaitingForInput` is milliseconds; doing both introduces a double-request race for one decision.

---

## Issue 1.1 — no way to see the full approval text

Two separate surfaces, one shared cause plus one of its own.

- **Shared:** the text is already truncated at the source (see *The spine*). `ApprovalToolArguments` is
  the capped string; the run panel's `ToolTip` shows the same capped string
  (`src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml:233-239`).
- **Flow card only:** the body is a single-line `TextBlock` with `TextTrimming="CharacterEllipsis"` and
  **no tooltip and no expander** (`src/Pia.Wpf/Controls/Flow/FlowView.xaml:163-169`).

### The change

Carry a fuller description (gated on **Q1** below), then make both surfaces able to show it: an
expander on the run panel's approval line, and a tooltip plus a bounded multi-line body on the Flow
card. Any new interactive control needs an `AutomationProperties.AutomationId` and its `[InlineData]`
row in `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` **in the same change**.

---

## Issue 2 — the model forgot it had already read the Excel

### Root cause: a resume rebuilds context from the chat, and the chat has no tool content

`HeadlessTurnExecutor.BeginRunAsync` clears `_messages` and re-seeds it **only from the persisted chat
rows** — role + prose (`src/Pia.Wpf/Services/HeadlessTurnExecutor.cs:283-306`). The tool exchanges are
appended to `_messages` alone (`:660`) and are never written to `_persisted`; the guardrail comment at
`:472-479` states the separation is deliberate and type-enforced.

So a park/resume boundary discards every tool call and tool result taken before the park. What resume
*does* restore is `AgentRunOrchestrator.SafeSeedResumeContext` (`:932-958`) — and only the `Done` step
rows, with `VisibleText: string.Empty`, which its own doc comment admits is not recoverable. Hence the
log line `Resume seeded 1 pre-pause step(s)`.

Compounding it, the parked `write_file` was the call that would have created
`.scratch/fehlzeiten_raw.txt`. It never ran, so at 14:08:18 the restarted step got
`search_files path not found under sandbox` — no data in context, no data on disk. Asking the user was
the only move left.

### The change

Two independent halves; do both.

**(a) Persist the pre-park tool exchanges where a resume can re-seed them.** They must **not** go into
`SyncAssistantChatMessage` — that path is cloud-synced (`AssistantChatSyncService` pushes every interim
save) and it would breach the `_messages`/`_persisted` guardrail. A run-scoped, local-only side store
keyed by run id is the shape that fits. Re-seed it in `BeginRunAsync` and let it flow through the
existing `AgentToolCarryover.ClearOldResults` + `AgentContextCompactor` seam unchanged. → gate **Q3**.

**(b) Make the grant carry the call, not just the name.** Persist the parked call verbatim (name,
arguments, call id) and on resume either replay it or seed the step with it. → gates **Q1**, **Q2**.

---

## Issue 3 — written to the working folder instead of the vault, and written twice

### Root cause A: the run's only vault call was withheld and discarded

`create_source` — the correct tool, whose description already says *"A NEW vault-relative source path,
e.g. `sources/meeting-notes-2026-08-11.txt`"* (`src/Pia.Wpf/Services/MemoryToolHandler.cs:441`) — was
called once and withheld. See *The spine*.

### Root cause B: in a run workspace the vault does not exist, and nothing says so

`RunWorkspaceService.CopyInAsync` **deliberately excludes the vault** from the workspace copy
(`src/Pia.Wpf/Services/RunWorkspaceService.cs:796-812`): the vault is owned by `MemoryService`, the
vault watcher and the ingest indexer, and a copy-in/copy-back cycle would fight the indexer.

The vault is `<AssistantFilesFolder>\Vault` (`src/Pia.Wpf/Infrastructure/Vault/AssistantFolderValidator.cs:22`),
i.e. a **child of the files sandbox**. So inside a run the model sees a sandbox root with no `Vault/`
in it, and "put it in my vault" resolves to the only writable place it can see: the sandbox root. That
is the "Work" folder in `dbl_file-not_vault.png`. Nothing in the files plugin's system-prompt addition
or in `write_file`'s description mentions the vault at all.

The user's expectation — `\Vault\sources` plus a question about the right subfolder — is reachable
today only through `create_source`, and the run never got back to it.

### Root cause C: two steps each wrote their own deliverable

Step 1 (after its context-stripped restart) overshot into step 2's deliverable and wrote
`Urlaubsübersicht_2026_pro_Mitarbeiter.md` (5 245 B, 14:10:33); step 2 then wrote
`Mitarbeiter_Urlaubszeiten_Zusammenfassung.md` (6 776 B, 14:11:09). Both were promoted. This is
**downstream of issue 2** — a step that cannot see what the previous step produced re-produces it under
a new name. Sequence the amnesia fix first; de-duplicating now de-duplicates a symptom.

### The change

1. **Guard.** Under a run workspace, `FilesToolHandler.PrepareWriteFile` refuses a path resolving into
   `Vault/` and names `create_source` / `update_source` in the refusal. → gate **Q4**.
   `PrepareWriteFile` is shared with the interactive path; the guard is workspace-scoped only because
   `TaskAmbient.Current?.WorkspaceRoot` is null interactively (`FilesToolHandler.cs:157-171`). Whether an
   *interactive* `write_file` into `<sandbox>\Vault` is the same defect is outside the reported scope —
   see the open questions.
2. **Disambiguate.** State in the files plugin's `systemPromptAddition`
   (`src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs`) that the sandbox root is *not* the vault
   and that vault writes go through the memory tools; state in the memory addition that `create_source`
   is the way to put a document in the vault.
3. **Ask for the subfolder.** When a goal names the vault without a target folder, the step instruction
   requires either an explicit `sources/<subfolder>` path or a `request_user_input` ask.
4. **De-duplicate across steps** (after the issue-2 fix): seed each step with the artifact refs already
   declared and forbid re-creating an existing deliverable under a new name.

---

## Issue 4 — "2 Freigabe(n) ausstehend" survives a completed run

`approval_counter_wrong.png` shows a run at **Abgeschlossen · 3 von 3 Schritten** still carrying
*2 Freigabe(n) ausstehend* next to *4 automatisch freigegeben*. The two rows are exactly the two parks
(14:07 `write_file`, 14:09 `remember`); the four are the four `GrantedByName` executions.

### Root cause: an "awaiting" pill counted from append-only history

`RunProgressViewModel.ApplyDecisionSummary` (`:1789-1822`) counts **timeline rows** per decision
category, and `ToolGateDecision.ParkedForApproval` maps to `Run_Timeline_Decision_AwaitingApproval`
(`:1879`). The timeline store is INSERT-only by design — `AgentTimelineService` has no update path, and
the park row is written with `DecidedAt = null` on purpose (`BackgroundAssistantTurnRunner.cs:616-621`).
When the human approves, a **new** row is written (`GrantedByName` → "Automatisch freigegeben"); the
park row is never superseded.

So the pill is a count of *"times this run ever asked"*, rendered with the copy of *"decisions still
open"*. It can only ever grow, and it survives the terminal state.

### The change

Derive the badge from **run state + pause envelope**, not from history. No schema or store change:

- The Awaiting pill appears only while the run is actually parked on a tool approval
  (`RunPauseEnvelope.ReadApprovalTool(run) is not null` on a `WaitingForInput` run), and its count is
  capped at 1 — `ToolApprovalStore.PendingToolName` is first-call-wins, so a run is never parked on two.
- Timeline **rows** keep their history, but a `ParkedForApproval` row the run has moved past renders as
  "nicht ausgeführt" rather than "Wartet auf Freigabe".

Two invariants worth pinning in tests: awaiting ≤ 1 while parked, and 0 on any terminal state.

---

## Decision gates

| # | Question | Decides | Why it blocks |
|---|---|---|---|
| **Q1** | May the **untruncated** arguments of a parked call be persisted (`AgentRuns.ExtraJson` or a local run store)? They are user content and include file bodies. | B1, B2, G-steps | A replayable park and a full approval text both need the real arguments. A "no" reduces both to raising the caps. |
| **Q2** | On grant, **replay** the exact parked call before re-running the step, or **seed** the step with the call so the model reissues it? | B2 | Replay changes the "step re-runs from the top" contract and the at-most-once reasoning behind the withhold guard. Seeding preserves it but stays advisory. |
| **Q3** | Where do pre-park tool exchanges live — local run-scoped store, workspace `.scratch`, or a widened chat schema? | C1 | The chat is cloud-synced; widening it exports tool payloads and breaches the documented `_messages`/`_persisted` guardrail. |
| **Q4** | Should `write_file` under a run workspace **hard-refuse** a `Vault/` path, or silently route it to `create_source`? | D1 | A silent route writes to a store the caller did not name; a refusal costs the run a round. |

### Answers — settled 2026-08-31 by the owner

All four are closed. The binding wording lives in the checklist's *Decision gates* table
([2026-08-31-approval-park-checklist.md](2026-08-31-approval-park-checklist.md)); in short:

- **Q1 — yes.** Untruncated payloads may be persisted, in a **local-only** store FK-cascaded off
  `AgentRuns`. Never `ExtraJson` (both resume claims `SET ExtraJson=NULL`), never the cloud-synced chat,
  never logged outside `SensitiveDebug`.
- **Q2 — replay once.** The grant executes the exact parked call before the step re-runs, with the
  persisted row marked replayed first, so at-most-once is structural rather than advisory.
- **Q3 — one table for both.** A parked call is a call with no result, so B1 and C1 share it. `.scratch`
  is out (model-writable; absent at `RunWorkspaceMode.None`); a widened chat schema is out.
- **Q4 — hard-refuse, narrowly.** Only a path resolving into a `Vault` subtree under a run workspace.
  Ordinary workspace writes and the whole interactive path are unchanged.

Do not tick a dependant of an open gate without revisiting it.

---

## Open questions

- **Verifier `declared=1` against three `Done` steps.** At 14:11:12 the artifact probe reports
  `declared=1 fileShaped=1 probed=1 found=1`, yet two files were promoted and both step 1 and step 2
  logged `artifactReported=True`. Either `ctx.CompletedSteps` loses artifact refs across the resume, or
  the probe de-duplicates. Record and investigate; not on the critical path.
- **Is an interactive `write_file` into `<sandbox>\Vault` the same defect?** Interactively the vault
  *is* visible under the sandbox root, so the write lands in the real vault but bypasses
  `MemoryService`, the vault watcher and the ingest indexer. Not observed in this run and not in scope;
  answer before widening D1 beyond the workspace.

## Resolved during investigation

- **The `write_file` at 14:07:45 with no gate row is not a second approval hole.**
  `PrepareWriteFile` returns `(Result: <error string>, Pending: null)` on a validation failure
  (`FilesToolHandler.cs:963-976` — missing `content`, or a `read_file` echo fed back as content), and
  `HandleToolCallAsync`'s `if (result is not null) return result;` short-circuits **before** the gate
  (`BackgroundAssistantTurnRunner.cs:464-466`). The call never reached the gate, so no row was correct.

---

## Verification

**Per step:** `dotnet build -t:Rebuild -v:n` in **Debug and Release**, `0 Warning(s)` / `0 Error(s)`
read off MSBuild's summary line. Steps that add an interactive control also add the `[InlineData]` row
in `ViewAutomationIdTests.cs`.

**Per group:** `dotnet test` with **no filter** at `failed: 0`. Once per group rather than once per
step — the suite runs ~11 min and class filters do not narrow it, so a per-step run costs about three
hours across this checklist for no extra signal.

**End to end:** a re-run of the original goal — one park, dialog inside a second, a resume that keeps
the extracted data, one summary file, in the vault, and a completed run showing zero pending approvals.

A vault-writing e2e is safe on a throwaway profile **provided the profile also patches
`assistantFilesFolder`**, which the 2026-08-26 harness already does. `Bootstrapper.InitializeAssistantFoldersAsync`
calls `paths.SetRoot(AssistantWorkspace.VaultRootFor(settings.AssistantFilesFolder))` at startup
(`src/Pia.Wpf/Bootstrapper.cs:321`), so the vault root follows the configured folder, not
`%LOCALAPPDATA%`. The caveat in
[`docs/agent_run_e2e/2026-08-26-agent-run-e2e-prompts.md`](../agent_run_e2e/2026-08-26-agent-run-e2e-prompts.md)
is narrower than it reads: `PIA_DATA_DIR` alone does not redirect the vault, but `assistantFilesFolder`
does. Verify the resolved root on the running instance before the first live vault write anyway.

Step tracking: [2026-08-31-approval-park-checklist.md](2026-08-31-approval-park-checklist.md).
