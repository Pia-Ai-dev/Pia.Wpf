# Prompt-cache hit rate on Anthropic rows: append-only agent steps, chunked replay cap, safe breakpoints (steps P0–P5)

**Status:** Done — measured 2026-10-16; P2 (size-based clearing), P3, P4 landed; PRs not yet opened.
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

**What to do first:** P0's prefix test (client, free, red first) and P5a, the server's usage
instrumentation and the **before** measurement on Claude Haiku 5.5. P5a must run before any of P2, P3 or P4
reaches the 18081 stack. P3 and P4 can be written meanwhile. No PR is open yet; these commits join the
existing branches.

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

**Re-checked against the live docs on 2026-10-15 (P0)** — the prompt-caching page and "Tool use with
prompt caching":

- 4 breakpoints: confirmed; top-level automatic caching takes one of the four slots.
- Eligible blocks: the live page lists tools, system, `text`, `image`, `document`, `tool_use` and
  `tool_result`, and excludes `thinking`, sub-content blocks (citations) and **empty `text` blocks**. It
  names neither `server_tool_use` nor `web_search_tool_result`, so their exclusion above is the bundled
  skill's claim, not the docs'. P4 part 1 therefore needs its live confirmation call before calling the
  S8 case a 400, and its walk-back must also skip empty `text` blocks.
- Lookback: confirmed, and the 20 positions count the breakpoint itself as the first; with no entry in
  the window the check stops, or resumes from the next explicit breakpoint.
- Server tools: confirmed. With at least one `cache_control` marker in the request, the API places a
  5-minute breakpoint on a server tool's result before the next in-request iteration. Whether it takes
  one of the four slots is not stated.

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
   prompt cache on. **Done by the owner on 2026-10-15 for every existing Anthropic row.**
2. **G7, decided by the owner 2026-10-15: yes.** New Anthropic rows default to prompt cache on — a
   one-line server change in the create defaults plus its test, on `feature/7836`.
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
   **G8, decided by the owner 2026-10-15: carry uncapped inside the kept window.** A result inside
   `ClearOldResults`' kept window is carried in the live step history exactly as the loop sent it, with
   no `CapResult`; a result outside the window is placeholdered as today. So the boundary re-writes
   nothing for oversized results, at the cost of a larger carried context. `Capture` →
   `AgentToolExchangeStore` / `RecordAsync` and the resume path keep the capped 4,000-char shape —
   persistence is unchanged and C1's persisted-shape tests stay untouched.
4. **The step reply is `exchange.Visible`**, which `BackgroundAssistantTurnRunner` builds by joining every
   round's text with `\n\n`, parsing think tags and stripping citations — not the final assistant
   message the model produced. Carry the final round's assistant message as received instead, and keep
   `Visible` for the UI and persistence only. This needs the final round surfaced from `AiClientService`
   (a new field or item next to `ToolRoundExchange`; the implementer picks the shape).
5. **`ClearOldResults` re-cuts every step.** It placeholders every result older than the newest 8
   (`AgentToolCarryover.KeptResults`, `:85-133`), recomputed on each step copy, so once a run has more
   than 8 results the earliest newly cleared result moves at every boundary and the prefix breaks there.
   **Clear by size (owner, 2026-10-16):** the cut is the smallest multiple of 8 for which the summed chars
   of the uncleared tail fit `CarriedResultBudgetChars` = 48,000, capped so the newest 8 always keep their
   body — a pure function of the result sizes. It replaces the first, count-based chunk rule
   (`n <= 16 ? 0 : ceil((n - 16) / 8) * 8`), which the AFTER C″ runs showed re-writing every boundary from
   s3 on: C″'s steps add 7–10 tiny write/read acks each, so the count crossed a chunk at every boundary
   (C″2: the boundaries where the cut stayed read 98.6% and 99.3%, the two where it moved 20.7% and 22.9%).
   Tiny acks cost almost nothing to carry, so under the size rule they never clear.

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

