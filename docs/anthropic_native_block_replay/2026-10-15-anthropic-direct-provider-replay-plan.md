# Direct Anthropic provider: replay web-search blocks and fix the tool-call probe (steps D0–D7)

**Status:** Planned — mechanism decided (D-store)
**Owner:** Marco Altmann
**Written:** 2026-10-15
**Origin:** Gate G5 of [2026-10-15-anthropic-native-block-replay-plan.md](2026-10-15-anthropic-native-block-replay-plan.md);
owner decision 2026-10-15: one plan for the replay flaw and the probe defect. Tracked in
[2026-10-15-anthropic-native-block-replay-checklist.md](2026-10-15-anthropic-native-block-replay-checklist.md) (group D).

## 0. Cold start — read this first

| What | Where |
|---|---|
| Issue | https://github.com/Pia-Ai-dev/Pia.Wpf/issues/71 (related: #70 Pia Cloud client side, Azure DevOps #7836 server) |
| Branch | `feature/anthropic-native-replay` — same branch and one client PR with #70's C steps |
| Worktree | `G:\Git\Pia.Wpf-anthropic-native-replay` — never switch the main checkout `G:\Git\Pia.Wpf` |
| Sibling plans | [client plan](2026-10-15-anthropic-native-block-replay-client-plan.md) (C steps), server plan in Pia `feature/7836` `docs/plans/2026-10-15-anthropic-native-block-replay.md` |
| Real key for e2e | `[Environment]::GetEnvironmentVariable('PIA_TEST_CLAUDE_KEY','User')` — never print it; a process started before the owner set it does not see it in `$env:` |

**What to do first:** D0 (spike, scratchpad only), then the probe fix D5 + D6 — small, independent, and live
on `develop`. Then D1 → D2 → D3 → D4.

## 1. The two defects

### 1.1 Web-search evidence dropped on replay

Evidence: decompiled `Anthropic` 12.48.0 (`ilspycmd` 9.1 over `~/.nuget/packages/anthropic/12.48.0/lib/net9.0`,
nested class `AnthropicChatClient` in `AnthropicClientExtensions`).

- Web search is switched on by `Services/Providers/Http/AnthropicRequestHandler.cs:58-68`, which appends
  `web_search_20250305` (`max_uses` 5) when `provider.UsesWebSearch` (`AnthropicProviderHandler.cs:33`).
- **Response → MEAI keeps everything** (`ContentBlockValueToAIContent` and the streaming state machine):
  `server_tool_use` → `WebSearchToolCallContent`, `web_search_tool_result` → `WebSearchToolResultContent`
  (raw block incl. `encrypted_content` in `RawRepresentation`), citations → `CitationAnnotation` (streamed as
  separate empty `TextContent`s), thinking → `TextReasoningContent` with the signature in `ProtectedData`.
- Pia's loop keeps those contents between rounds (`AiClientService.cs:371`, `:701-704`).
- **MEAI → request drops them** (`CreateMessageParams`): a block is passed through only when
  `RawRepresentation is ContentBlockParam`; the response-side `ServerToolUseBlock` /
  `WebSearchToolResultBlock` are not, so both web-search contents fall to `continue`. Text is rebuilt as a
  bare `TextBlockParam` — citations gone. Thinking is rebuilt as `ThinkingBlockParam{Thinking, Signature}`
  — **intact; keep it that way.**

Same symptom as #70: from round 2 on, the model sees itself citing searches that are not in the history.

### 1.2 Tool-call probe 400s under adaptive thinking, and re-runs every turn

- `AiClientService.TestToolCallEmittedAsync` (`:1340-1380`) sets `ChatToolMode.RequireAny` (`:1364`); the
  adapter maps it to `tool_choice: {type: "any"}`.
- With a reasoning effort set, `AnthropicRequestHandler.Rewrite` adds `thinking: {type: "adaptive"}`
  (`:76-82`, `ReasoningEffortMapping.cs:46-57`). Forced tool choice plus thinking is a 400.
- `ProviderCapabilityService.GetPlanningCapabilityAsync` (`:25-75`) turns the exception into `Unknown`, and
  `Unknown` is never cached (`:55-63`). Callers: `ViewModels/Models/ChatSessionManager.cs:865` (every
  non-planned turn) and `ViewModels/AssistantViewModel.cs:2509`. So every turn pays for a failed probe and
  the provider is never marked capable.

