# Anthropic native block replay — client plan (Pia.Wpf)

**Status:** Planned — not started. All owner gates decided 2026-10-15 (table below). Appendix B keeps the
client-carried envelope plan in case G1 is ever reopened.
**Owner:** Marco Altmann
**Written:** 2026-10-15
**Origin:** [2026-10-15-anthropic-native-block-replay-plan.md](2026-10-15-anthropic-native-block-replay-plan.md)
(overview, root cause) and its [checklist](2026-10-15-anthropic-native-block-replay-checklist.md). Server
plan: Pia `feature/7836`, `docs/plans/2026-10-15-anthropic-native-block-replay.md`.

## 0. Cold start — read this first

| What | Where |
|---|---|
| Issue | https://github.com/Pia-Ai-dev/Pia.Wpf/issues/70 |
| Server work item | Azure DevOps #7836 (Bug); branch `feature/7836` in `G:\Git\Pia`, worktree `G:\Git\Pia-7836` |
| Branch | `feature/anthropic-native-replay` (from `origin/develop` 18e06e3d) |
| Worktree | `G:\Git\Pia.Wpf-anthropic-native-replay` — never switch the main checkout `G:\Git\Pia.Wpf` |
| e2e | stack on port **18081** only; 8081 is the owner's real-data stack — never touch it |

Owner gate decisions, 2026-10-15 (relayed by the coordinator session PIACOORDINATOR):

| Gate | Decision | Client consequence |
|---|---|---|
| G1 store vs. envelope | **A** — server-side store keyed by the `toolu_` id; one server instance per tenant | no wire change on the client |
| G2a across steps of one agent run | **yes** | no client code: carried calls keep their ids (§5) |
| G2b across chat user turns | **deferred** — on the checklist's "not yet planned" list | — |
| G3 seal | moot under A | — |
| G4 `pause_turn` | resume inside the server stream, cap 3 (server S8) | none |
| G5 direct Anthropic provider | **third plan**, including the probe defect: [2026-10-15-anthropic-direct-provider-replay-plan.md](2026-10-15-anthropic-direct-provider-replay-plan.md), issue #71 | D steps, same branch and PR |
| G6 thinking replay | deferred; S6b blocked | none |
| Store TTL | **120 min sliding**; memory bounded by `MaxChars`; park → resume misses accepted | — |

Server facts the client relies on (Pia `feature/7836` @ 44f7653a): ids are the only match key — `tool_use`
input comes from the store, `tool_result` content from the client; at most 8 messages are replayed per
request (`AnthropicNativeReplayMaxMessages`, mirroring `KeptResults`); S5 keeps the **last** occurrence of a
repeated id, which drops the `"Paused:"` placeholder pair — defence in depth only, C2 stays the primary fix;
server S0 case 8 checks whether a duplicate id is a 400 today. Carried messages with `tool_calls` but no text
replay blocks-only, so `AgentToolCarryover` stays as it is.

**e2e key:** the owner set `PIA_TEST_CLAUDE_KEY` in the **User** environment scope. Load it with
`[Environment]::GetEnvironmentVariable('PIA_TEST_CLAUDE_KEY','User')`, never print it; a process started
before it was set (and a `/clear`) does not see it in `$env:`. Provisioning the Anthropic row on the 18081
stack is part of C7.

**What to do first:** C2 — prove or refute the duplicate-id defect on headless resume (§2.2) with a failing
test; it is live on `develop` regardless of this feature. Then C1's pinning tests, then C3.

## 1. Option A in one paragraph

The server keeps each Anthropic response's native blocks (`server_tool_use`, `web_search_tool_result`,
cited text) in an in-process, size-bounded store with a sliding TTL (default 60 min), keyed by the
`toolu_…` id of the client tool calls in that response. When a later request's assistant message carries
that id in `tool_calls`, the server replays the stored blocks instead of reconstructing the message. No wire
field and no new client type; already-shipped clients benefit. A message without a tool call (the final
answer) has no anchor; a miss (TTL, restart) falls back to today's reconstruction.