Tests: P0's prefix test goes green for both executors. `ClearOldResults` table tests by size: tiny results
never clear for 1–60 results; with bodies of 1/20 of the budget the cut moves only in multiples of 8 and only
once the tail exceeds the budget (20 → 0, 21 → 8, 29 → 16, 60 → 40); a result larger than the budget never
clears the newest 8. The existing C1 and carryover tests
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

The baseline shows no lookback miss — in-step reads were 98–99.7% — so part 2 is insurance, and P5's
before/after comparison checks that it costs nothing. Tests: marker positions for a tool-loop request, a fresh user turn, an S8 resume
body and a request with no assistant message; never more than four markers; never on an ineligible block.

### P5 — Before/after measurement and gates (both, S)

The benefit must be measured, not assumed: the same scenarios run once on the code **before** P2–P4 and
once **after**, cheaply on Claude Haiku 5.5, then validated on Sonnet 5.5 / Opus 5.5. Caches are
model-scoped, so only like is compared with like.

**P5a — Instrumentation and baseline. Runs before P2, P3 or P4 reach the stack.**

1. Server: `token_usage_logs` records cache reads but not cache writes (input = uncached + write + read).
   Add a count-only Debug line per Anthropic upstream response with `input_tokens`,
   `cache_creation_input_tokens`, `cache_read_input_tokens` and `output_tokens` — no ids, no content, like
   the replay summary in `418310ad` — and commit it on its own, before P3/P4.
2. Rows on 18081, all with prompt cache and native web search on: add a `claude-haiku-5-5` row next to the
   Opus and Sonnet ones. Basic runs route every role of the test account (default, Assistant, persona type
   `fast`) to Haiku; validation runs restore the Opus/Sonnet routing of round 1. First, one smoke call to
   confirm native web search works on the Haiku row; if it doesn't, tell the coordinator before going on.
