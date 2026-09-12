# Agent-run visibility: the blank bubble, the double-counted approvals, the silent step

**Status:** Implemented
**Owner:** Marco Altmann
**Written:** 2026-09-12
**Origin:** User report against a live 5-step research run — "why do we show a single P logo for the
current step without any output?" and "this run showed me 2 tool prompts, I accepted both; why do we
have a count of 4?"

Three separate defects sit behind those two questions. All three are pinned against the code and
against `%LOCALAPPDATA%\Pia\Logs\pia-2026-09-12.log` (run `45918bf8`, chat `7e549007`).

## A. The blank assistant bubble

`HeadlessTurnExecutor` persists every completed step's reply unconditionally:

```csharp
_persisted.Add(new SyncAssistantChatMessage { Content = exchange.Visible, ... });
```

`src/Pia.Wpf/Services/HeadlessTurnExecutor.cs:920`. A step whose exchange *completes* with no visible
prose — the model emitted only tool calls, or declared `emit_step_result` and said nothing — writes a
row with `Content = ""`.

Log evidence:

```
12:09:16  step outcome: offered=True confirmed=False succeeded=False
12:09:16  interim-persisted chat 7e549007… (3 messages)
```

`succeeded=False` there is the whitespace fallback at `HeadlessTurnExecutor.cs:893` reading
`exchange.Visible` as empty — and the row was persisted anyway.

It is **not** the parking attempt. The park arm (`HeadlessTurnExecutor.cs:849`) returns before the
transcript append and before the interim persist, by design, so the parked step re-runs clean.

The row renders as a bare avatar because three independent conditions all hide on it:
`PiaAssistantMessage.xaml:42` collapses the markdown border on `HasContent=False`, line 119 collapses
the toolbar on the same, and `PiaReasoningView`'s live strip needs
`ShowLiveReasoning = IsStreaming && !HasContent` — a persisted row has `IsStreaming=false`. The
avatar itself lives one level up, in `AssistantView.xaml:106`, and has no condition at all.

The interactive path cannot produce this: `ChatSession.CleanupPerExchange` substitutes
`Msg_Assistant_EmptyResponse` (`ChatSession.cs:711`). Headless has no equivalent.

**Why not synthesize the same text.** A silent completion is legitimate — the step-success comment at
`HeadlessTurnExecutor.cs:888` records "a claim of true is a Done step even with no visible text". Text
saying the assistant did not respond would misreport a step that succeeded. Skip the row instead.

A second, latent producer exists: `CleanupPerExchange` with `cancelled=true` leaves `Content=""`,
`IsStreaming=false` and the message in `Messages`. That is why A needs a render-side half as well as a
source guard — and why the already-persisted ghosts in the user's chats must still disappear.

## B. Two approvals, four timeline rows

Each approved call is recorded twice, because the park and the answer are separate executions:

| | written at | decision | outcome | rendered |
|---|---|---|---|---|
| park | `BackgroundAssistantTurnRunner.cs:660` | `ParkedForApproval` | `NotExecuted` | "Not executed" |
| replay | the resumed step's gate | `GrantedByName` | `Ok` | "Auto-approved" |

`ToolGateEnums.cs:91` states the split outright: "the re-executed step's call is a fresh row carrying
`GrantedByName`, because the approval reaches it as a grant."

Log proof, matching the reported screenshot row for row:

```
12:10:56  parked write_file for human approval (first=True)
12:11:11  replaying 1 approved call(s) of write_file
12:12:27  parked edit_file for human approval (first=True)
12:15:43  replaying 1 approved call(s) of edit_file
```

Presentation then makes both halves read wrong:

- `RowLabelKey` (`RunProgressViewModel.cs:1985`) relabels a superseded park row "Not executed". True
  of that row in isolation, but as a summary pill it claims two calls never ran.
- `DecisionLabelKey` (`RunProgressViewModel.cs:1962`) buckets `GrantedByName` with the standing grants,
  so a call the user personally approved reports as "Auto-approved". The "Approved" bucket below it
  (`ApprovedOnce`/`Always`/`ForSession`) is unreachable on the unattended surface.

**Why the fix is not a blanket remap.** `GrantedByName` also fires for a scheduled job's configured
envelope (`ToolAutonomy.cs:144` — "headless launch envelope / scheduled job"), where nobody was asked.
The honest discriminator is the pairing: a `GrantedByName` row is an approval iff a `ParkedForApproval`
row for the same tool precedes it in the same step. Pair on `CallId`, fall back to tool name + `Seq` —
the park arm writes one row for two same-tool calls, so `CallId` alone leaves the second replay
unpaired.

Merge the pair into one row rather than hiding the park row: the audit trail keeps both facts, the
count becomes 2, and the pills recompute off the merged list.

## C. Nothing to see while a step runs

By design, not a defect — but the consequence is the user's actual complaint. After plan approval the
run resumes through `HeadlessRunLauncher`, whose executor is documented "No streaming, no action-card
UI" (`HeadlessTurnExecutor.cs:20`). The chat therefore receives exactly one message per step, written
when the step ends. 1 min 36 s of silence is expected behaviour.

The data the user wants already exists and is already live:

- every executed tool emits a timeline row carrying tool name, decision, outcome, round and duration —
  auto-run calls included, not only gated ones (`BackgroundAssistantTurnRunner.cs:626`);
- `ITimelineWatcher.TimelineAppended` already pushes each append into `RunProgressViewModel`
  (`RunProgressViewModel.cs:725`).

What is missing is only presentation: `_isTimelineExpanded` defaults to `false`
(`RunProgressViewModel.cs:481`), so the one live surface is shut exactly while the run is most opaque,
and nothing at all appears in the chat.

**The live bubble stays out of `session.Messages`.** A placeholder appended there would be caught by
the full-replace `PersistAsync`, would enter the model context (`ChatSession.cs:404-414`), would round
-trip through `AssistantMessageMapper.ToDto`, and would have to be reconciled against the windowed
transcript and against `PullMissingTranscriptRowsAsync`'s id-keyed append. Instead it is a pinned
pseudo-bubble rendered below the transcript `ItemsControl`, bound to the same `CurrentActivity`
property the run panel's line uses, so the two surfaces cannot disagree and nothing needs removing
when the real row lands. It shows on `ForeignRunActive`, which already excludes parks and a session's
own live run (that one streams its own step message).

## Scope decided with the owner

All of A, B and C, including the in-chat live bubble.

## Out of scope

Streaming from `HeadlessTurnExecutor` itself. It is pool-thread, constructed in a fresh DI scope per
run, and deliberately UI-free; the timeline bridge already carries everything needed.
