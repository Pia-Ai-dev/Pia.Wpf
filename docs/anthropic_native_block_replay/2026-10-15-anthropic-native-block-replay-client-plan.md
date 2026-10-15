# Anthropic native block replay — client plan (Pia.Wpf, steps C1–C7)

**Status:** Planned — not started; wire contract pending from the server session; gates G1–G6 open
**Owner:** Marco Altmann
**Written:** 2026-10-15
**Origin:** [2026-10-15-anthropic-native-block-replay-plan.md](2026-10-15-anthropic-native-block-replay-plan.md)
(overview, root cause, server half) and its
[checklist](2026-10-15-anthropic-native-block-replay-checklist.md).

## 0. Cold start — read this first

| What | Where |
|---|---|
| Issue | https://github.com/Pia-Ai-dev/Pia.Wpf/issues/70 |
| Branch | `feature/anthropic-native-replay` (from `origin/develop` 18e06e3d) |
| Worktree | `G:\Git\Pia.Wpf-anthropic-native-replay` — never switch the main checkout `G:\Git\Pia.Wpf` |
| Server half | `G:\Git\Pia`, steps S0–S9; owns the wire contract (§2) |
| e2e | stack on port **18081** only; 8081 is the owner's real-data stack — never touch it |

Gate state at writing (decisions are the owner's, relayed by the coordinator session):

| Gate | State | Client recommendation |
|---|---|---|
| G1 envelope size / store | open | — (server measures in S0); the client needs the number for the carry-over cap (§4.3) |
| G2 across turns | open | **split it**: G2a cross-step inside an agent run → yes, bounded; G2b across chat user turns → defer (§5) |
| G3 seal | open | — (server) |
| G4 `pause_turn` | open | — (server); a distinct finish reason would need a C1 mapping line |
| G5 direct Anthropic provider | open | **yes, same flaw** → third plan (§6) |
| G6 thinking replay | open | — (server); no client change either way: the envelope is opaque |

**What to do first:** check whether the coordinator has relayed the final wire contract (§2 lists the
assumptions to replace). Then C1 → C2 → C4 test-first; they are independent of every open gate. C3
follows; its carry-over rows wait on G2a.

## 1. What the client must do

Pia Cloud will send one opaque sealed string per assistant response. The client must keep it attached to
the assistant message that produced it, through every list that message travels in, and send it back on
that message. It never opens, logs or edits it. Any site that rewrites an assistant message's **text or
tool calls** must drop it: the server replays the envelope's blocks *instead of* the message's content, so
an envelope on an edited message would silently undo the edit.

Invariant for every C-step: **an envelope rides only on the unmodified assistant message it arrived on.**

## 2. Wire contract — assumptions to confirm

Owned by the server (overview §5.1). Until the coordinator relays the final shape, the client plan assumes:

1. Stream: one choiceless chunk `data: {"pia_native":"<sealed>"}` after the last content chunk, before
   `data: [DONE]`. May arrive after the `finish_reason` chunk and after the usage chunk.
2. Buffered: `choices[0].message.pia_native` (string).
3. Request: the assistant message object carries `"pia_native": "<sealed>"` beside `content` / `tool_calls`.
4. At most one envelope per response. Absent when reconstruction suffices.
5. Envelope `tool_use` ids are the Anthropic-issued ids the client sees in `tool_calls`. (The client
   synthesizes `pia_call_N` only for a provider that omits ids — `PiaCloudChatClient.cs:327` — which
   Anthropic never does.)

Asked of the server, see §8: a plain-text size hint next to the sealed value (for compaction), and whether
a G4 "paused" finish reason reaches the client.

## 3. Research results (2026-10-15, against `origin/develop`)

### 3.1 Microsoft.Extensions.AI 10.9.0 keeps an unknown `AIContent`

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
  registered. Nothing in `src/` serializes `ChatMessage` today; C4 adds a test that keeps it that way or
  registers the type.
- Do **not** carry the envelope in `AdditionalProperties`: without a `MessageId` those land on
  `response.AdditionalProperties`, not on the message.

### 3.2 Microsoft.Agents.AI 1.19.0 compaction

`AgentContextCompactor` (`ContextWindowCompactionStrategy(window, maxOut, 0.45, 0.70)`), run each round at
`AiClientService.cs:288`:

- An assistant message **without** tool calls survives as the same instance — envelope intact.
- An assistant message **with** `FunctionCallContent` is grouped with its results; tool-result eviction
  replaces the whole group with a new text-only summary. Envelope and `tool_use` vanish together, so the
  server's id check never sees a mismatch. Acceptable.
- **Size estimate:** `CompactionMessageIndex.ComputeContentByteCount` is a type switch; an unknown subtype
  counts **0 bytes**. Replayed search results are input tokens the budget does not see → C3 adds a charge.

## 4. Steps

Effort/Value scales as in the checklist. Tests live in `tests/Pia.Wpf.Tests/Services/` unless named.

### C1 — Parse the envelope · *Deps:* S4 contract · XS · Enabler

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

### C2 — Send it back · *Deps:* C1 · XS · High

- `SerializeMessages` (`:455-559`): in the assistant branch, `case PiaNativeContent n:` remembers the last
  one; after `content` / `tool_calls` are set (`:542-553`), write `msgObj["pia_native"] = n.SealedValue`.
  User, system and tool messages never carry it, even if one slipped in.
- Tests (same file): serialize round-trip — assistant message with text + calls + envelope → JSON has
  `pia_native` beside `content` / `tool_calls`; no envelope → no key; two envelopes → last wins.
- Loop test, new `AiClientServiceNativeContentTests.cs` on the fake-client pattern of
  `AiClientServiceToolLoopArmTests.cs`: a two-round tool loop where round 1's stream carries an envelope
  → round 2's request JSON (captured by the stub handler) holds it on the round-1 assistant message. This
  is the test that proves the reported bug's within-step loop is fixed end to end on the client.

### C3 — Hand-built and rewriting paths · *Deps:* C2, G2a for rows marked † · M · High

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

### C4 — Other chat clients tolerate it · *Deps:* C1 · XS · Enabler

The envelope only reaches another client if a history built on Pia Cloud is sent elsewhere — today only
through a chat whose provider is switched between turns, which needs G2b. Still pinned now:

- New `NativeContentForeignClientTests.cs`: for each handler in `src/Pia.Wpf/Services/Providers/`
  (OpenAI, Azure OpenAI, OpenAI-compatible, OpenRouter, vLLM, Mistral, Ollama, Anthropic), build the
  `IChatClient` against a capturing `HttpMessageHandler`, send a history holding a `PiaNativeContent` →
  no throw, and the request body contains neither the sealed value nor a `pia_native` key.
- JSON: nothing on `develop` serializes a `ChatMessage`, so no registration now. If a later change adds
  one (cache, OTel with sensitive data, persistence), it registers
  `AIJsonUtilities.AddAIContentType<PiaNativeContent>` on its options; a test there round-trips the type.

### C5 — Across chat user turns (G2b) · *Deps:* G2b, C3 · M · Med

Planned only to price it; recommended **deferred** (§5). If G2b opens:

- `AssistantMessage` gains an in-memory-plus-persisted `NativeEnvelope` string; `BuildChatMessage`
  (`Models/AssistantMessage.cs:386-410`) appends `PiaNativeContent` when set.
- SQLite: `AssistantChatMessages.NativeEnvelope TEXT` via the idempotent `PRAGMA table_info` / `ALTER`
  pattern (`Infrastructure/SqliteContext.cs:893-942`) and in the `CREATE TABLE` (`:430-447`); write path
  `AssistantChatService.cs:257-285`, read path `:1106-1150`.
- Mapping: `ViewModels/AssistantMessageMapper.cs:14-52`; DTO `src/Pia.Shared/Models/SyncAssistantChat.cs`
  new property. The server needs no change — its `AssistantChatDto` round-trips unknown fields through
  `[JsonExtensionData]`, and under E2EE the whole chat is one AES-GCM blob (`SyncMapper.cs:1099-1146`).
  The server repo sees the DTO through its `lib/Pia.Wpf` submodule, so a submodule bump follows.
- Rebuild sites: `ViewModels/Models/ChatSession.cs:414-443` (and `:1045`),
  `ViewModels/AssistantViewModel.cs:2700`, `HeadlessTurnExecutor.cs:337-361`,
  `BackgroundAssistantTurnRunner.cs:235` / `:767`.
- Edit / regenerate: an edited or regenerated assistant message drops its envelope (invariant §1).
- Size: every save re-upserts the whole chat (`AssistantChatSyncService.cs:285-312`) against the server's
  5 MB `MaxBodyBytes` with no chunking; +33 % under E2EE base64. Needs a per-chat envelope budget that
  evicts the oldest envelopes first.

### C6 — Release-note text · *Deps:* C2 · XS · Med

`RELEASE.md` is edited only on `release/*` / `hotfix/*` (develop's `.claude/skills/release-flow/SKILL.md`),
so this step drafts the text here and the release cut applies it:

> - Pia Cloud: with Claude and native web search, answers no longer retract or
>   repeat searches they already made after a tool call. Pia now keeps
>   Claude's search results in the conversation it sends back.

Adjust once G2a/G2b are decided (add "across agent-run steps" if G2a ships).

### C7 — Client gate · *Deps:* C1–C4, C6, S9 · XS · High

- `dotnet build -t:Rebuild -v:n` in Debug and Release: `0 Warning(s)`, `0 Error(s)`.
- `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1` →
  `Failed: 0` (redirect, never a pipe; `dotnet test` is not the gate).
- e2e on 18081 with a server build carrying S1–S7: the MongoDB checkup routine on an Anthropic row with
  native WEB on → no retraction narration, no repeated search after a client tool call. Repeat the
  "Detailrecherche pro Security-Layer" agent run for the step-boundary case once G2a ships. Record input
  token totals against the Linkup run.

## 5. G2 — recommendation: split it

**G2a — across steps of one agent run: recommend yes, bounded.** Client-only: `AgentToolCarryover.Capture`
plus one exchange row kind; the data is local, never synced (`SqliteContext.cs:604`), no E2EE touch, no
server change. It closes the bug's shape at step boundaries (§4 C3). Cost: replayed search results are
re-billed per step (prompt cache softens it), and the cap needs G1's measured size. Under "no", C3's †
rows drop the envelope and the step boundary keeps today's behaviour.

**G2b — across chat user turns: recommend defer.** Cheap locally (one column), free on the server DTO,
but: the whole chat re-uploads on every save against a 5 MB per-chat cap with no chunking; the envelope
is sealed with a server key that rotates, so an envelope replayed days later often falls back anyway;
and every later turn re-bills the search results. Under "yes", C5 as written, plus the per-chat envelope
budget.

## 6. G5 — recommendation: yes, a third plan

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
involvement. Unverified: whether `ToChatResponse` coalesces the streamed signature-only
`TextReasoningContent` into the preceding thinking correctly.

Side finding, same provider: `AiClientService.cs:1364` sets `ChatToolMode.RequireAny` on the tool-call
probe (`TestToolCallEmittedAsync`, from `ProviderCapabilityService.cs:49`, reached from
`ChatSessionManager.cs:865` and `AssistantViewModel.cs:2509`). With an effort set the handler adds adaptive
thinking (`ReasoningEffortMapping.cs:46-57`); forced tool choice plus thinking is a 400, the probe returns
`Unknown`, `Unknown` is never cached (`ProviderCapabilityService.cs:55-63`) — so a paid probe runs again
on every turn and the provider is never marked capable. Belongs in the third plan or its own issue.

## 7. Not in this plan

- Source chips from `web_citations` — cheap once C1 reads choiceless chunks.
- Rendering anything from the envelope; it stays opaque.

## 8. Open questions (to the coordinator)

1. Final wire contract — field name, chunk position, buffered location (§2).
2. A plain size hint beside the sealed value (e.g. `pia_native_tokens` or raw byte count) for the
   compactor charge — or the client uses `SealedValue.Length` as a bound.
3. G2a interim-prose duplication: can the server replay server-tool pairs and `tool_use` without the
   envelope's text blocks when asked, or is the duplication accepted?
4. G4: if the server hands the client a distinct finish reason, its name, so C1 maps it.
5. Who provisions an Anthropic row with native WEB on for the 18081 e2e stack (C7).
