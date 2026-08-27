# Agent-run e2e results — foreground + background, OpenRouter/DeepSeek

**Status:** complete · **Owner:** Marco Altmann · **Written:** 2026-08-26
**Origin:** [2026-08-26-agent-run-e2e-prompts.md](2026-08-26-agent-run-e2e-prompts.md) — six goals driven
through the real UI with WinWright to prove the foreground and background agent-run flows.

## Setup

Hermetic: a throwaway profile at `%TEMP%\pia-e2e-0826\{roaming,local,files}`, seeded by copying the real
`settings.json` / `providers.json` / `templates.json` (so the DPAPI-encrypted OpenRouter key and the
sign-in survive) and passed to `ww_launch` via `PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR`. Patched in the copy:
`syncEnabled:false`, `autoIngestSources:false`, `defaultWindowMode:1`, `assistantFilesFolder` → the
throwaway `files\`. Concurrency left at the real values (`maxParallelBackgroundRuns:2`,
`maxParallelRequestsPerProvider:4`).

Provider: **DeepSeek via OpenRouter** (`deepseek/deepseek-v4-flash`), already
`modeProviderDefaults.Assistant`. Persona: Pia · Business (full tool scope). Debug build at
`src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.exe`, rebuilt clean (0 warnings, 0 errors).

`%APPDATA%\Pia\{settings,providers,templates}.json` and `%LOCALAPPDATA%\Pia\history.db` were SHA-256
hashed before and after: **all four unchanged**. The real profile was never touched.

## Verdict

| # | Goal | Folder | Shape | Run state | Output correct? |
|---|---|---|---|---|---|
| BG1 | expense overspend | `Finance` | background | Completed | **yes** — 6 over-budget categories, €3,110 total, both PENDING lines |
| BG2 | broken-link audit | `Docs` | background | Completed | **yes** — 8 links, 3 broken, all 3 `.issues.txt` siblings |
| BG3 | config drift | `Config` | background | Completed after 2 manual unblocks | structure right (2 missing / 1 extra / 4 changed), **2 values fabricated** (Finding 1) |
| FG1 | reorder report | `Inventory` | foreground | Completed | **NO — every row fabricated** (Finding 1) |
| FG2 | changelog merge | `ReleaseNotes` | foreground | Completed | 4 of 5 — `delete_file` hard-denied (Finding 2) |
| FG3 | ticket triage | `Support` | foreground | Completed | **yes** — 3/4/2, both URGENT tickets |

Eleven run rows in total: six user runs plus five delegated child runs (BG1 fanned out 3, BG3 fanned out 2).

**Parallelism confirmed** — foreground and background overlap, from the run timestamps:

- BG1 `20:02:44–20:09:00` ∥ FG1 `20:05:53–20:11:45` — **~3 min** overlap
- BG3 `20:16:23–20:30:57` ∥ FG2 `20:17:55–20:24:07` — **~6 min** overlap
- BG3 ∥ FG3 `20:26:04–20:29:20` — **~3 min** overlap

(BG2 and BG3 were also briefly concurrent with FG2, but only for a 5-second window — BG2 settled at
20:18:00, FG2 started at 20:17:55 — so that three-way is not worth leaning on.)

Foreground runs are necessarily **sequential**: `WindowManagerService._windows` is keyed by `WindowMode`,
so only one Assistant window can exist, and while one streams `Assistant_RunInBackground` and `Send` are
both disabled.

### Not covered

- **The background-run queue was never exercised.** `maxParallelBackgroundRuns` is 2 and at most two
  background runs were ever in flight, so nothing queued. The queue-on-a-full-pool path is untested here.
- **The `ToolApproval_*` card flow was never exercised.** It was FG2's job and Finding 2 explains why it
  could not happen. Nothing in this run says the card works.
- **Reject was never pressed** — both plan gates were approved.
- Only the `deepseek/deepseek-v4-flash` model was used; Findings 1 and 3 are partly model-dependent.

## What worked

- **Working directory per chat.** All six chats were pointed at a different folder through
  `ChatChip_Toggle → ChatChip_WorkingDir`, and each run's isolated workspace was seeded from exactly that
  folder (`AgentRuns.WorkingSubpath`, verified against the workspace contents).
- **Workspace isolation.** Every run got `<local>\runs\<runId>` plus a `<runId>.workspace.json` sibling,
  provisioned in Copy mode; artifacts were published back to the working folder on settle and the
  workspace torn down. No run ever wrote straight into the files folder.
- **Plan-approval gate.** Both foreground runs parked at `WaitingForInput` showing a numbered plan with
  Approve / Reject, and the composer was correctly locked ("Approve or reject the proposed plan before
  sending another message").
- **Run panel.** Per-step checkmarks, live "step N of M", elapsed time, per-step token counts, model name,
  and a Tool activity chip with auto-approved / denied counts.
- **Sub-agent fan-out.** Both fan-outs dispatched, their children ran concurrently and their results
  merged into the parent.
- **Park recovery.** A `needs-goal` park showed its clarifying question, a note field and Continue;
  answering in the chat resumed the child within seconds.
- **Detachment.** Navigating to Chat history mid-run did not disturb the running foreground run — it kept
  executing and finished normally.

## Findings

### 1. A step cannot see what an earlier step read, and the model fills the gap by inventing (highest severity)

FG1 read `inventory.csv` correctly in step 1 — the log shows the tool result verbatim:

```
[run 36c6add1 step 1] Tool read_file handler result (464 chars): total_lines=13
2|SKU-1001,Blue Widget,4,10,3.50
```

Two steps later it wrote, without re-reading anything in that step:

```
[run 36c6add1 step 3] Tool call write_file args: {"path":"reorder-list.csv",
  "content":"sku,name,on_hand,reorder_point,order_qty\nSKU-1001,Bolt M8 x 30mm,45,50,105\n…"}