So the client's whole job is the **id guarantee**: every `tool_calls[].id` goes back verbatim on every path
for as long as the loop lives. Rewritten arguments or results keep their id (§2.1), so the server must match
on the id alone, and native content wins over the client's copy of those blocks.

## 2. Tool call id guarantee (audit 2026-10-15, against `origin/develop`)

### 2.1 Where an id is created, kept, rewritten or dropped

| Site | Id | Arguments / result |
|---|---|---|
| `PiaCloudChatClient.cs:208`, `:222` (stream), `:654` (buffered) | verbatim from the wire | — |
| `PiaCloudChatClient.cs:327` | **synthesized** `pia_call_N`, only when the stream has no id (N per request, repeats across rounds) | — |
| `PiaCloudChatClient.cs:343`, `:654` | **`""`** when the provider sends none | — |
| `PiaCloudChatClient.SerializeMessages` (`:482`, `:505`) | verbatim | args re-serialized from the dictionary (bytes may differ; null → `"{}"`) |
| `AiClientService.cs:701-704`, `:732` (malformed args), `:751` | verbatim | — |
| `TokenizingAiClientService.cs:353` | verbatim | args detokenized on a copy only the handler sees; never sent |
| `AgentToolCarryover.Capture` (`:45`) / `ClearOldResults` (`:121`) | verbatim | results capped at 4000 chars / replaced by a placeholder |
| `AgentToolExchangeSerializer` (`:84`/`:114` → `:148`/`:152`) | saved and restored verbatim, no length limit | args over 1 MiB not saved → `{}`; a malformed call restores as `{}` |
| `HeadlessTurnExecutor.cs:579` (approved-call replay) | stored id; a new Guid only if it is empty | args capped at 400 chars and tokenized (`SeedReplayedCall`, `:635-656`); new result |
| `BackgroundAssistantTurnRunner.cs:712` → `HeadlessTurnExecutor.cs:1020` (parked rows) | verbatim | — |
| `AgentTimelineScope.SanitizeCallId` (`IAgentTimelineService.cs:137`) | timeline only; never sent back | — |
| `AgentContextCompactor` (`AiClientService.cs:285-290`, `:809`) | **dropped** — call and result vanish together | — |
| Store caps 500 rows / 4 M chars (`AgentToolExchangeStore.cs:18`, `:21`) | **dropped** — rounds past the cap are not carried | — |
| `AgentToolCarryover.WithoutToolExchanges` (`:66`) | **dropped** — tool-less turns | — |
| Chat history across user turns (`ViewModels/Models/ChatSession.cs:1036`) | **never sent** — finished turns re-send text only | — |

Result: every Anthropic `toolu_` id goes back verbatim. Ids are invented only when the wire has none
(`pia_call_N`, `""`, Guid) — never on an Anthropic row. Every drop removes call and result together, which
is safe for the server's pair-integrity pass.

### 2.2 Suspected defect: the same id twice after a headless resume

A run that parks for tool approval records the parking round as an ordinary call (`Kind` 1) with its
`"Paused:"` result (`Kind` 2) via `BackgroundAssistantTurnRunner.cs:391-394`. On resume, `ReadCarriedAsync`
(`AgentToolExchangeStore.cs:189-197`, `Kind IN (1, 2)`) puts that pair at the tail
(`HeadlessTurnExecutor.cs:378`), and `ReplayApprovedParkedCallsAsync` (`:554-586`) seeds a second assistant
message with the **same CallId**, capped args and the real result. The guard comment at
`AgentToolExchangeStore.cs:191` covers re-reading the parked row, not this. No test covers it. If it holds,
the request carries two `tool_use` blocks with one id — a 400 on the direct Anthropic provider today, and an
ambiguous key for the server store. Found by reading only; C2 proves or refutes it.

### 2.3 Longest gap between two requests that resend the same ids

