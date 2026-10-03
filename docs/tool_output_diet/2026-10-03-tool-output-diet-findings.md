# Tool output diet — findings

**Status:** Measurement and two cheap fixes implemented on `feature/tool-output-diet`; no
measurement taken yet. The model-based tiers below are not planned.
**Owner:** Marco Altmann
**Written:** 2026-10-03
**Origin:** The "Claude Context Diet" write-up
(<https://claude.ai/artifact/9Ud9iisBP4AzuxbKbcjDyw>), read against Pia's own agent loop with the
question: where in Pia's tool-heavy workflows would that principle win anything?

## The principle

A tool result stays in the conversation, so the model reads it again on every later request. In
the write-up, a Claude Code `PostToolUse` hook swaps large *discovery* outputs (code search, docs,
logs, web results) for a short fact list written by a cheaper model, archives the raw text with a
pointer, and leaves exact reads (file contents, diffs), errors and anything secret-looking
untouched. Over 7 days the main model read 186,653 tokens of tool output instead of 1,848,179
(−89.9%). Three findings from it matter here:

- The cost is the **re-read**, not the first read. One 55k-token grep kept for 30 turns is 1.66M
  input tokens.
- The cheap reader has to be good. Haiku 4.5 invented file names that a JSON validator passed;
  the cheapest GPT reader dropped facts. A Sonnet-class model was the floor.
- Its savings figure assumes re-reads are billed at the **cached** price.

## Where Pia already applies it

- **Chat across turns.** Interactive history carries no tool content at all; `ChatToolSummary`
  appends a names-and-targets record instead. The write-up's "a grep stays for 30 turns" case
  cannot happen in a Pia chat.
- **Agent runs across steps.** `AgentToolCarryover` keeps the 8 newest results, each capped at
  4,000 chars, and replaces older ones with a "call X again" placeholder.
- **Over budget.** `AgentContextCompactor` evicts tool results once a request passes 45% of the
  input budget, and runs before every tool round.

## Where it would win

1. **MCP results were uncapped.** `McpPluginToolHandler` returned the server's whole result and
   `AiClientService.DispatchToolCallsAsync` wrapped it unchanged. Built-in tools all have caps
   (files and git 100K chars, `pia_help` 6K per section, screen text 8K); an MCP server had none.
   That is the write-up's largest row (MCP code-graph search, 634K tokens raw) with nothing in
   front of it.
2. **Re-reads inside one step's tool loop.** A step allows 24 rounds by default (48 max). A
   result rides every later round until the compactor's 45% threshold, which is roughly 75–90k
   tokens on a 200k-window model. A 25k-token result read in round 2 that rides 20 more rounds is
   ~500k input tokens.
3. **Prompt caching is off by default.** `AiProvider.EnablePromptCache` is Anthropic-only and
   defaults to false, because a cache write costs more than the input it replaces on a one-shot
   request. In a tool loop the same prefix is re-sent every round, so a default Anthropic setup
   pays the full input price for every re-read — 10× what a cached read costs.
4. **The carryover cap keeps the head.** A 200-hit search carried to the next step keeps its first
   4,000 chars — the first hits in file order, not the relevant ones.

## Where it does not apply

- **Native web search** (Anthropic's server-side `web_search` tool): its results come back inside
  the provider response and never pass through `DispatchToolCallsAsync`.
- **Exact reads** — file reads before an edit, `git_diff`, `git_show`: the model needs the real
  text. The write-up keeps these raw too.
- **Screen text, `pia_help`, chat search**: already capped at 8K, 6K and 1.5K chars.

## Constraints specific to Pia

- **PII tokenization.** `TokenizingAiClientService.WrapToolHandler` tokenizes a tool result after
  the handler returns. Any model-based compressor must sit after that point — at the dispatch
  site — so it only sees tokenized text, and must copy placeholders such as `[Phone_9]` verbatim.
  A cap (pure truncation) is safe on either side; it runs before tokenization so it can never cut
  a placeholder in half.
- **No helper-model slot.** Per-step personas and providers exist, but there is no "cheap model
  for housekeeping" role. That role, plus a quality check per provider type (nine of them), is the
  real cost of a model-based compressor.
- **Latency.** The write-up measured 7–8 s per compression. Acceptable in a background run,
  visible in a streaming chat, and it must not count against the per-round timeout.
- **A raw archive is new sensitive data at rest.** It would need `PiaPaths` and the retention
  service. Pia's read tools are re-callable, so "summary + call again" may make an archive
  unnecessary.

## What this branch implements

All three are in **Settings → Assistant → Agent runs → Tool output**, read on every request, and
device-local: the sync projection (`SyncMapper.ToSyncSettings`) is an explicit allowlist that does
not include them, so flipping one for a measurement does not flip other devices.

- **Measurement, always on.** Counts only, at Information level, so a release log carries it:
  - per result: `Round {Round}: tool {ToolName} returned {ResultChars} chars`
  - per MCP cut: `MCP tool {ToolName} result cut from {ResultChars} to {MaxChars} chars`
  - per finished exchange, one line beginning `Tool loop usage:` with `provider`, `requests`,
    `results`, `resultChars`, `largest` (chars and tool name), `toolResultCharsSent`,
    `inputTokens`, `cachedInputTokens`, `cacheWriteTokens`, `mcpCap` and `promptCache`.
- **"Limit the size of MCP tool results"** (`McpToolResultCapEnabled`, default **on**) at
  **50,000 chars** (`McpToolResultMaxChars`, slider 5,000–200,000). A longer result is cut and
  ends with a note telling the model the original length and to call again with a narrower query.
- **"Cache the prompt between tool rounds (Anthropic)"** (`ToolLoopPromptCacheEnabled`, default
  **off**). For an Anthropic provider whose own prompt-cache flag is off, a request that offers
  tools gets the same cache breakpoints the provider flag would add. Other providers ignore it.

`toolResultCharsSent` is the sum, over every request of the exchange (including a wrap-up turn), of
the tool-result chars that request carried — that is, what the model read, re-reads included.
`resultChars` counts each result once. Their ratio is the re-read multiplier.

`promptCache` reads `provider` (the provider row's own flag is on), `tool-loop` (on because of the
new setting), `off`, or `n/a` (not an Anthropic provider).

Scope: the summary line is written when an exchange completes, not when it times out, is truncated
or is cancelled — the per-result lines still land in those cases. The cache setting covers the
tool loop in `AiClientService.GetChatCompletionWithToolsAsync` only; the planner's and verifier's
requests build their own clients and are unaffected. **How to measure** below says how to read the
numbers.

## How to measure

1. Pick one repeatable, tool-heavy task — ideally an agent run that searches files, calls an MCP
   server and reads git history — and one Anthropic provider with its own prompt cache **off**.
2. Run it with both new settings off. Then with only the MCP cap on. Then with only the cache on.
   Keep the provider, model, persona and task identical.
3. Pull the lines from the log folder (`%LOCALAPPDATA%\Pia\Logs`, or `Logs` under
   `PIA_LOCAL_DATA_DIR` when that is set):

   ```powershell
   Select-String -Path "$env:LOCALAPPDATA\Pia\Logs\pia-*.log" -Pattern "Tool loop usage:|MCP tool .* result cut"
   ```

4. Compare per run:
   - `toolResultCharsSent / 4` approximates the tool-output tokens the model read. Divided by
     `inputTokens` it is tool output's share of the input — the number the write-up's −89.9% is
     about.
   - `toolResultCharsSent / resultChars` is the re-read multiplier.
   - With the cache on, `cachedInputTokens` against `inputTokens` is the hit share. Anthropic bills
     a cached read at 0.1× and a five-minute cache write at 1.25× the input price, so the input
     cost is proportional to `(inputTokens − cachedInputTokens − cacheWriteTokens) +
     0.1 × cachedInputTokens + 1.25 × cacheWriteTokens`. That formula assumes `inputTokens`
     already includes both cached reads and cache writes. The Anthropic SDK's adapter (12.48.0)
     appears to work that way — it sums two values before setting `InputTokenCount` — but this
     was read from its IL, not checked against a live run. Confirm it against the Anthropic
     console's usage for one run before trusting the ratios.
   - `largest` and the per-result lines show which tools dominate.

## Defaults and when to flip them

- **MCP cap on** — it bounds a single result that could otherwise overflow a small window and fail
  the step, and 50,000 chars is half the cap built-in file and git tools already apply. Lower it if
  the measurement shows MCP results dominate `toolResultCharsSent` and the runs still succeed.
- **Tool-loop cache off** — it changes billing, and an Anthropic-compatible endpoint behind a
  custom URL may reject `cache_control`. Flip it to on by default once a run shows a cache hit
  share well above the extra write cost (write tokens × 0.25 < read tokens × 0.9). The wrap-up turn
  after the last tool round shares the cache-enabled client but sends no tools, so its prefix
  differs: it pays one cache write without reading. It runs only when the rounds run out or an
  answer comes back empty.

## Next steps, not yet planned

Gate: do these only if the measurement shows tool output is a large share of input (say over
half) and a few tools account for most of it.

- **Fixed-rule shortening** for structured output (search hits, `git_log`, MCP JSON): first N
  items plus a count of the rest, applied as the result arrives, so the prompt-cache prefix stays
  stable. Cannot invent anything; `ScreenTextCompactor` is precedent.
- **Model-written fact lists** for prose output (docs, logs, MCP text), starting in background agent
  runs and in the cross-step carryover in place of the 4,000-char head. Needs the helper-model
  role, a Sonnet-class floor, fail-open behaviour and the tokenization constraint above.
