# Prompt-cache hit rate on Anthropic rows: append-only agent steps, chunked replay cap, safe breakpoints (steps P0–P5)

**Status:** Planned — not started. Round 2 of the native-replay work; lands on the same two branches and PRs.
**Owner:** Marco Altmann
**Written:** 2026-10-15
**Origin:** The cache-read measurement of the testing phase (agent run `83c6d629` on the 18081 e2e stack,
evidence in `G:\Git\Pia-7836\artifacts\native-replay-testing\`) and the owner's request to plan
the four levers it pointed at. Tracked in
[2026-10-15-anthropic-native-block-replay-checklist.md](2026-10-15-anthropic-native-block-replay-checklist.md) (group P).

## 0. Cold start — read this first

| What | Where |
|---|---|
| Client steps (P0, P2) | `feature/anthropic-native-replay`, worktree `G:\Git\Pia.Wpf-anthropic-native-replay`, GitHub #70 — never switch the main checkout `G:\Git\Pia.Wpf` |
| Server steps (P3, P4) | Pia `feature/7836`, worktree `G:\Git\Pia-7836`, Azure DevOps #7836 — never the main checkout `G:\Git\Pia`; commit subjects `#7836: ` |
| Ops step (P1) | the owner, in the prod admin console |
| e2e | the throwaway stack on **18081** (left running, recipe in `G:\Git\Pia-7836\artifacts\native-replay-testing\README.md`); **8081 is the owner's real data — never touch it** |
| Key for billed runs | `[Environment]::GetEnvironmentVariable('PIA_TEST_CLAUDE_KEY','User')` — never print it |

**What to do first:** P0's prefix test (client, free, red first). P3 and P4 are small and independent of
it. No PR is open yet; these commits join the existing branches.

Line numbers below are from client `bd9cc6d8` and server `10958637`; re-check them before editing.

## 1. Baseline

Agent run `83c6d629` on Pia Cloud: 3 steps, 19 upstream requests, prompt cache on. Source: PIASERVER's
read of `token_usage_logs` (input = uncached + cache write + cache read; cache writes are not recorded
separately).

| Request | Input | Cache read | Share |
|---|---:|---:|---:|
| step 0, rounds 2–3 | ~18.6k | ~18.5k | 99% |
| step 1, round 1 (**step boundary**) | 18,882 | 17,800 | 94% |
| step 1, round 2 (the search round) | 82,293 | 65,365 | 79% |
| step 1, round 3 (first replay round) | 42,056 | 27,418 | 65% |
| step 1, rounds 4–9 | 51–61k | — | 82–99.5% |
| step 2, round 1 (**step boundary**) | 61,303 | 17,800 | 29% |
| step 2, rounds 2–3 | ~62.5k | — | 98–99.7% |
| whole run | 744,309 | 613,420 | **82.4%** |

The routine run `4d8d074f` ran with prompt cache **off** (the rows were created that way) and paid about
45k uncached input per follow-up round.

What this shows and what it doesn't:

- **Inside a step the cache works** — 98–99.7% once the new content has been written once.
- **Every step boundary reads only 17,800**, the entry at the system marker, at both boundaries. A read can
  only land where an earlier request wrote a breakpoint, and the only one before `messages` is the system
  marker — so this says that *everything in `messages` missed*, not where it diverged. About 43.5k was
  re-written at the step 1 → 2 boundary, including the five replayed search turns. P0 finds the
  divergence points in code; §3 P2 lists the ones already visible.
- **The 65% on the first replay round is inherent, not a miss.** Round 3 read 27,418 — above the system
  marker — so it found the entry the web-search tool writes automatically after its result inside round 2.
  The unread ~14.6k is round 2's own answer text and its tool result, which enter the prompt for the first
  time in round 3 and must be written once.

## 2. Caching rules this plan relies on

Source: the `claude-api` skill's `shared/prompt-caching.md` (bundled with Claude Code, model data cached
2026-10-06). P0 re-checks them against the live prompt-caching docs.