| Gap | Bound |
|---|---|
| Provider round-trip | `provider.TimeoutSeconds`, default 300 s (`AiClientService.cs:228`, `:261`) |
| Tool dispatch inside the loop | **unbounded** — the handler gets no token (`AiClientService.cs:745`, remarks `:672-678`) |
| Interactive approval card | **unbounded** — completion source with no timer (`ChatSession.cs:1465-1477`) |
| MCP tool call | **unbounded** — `ct = default` (`McpPluginToolHandler.cs:151`, `:197`) |
| Rounds per loop | ≤ 48 (`IAgentTurnExecutor.cs:18`) |
| Steps of one run (earlier steps' ids re-sent) | wall clock 20 min agent / 45 min scheduled, max 120 min, checked between steps (`RunContext.cs:150`) |
| Park for approval or input, then resume | **unbounded** — days; a parent run waits on its child the same way (`HeadlessRunLauncher.cs:268`) |

No TTL covers every case. **Gap figure:** within one step's loop, minutes (bounded by the user, not the
code). Across the steps of one run, every later step re-sends every carried id (`HeadlessTurnExecutor.cs:738`,
`ChatSession.cs:1043`; `ClearOldResults` keeps ids), so a sliding TTL only has to span the longest single gap
— except where a tool-less step or compaction skips a group for a while, which can approach the run's
120-min ceiling. **Decided: 120 min sliding**; park → resume after longer is an accepted miss.

## 3. Steps

Effort/Value scales as in the checklist. Tests live in `tests/Pia.Wpf.Tests/Services/` unless named.

### C1 — Pin the id guarantee · *Deps:* — · S · High

Tests only; no production change expected. Each fails if a path renames, regenerates or drops an id it
should keep.

- `PiaCloudChatClientToolCallTests.cs`: a `toolu_…` id from stream and from buffered JSON survives
  `ToChatResponse()` and `SerializeMessages` byte-for-byte in round 2's request.
- New `AiClientServiceToolCallIdTests.cs` on the fake-client pattern of `AiClientServiceToolLoopArmTests.cs`:
  a three-round loop with parallel calls — every request repeats every earlier id in order; again with
  tokenization on (`TokenizedToolPipelineTests.cs` pattern) and with in-step compaction
  (`AiClientServiceInStepCompactionTests.cs` pattern: an evicted pair leaves no orphan id).
- `AgentToolCarryoverTests.cs`: `Capture` and `ClearOldResults` keep ids.
- `AgentToolExchangeSerializerTests.cs` / `AgentToolExchangeStoreTests.cs`: save → restore keeps ids and the
  parallel-call grouping; an over-cap args row keeps its id.
- `HeadlessTurnExecutorTests.cs`: a resumed run's first request carries the earlier steps' ids verbatim.

### C2 — Duplicate id after a headless resume · *Deps:* — · S · High

- Failing test first, in `UnattendedApprovalParkTests.cs` or `HeadlessTurnExecutorTests.cs`: park a run on
  an approval, approve, resume; assert no request holds two `FunctionCallContent`s with one `CallId`.
- If it fails: drop the carried `"Paused:"` pair for every call `ReplayApprovedParkedCallsAsync` re-seeds
  (filter `carried.Trailing` by the replayed ids before `HeadlessTurnExecutor.cs:378` adds it), so the seeded
  call with its real result is the only copy. Withheld calls (`Kind` 4) the same way.
- If it passes: the test stays as the guard; record the reason in its name.

### C3 — Measure what the client cannot see · *Deps:* — · XS · Med

Replayed search results are input the client never sees, so `AgentContextCompactor` under-counts the real
request (it also counts nothing it does not know). No client estimate is possible. One `LogInformation`
per round, counts only, comparing the compactor's estimate with the provider-reported input tokens, so S0
and C7 can measure the gap before anyone builds a correction. If it overflows windows in practice, the
server reports the replayed size rather than the client guessing.

### C4 — (folded into C1 under A)

The chat-client tolerance step exists only under B; see B-C4.

## 4. Release text and gate

### C6 — Release-note text · *Deps:* C2 · XS · Med

