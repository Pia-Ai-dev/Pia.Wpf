# Carrying tool calls and results across a step boundary

**Status:** executable · **Owner:** Marco Altmann · **Written:** 2026-08-27
**Origin:** Finding 1 of [2026-08-26-agent-run-e2e-results.md](2026-08-26-agent-run-e2e-results.md) — two of six
runs wrote plausible-looking files whose every value was invented, and one of them passed its own
row-count verification.

## The defect

A step cannot see what an earlier step read.

`HeadlessTurnExecutor.RunExchangeStepAsync` appends exactly one thing to the accumulating `_messages`
after each step: `exchange.Visible`, the assistant's visible prose. The next step's request is built
from `_messages`. Tool calls and tool results exist only inside `AiClientService`'s `workingMessages`
— a *copy* of the request, discarded when the in-step tool loop ends — so nothing structured survives
the boundary. The live path has the same shape: `ChatSession.BuildStepChatMessagesAsync` rebuilds the
request from `Messages` via `AssistantMessage.ToChatMessage()`, which carries text and an optional
image and no tool content at all.

The stream vocabulary makes this invisible to the executors: `TextDelta`, `ReasoningDelta`,
`ToolRoundCompleted`, `Finished` (`src/Pia.Wpf/Models/ChatStreamItem.cs`). `ToolRoundCompleted` is a
bare marker — it carries no payload and is emitted *before* dispatch on purpose.

So data read in step N reaches step N+1 only if the model happened to restate it in prose. When it did
not, the model filled the gap by inventing — silently, and in a form that survives a later "read both
files back and check the row counts" verification step, because the invented file is internally
consistent.

## What every reference agent does instead

Pia's per-step "clean transcript" is the outlier. The practice was checked against three
implementations plus Anthropic's own guidance:

- **OpenCode** keeps the full tool history in the model context and separates stored history from
  model context. It caps serialized tool output at 2000 chars, keeps the newest ~15k tokens verbatim,
  and summarizes the rest only once estimated tokens exceed the limit minus a 20k buffer.
  <https://opencode.ai/v2/docs/compaction/>
- **Hermes Agent** keeps `tool_call`/`tool_result` pairs in context. At 50% fill its first, LLM-free
  pass replaces old tool results (>200 chars) outside a ~20k-token protected tail with
  `[Old tool output cleared to save context space]`, protects the system message and the first
  exchange, and sanitizes orphaned pairs after every compression.
  <https://hermes-agent.nousresearch.com/docs/developer-guide/context-compression-and-caching>
- **Anthropic's cookbook** calls tool-result clearing the lightest-touch compaction: keep the
  `tool_use` block — the record that the call was made, with its input — and replace the body of all
  but the last N `tool_result` blocks with a short placeholder, because re-callable tools (file reads,
  searches) can simply be called again.
  <https://platform.claude.com/cookbook/tool-use-context-engineering-context-engineering-tools>
- **Open WebUI** sends the entire conversation including prior tool results on every request.
  <https://docs.openwebui.com/features/extensibility/plugin/tools/>

## The change

Carry the tool exchanges forward, bounded by tool-result clearing and the existing compactor.

### 1. Capture seam — `AiClientService`

A new stream item, yielded once per tool round, immediately after the dispatch that produced it:

```csharp
public sealed record ToolRoundExchange(int Round, IReadOnlyList<ChatMessage> Messages) : ChatStreamItem;
```

`AiClientService` snapshots `workingMessages.Count` before `DispatchToolCallsAsync`, then yields the
slice appended after it. The slice must be **materialized** — the round-start compaction
*reassigns* `workingMessages`, so a deferred `Skip()` would enumerate a list this round never wrote to.

The seam sits downstream of both the streaming and the non-streaming branch, so one insertion covers
both provider paths — the same reason the in-loop compaction sits where it does.

It is a second item, not a replacement for `ToolRoundCompleted`: that one is yielded *before* dispatch
so consumers know a fresh model turn is coming even when the dispatch throws. The new one can only be
yielded after a dispatch that returned.

**Why this seam and not the handler seam.** Recording at
`BackgroundAssistantTurnRunner.HandleToolCallAsync` / `ChatSession.HandleToolCall` would capture the
*pre*-tokenization result. `TokenizingAiClientService.WrapToolHandler` tokenizes the handler's return
value before `AiClientService` ever sees it, so the result that lands in `workingMessages` — and
therefore in the slice — is already the placeholder form the model saw. Re-sending it leaks nothing.

The decorator's `else { yield return item; }` arm passes the new item through untouched, and
`BufferedDetokenize` never sees it. Locked by a test.

**One correction to that premise, fixed here.** `DetokenizeToolCallArguments` *mutated*
`toolCall.Arguments` in place, so after a write-tool dispatch the `FunctionCallContent` sitting in
`workingMessages` held the real PII, not the placeholder — and the next tool round already re-sent it
to the provider. Carrying that object across a step boundary would have widened an in-step leak into a
whole-run one. The decorator now hands the handler a detokenized **copy** and leaves the original
tokenized, which is what makes the paragraph above true for calls as well as results.