- The prompt renders `tools` → `system` → `messages`; any byte change invalidates everything after it.
- At most **4** `cache_control` breakpoints per request. They may sit on `text`, `image`, `tool_use`,
  `tool_result` and `document` blocks — **not** on `server_tool_use`, `web_search_tool_result` or
  `thinking`.
- Each breakpoint looks back **at most 20 positions** for an earlier entry; a run of consecutive
  `tool_use` blocks counts as one position, and so does a run of `tool_result` blocks. A turn that adds
  more than 20 other positions (a cited answer splits into many `text` blocks) can push the previous
  entry out of reach.
- Server tools such as web search write a 5-minute entry after their results by themselves when the
  request already uses caching.
- Prices: cache reads $0.20/MTok on Opus 5.5 (0.05× input) and Sonnet 5.5 (0.1×); 5-minute writes 1.25×.
  A boundary that re-writes 43.5k instead of reading it costs about 54k input-equivalents instead of 2–4k.
- Preserved thinking: a replayed `thinking` block is valid only while `system`, `tools` and every earlier
  message are byte-identical to when it was produced; accounts created on or after 2026-08-31 get a 400
  otherwise. This is why P2 never carries thinking across a step boundary.

## 3. Steps

### P0 — Prefix test and doc re-check (client, XS)

A unit test that makes the step-boundary divergence visible, written red first; it is P2's first test.

- For **both** step executors — `HeadlessTurnExecutor` and the live path through
  `ChatSession.BuildStepChatMessagesAsync` — run a two-step agent run against a recording fake chat
  client: step 1 makes two tool rounds and a final answer, step 2 makes one round.
- Serialize every recorded request the way Pia Cloud does (`PiaCloudChatClient.SerializeMessages`) and
  assert: step 2's first request = step 1's last request + step 1's final assistant message + step 2's
  instruction, message for message and byte for byte. Same persona both steps, so the system message is
  equal too; a second case with different personas asserts only from the first non-system message.
- Today it fails. Record the first divergence it reports in the commit message; the five candidates are
  P2's list below.
- Re-read the live prompt-caching docs for the four facts in §2 that P4 depends on (eligible block types,
  4 breakpoints, 20-position lookback, the automatic write after server-tool results). Note any
  difference in this plan before P4 is built.

### P1 — Prompt cache on for the prod Anthropic rows (owner, XS)

The biggest lever, and no code. `EnablePromptCache` is opt-in per catalog row and defaults to off (server
`AdminAiProviderService.cs:719`); the e2e rows were created off, so the prod rows most likely are too.
Without it every replayed search is billed at full input price on every later round.

1. In the prod admin console, open every Anthropic row (`claude-opus-high` and its siblings) and switch
   prompt cache on.
2. **G7, owner:** should new Anthropic rows default to prompt cache on? If yes, it is a one-line server
   change in the create defaults plus its test, on `feature/7836`.
3. DSB: prompt caching means Anthropic keeps the cached prefix for the TTL (5 minutes, refreshed on every
   read). Add that to the open DSB item next to the in-RAM native-content store; do not edit
   `docs/dsb` from this plan.

### P2 — Append-only step history (client, M)

**Goal:** step N+1's request is step N's last in-loop request, followed by step N's final assistant
message and step N+1's instruction — byte-identical, so the step boundary reads the whole prefix instead
of re-writing it. Pia Cloud gains fully; see the thinking constraint for the direct path.

How the two executors build a step request today:

- `HeadlessTurnExecutor`: `_messages` holds the run system prompt, the goal, and per finished step its
  `exchange.ToolExchanges` (`:968`) and `exchange.Visible` (`:971`). A step request is a copy: the step's
  system prompt, `_messages.Skip(1)`, and the instruction with `AppendTimeNote` (`:764-771`), then
  `ClearOldResults` / `WithoutToolExchanges` (`:791-793`) and compaction.
- `ChatSession.BuildStepChatMessagesAsync` (`:1029-1067`): the step's system prompt, then each UI
  message with that message's `_stepToolExchanges` spliced in front of it (`:1040-1046`), then the
  instruction with nudge and time note (`:1060-1061`), then `ClearOldResults` (`:1066`).