```

Every product name and every number is invented. **This is not the PII tokenizer.** The tokenizer wraps
the tool handler, so a reader could object that the model saw placeholders and restored them wrong — but
no substitution scheme turns `4,10` into `45,50`, and the *set* of SKUs changed too: the fixture's
below-reorder products are 1001/1003/1005/1011, the written ones are 1001/1004/1005/1007/1012. The model
invented rows, it did not mis-restore them.

The run then **passed its own verification**: step 4 was
"read back both files and verify row counts match", which compared 5 rows to 5 rows and agreed. The run
reported Completed and the report file reads as authoritative.

Mechanism, in the code: `HeadlessTurnExecutor.cs:638` appends only `exchange.Visible` — the assistant's
visible text — to `_messages` after each step, and the next step's request is built from `_messages`
(`HeadlessTurnExecutor.cs:455`; the live path has the same shape at
`ChatSession.BuildStepChatMessagesAsync`, `ChatSession.cs:937`). Tool calls and tool results live only in
the working list *inside* one step and are dropped at the step boundary. So data read in step N is
available in step N+1 only if the model happened to restate it in prose.

**It hit a second run.** BG3's `config-drift.csv` gets the *shape* right — 2 missing, 1 extra, 4 changed,
exactly the seeded drift — but two literal values are invented:

| field | fixture | written |
|---|---|---|
| `REGION` baseline | `eu-central` | `us-east-1` |
| `SENTRY_DSN` actual | `https://example.invalid/1` | `https://abc123@sentry.example.com/1` |

The key *names* survived because a child run wrote an `env-pairs.md` scratch file; the values it did not
copy into that file were gone by the time the report was written.

Corroborating from the other direction: BG2 hit the same wall and worked around it, spontaneously writing
unrequested `file-list.txt` and `step-2-results.md` scratch files to carry state across steps — and its
output was fully correct. FG3 and BG1 were correct because their read and write landed in adjacent steps
with the ids restated in the summary.

So 2 of 6 runs produced fabricated content, and the pattern is exactly "how many step boundaries sat
between the read and the write". This is the finding worth acting on: it is silent, it survives the run's
own verification, and what it leaves behind is a plausible-looking file with invented content. Candidate
mitigations: carry the raw tool results of the previous step into the next step's context, or make
`emit_step_result` require the data a later step will need, or have the planner prefer read-and-write in
one step.

### 2. `delete_file` can never be approved from inside an agent run

FG2's plan step 6 was "delete every successfully merged fragment file". It was denied four times:

```
[run e36b989f step 2] Background turn denied ungranted write tool delete_file
Denied: 'delete_file' is a write action not granted to this background job. Do not retry.
```

Two things follow, and the second is the interesting one.

**Why it is denied rather than asked.** `RunAutonomyPolicy.PresetClasses` includes `ToolClass.Files`, but
`ToolAutonomy.Resolve` excludes delete-like names from the policy arm, and the unattended approval park
also refuses to ask about a delete-like tool. So on the unattended surface the only way through is a
persisted "Always allow" in Settings → Tool access.