### 2. Headless

`BackgroundAssistantTurnRunner.RunExchangeAsync` collects the items; `ExchangeResult` gains a
`ToolExchanges` member. `HeadlessTurnExecutor.RunExchangeStepAsync` appends them to `_messages`
**before** the visible assistant reply, so call and result stay adjacent and in round order.

`_persisted` does not grow. The HARD GUARDRAIL still holds by construction: `_messages` is
`List<ChatMessage>`, `_persisted` is `List<SyncAssistantChatMessage>`, different types, appended in
parallel, never cross-read, and the only route to the DB is `BuildChatSnapshot`'s
`Messages = [.. _persisted]`.

### 3. Live

`ChatSession` keeps a non-UI `Dictionary<Guid, List<ChatMessage>>` of tool exchanges per step, keyed by
the step's `AssistantMessage.Id`. It is never added to `Messages`, which is rendered and persisted.
`BuildStepChatMessagesAsync` merges it in step order: for each transcript message, that message's
carried exchanges first, then the message itself.

The sink is passed into `RunModelExchangeAsync` only from the step call site — the ordinary
interactive turn passes null and accumulates nothing.

### 4. Tool-result clearing, in code and provider-independent

`AgentToolCarryover` (internal static, `src/Pia.Wpf/Services/`):

- **`Capture`** — caps each carried result at **4000 chars** at capture time, so what is stored is
  already bounded and the executors' lists cannot grow without limit. The step that needed the result
  in full already had it, inside its own tool loop.
- **`ClearOldResults`** — run on the step request *before* compaction. Every carried
  `FunctionResultContent` older than the last **K = 8** tool results gets its body replaced with
  `[result cleared; call <tool> on <path> again if you need it]` (the `on <path>` clause only when the
  matching call carried a `path` argument). The `FunctionCallContent` stays — that is the record that
  the call was made, and it is what lets the model re-issue it.

Both build new objects; neither mutates a `ChatMessage` or an `AIContent` in the stored list. A carried
message can be shared with `_messages` and, on the live side, sits in a session-lived dictionary — an
in-place rewrite would corrupt the transcript every later step is built from, and no test would see it.

Order is **build → clear → compact**. Clearing first means the compactor never spends a summarization
pass on a body that was about to become a placeholder.

`AgentContextCompactor` already refuses to split a call/result pair — `HasToolContent` keeps the image
pin (its only mid-list lift) off a tool message, and the library's own
`ToolResultCompactionStrategy` treats a call group as atomic. A test asserts that a carried-and-cleared
list survives compaction with its CallId set intact.

### 5. Resume

A resume re-seeds `_messages` from the persisted chat, which holds role + content only and no tool
structure. That is acceptable: the post-resume state equals the post-clearing state, which the model is
already told how to handle.

**Superseded** by [../agent_run_approval_park/2026-08-31-approval-park-implementation-plan.md](../agent_run_approval_park/2026-08-31-approval-park-implementation-plan.md)
group C: a run's tool exchanges are now persisted to `AgentToolExchanges` and re-seeded on resume, so the
post-resume state is the pre-park state, not the post-clearing one.

### 6. Telling the model

One sentence, appended to the per-step instruction in `HeadlessTurnExecutor.BuildInstruction` and its
live twin in `BuildStepChatMessagesAsync`:

> Tool results from earlier steps that are shown cleared are not in your context — read the file again
> before using its content; never reconstruct it from memory.

Not on the goal-verbatim / degrade-turn shape, which bypasses the step instruction on both sides.

### 7. Planner nudge

One line in the plan and replan prompts: a step that writes from data it must first read should read
and write in that one step. Fewer boundaries beats a better boundary crossing.

## Tests

| Test | What it pins |
|---|---|
| two-step headless run | step 2's request contains step 1's `read_file` result verbatim |
| headless run with more than K rounds | the oldest result is cleared and the placeholder names the tool and the path; the call survives |
| live parity twin | `BuildStepChatMessagesAsync` merges the carried exchanges in step order |
| `AgentToolCarryover` unit tests | cap at capture, keep newest K, placeholder wording, no mutation of the input |
| compactor pair integrity | a carried-and-cleared list compacts without orphaning a CallId |
| tokenizer pass-through | the new item crosses `TokenizingAiClientService` untouched, and a carried result still holds its placeholder |
| tokenizer non-mutation | a write tool's handler sees the real value while the message in the transcript keeps the placeholder |

## What this does not do

- It does not summarize. Clearing is the whole eviction policy; the existing compactor is the only
  summarizer, and it runs after.
- It does not persist tool exchanges. They live for the run (headless) or the session (live) and never
  reach `history.db`. **Superseded** by the same plan's group C: the headless path now writes them to
  `AgentToolExchanges` in `history.db`, device-local and purged with the run.
- It does not remove the reason a model writes scratch files. That is Finding 6's `.scratch/`
  convention, sequenced after this.