- Both carry what `ToolRoundExchange` delivered: `AgentToolCarryover.Capture(...)` of the round's
  appended messages (`AiClientService.cs:475`).

The divergence points, each to be removed or accepted explicitly:

1. **The step instruction is dropped.** It goes onto the copy only, never into the carried history, so the
   next step lacks it exactly where step N's loop had it. Keep the instruction message *as sent* —
   the exact string including time note and nudge — in the carried history, in front of the step's
   rounds.
2. **Assistant text is stripped.** `Capture` keeps only `FunctionCallContent` / `FunctionResultContent`
   (`AgentToolCarryover.cs:37-68`). Carry each round's assistant message as it was in `workingMessages`:
   text and calls. (On Opus 5.5 and Sonnet 5.5 between-tool text mostly arrives as thinking, so this is
   often empty — but not always.)
3. **Results are capped at capture time** (4,000 chars, `CapResult`), while the loop sent them in full.
   **G8, default kept:** keep the cap, applied at the boundary. Then the prefix still matches up to step
   N's first oversized result and is re-written from there — one finished step's tail, once — and context
   growth stays bounded as today. The alternative, carrying results uncapped while they are inside the
   kept window, removes that rewrite at the cost of larger carried context; owner's call.
4. **The step reply is `exchange.Visible`**, which `BackgroundAssistantTurnRunner` builds by joining every
   round's text with `\n\n`, parsing think tags and stripping citations — not the final assistant
   message the model produced. Carry the final round's assistant message as received instead, and keep
   `Visible` for the UI and persistence only. This needs the final round surfaced from `AiClientService`
   (a new field or item next to `ToolRoundExchange`; the implementer picks the shape).
5. **`ClearOldResults` re-cuts every step.** It placeholders every result older than the newest 8
   (`AgentToolCarryover.KeptResults`, `:85-133`), recomputed on each step copy, so once a run has more
   than 8 results the earliest newly cleared result moves at every boundary and the prefix breaks there.
   Clear in chunks instead: `cleared = n <= 16 ? 0 : ceil((n - 16) / 8) * 8` for `n` results — a pure
   function of the count that keeps the newest 9–16 once clearing starts and moves the cut once per 8 new
   results.

Constraints:

- **Thinking stays out of the carry.** Strip `TextReasoningContent` from carried messages exactly as today.
  Carrying a signed thinking block into a step whose system prompt or tools differ is a preserved-thinking
  history edit (400 for newer accounts). Pia Cloud never sends thinking upstream (G6 deferred), so its
  requests are unaffected; the direct path's in-loop requests do contain thinking, so it gets no boundary
  gain from P2 (non-goal).
- **Persistence unchanged.** `Capture`'s stripped, capped output also feeds `AgentToolExchangeStore`
  through `RecordAsync` (`BackgroundAssistantTurnRunner.cs`, the `ToolRoundExchange` case) and the
  resume path, and C1's pinning tests lock it. Change the live carry only. A resumed run is re-seeded from
  the stripped rows, so its first step after the resume misses; accepted.
- **Behaviour change, say so in the commit:** earlier steps' instructions now stay visible to the model,
  in order, with the current instruction last.
- In-step compaction (`AiClientServiceInStepCompactionTests`) and a tool-less step
  (`WithoutToolExchanges`) break the prefix for that step; accepted.
- Tool-call ids stay verbatim (C1); the server's native replay keys on them and now sees the assistant
  text it stored, so carried turns replay verbatim rather than blocks-only.

Tests: P0's prefix test goes green for both executors. Add a `ClearOldResults` table test (counts 1–40:
nothing is cleared up to 16, the cut only moves at 17, 25 and 33, and 9–16 results keep their body after
that). The existing C1 and carryover tests
stay green; C1's persisted-shape tests must not change.

### P3 — Replay cap in chunks (server, XS)

`AnthropicProviderHandler.SelectReplays` (`:378-410`) keeps the newest `MaxMessages` (8) hits:
`capped = hits.Count - MaxMessages`. Once a run has more than 8 anchored search turns, every new one
moves the cut by one, the oldest replayed turn falls back to reconstruction, and the prefix breaks there —
on every round.