## 2. Constraints

- **Thinking binding.** The direct path already replays thinking blocks (SDK rebuilds them). A replayed
  thinking block is valid only while every earlier message is byte-identical to when it was produced; for
  accounts created on or after 2026-08-31 a mismatch is a 400 by default (overview §4). So any replay must
  be **deterministic across the requests of one loop**: once a message is replayed one way, every later
  request must replay it the same way. A store miss mid-loop (eviction), or flipping between verbatim and
  blocks-only, invalidates every later thinking block.
- **Pair integrity.** `server_tool_use` and its `web_search_tool_result` go together, unmodified, in order;
  a lone one is a 400. `encrypted_content` / `encrypted_index` replay verbatim.
- **Prompt cache.** `AnthropicRequestHandler` keeps thinking config per provider row, never per turn, because
  a change invalidates the cache (`:70-71`). A per-request thinking change must stay scoped to the probe.
- **Streaming UX.** Response parsing must not buffer the stream the SDK reads.

## 3. Mechanism — D-store (owner decision 2026-10-15)

**D-store:** mirror the server's Option A inside `AnthropicRequestHandler`, which is already
the `DelegatingHandler` that rewrites every request body. Tee each response (SSE or buffered), accumulate the
native content blocks, store them keyed by the response's client `tool_use` ids, and on later requests
rewrite each assistant message whose `tool_use` ids hit the store, with **the server's replay rule**:

- whitespace-normalized text of the message equals the stored text blocks → replay the stored content
  verbatim (citations, search pairs, and the original thinking blocks — byte-identical to the original);
- different or empty (the `AgentToolCarryover` case) → blocks-only: the message's own thinking blocks
  first (unchanged), then its own text as one block if non-empty, then the stored search pairs and the
  `tool_use` blocks the request still holds, in stored order. Stored text blocks are dropped.

Why: `AgentToolCarryover.Capture` (`:45`) keeps only `FunctionCallContent` / `FunctionResultContent`, so
carried calls reach the next step with their ids. Under D-store they hit the store exactly as they hit the
server's — **G2a works on the direct path with no change to `Capture`**, matching the owner's G2a decision
and the server's "replay is keyed, not scoped". It also depends on the Messages API wire shape, not on the
SDK's content model, so an SDK bump cannot silently break it.

Cost: a second implementation of the server's S2 accumulator and replay rule (in this repo, C#), and a
process-wide store with eviction.

**Considered, not chosen — D-content:** a `DelegatingChatClient` converting the SDK's response-side
web-search blocks to `*Param` blocks before each call. Smaller and deterministic within a step, but G2a
would not reach the direct provider: `Capture` drops `WebSearch*` contents at the step boundary, and keeping
them would hand them to whatever provider the next step resolves.

## 4. Steps

Effort/Value scales as in the checklist. Tests in `tests/Pia.Wpf.Tests/Services/Providers/` unless named.

### D0 — Spike and fixture · *Deps:* — · XS · Enabler

Scratchpad only, never the branch. Regenerate the decompile (recipe in §1.1) and settle:

1. Does `ToChatResponse()` merge the streamed signature-only `TextReasoningContent` into the preceding
   thinking, so the SDK rebuilds signed thinking blocks on the streaming path? (D3's tests pin the answer.)
2. Record one real streamed response with two searches and a client tool call (key from §0), byte-for-byte,
   as the fixture for D1 and D3 (`tests/Pia.Wpf.Tests/TestData/`). Use a neutral public query; the fixture
   holds no user data.

### D1 — Native content accumulator · *Deps:* D0 · M · Enabler

- New `src/Pia.Wpf/Services/Providers/Http/AnthropicNativeAccumulator.cs`: consumes Messages API SSE events
  (`content_block_start` / `_delta` / `_stop`, `message_delta`) and rebuilds the response's content array —
  `text` + `citations_delta`, `thinking` + `signature_delta`, `redacted_thinking`, `tool_use` /
  `server_tool_use` from `input_json_delta`, `web_search_tool_result` verbatim. Also the buffered JSON
  `content` array as-is.
- `AnthropicRequestHandler.SendAsync`: wrap the response content in a pass-through stream that feeds lines to
  the accumulator as the SDK reads them (no buffering); on end of stream hand the result to the store (D2).
- Logging: block counts and byte sizes only; never content (CLAUDE.md Privacy-First Logging).
- Tests, new `AnthropicNativeAccumulatorTests.cs`: the D0 fixture rebuilds exactly; a pending
  `server_tool_use` (parallel search + client tool, `stop_reason` `tool_use`); a `max_uses_exceeded` result;
  a stream cancelled mid-block stores nothing; the tee yields byte-identical output to the SDK.

### D2 — Store · *Deps:* D1 · S · Enabler

- New `AnthropicNativeStore` (singleton, owned by `AnthropicProviderHandler`, registered at
  `Bootstrapper.cs:538`; passed into `AnthropicRequestHandler`'s constructor). Key: each client `tool_use` id
  of a response → that response's content. No model in the key: pairs carry no model binding (server S0
  case 3 verifies). Sliding TTL 120 min (same as the server), bounded by
  total chars with least-recently-used eviction. Responses with no search pair and no citation are not stored.
- Tests, new `AnthropicNativeStoreTests.cs`: hit/miss, slide on read, eviction order.

### D3 — Replay rewrite · *Deps:* D2 · S · High

- `AnthropicRequestHandler.Rewrite` gains the store: for each assistant message whose `tool_use` ids hit the
  store, apply the §3 rule. Ids are the only match key; the client's `tool_result` content is
  never touched; `tool_use` input comes from the store.
- At most 8 replayed messages per request, newest first (mirrors the server's
  `AnthropicNativeReplayMaxMessages` and `AgentToolCarryover.KeptResults`).
- A duplicate id in one request: the last occurrence wins (mirrors server S5) — defence in depth behind
  client step C2.
- Tests, extend `AnthropicRequestHandlerTests.cs`: verbatim replay restores search pairs and citations;
  blocks-only for a carried message without text; thinking blocks untouched in both; no hit → body unchanged
  byte-for-byte; the 8-message cap; a two-round loop test through `AnthropicProviderHandler` +
  `AiClientService` with a capturing inner handler (pattern of `AnthropicProviderHandlerTests.cs`).

### D4 — Thinking-binding guard · *Deps:* D3 · XS · High

- Determinism: an entry that has been replayed is not evicted while its TTL slides (eviction prefers entries
  never hit), so a live loop does not flip from replay to fallback.
- Belt and braces, if D0/e2e shows a 400 on a binding mismatch: send
  `thinking.block_binding.prefix_mismatch_behavior: "drop_block"` with beta header
  `thinking-binding-controls-2026-08-01` on requests that replay a message holding thinking — only if the
  provider's `Endpoint` is `api.anthropic.com` or confirmed to support the beta.
- Test: a replayed entry survives an eviction pass that removes an un-hit one.

### D5 — Probe: no thinking with forced tool choice · *Deps:* — · XS · High

- `AnthropicRequestHandler.Rewrite`: when the body's `tool_choice.type` is `any` or `tool`, write
  `thinking: {type: "disabled"}` instead of adaptive for that request (the existing `disableThinking` path).
  Only forced-tool requests change, so the prompt cache of ordinary turns is untouched. The probe already
  sends `NoThinkSystemPrompt`, so intent matches.
- Tests in `AnthropicRequestHandlerTests.cs`: `any` + effort → disabled; `auto` + effort → adaptive as today;
  no `tool_choice` → unchanged.

### D6 — Probe: don't re-pay a failed probe every turn · *Deps:* — · XS · Med

- `ProviderCapabilityService` (`:55-63`): cache `Unknown` with a short expiry (e.g. 15 min) instead of never;
  `Capable` / `Weak` stay cached as today. Keep "never blocks".
- Tests in `ProviderCapabilityServiceTests.cs`: a throwing probe runs once within the expiry and again after.

### D7 — Gate · *Deps:* D0–D6 · XS · High

- Rides with the client gate C7: zero-warning Debug + Release rebuild, test exe `Failed: 0`.
- e2e with the real key (§0) on a direct Anthropic row, effort High, web search on: the MongoDB checkup
  routine → no retraction narration, no repeated search after a client tool call; a three-round loop with
  thinking → no 400; the planning probe reports `Capable`.

## 5. Not in this plan

- `web_search_20260209` / `web_fetch_20260209` on the direct path (needs the same replay, plus code-execution
  blocks).
- Source chips from direct-path citations.