3. Build the stack from the server commit that has the instrumentation and nothing of P3/P4, and the client
   Debug exe from a commit without P2 (P0's test-only commits are fine).
4. Fixed scenarios, saved on the throwaway profile so before and after send the same prompt, persona and
   tools:
   - **A** — a C7b-shaped agent run: 3 steps, web research on several items in step 2, a note written per
     item.
   - **B** — the C7a-shaped routine: one search, a client tool call, an answer.
   - **C** — a long run with at least 10 search turns, so P3's cut has to move.
5. Haiku: A, B and C three times each. Validation: A once on the round-1 routing (Opus plans, Sonnet runs
   the steps).
6. For every run keep the per-request table (time, role, model, input, write, read, uncached, output) and
   the replay summary lines, under `artifacts\native-replay-testing\cache-before\` in each worktree.

**P5b — Gates.** Server: the S9 gate (fast subset, Release build, format on touched files). Client: Debug
and Release rebuild at 0 warnings / 0 errors, the test exe at `Failed: 0`.

**P5c — After.** Rebuild the stack from the new server HEAD, and the client from its new HEAD. Run the same
scenarios with the same counts on Haiku, then A once on the validation routing; same tables, under
`…\cache-after\`.

**Comparison** — one table per scenario and model, before against after:

- the read share of the whole run, and per step boundary (the first request of each step);
- the cache-write tokens per step boundary;
- cost-equivalent input per run: uncached × 1 + write × 1.25 + read × the model's read rate (0.1× Haiku and
  Sonnet 5.5, 0.05× Opus 5.5), and per completed step, since runs differ in round count;
- for C, how often the replay cut moved (the replay summary's `capped`).

A change counts as a benefit only if the Haiku median beats the before median by more than the spread
between runs, and the validation run points the same way. Report any of P2, P3 or P4 that shows no
measurable benefit; the owner decides whether it stays. The bar from §1 still holds: step boundaries read
the carried prefix, the run is above 90% cache reads, no 400 or 413, 0 pair drops.

Billing: Haiku runs are cheap (input $0.10/MTok). Sonnet/Opus validation: one run before and one after; more
only with the owner's approval. At most two attempts per failed run. Tick P0–P5 in the checklist (server
steps naming the Pia commit) and add both tables to this plan.

### P5 results — measured 2026-10-16 (coordinator-verified)

Evidence: `G:\Git\Pia-7836\artifacts\native-replay-testing\` (`cache-before\`, `cache-after\`, `cache-fix\`,
`comparison.md`, generator `compare.py`); every counted run was recomputed from its CSV by the coordinator.
BEFORE: server 074a22e5 (image 72a02f00), client 5083b7da. AFTER: server 382fe727 (image 466e0502), client
96802ba0. Haiku 5.5 ran A, B and C″ three times each per phase; the validation ran A once per phase (Opus plans,
Sonnet runs the steps). Cost-eq = uncached + 1.25 × write + r × read (r = 0.1 Haiku and Sonnet, 0.05 Opus).
Rule: the AFTER median beats the BEFORE median by more than the larger of the two ranges, and the validation run
points the same way. "Suppl." columns are supplementary and do not decide. Figures are quoted from
`comparison.md`, which sums per-row rounded cost-eq, so totals can differ by ±2 from the per-run tables.

Protocol changes made during BEFORE, applied to both phases: MaxReplans 0; every run cold (≥ 6 min) with a gap
audit; requests attributed by matching client and server (input, output) pairs; ping and title kept out of the
sums; C replaced by C″ (14 licence lookups over 6 steps, at most 4 searches per step) after the original C hit
the max_uses regression and the version-based C′ hit the PII tokenizer; PII tokenization on.

**A — 3-step research run (P2)**

| metric | BEFORE median (range) | AFTER median (range) | rule |
|---|---|---|---|
| run read share | 78.8% (5.0) | 89.3% (4.0) | pass, +10.4 pts |
| s2 boundary read / write | 29.8% (5.1) / 42,040 (9,103) | 98.8% (0.3) / 706 (51) | pass |
| cost-eq per run | 216,954 (51,774) | 200,311 (80,898) | not significant |
| cost-eq per step | 72,318 (17,258) | 66,770 (26,966) | not significant |
| suppl. boundary cost-eq | 59,003 (22,659) | 33,559 (25,315) | pass, narrowly |
| suppl. cost-eq per round | 16,689 (1,370) | 13,541 (2,439) | pass, −18.9% |
| validation, 1 run each | 81.4%; s2 37.0% / 30,309; 158,035 | 90.7%; s2 98.6% / 772; 156,893 | agrees |

AFTER used more rounds (Haiku 19/18/13 against 15/13/13, validation 17 against 13), which keeps the run cost flat
while the cost per round falls; with three runs the cause cannot be attributed.

**B — routine (P4, no regression)**

The like-for-like unit is s0's first two rounds: round 1 read 56.7% → 52.9% and round 2 40.0% → 40.1% (medians),
median writes within ±300 tokens, all inside BEFORE's spread; no 400 in any run. Run totals are not comparable (BEFORE planned
1/1/1 steps, AFTER 2/1/2): 61.3% / 113,584 → 56.4% / 124,148. AFTER's two extra boundaries read 98.5% and 98.4%.

**C″ — 6 steps, 14 searches (P2 and P3)**

| metric | BEFORE median (range) | AFTER median (range) | rule |
|---|---|---|---|
| run read share | 76.5% (1.4) | 87.4% (5.7) | pass |
| cost-eq per run | 1,244,891 (21,284) | 798,157 (216,326) | pass, −36% |
| cost-eq per step | 207,482 (3,547) | 133,026 (36,054) | pass |
| replay-cut moves | 6 (1) | 2 (1) | pass |
| suppl. cost-eq per round | 33,061 (2,079) | 20,466 (4,488) | pass |
| suppl. boundary cost-eq | 300,289 (11,770) | 191,456 (134,814) | not significant |
| suppl. write: cut-move / boundary / other / total | 433,101 / 233,093 / 129,609 / 797,538 | 105,766 / 136,524 / 121,762 / 392,313 | P3 −327k, P2 −97k |

Apart from s1 (83–84% in both phases, a search inside the request), every AFTER boundary miss in C″ (20–58% read,
37–74k write) fell on a step where the count-based ClearOldResults cut moved; where it stayed, the boundary read
91–99%. The validation scenario never passes 8 anchored hits, so no
Opus/Sonnet run speaks to P3.

**Fix phase (single runs, observations)** — server 5bc4b685 (image 8bf5f8eb), Haiku:

- C0-maxuses (4b05947c, client 96802ba0, tokenization off): 16 searches in one tool loop where the old code
  stopped at 5; the budget was ≥ replayed + 5 on every request (16, with at most 8 replayed under the cap of 8);
  0 pair drops. The run failed on the client's 24-round step cap, not on search.
- C″-fix (326ad7cd, client 1343deec, size-based ClearOldResults): boundaries s1–s4 read 97.0 / 98.6 / 98.9 /
  99.1% with 584–713 write — s3 and s4 are where the count rule would have moved; run read share 89.6% over
  s0–s4; cut moved 3×, 0 pair drops. s5 was not reached: Pia.Server's own per-user AI rate limit (30 requests per
  60 s) answered 429 once Haiku's rounds ran at about 2 s.

**Verdicts**

- **P2 — benefit under the rule.** Its target, the step boundary, passes on Haiku and the validation agrees (A s2:
  29.8% → 98.8% read, 42,040 → 706 write). Run cost in A is not significant because AFTER used more rounds; P2's
  declared behaviour change (earlier steps' instructions stay visible) is a candidate cause, and settling it would
  take about three more Haiku A runs per phase. The count-based clearing that limited it in C″ is replaced by the
  size rule (1343deec), whose fix run hit every boundary it reached.
- **P3 — benefit on Haiku only; the rule's validation clause cannot be met.** Haiku C″ passes clearly (cut moves
  6 → 2, write at cut moves −75%, run cost −36% together with P2), but scenario A never passes 8 anchored hits, so
  no Opus/Sonnet run speaks to it. The owner accepted it on the Haiku evidence (2026-10-16).
- **P4 — no measurable benefit, as expected.** It is insurance; part 1 turned out to be hygiene (a marker on
  `server_tool_use` is accepted). No regression in B's in-step rounds and no 400 anywhere. The owner kept it
  (2026-10-16).
- **Bar from §1:** no 400 or 413 in any counted run; run read share above 90% only on the validation (90.7%) —
  Haiku medians 89.3% (A), 87.4% (C″), 56.4% (B); one pending→orphan pair drop in BEFORE C″1 and AFTER C″1,
  pre-existing from round 1 and fixed by 5bc4b685 (unit-tested; no live split occurred in the fix runs).

## 4. Decisions taken in this plan

- P2 carries the instruction, the round messages and the final assistant message as sent and received; the
  UI, persistence and resume keep today's shapes.
- G8 (owner, 2026-10-15): carried results are uncapped while inside the kept window and placeholdered
  outside it; the persisted and resumed shape keeps the 4,000-char cap.
- G7 (owner, 2026-10-15): new Anthropic catalog rows default to prompt cache on (server).
- `ClearOldResults` and the server replay cap both cut in chunks, stateless: the client in multiples of 8
  once the carried result bodies pass 48,000 chars, never the newest 8 (owner, 2026-10-16, replacing the
  count rule); the server in steps of `M / 2`.
- Thinking is never carried across a step boundary.
- Owner, 2026-10-16, after the P5 measurement: P3 accepted on the Haiku evidence (no Opus/Sonnet scenario
  passes 8 anchored hits); P4 kept as insurance; `ClearOldResults` clears by size (client 1343deec); the
  pair-aware replay cut (Pia 5bc4b685) and the max_uses budget that counts replayed searches (Pia 8399675f)
  landed on the server.

## 5. Not in this plan

- The 1-hour cache TTL for agent runs with long tool calls or approval waits (2× write cost; pays off only
  for gaps of 5–60 minutes).
- The compactor's token estimate, which cannot see search results (4–5.6k estimated against 43–95k
  reported in D7).
- Running title generation on a cheaper model (titles ran on Opus 5.5).
- Carrying thinking across steps on the direct path when system and tools are unchanged.
- G2b (replay across chat user turns).