**Why a Send-started run is on the unattended surface at all.** It does not start there.
`ChatSessionManager` dispatches a Planned run on `LiveTurnExecutor`
(`ChatSessionManager.cs:958`), whose own comment says "for an interactive Planned run the action card is a
normal path" — so the approval card *is* reachable in principle. What moves it is the **plan-approval park
and Continue**: `RunProgressViewModel`'s Continue calls `IAgentRunResumeService.ResumeAsync`, implemented
by `HeadlessRunLauncher`, which builds a `HeadlessTurnExecutor` (`HeadlessRunLauncher.cs:929`). The log
shows the transition:

```
22:17:55  Created run e36b989f shape=Planned state=Planning trigger=User
22:18:41  Resuming run e36b989f (chat …, parent=False)          <- my Approve click
22:19:07  [HeadlessTurnExecutor] [run e36b989f step 0] …        <- every step from here on
```

So: **pressing Approve permanently moves a foreground run onto the headless executor.** With
`agentPlanReasoningTurnEnabled: true` (this profile, and the default), every foreground agent run parks
for approval, so in practice the interactive card is never reached after the plan gate. Whether it is
reachable with the plan-reasoning turn switched off was not tested.

Consequences: the user-facing denial text says **"this background job"** for a run the user started with
Send and is watching, and there is no in-product path to grant the tool at that moment. The model reported
it honestly ("Merged fragments deleted: 0 of 4 … You'll need to delete those 4 files manually"), which is
the right behaviour given the denial.

### 3. An unattended background run can dead-end on a needless clarification

BG3's goal was fully specified (three named files, three named sections, two named output files). The
parent still delegated a child with the goal *"Compare each environment file against the baseline…"*, and
that child's planner declined to ground it and asked:

> Do you have a preferred tool for comparing .env files (e.g., dotenv-diff, env-diff, or another)? Or would
> you like me to use a general file comparison approach?

The run has file tools and no shell, so the question has no useful answer. The child parked `needs-goal`
(`ExtraJson: {"paused":true,"reason":"needs-goal"}`) and the parent re-parked behind it
(`{"paused":true,"reason":"children-parked"}`). With nobody at the machine, BG3 would have produced
nothing.

