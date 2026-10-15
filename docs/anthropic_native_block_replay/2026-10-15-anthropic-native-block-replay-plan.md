# Anthropic native block replay: keep server-side web search (and thinking) in the history Pia Cloud replays

**Status:** Planned — all gates decided by the owner 2026-10-15: G1 **A** (server-side store keyed by the tool
call id), G2a yes / G2b deferred, G3 moot, G4 resume in the server stream (cap 3), G5 a third plan, G6
deferred; store TTL 120 min sliding. Plans: server — Pia `feature/7836`,
`docs/plans/2026-10-15-anthropic-native-block-replay.md`; client —
[2026-10-15-anthropic-native-block-replay-client-plan.md](2026-10-15-anthropic-native-block-replay-client-plan.md);
direct provider — [2026-10-15-anthropic-direct-provider-replay-plan.md](2026-10-15-anthropic-direct-provider-replay-plan.md).
§5.1 below describes Option B and is kept as its appendix; §5.3's client parse/serialize applies only to B.
**Owner:** Marco Altmann
**Written:** 2026-10-15
**Origin:** Owner bug report 2026-10-15: Pia Cloud chats on `claude-opus-5-5` / `claude-sonnet-5-5`
(catalog row `claude-opus-high`, reasoning High, WEB enabled) show answer text that reads like a thinking
block — repeated self-corrections ("Ich habe bisher keine einzige Webabfrage ausgeführt, obwohl meine
Einträge das behaupten") and an agent-run step looping for 36 min / 32 tool calls. Diagnosed in a Claude
Code session the same day; the owner chose the full fix (replay Anthropic's native blocks) over a
prompt-level mitigation or disabling native search.

Companion tracking surface:
[2026-10-15-anthropic-native-block-replay-checklist.md](2026-10-15-anthropic-native-block-replay-checklist.md).

Two repositories: the server `G:\Git\Pia` (owns the wire contract) and this client `G:\Git\Pia.Wpf`.
Suggested split: one session per repository, server first (§5.4).

## 1. Symptom and evidence

Screenshots from the owner, Pia.Wpf v1.4.278, provider Pia Cloud:

1. Agent run "Detailrecherche pro Security-Layer", step 2 of 3. The paragraphs cycle: "Ich führe jetzt
   echte Websuchen aus. In den bisherigen Fassungen tauchen keine Suchergebnisse auf …" → "Jetzt liegen
   echte Suchergebnisse vor. Ich schreibe die Arbeitsnotiz neu." → `write_file` → the same again.
   Sentences run together across a search ("abschließe.Jetzt").
2. Routine "MongoDB Checkup": "Ich habe einen Fehler gemacht … ohne die Release-Notes-Seite abzurufen",
   "Das Release ist bestätigt", then again "Ich habe bisher keine einzige Webabfrage ausgeführt". Same
   run-together sentences ("wirklich.Das", "prüfen.Jetzt").

**A/B by the owner, same day:** the same MongoDB routine on Opus 5.5 with **Linkup** as the external
search provider (native WEB off) gave a short, clean answer with sources. Same model, same prompt — only
the search path differs.

It is not leaked reasoning. Thinking travels `thinking_delta` → `reasoning_content` (server
`AnthropicProviderHandler.cs:852-858`) → `TextReasoningContent` (client `PiaCloudChatClient.cs:190-198`) →
the reasoning view. The bubble shows the model's ordinary text; its content is a reaction to the history it
is shown.

## 2. Root cause (verified 2026-10-15)

Native web search (`web_search_20250305`, server `AnthropicProviderHandler.cs:45-49`, tool added at
`:343-351`) runs inside one Messages API call. The response content is
`text → server_tool_use → web_search_tool_result → text (cited) → tool_use`. Pia keeps only the text and the
client tool call:

- **Server, stream:** `ConvertBlockStart` (`:794-836`) drops `server_tool_use` and turns
  `web_search_tool_result` into a choiceless `web_citations` chunk carrying title/url/page_age only
  (`CollectCitations`, `:627-643`). `ConvertBlockDelta` (`:838-885`) drops `signature_delta` and
  `citations_delta` in its default case.
- **Server, request:** `BuildAssistantContent` (`:210-236`) rebuilds each assistant turn from `content` +
  `tool_calls` alone. The class doc (`:19-34`) records both drops as deliberate.
- **Client:** nothing in `src/` reads `web_citations`, and `SerializeMessages`
  (`PiaCloudChatClient.cs:455ff`) writes back `TextContent` + `FunctionCallContent` only.

So after the first client tool call (vault read, `write_file`, …) the next request shows the model its own
sentence "Jetzt liegen echte Suchergebnisse vor" with no search anywhere in the history. Opus 5.5 is built
not to state facts its inputs don't support; it concludes it fabricated, retracts, searches again, writes
again — and loses the evidence again on the next round. Each self-correction in screenshot 2 is one round.

**Why Linkup is clean:** it runs as a server connector through `ToolChatOrchestrator`
(Pia `src/Pia.Server/Connectors/ToolChatOrchestrator.cs:140-260`), which discards a round's text whenever
the round has tool calls — server calls `continue`, client calls leave through `ToolCallHandbackSse(calls)`
alone. The search results are stripped as well, but so are the claims, so the history stays consistent.
`WebSearchToolActivator.cs:49` activates the delegated provider only when native search is off.

**Secondary defects on the same path:**

- **Run-together text.** A `text` block opening after a server tool result is appended with no separator
  (`ConvertBlockStart` returns null for `text`). Consecutive text blocks must *not* be separated in general:
  with citations, Anthropic splits one sentence into several text blocks.
- **`pause_turn` ends the turn.** `MapStopReason` (`:677-683`) maps it to `stop`. `pause_turn` means the
  server-side sampling loop hit its iteration limit (default 10); resuming means re-sending the assistant
  content including the trailing `server_tool_use`, without a "Continue." user message — impossible
  without the native blocks. Exhausting `max_uses` (5) is different: further searches return a
  `web_search_tool_result` whose content is a `max_uses_exceeded` error object, and the turn goes on.
- **No source chips** on Pia Cloud native search, because the client never reads `web_citations`.

## 3. Goal and non-goals

**Goal:** on a Pia Cloud row backed by Anthropic, every request Pia sends upstream carries the assistant
turns as Anthropic produced them — `server_tool_use` + `web_search_tool_result` (with `encrypted_content`)
and text blocks with their citations — so the model sees its own evidence. Within a turn's tool loop at
minimum; across user turns per gate G2.

That alone fixes the reported bug. Replaying `thinking` / `redacted_thinking` with signatures is a
separate, second increment behind gate G6: today's code already omits thinking from replayed turns and the
API accepts that shape, while thinking replay imports the one constraint that can turn a request into a
400 (§4, preserved thinking).

**Non-goals:** what the client renders; the Linkup/connector path (already consistent); the direct
Anthropic provider in Pia.Wpf (gate G5); moving to `web_search_20260209` (this work enables it, see §8).

## 4. Constraints from the Messages API

Source: the claude-api reference bundled with Claude Code (model data cached 2026-10-06). S0 re-verifies
each point against the live docs before anything is built on it.

- **Server-tool pairs:** replay `server_tool_use` and its `web_search_tool_result` together and
  unmodified; a lone `server_tool_use` is a 400. `encrypted_content` / `encrypted_index` are opaque —
  replay verbatim. Block order inside the assistant turn is preserved exactly.
- **An assistant turn with server-tool blocks but no thinking block** must be accepted under adaptive
  thinking — the shape of the first increment (S6a). The handler doc (`:24-26`) states it for turns without
  server tools; S0 confirms it with a `server_tool_use` pair in the turn.
- **Preserved thinking (Opus 5.5, Sonnet 5.5) — second increment only (G6).** A replayed thinking block is valid only while the
  `system` prompt, the `tools` array and every earlier message are byte-identical to when it was produced.
  For accounts created on or after 2026-08-31 a mismatch is a **400** by default. Pia rebuilds history from
  the OpenAI-shaped canonical form, and the client varies ambient context, tools and compaction between
  requests, so a byte-identical prefix cannot be promised. Every request that replays a thinking block must
  therefore send `thinking.block_binding.prefix_mismatch_behavior: "drop_block"` with beta header
  `thinking-binding-controls-2026-08-01`; drops are reported in `input_transformations` (log the count,
  never the content). The beta must also be available on every endpoint an Anthropic row can name
  (`config.Endpoint`), not only `api.anthropic.com`. Thinking blocks are also model-bound: an Opus 5.5 block is read by no model except
  Fable 5.1 / Mythos 5.1 — others drop it silently, no error.
- **Progress notes:** the server sends `display: "summarized"`. On Opus 5.5 / Sonnet 5.5, notes longer than
  a sentence or two between tool calls arrive as progress-update thinking blocks (mixed with the summaries);
  short notes stay `text`. Unchanged by this plan — it explains what lands in the reasoning view versus the
  bubble.
- **`pause_turn`:** resume by re-sending the assistant content as-is; the API detects the trailing
  `server_tool_use`.

## 5. Design sketch

### 5.1 Option B — client-carried envelope (not chosen; see the server plan for Option A)

One opaque value per assistant message, working name `pia_native`:

- **Server → client, streaming:** one choiceless chunk `data: {"pia_native":"<sealed>"}` after a response's
  last content, before `data: [DONE]`. Current clients skip choiceless chunks
  (`PiaCloudChatClient.cs:165-166`), so this is backward compatible.
- **Server → client, buffered:** `choices[0].message.pia_native`.
- **Client → server:** the assistant message object carries `"pia_native": "<sealed>"` next to `content` /
  `tool_calls`. `BuildChatJson` deep-clones messages verbatim (`AiProxyService.cs:662-695`), so the field
  reaches the handlers today — hence §5.2's central strip, and server rollout before client rollout.
- **Payload:** `{ "v": 1, "provider": "anthropic", "model": "<model id>", "content": [ …the response's
  content blocks, verbatim… ] }`, compressed, then protected with ASP.NET Core Data Protection (purpose e.g.
  `Pia.AiProxy.NativeContent.v1`; the server already uses it in `AiProviderSecretProtector`). Sealing makes
  it tamper-proof — a client cannot inject forged blocks, or text that bypasses checks run on `content` —
  and opaque to every layer in between. Unseal failure (rotated key, other instance, corruption) falls back
  to today's reconstruction; it is never a 400.
- Emitted only when the content holds something reconstruction cannot reproduce: a server-tool pair or a
  citation (and, once G6 opens it, a signed thinking block). A plain text + `tool_use` response gets no
  envelope. Until G6, the server leaves thinking blocks out of the sealed content, so the envelope never
  carries what it may not replay.

### 5.2 Server (`G:\Git\Pia`)

- **Accumulate, stream:** `StreamState` (`:693-702`) gains a block list. `content_block_start` opens a copy
  of `content_block`; deltas append (`text_delta` → text, `citations_delta` → citations, `thinking_delta` →
  thinking, `signature_delta` → signature, `input_json_delta` → raw JSON buffer for `tool_use` /
  `server_tool_use`, parsed at stop); `content_block_stop` (dropped today) closes the block; `message_delta`
  / `message_stop` emits the sealed chunk. The transformer is a pure static function today — sealing needs
  the protector, so `CreateStreamLineTransformer` gets it through the handler instance.
- **Accumulate, buffered:** the response parser (`:530-620`) already walks `content`; seal it as-is.
- **Strip centrally:** `BuildChatJson` removes `pia_native` from every message before any handler sees it
  and passes the unsealed blocks to the handler through a side channel (a per-message-index map, or a new
  `BuildRequest` overload — elaboration decides). Invariant, with a test per handler: no non-Anthropic
  upstream ever receives the field (Mistral answers 422 to unknown fields).
- **Replay (S6a):** `BuildAssistantContent` uses the native content verbatim when (a) the target handler is
  Anthropic, (b) the envelope's model equals the request's model, and (c) the envelope's `tool_use` ids equal
  the message's `tool_calls` ids in order. Otherwise today's reconstruction. No thinking blocks.
- **Thinking replay (S6b, behind G6):** the envelope also keeps `thinking` / `redacted_thinking`, and every
  request that replays one adds the `drop_block` binding and beta header (§4).
- **Paragraph break:** in stream and buffered paths, a `text` block opening after a
  `web_search_tool_result` gets a leading `\n\n` when the previous text does not end in whitespace.
  Independent of the envelope; can ship first.
- **`pause_turn`:** with replay available, `ChatStreamCoreAsync` (`AiProxyService.cs:487ff`) can resume —
  append the paused assistant content, re-issue, stream the continuation into the same client stream (cap,
  e.g. 3). Gate G4.
- **`ToolChatOrchestrator`:**
  1. A round consumed server-side appends its assistant message via `AppendServerToolCallsAsync`; carry the
     round's native content on it so a native search in that round survives the server loop.
  2. `ToolCallHandbackSse` (`:601`) must forward the round's envelope chunk with the handed-back calls.
  3. A final round's lines already pass through `contentLines`; verify `ParseDelta` classifies the envelope
     chunk as content.
  4. The cap path (`CallRawAsync` + `StripServerToolsFromResponse`, `:467`) carries the buffered envelope.
  5. The sensitive-hit switch (`MaybeSwitchToSensitive`) changes the model mid-loop; check (b) makes that a
     clean fallback.

  The client receives the last round's envelope only; server-consumed rounds stay invisible, as today.
- **Accounting:** replayed search results are input tokens on every later request; the prompt cache
  (`ApplyCacheBreakpoints` under `EnablePromptCache`, cache reads at 0.05× on Opus 5.5) softens it. S0
  measures it.
- **Logging:** never the envelope or its content — length, and the `input_transformations` drop count.

### 5.3 Client (`G:\Git\Pia.Wpf`)

Worked out at file and test level, with the spike results, in
[2026-10-15-anthropic-native-block-replay-client-plan.md](2026-10-15-anthropic-native-block-replay-client-plan.md).

- **Parse:** `PiaCloudChatClient` reads `pia_native` from the stream (choiceless chunk) and from the buffered
  message, and yields it as a dedicated `AIContent` subtype (working name `PiaNativeContent`).
  `AiClientService` builds the round's assistant message with `updates.ToChatResponse()` (`:371`) and adds
  `response.Messages` to `workingMessages` (`:701-704`) — verify Microsoft.Extensions.AI 10.9.0 keeps an
  unknown `AIContent` in the message's `Contents`.
- **Serialize:** `SerializeMessages` writes `pia_native` on the assistant message object, at most one per
  message.
- **Hand-built assistant messages** must carry the content or drop it on purpose (dropping = today's
  behaviour for that message): `HeadlessTurnExecutor.cs:637` and `:941`, `AgentToolCarryover.cs:45` and
  `:121`, `AgentToolExchangeSerializer` (persisted agent-run exchanges; `MaxRowChars` is 1 MiB per row — the
  envelope may need its own column or a cap), `AgentContextCompactor` (compacted rounds drop theirs),
  `BackgroundAssistantTurnRunner`.
- **Other `IChatClient`s** (OpenAI, Mistral, Ollama, the Anthropic SDK adapter, …) must ignore the content
  type — test that none throws or serializes it.
- **Across user turns (G2 only):** chat persistence (`AssistantMessage`, SQLite via `SqliteContext`) and
  sync (`SyncAssistantChat` / server `AssistantChatDto`; Pia `docs/ai_context/e2ee_guidelines.md`) would
  need a field for the envelope.
- **Logging** per CLAUDE.md "Privacy-First Logging": the envelope is never logged; `SensitiveDebug` of its
  length at most.

### 5.4 Rollout

Server first: backward compatible both ways — current clients skip the chunk, requests without the field
reconstruct as today. Client second. Until both ship, the workaround holds: native WEB off on Anthropic rows,
Linkup as the search provider.

## 6. Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| G1 | How large is a real envelope (5-search research round: raw, compressed, sealed) against `KestrelMaxRequestBodyMb` = 5 and the per-round upload? Client-carried envelope (stateless, as sketched) or a server-side store keyed by a reference id (small wire; costs retention, multi-instance sharing, regenerate/edit semantics)? | S3 and every step after it |
| G2a | Carry the envelope across the steps of one agent run (local only)? | C3 carry-over rows |
| G2b | Carry it across chat user turns too (persist + sync)? Search results are re-billed as input on every later turn. | C5 |
| G3 | Seal with Data Protection (is the keyring shared across instances?) or HMAC with a dedicated secret? Either way, rotation degrades to the fallback. | S3 |
| G4 | Resume `pause_turn` inside the server stream, or hand the client a distinct finish reason? | S8 |
| G5 | Does the direct Anthropic provider in Pia.Wpf (`Anthropic` SDK 12.48.0 adapter plus `AnthropicRequestHandler` adding `web_search_20250305`) drop the same blocks? If yes, a third plan. | — |
| G6 | Replay thinking blocks at all? Needs the `drop_block` beta on every endpoint a row can name, and pays off only where the prefix stays stable often enough for blocks to survive (measure the drop rate). No answer leaves S6a's fix intact. | S6b |

## 7. Verification

- **Server unit tests:** recorded SSE fixtures (text → `server_tool_use` → `web_search_tool_result` → cited
  text → `tool_use`; a `max_uses_exceeded` result; `pause_turn`; for S6b also thinking + signature and
  `redacted_thinking`);
  seal/unseal round-trip; replay versus fallback (bad seal, model mismatch, id mismatch); the strip test per
  non-Anthropic handler; paragraph break; orchestrator handback carries the envelope.
- **Client tests:** stream and buffered parse; `ToChatResponse` keeps the content; serialize round-trip;
  each hand-built path.
- **End to end** on the throwaway e2e stack (18081 — never the owner's 8081 stack): the MongoDB routine on an
  Anthropic row with native WEB on. Expect no retraction narration and no repeated search after a client
  tool call; compare token usage with the Linkup run. After S6b, also log the `drop_block` count.
- **Gates:** Pia.Wpf zero-warning rebuild in Debug and Release and the test exe at `Failed: 0` (CLAUDE.md);
  Pia per its own CLAUDE.md.

## 8. Not planned here

- Source chips for Pia Cloud native search (`web_citations`) — cheap once C1 parses the stream.
- No client consumer for `knowledge_citations` either (server emits it, `ToolChatOrchestrator.cs:579`) —
  check whether connector citations reach the UI another way.
- The connector path strips search results across client tool rounds — consistent, but may search again.
- `web_search_20260209` / `web_fetch_20260209` (dynamic filtering) on 5.5-family rows — they nest
  code-execution blocks and only become feasible with native replay.
- `display: "updates"` (beta) for 5.5-family progress notes instead of `"summarized"`.
- Forced tool choice is a 400 on Opus 5.5 / Sonnet 5.5: the server's `MapToolChoice` maps `"required"` to
  `any` (`AnthropicProviderHandler.cs:365`), and `AiClientService.cs:1364` sets `ChatToolMode.RequireAny`
  on a direct-provider path. Nothing on the Pia Cloud path sends it today (`BuildRequestBody` emits no
  `tool_choice`); check the direct path.