Replace it with a pure function of the hit count that moves the cut in chunks of `B = max(1, M / 2)`:
`cut = n <= M ? 0 : ceil((n - M) / B) * B`. At most `M` turns replay (the cap's promise holds), at least
`M - B + 1`, and the cut moves once per `B` new anchors.

- Not the client's sticky D3: that needs store state and would break the tested "a miss is byte-identical
  to today" invariant. The S6b note (sticky if G6 opens) stays.
- Only matters in runs with more than 8 search turns; C7b had 5, so the baseline never exercised it.
- Tests: for `n` in 1–24 with `M = 8`, the replayed set never exceeds 8, the cut changes only at
  `n = 9, 13, 17, 21`, and the replay summary's `capped` count matches.

### P4 — Breakpoint placement (server, S)

`ApplyCacheBreakpoints` (`AnthropicProviderHandler.cs:732-754`) marks the last system block (or the last
tool) and `content[^1]` of the final message, whatever its type. Two changes:

1. **Latent 400.** An S8 resume request ends with the paused assistant content, whose last block can be
   `server_tool_use` — not a valid breakpoint target. With prompt cache on, the path S8 exists for would
   400. Walk back within the message to its last eligible block (`text`, `image`, `tool_use`,
   `tool_result`, `document`); no marker if there is none. Unit test with an S0 fixture.
   Optional live confirmation: one small billed call with a marker on a `server_tool_use` block (expect a
   400), using the S0 fixture blocks.
2. **Insurance against the 20-position lookback.** Add a marker on the last eligible block of the message
   that **precedes the newest assistant message** — the previous request's final message, where the
   previous entry was written. A cited search turn can add more than 20 positions; this marker lands
   exactly on the earlier entry whatever the turn adds. That makes three markers (system or tools,
   previous-final, final) of the four allowed. Skip it when that message is the final message.

The baseline shows no lookback miss — in-step reads were 98–99.7% — so part 2 is insurance, and P5 checks
that it costs nothing. Tests: marker positions for a tool-loop request, a fresh user turn, an S8 resume
body and a request with no assistant message; never more than four markers; never on an ineligible block.

### P5 — Gates and measurement (both, S)

1. Server: the S9 gate again (fast subset, Release build, format on touched files) and the replay summary
   line unchanged in shape.
2. Client: Debug and Release rebuild at 0 warnings / 0 errors, the test exe at `Failed: 0`.
3. Rebuild the 18081 stack from the new server HEAD with the existing override; the rows already have
   prompt cache on.
4. One C7b-shaped agent run on Pia Cloud (3 steps, research in step 2). Read the per-request cache share
   from `token_usage_logs`, as in §1. Targets:
   - each step boundary reads at least the previous step's last request's input, minus the tail P2's G8
     cap re-writes;
   - the whole run above 90% cache reads (baseline 82.4%);
   - no 400, no 413, replay summary with 0 pair drops.
5. A run with more than 8 search turns, to watch P3's cut move live, only if the owner funds it;
   otherwise P3 rests on its unit tests.
6. Billing: a C7b-shaped run was about 0.75M input tokens, now mostly cache reads. At most two attempts.
7. Tick P0–P5 in the checklist (server steps naming the Pia commit) and add the measured numbers here.

## 4. Decisions taken in this plan (owner may override)

- P2 carries the instruction, the round messages and the final assistant message as sent and received; the
  UI, persistence and resume keep today's shapes.
- G8: the 4,000-char cap on carried results stays, applied at the boundary.
- `ClearOldResults` and the server replay cap both cut in chunks (8 at a time above 16 on the client,
  `M / 2` on the server), stateless.
- Thinking is never carried across a step boundary.

## 5. Not in this plan

- The 1-hour cache TTL for agent runs with long tool calls or approval waits (2× write cost; pays off only
  for gaps of 5–60 minutes).
- The compactor's token estimate, which cannot see search results (4–5.6k estimated against 43–95k
  reported in D7).
- Running title generation on a cheaper model (titles ran on Opus 5.5).
- Carrying thinking across steps on the direct path when system and tools are unchanged.
- G2b (replay across chat user turns).