Recovery needed **two** separate human actions, because a `children-parked` parent does not auto-resume
when its children finish — by design (`AgentRunOrchestrator.cs:381`, "one Continue on the parent
re-dispatches the group"). Both children reached Completed and the parent still sat at `WaitingForInput`
until Continue was clicked.

### 4. The parked parent is reachable only through the Flow rail, and that card is not automatable

The parent's stub chat is in-flight, so it does **not** appear in Chat history — the known gap. Its only
affordance is the Flow rail's "Continue run" link. That link is not reachable through UIA:

`RootFlowView`'s item list reports its `DataItem` containers but, for most of them, **no children at all** —
no `TitleText`, no `BodyText`, no `Flow_ActionLink_<id>`. Which containers expose their content varies
between calls (first observed: only the oldest; later: oldest and newest; after two dismissals: none).
`ww_hover`, `ww_click` on the container and `ww_inspect find_by_description` all failed to realize them.
Resuming BG3 needed a physical click at a pixel offset inside the card — exactly what the playbook says to
avoid. The `Flow_*` ids in `docs/ui_automation/ui-automation-playbook.md` are correct but not reliably
resolvable at runtime.

Worth fixing before anyone writes a recorded script that touches the Flow rail.

**Follow-up 2026-08-27 — partly diagnosed differently, and closed.** The cards are now addressable per
card: the `DataItem` container carries `Flow_Card_<id>` (named by the card title) and title/body/decisions
carry per-item ids. The real, reproducible defect was that title and body were addressable only through
their shared `x:Name`, so `#TitleText` resolved to one hit *per card* and silently returned the first,
while the container answered to no id at all — "the card for run X" had no selector. The *realization*
half did not reproduce: 4 and 8 seeded cards exposed every id on every consecutive dump, pinned and in
overlay mode, scrolled past the viewport, after dismissals and after a collapsed-rail peek cycle. The
suspected two-host template clash is refuted. See the playbook's "Known gaps" for what is left open.

### 5. The PII tokenizer put a placeholder into a written artifact

BG1's `overspend-report.md`, written to disk, contains:

```
## Unsettled

- expenses-q1.csv: [Phone_9],travel,1500.00,client visit PENDING
- expenses-q2.csv: [Phone_15],catering,450.00,offsite dinner PENDING
```

`2026-03-27` and `2026-06-01` were tokenized as phone numbers before the model saw them, and the token was
written through into the file. This is the known hyphenated-date detector bug, but here it is not a test
artifact — it corrupts a user-visible deliverable. The same run's `pending-results.md` has the real dates,
so it is not uniform within one run; the log shows `tokenization=False` on some relays and `=True` on
others for the same run, but I did not isolate which turn wrote which file.

### 6. A settled run silently rewrites the saved Agent-mode default

`AssistantViewModel.OnRunProgressSettled` (`AssistantViewModel.cs:508`) drops the lever back to Chat when a
run settles, with a stated reason: "A finished run must not silently arm the NEXT send as a fresh run."
Fine as a per-composer decision.

But `OnAgentModeEnabledChanged` (`AssistantViewModel.cs:738`) persists **every** change via
`PersistAgentModeDefaultAsync`, and it cannot tell a user's click from that automatic fall-back. So a run
finishing rewrites `AssistantAgentModeDefault` to `false` — the user's saved preference, changed by
something they did not do. (Code-evident. The end-state value in the throwaway profile is `true` only
because the last thing I did was click Agent.)

What this looks like in use: the lever is on Agent at launch, a run finishes, it is on Chat, and a new chat
**inherits** that — verified both directions (flip to Agent → new chat → still Agent; leave on Chat → new
chat → still Chat). New chat does not itself reset anything. In Chat mode `Assistant_RunInBackground` is
not rendered at all, so "new chat → type → Run in background" silently offers no such button.

### 7. Run scratch files are promoted into the working folder, and then contaminate the run

Files the model wrote only as intermediate scratch are promoted out of the isolated workspace along with
the real deliverables: `file-list.txt` and `step-2-results.md` landed in `Docs`, `env-pairs.md` in
`Config`. They are also visible to the run's own later tool calls — BG3's `config-todo.txt` has three
entries where the fixture has two, and the third is a `TODO` match found **inside its own scratch file**:

```
staging.env line 1: # TODO: align with baseline before the next release
staging.env line 6: # TODO: REGION is unset on purpose for now
env-pairs.md line 44: - `REGION` — not set; noted as intentional (`# TODO: REGION is unset…`)
```

A run's own intermediate output being searchable by its later steps is a self-contamination loop, and
promoting it leaves litter in the user's folder. Same evidence as Finding 1 — the scratch files exist
precisely because the model is compensating for the lost cross-step context.

### 8. Composer hint says "background run" for a foreground run

While a Send-started agent run executes, the composer reads *"A background run is writing to this chat.
Sending resumes when it finishes."* Technically true after Finding 2's hand-off, but it reads as if the
user pressed the wrong button.

## Automation notes

The four selector lessons from this run (chip-toggle phase, the picker opening inside the current folder,
the split `ww_keyboard` calls, and the Flow-rail realization gap) have been folded into
`docs/ui_automation/ui-automation-playbook.md` under "Known gaps", so they live where the next script
author will look.

**2026-08-27:** three of the four are fixed in the app rather than worked around — the chip toggle now
decides from the popup's own `IsOpen`, the picker's focus move is synchronous so a one-call key burst
survives, and the rail's cards carry per-card ids. The picker opening inside the current folder stays as
documented behaviour. Those playbook entries now say what changed.

## Reproducing

The harness sits beside the recorded-script one at `tests/ui-scripts/agent-run-e2e/` — three Node
scripts, no dependencies, `node:sqlite` needs Node >= 22.5. The "Agent-run e2e (unrecorded)" section
of [tests/ui-scripts/README.md](../../tests/ui-scripts/README.md) makes a cold run possible.

```powershell
dotnet build
node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-e2e
# then ww_launch the Debug exe with PIA_DATA_DIR / PIA_LOCAL_DATA_DIR pointed at roaming\ and local\
node tests/ui-scripts/agent-run-e2e/watch.mjs $env:TEMP\pia-e2e         # second terminal, while you drive
node tests/ui-scripts/agent-run-e2e/probe.mjs $env:TEMP\pia-e2e all     # after the runs settle
node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-e2e verify
```

The six prompts and the fixture expectations are in
[2026-08-26-agent-run-e2e-prompts.md](2026-08-26-agent-run-e2e-prompts.md). The throwaway profile of
the 2026-08-26 session (artifacts, `history.db`, `local\Logs\pia-2026-08-26.log`) is kept at
`%TEMP%\pia-e2e-0826` — delete it when the findings above have been triaged.

## Re-run 2026-08-27 — after the seven fixes

Same harness, a fresh throwaway profile at `%TEMP%\pia-e2e`, same provider
(`deepseek/deepseek-v4-flash` via OpenRouter) and the same persona. Four of the six goals were driven —
FG1, BG1, BG3, FG2 — plus one extra short run to catch the composer hint mid-flight. Real profile
verified untouched at the end.

| Goal | Then | Now |
|---|---|---|
| FG1 `Inventory` | **every row fabricated** | **correct**: 1001/1003/1005/1011, every on_hand / reorder_point matching `inventory.csv`, 1008 excluded, order quantities exactly `(reorder_point * 3) - on_hand` |
| BG1 `Finance` | correct table, **`[Phone_9]` written into the deliverable** | **correct**, and `overspend-report.md` carries `2026-03-27` and `2026-06-01`; no `[Phone_` anywhere under `files\` |
| BG3 `Config` | 2 values invented, `config-todo.txt` had **3** hits (one from its own scratch file) | **correct**: `eu-central` and `https://example.invalid/1` both survive; `config-todo.txt` has exactly the **2** seeded hits |
| FG2 `ReleaseNotes` | `delete_file` **hard-denied four times** | **parks**, the card names all four paths, Continue grants it and the deletes execute — see the caveat below |

### The seven, one at a time

1. **Cross-step tool context.** FG1 ran 6 steps and 35 tool rounds and produced fixture-exact output
   where it previously invented every row. **The wire format holds**: from step 2 on, every request
   carries the earlier steps' `tool_calls` + `role:tool` messages and the provider accepted all of them
   — 0 provider errors in the log. That shape had no test coverage at all (every test substitutes
   `IAiClientService`), so this run is its only evidence.
2. **`delete_file`.** The envelope reads
   `{"paused":true,"reason":"tool-approval","tool":"delete_file","args":"path=fragments/0001-agent-panel.md, path=fragments/0002-timeout.md, path=fragments/0003-workdir.md, path=fragments/0005-e2ee.md"}`
   — all four, because the model issued four delete calls in one round and the store accumulates them.
   The panel rendered *"Waiting for your approval to use delete_file"* over
   *"Affects path=fragments/0001-agent-panel.md, …"* with Continue / Deny. Continue re-ran the step and
   the log shows `Background turn executing delete_file (GrantedByName)` … `delete_file succeeded` for
   each. **Caveat below.**
3. **Child decline / parent auto-resume.** Not exercised: neither BG1 nor BG3 fanned out this time
   (both planned 3–4 in-process steps), so no child ran and no parent parked. Covered by tests only.
4. **PII.** BG1's report has the real dates. Nothing under `files\` contains a placeholder.
5. **Agent-mode default.** `assistantAgentModeDefault` is still `true` after five runs settled. It would
   have read `false` after the first.
6. **`.scratch/`.** The model used it unprompted in both runs that wanted working notes —
   `.scratch/reorder-working-notes.md` (FG1) and `.scratch/step5-final-report.md` (FG2) — and neither
   was promoted. No `file-list.txt` / `step-2-results.md` / `env-pairs.md` anywhere. BG3's
   `config-todo.txt` dropping from 3 hits to 2 is the self-contamination loop closing.
7. **Composer hint.** Reads *"A run is writing to this chat. Sending resumes when it finishes."*

### The one thing that did not fully land

**A delete still cannot reach the user's folder, and that is workspace isolation, not the gate.** The
four deletes really executed — inside `runs\<runId>\fragments\` — but copy-mode promotion never
propagates deletions, by explicit design (`RunWorkspaceService.CopyOut`: *"A run cannot delete a user
file by promoting — that is the difference between 'promote' and 'sync', and write arbitration belongs
to a later batch"*). So `fragments/` still holds all six files, and the model's own report
(*"Deleted: 4 — the four merged fragment files have been removed from fragments/"*) is true of the
workspace and false of the folder.

Finding 2 was about the GATE and the gate is fixed: the tool is reachable, the approval is informed,
and the call runs. Making the effect durable is a separate, pre-existing gap — promotion has no delete
channel — and it is worth its own decision, because "the run says it deleted your files and they are
still there" is a worse outcome than the old honest denial.

### Not covered by this re-run

- **Sub-agent fan-out** (finding 3), for the reason above.
- **Worktree-mode promotion.** The fixture folders are not git repos, so `ExcludeScratchPathspec` never
  ran live; it is covered by tests only.
- **BG2 `Docs`** and **FG3 `Support`**, which were correct in the first session and test nothing this
  batch changed.
- **Reject** on either gate, and the background-run queue (never more than two in flight).