`RELEASE.md` is edited only on `release/*` / `hotfix/*` (develop's `.claude/skills/release-flow/SKILL.md`),
so this step drafts the text here and the release cut applies it. Under A the search fix ships with the
server, so the client note covers C2 only — and only if C2 found the defect:

> - Agent runs resumed after a tool approval no longer send the approved
>   call twice, which some providers rejected.

The search fix belongs in the server's release notes; if the client release goes out together, add:

> - With Claude and native web search on Pia Cloud, answers no longer retract
>   or repeat searches they already made after a tool call.

### C7 — Client gate · *Deps:* C1–C3, C6, S9 · XS · High

- `dotnet build -t:Rebuild -v:n` in Debug and Release: `0 Warning(s)`, `0 Error(s)`.
- `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1` →
  `Failed: 0` (redirect, never a pipe; `dotnet test` is not the gate).
- e2e on 18081 with a server build carrying S1–S7: the MongoDB checkup routine on an Anthropic row with
  native WEB on → no retraction narration, no repeated search after a client tool call. Repeat the
  "Detailrecherche pro Security-Layer" agent run for the step-boundary case once G2a ships. Record input
  token totals against the Linkup run.

## 5. G2 — decided: G2a yes, G2b deferred

**G2a — across steps of one agent run: yes.** Under A it needs no client code:
`AgentToolCarryover.Capture` already carries each round's calls and results with their ids into the next
step, so a store hit replays the native blocks at the step boundary too, while the TTL holds (§2.3). Open on
the server: `Capture` drops a round's text on purpose, so a carried message has `tool_calls` but no
`content`; replaying the stored text there brings back interim prose next to the step's visible reply. The
server is defining that rule; the client needs nothing either way. Under "no", the step boundary keeps
today's behaviour, and the reported 36-minute loop is still fixed — it ran inside one step.

**G2b — across chat user turns: deferred.** Under A there is no anchor: a finished turn is re-sent as
text only (`ViewModels/Models/ChatSession.cs:1036`, `Models/AssistantMessage.cs:386-410`), so no earlier
turn's ids reach the server, and the final answer never has one. Making it work means persisting and
re-sending each turn's tool exchanges in chat history — a new step, not planned here.

## 6. G5 — decided: a third plan

Planned in [2026-10-15-anthropic-direct-provider-replay-plan.md](2026-10-15-anthropic-direct-provider-replay-plan.md) (issue #71). The evidence that led there:

The direct Anthropic provider loses the same evidence, by a different mechanism (decompiled `Anthropic`
12.48.0, `AnthropicClientExtensions.AnthropicChatClient`):

- Web search is enabled by `Services/Providers/Http/AnthropicRequestHandler.cs:58-68` (appends
  `web_search_20250305`, `max_uses` 5) when `provider.UsesWebSearch` (`AnthropicProviderHandler.cs:33`).
- Response → MEAI keeps everything: `server_tool_use` → `WebSearchToolCallContent`,
  `web_search_tool_result` → `WebSearchToolResultContent` (raw block incl. encrypted content), citations
  as `CitationAnnotation`, thinking → `TextReasoningContent` with the signature in `ProtectedData`.
- Pia's loop keeps those contents between rounds (`AiClientService.cs:371`, `:701-704`).
- MEAI → request drops them: `CreateMessageParams` passes a block through only when
  `RawRepresentation is ContentBlockParam`, and the response-side `ServerToolUseBlock` /
  `WebSearchToolResultBlock` are not; both web-search contents fall to `continue`; text is rebuilt without
  citations. Thinking with signature **is** rebuilt correctly.

So the direct path reproduces "claims without evidence" in round 2, with thinking intact. Fix direction for
the third plan: a client-side `DelegatingChatClient` (or a pre-round pass) that converts the two raw
blocks to `ServerToolUseBlockParam` / `WebSearchToolResultBlockParam` and re-attaches citations — no server
involvement. Reproduce the evidence with `ilspycmd` 9.1 over `~/.nuget/packages/anthropic/12.48.0/lib/net9.0`, nested
class `AnthropicChatClient` in `AnthropicClientExtensions` (`ContentBlockValueToAIContent`,
`CreateMessageParams`). Unverified: whether `ToChatResponse` coalesces the streamed signature-only
`TextReasoningContent` into the preceding thinking correctly.

Side finding, same provider: `AiClientService.cs:1364` sets `ChatToolMode.RequireAny` on the tool-call
probe (`TestToolCallEmittedAsync`, from `ProviderCapabilityService.cs:49`, reached from
`ChatSessionManager.cs:865` and `AssistantViewModel.cs:2509`). With an effort set the handler adds adaptive
thinking (`ReasoningEffortMapping.cs:46-57`); forced tool choice plus thinking is a 400, the probe returns
`Unknown`, `Unknown` is never cached (`ProviderCapabilityService.cs:55-63`) — so a paid probe runs again
on every turn and the provider is never marked capable. Belongs in the third plan or its own issue.

## 7. Not in this plan

- Source chips from `web_citations` — a separate small client change.
- Id-less providers: `pia_call_N` repeats across rounds of one loop (`PiaCloudChatClient.cs:327`). Harmless
  on Anthropic rows; worth a look if a server-side store ever serves an id-less provider.

## 8. Open questions

None. The direct-provider mechanism is decided (D-store), see
[2026-10-15-anthropic-direct-provider-replay-plan.md](2026-10-15-anthropic-direct-provider-replay-plan.md) §3.

## Appendix B — client steps if Option B is ever chosen

Not current. Kept because G1 could be reopened (e.g. multi-instance deployments).

### B.1 Contract v1 (as frozen by the server for Option B)

- Field `pia_native`, an opaque string; never parsed, logged or modified by the client.
- Stream: one extra SSE line per upstream response, after the finish chunk, before `data: [DONE]`, with
  `"choices":[]` and `"pia_native":"<sealed>"`; never an `error` key. Accepted anywhere before `[DONE]`,
  including after a `knowledge_citations` chunk. Last one wins; at most one per assistant message.
- Buffered: `choices[0].message.pia_native` (string).
- Request: on the **assistant** message object, beside `content` / `tool_calls`, echoed verbatim. Nothing
  when the client rewrites that message (compaction, carry-over rebuild, edit). Never on another role.
- Emitted only for a `server_tool_use` (also a pending one), a `web_search_tool_result` or cited text. Over
  the server's cap → none. Unseal failure → silent fallback, never a 400.
- Estimated ~130–330 KB per 5-search round; a carried loop re-uploads every earlier envelope and nears
  `KestrelMaxRequestBodyMb` = 5 → 413, hence B-C2's newest-N rule.

### B.2 Research results (2026-10-15, against `origin/develop`)

#### B.2.1 Microsoft.Extensions.AI 10.9.0 keeps an unknown `AIContent`

Scratch spike (not on the branch) plus decompiled `ChatResponseExtensions`:

- `ToChatResponse()` starts a new message only when `AuthorName`, `MessageId` or `Role` is set on **both**
  sides and differs. `PiaCloudChatClient` sets `Role = Assistant` on every update and never sets
  `MessageId`, so the envelope update joins the one assistant message — also when it trails the
  `FinishReason` and `UsageContent` updates. A different `Role` or non-empty `MessageId` on the envelope
  update would split it into its own message.
- `CoalesceContent` merges only built-in types; a custom subtype is never merged or dropped.
- `AiClientService.cs:371` (`ToChatResponse`) and `:701-704` (`workingMessages.Add`) carry it into the next
  round unchanged. `FunctionInvokingChatClient` keeps it too (not used by Pia).
- **JSON:** `AIContent` is a closed `[JsonPolymorphic]` hierarchy. Serializing a `ChatMessage` holding the
  subtype throws `NotSupportedException` unless `AIJsonUtilities.AddAIContentType<PiaNativeContent>` is
  registered. Nothing in `src/` serializes `ChatMessage` today; B-C4 adds a test that keeps it that way or
  registers the type.
- Do **not** carry the envelope in `AdditionalProperties`: without a `MessageId` those land on
  `response.AdditionalProperties`, not on the message.

#### B.2.2 Microsoft.Agents.AI 1.19.0 compaction

`AgentContextCompactor` (`ContextWindowCompactionStrategy(window, maxOut, 0.45, 0.70)`), run each round at
`AiClientService.cs:288`:

- An assistant message **without** tool calls survives as the same instance — envelope intact.
- An assistant message **with** `FunctionCallContent` is grouped with its results; tool-result eviction
  replaces the whole group with a new text-only summary. Envelope and `tool_use` vanish together, so the
  server's id check never sees a mismatch. Acceptable.
- **Size estimate:** `CompactionMessageIndex.ComputeContentByteCount` is a type switch; an unknown subtype
  counts **0 bytes**. Replayed search results are input tokens the budget does not see → B-C3 adds a charge.

### B.3 Steps

### B-C1 — Parse the envelope · *Deps:* contract v1 · XS · Enabler

- New `src/Pia.Wpf/Models/PiaNativeContent.cs`: `public sealed class PiaNativeContent(string sealedValue) :
  AIContent` with `SealedValue`; `ToString()` returns a length-only marker so a stray log line cannot leak
  it. No `<summary>` beyond one line (CLAUDE.md).
- `PiaCloudChatClient.GetStreamingResponseAsync`: read `json["pia_native"]` after the `error` check
  (`:162-163`) and before the `choices` early-continue (`:165-166`). Buffer it like `pendingUsage`
  (`:132`, `:147-148`) and yield it **once after the loop**, next to the usage flush (`:272-279`), as
  `new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new PiaNativeContent(v)] }` — no
  `MessageId`. A second envelope in one stream replaces the first and logs a warning (count only).
- `ParseMessageObject` (`:632-664`): read `message["pia_native"]`, append `PiaNativeContent` last.
- Logging: never the value; `SensitiveDebug` of its length at most.
- Tests — new `PiaCloudChatClientNativeContentTests.cs`, using the stub-handler pattern of
  `PiaCloudChatClientReasoningTests.cs`: stream with envelope after finish + usage → exactly one assistant
  message, envelope last, usage intact; stream without envelope → no `PiaNativeContent`; envelope with
  tool calls → same message as the `FunctionCallContent`s; buffered response with `message.pia_native`;
  malformed (non-string) `pia_native` ignored, no throw.

### B-C2 — Send it back · *Deps:* B-C1 · XS · High

- `SerializeMessages` (`:455-559`): in the assistant branch, `case PiaNativeContent n:` remembers the last
  one; after `content` / `tool_calls` are set (`:542-553`), write `msgObj["pia_native"] = n.SealedValue`.
  User, system and tool messages never carry it, even if one slipped in.
- Request-size bound: only the newest N envelopes of one request are written (N from S0's measured size,
  N × size well under 5 MB; start at 4); older ones are omitted, which is the server's fallback. Test: a
  10-round history writes exactly N, newest last.
- Tests (same file): serialize round-trip — assistant message with text + calls + envelope → JSON has
  `pia_native` beside `content` / `tool_calls`; no envelope → no key; two envelopes → last wins.
- Loop test, new `AiClientServiceNativeContentTests.cs` on the fake-client pattern of
  `AiClientServiceToolLoopArmTests.cs`: a two-round tool loop where round 1's stream carries an envelope
  → round 2's request JSON (captured by the stub handler) holds it on the round-1 assistant message. This
  is the test that proves the reported bug's within-step loop is fixed end to end on the client.

### B-C3 — Hand-built and rewriting paths · *Deps:* B-C2, G2a for rows marked † · M · High

Each site carries the envelope or drops it on purpose; each gets a test pinning which.

| Site | Today | Plan |
|---|---|---|
| `TokenizingAiClientService.cs:136-141` (buffered detokenize) | `Contents.Clear()` + one `TextContent` — drops every non-text content | Rebuild text only, keep non-text contents, **drop** `PiaNativeContent` (text changed). No tool loop follows this path, so dropping costs nothing; the fix is for `FunctionCallContent`'s sake and the invariant |
| `TokenizingAiClientService.TokenizeMessages` (`:277-294`) | Keeps non-text, re-tokenizes text | **Keep.** The envelope holds the model's own tokenized output and `workingMessages` (inside the inner service) holds tokenized text, so the two agree. Test: tokenized request still carries it |
| `AgentContextCompactor.CompactAsync` (`:84`) | Library counts the envelope as 0 bytes | Charge envelope size against the budget the way `ImageTokenCharge` (`:72`) charges images: a per-envelope estimate from the size hint (§8) or `SealedValue.Length` as a fallback bound. Test in `AgentContextCompactorTests.cs`: an over-budget list caused only by envelopes compacts |
| `AgentToolCarryover.Capture` (`:34-57`) † | Keeps `FunctionCallContent` / `FunctionResultContent` only | **G2a yes:** also keep `PiaNativeContent` on the message that holds the calls, capped (drop when `SealedValue.Length` > a cap set from G1's measurement). **G2a no:** drop, as today. Tests in `AgentToolCarryoverTests.cs` |
| `AgentToolCarryover.ClearOldResults` (`:79-127`) | Rewrites results only; keeps call messages | Keep — the call message is copied as-is. Test that it survives |
| `AgentToolCarryover.WithoutToolExchanges` (`:66-74`) | Drops tool-content messages | Envelope goes with them — correct for a tool-less turn |
| `AgentToolExchangeSerializer.ToRows` (`:29`) / `AgentToolExchangeStore` † | Persists call/result rows only (`MaxRowChars` 1 MiB) | **G2a yes:** a new exchange `Kind` (append-only enum `AgentToolExchangeKind`, `Models/AgentToolExchange.cs:7`) for the envelope row, anchored to the round; idempotent column add per `SqliteContext.cs:893-942` only if `ResultText` cannot hold it. Counts against `MaxCharsPerRun` (4 M). Read back in `HeadlessTurnExecutor.ReadCarriedAsync` (`:386-438`). **G2a no:** nothing |
| `HeadlessTurnExecutor.SeedReplayedCall` (`:637`) | Synthetic call + result | **Drop on purpose** — a re-run replay has no native response. Comment-free; the test says it |
| `HeadlessTurnExecutor` step reply (`:938-941`) | Adds carried exchanges, then `exchange.Visible` as a text-only assistant message | `Visible` stays text-only. Under G2a the envelope rides on the carried call message (row above), not here |
| `HeadlessTurnExecutor` history rebuild (`:337-361`) / `BackgroundAssistantTurnRunner` (`:235`, `:767`) | Rebuild from `SyncAssistantChatMessage` text | No change unless G2b opens (C5) |
| `BackgroundAssistantTurnRunner` round capture (`:391-394`) | `toolExchanges.AddRange(round.Messages)` + `RecordAsync` | Inherits whatever `Capture` returns; one test that the envelope reaches `RecordAsync` under G2a |
| `AdvancedCreationService.cs:109` | Text-only assistant message | Not a tool loop; no change |

Why G2a matters (§5): the reported agent step ran inside one step's tool loop, which C2 fixes. But a step
boundary recreates the bug once per step: `:938` carries the calls without their search evidence and
`:941` adds prose that cites it.

Known cost under G2a: the envelope also holds the round's interim prose, which `Capture` drops today on
purpose ("would read as the model having said it twice"). Replaying the envelope brings that prose back
next to `Visible`. Accepted unless the server can replay the server-tool pairs without the text — open
question §8.

### B-C4 — Other chat clients tolerate it · *Deps:* B-C1; must land before B-C3's † rows · XS · Enabler

Under G2a this is a hard prerequisite, not a nicety: an agent run resolves provider and persona **per
step** (`HeadlessTurnExecutor.cs:915-920`, `:763`), so step N's carried call message — envelope included —
goes into step N+1's request on whatever `IChatClient` that step resolved, including the direct OpenAI,
Mistral and Anthropic adapters. Without G2a it reaches another client only through a chat whose provider
changes between turns (G2b). A different Pia Cloud *model* at step N+1 is the server's concern: its model
check (overview §5.2, condition b) falls back to reconstruction.

- New `NativeContentForeignClientTests.cs`: for each handler in `src/Pia.Wpf/Services/Providers/`
  (OpenAI, Azure OpenAI, OpenAI-compatible, OpenRouter, vLLM, Mistral, Ollama, Anthropic), build the
  `IChatClient` against a capturing `HttpMessageHandler`, send a history holding a `PiaNativeContent` →
  no throw, and the request body contains neither the sealed value nor a `pia_native` key.
- JSON: nothing on `develop` serializes a `ChatMessage`, so no registration now. If a later change adds
  one (cache, OTel with sensitive data, persistence), it registers
  `AIJsonUtilities.AddAIContentType<PiaNativeContent>` on its options; a test there round-trips the type.

