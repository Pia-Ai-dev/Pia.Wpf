# Anthropic native block replay — checklist

**Status:** Planned — steps not started; all gates decided 2026-10-15, including the D mechanism (D-store)
**Owner:** Marco Altmann
**Written:** 2026-10-15
**Origin:** [2026-10-15-anthropic-native-block-replay-plan.md](2026-10-15-anthropic-native-block-replay-plan.md)

| Group | Plan | Repository | Tracking |
|---|---|---|---|
| S | Pia `docs/plans/2026-10-15-anthropic-native-block-replay.md` (branch `feature/7836`) | `G:\Git\Pia` | Azure DevOps #7836 |
| C | [2026-10-15-anthropic-native-block-replay-client-plan.md](2026-10-15-anthropic-native-block-replay-client-plan.md) | this one | https://github.com/Pia-Ai-dev/Pia.Wpf/issues/70 |
| D | [2026-10-15-anthropic-direct-provider-replay-plan.md](2026-10-15-anthropic-direct-provider-replay-plan.md) | this one | https://github.com/Pia-Ai-dev/Pia.Wpf/issues/71 |

C and D land on `feature/anthropic-native-replay` as one client PR.

`S` steps live in the server repository `G:\Git\Pia`, `C` and `D` steps in this repository. A `C` or `D` box is ticked in
the commit that lands its step. An `S` step's commit lands in the other repository, so its box is ticked
here by hand once that commit exists, naming the Pia commit in the line.

## Scales

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface · `L` a week or more, a new subsystem.

**Value** — `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| G1 | Client-carried sealed envelope or server-side store? **Decided: A** — in-process store keyed by the `toolu_` id, size-bounded, sliding TTL; prod runs one server instance per tenant. | — |
| G2a | Replay across the steps of one agent run? **Decided: yes** — no client code; carried calls keep their ids, messages without text replay blocks-only. | — |
| G2b | Replay across chat user turns? **Deferred** — see "Not yet planned". | — |
| G3 | Data Protection or HMAC? **Moot under A.** | — |
| G4 | Resume `pause_turn` in the server stream, or a distinct finish reason for the client? **Decided: resume in the server stream, cap 3.** | S8 |
| G5 | Does the direct Anthropic provider have the same flaw? **Yes — decided: a third plan (group D), including the tool-call probe defect.** Mechanism decided: **D-store** (client-side store keyed by `toolu_` id, the server's replay rule). | D1–D4 |
| G6 | Replay thinking blocks at all? **Deferred.** | S6b |

## Steps

- [ ] **S0 — Measure and verify, live.** `AnthropicNativeReplayLiveTests` on `claude-opus-5-5` and `claude-sonnet-5-5` (key from the User environment scope, `PIA_TEST_CLAUDE_MODEL`), adaptive thinking, `web_search_20250305`: size of a 5-search round; replay without thinking is accepted; pairs carry no model binding (replayed under the other model); the pending case (parallel search + client tool → orphan result opens the next response, `tool_result` + text is a 400); completed pairs with web search off; `pause_turn`; a blocks-only turn opening with `server_tool_use` is accepted (case 7); a duplicate `toolu_` id in one request — 400 today? (case 8). *Deps:* — · *Effort:* XS · *Value:* Enabler
- [ ] **S1 — Paragraph break after a search result.** A `text` block opening after a `web_search_tool_result` gets a leading `\n\n` in the stream and buffered paths; cited text blocks stay joined. *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **S2 — Native content accumulator.** `StreamState` rebuilds every content block from start/delta/stop events — including an orphan result as the first block, a trailing pending `server_tool_use`, `max_uses_exceeded` and unknown block types — and the buffered parser keeps `content` as parsed; forwarded output stays byte-identical. *Deps:* — · *Effort:* S · *Value:* Enabler
- [ ] **S3 — Native-content store.** `AnthropicNativeContentCache` (modelled on `OpenAiReasoningItemCache`): every `toolu_` id of a response aliases one entry; sliding 120-min TTL refreshed on read for the entry and all its aliases; bounded by `AnthropicNativeReplayCacheMaxChars`; `AnthropicNativeReplayEnabled` kill switch; a miss is today's behaviour. *Deps:* — · *Effort:* S · *Value:* Enabler
- [ ] **S4 — Capture into the store.** The Anthropic handler stores a response's blocks (thinking filtered out) under its client tool call ids when it holds a server-tool pair or a citation; a `pause_turn` response is also stored under its message id for S8. *Deps:* S2, S3 · *Effort:* XS · *Value:* Enabler
- [ ] **S5 — Pair integrity and dedupe.** `EnforceServerToolPairs` runs before replay: the last occurrence of a repeated `toolu_` id wins and every earlier `tool_use` + `tool_result` with that id is dropped (the parked-resume shape — defence in depth behind C2); pending and orphan pairs are kept only where legal; server-tool blocks are dropped when the row has web search off. After replay, a repeated `srvtoolu_` id keeps only its last pair. *Deps:* S3 · *Effort:* S · *Value:* High
- [ ] **S6a — Replay.** Anthropic handler only; matched on the message's tool call ids alone (ordered subset), no model check for pairs or citations. Verbatim when the normalized text matches, otherwise blocks-only (client text first, then the stored pairs and `tool_use` blocks); stored `tool_use` input wins over the client's args; only the newest `AnthropicNativeReplayMaxMessages` = 8 anchored messages replay; a miss is byte-identical to today. No thinking. *Deps:* S4, S5 · *Effort:* S · *Value:* High
- [ ] **S6b — Replay thinking.** The store keeps `thinking` / `redacted_thinking`, and requests that replay one send the `drop_block` binding and beta header. *Deps:* G6, S6a · *Effort:* S · *Value:* Med
- [ ] **S7 — Orchestrator, proof only.** No production change under A: tests that a server round's native blocks replay on its server-only message, a client handback's ids hit on the next request, the cap round stays legal, and a sensitive-hit switch replays pairs on another Anthropic model and never reaches a non-Anthropic handler. *Deps:* S4, S6a · *Effort:* S · *Value:* High
- [ ] **S8 — `pause_turn` continuation.** The proxy resumes a paused turn inside the client stream from the stored content (internal resume marker, stripped from client input), cap 3 continuations, usage summed across legs. *Deps:* G4, S4, S6a · *Effort:* S · *Value:* Med
- [ ] **S9 — Server gate.** The Pia repository's build, test, format and doc rules, the S0 live test once with a key, and the MongoDB routine e2e on 18081. *Deps:* S1–S8 · *Effort:* XS · *Value:* High
- [x] **C1 — Pin the id guarantee.** Tests that every `tool_calls[].id` goes back verbatim on every client path — tool loop, tokenization, in-step compaction, carry-over, exchange save/restore, headless resume (audit in the client plan §2). *Deps:* — · *Effort:* S · *Value:* High
- [x] **C2 — Duplicate id after a headless resume.** Prove or refute that an approved parked call goes out twice under one id (carried `Paused:` pair plus the seeded replay); fix by filtering the carried pair. *Deps:* — · *Effort:* S · *Value:* High — confirmed: one call went out twice (two with a withheld sibling); the replay now drops the carried pair it re-seeds.
  - [x] **C2a — Carried result after a second resume.** The replay also writes its real result into the carried `Result` row (tokenized, capped like the carry-over), so a park → resume → park → resume run no longer carries the first call as `Paused:`. *Deps:* C2 · *Effort:* XS · *Value:* High
- [x] **C3 — Measure what the client cannot see.** Log the compactor's estimate against the provider-reported input tokens per round (counts only), so S0/C7 can size the replayed input. *Deps:* — · *Effort:* XS · *Value:* Med
- ~~**C4 — Other chat clients tolerate it.**~~ Dropped under A (no new content type); kept as B-C4 in the client plan's Appendix B.
- ~~**C5 — Across user turns.**~~ Dropped under A: no persistence or sync is needed. Cross-turn replay (G2b) would be a new step.
- [x] **C6 — Release-note text.** Drafted in the client plan, applied at the release cut (`RELEASE.md` is edited only on `release/*` / `hotfix/*`). *Deps:* C2 · *Effort:* XS · *Value:* Med
- [ ] **C7 — Client gate.** Zero-warning rebuild in Debug and Release, test exe `Failed: 0`, and the MongoDB routine end to end on the e2e stack with native WEB on (key from the User-scope `PIA_TEST_CLAUDE_KEY`). *Deps:* C1–C3, C6, S9 · *Effort:* XS · *Value:* High
- [ ] **D0 — Spike and fixture.** Scratchpad only: does `ToChatResponse` keep streamed thinking signatures; record a real two-search fixture for D1/D3. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [ ] **D1 — Native content accumulator.** `AnthropicRequestHandler` tees the response without buffering and rebuilds its content blocks. *Deps:* D0 · *Effort:* M · *Value:* Enabler
- [ ] **D2 — Store.** Process-wide store keyed by `toolu_` id, 120-min sliding TTL, char-bounded LRU. *Deps:* D1 · *Effort:* S · *Value:* Enabler
- [ ] **D3 — Replay rewrite.** `Rewrite` replays stored blocks with the server's rule (verbatim when the text matches, else blocks-only), at most 8 messages, thinking untouched. *Deps:* D2 · *Effort:* S · *Value:* High
- [ ] **D4 — Thinking-binding guard.** A replayed entry is never evicted while live; `drop_block` binding only if e2e shows a mismatch 400. *Deps:* D3 · *Effort:* XS · *Value:* High
- [x] **D5 — Probe without thinking.** A forced-tool request (`tool_choice` `any`/`tool`) gets `thinking: disabled`; ordinary turns unchanged. *Deps:* — · *Effort:* XS · *Value:* High
- [x] **D6 — Don't re-pay a failed probe.** `ProviderCapabilityService` caches `Unknown` for a short expiry. *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **D7 — Direct-provider gate.** Rides with C7, plus e2e on a direct Anthropic row with thinking and web search: no retraction, no 400, probe reports `Capable`. *Deps:* D0–D6 · *Effort:* XS · *Value:* High

## Suggested order

1. **S0** first — measures the real size and confirms the pending-search and duplicate-id shapes.
2. Quick wins, independent of everything else: **S1**, **C2** (live defect), **D5 + D6** (live defect),
   **D0** (fixture for D1/D3).
3. **S2 → S3 → S4 → S5 → S6a** — the server slice; it fixes the reported loop for shipped clients too.
   **C1** alongside — locks the id guarantee the server relies on.
4. **D1 → D2 → D3 → D4** — D-store.
5. **S7 → S8**.
6. **S9**, then **C3, C6, C7, D7** — one client PR.

## Not yet planned

- **G2b** — replay across chat user turns. Finished turns are re-sent as text only, so there is no anchor; it needs each turn's tool exchanges persisted and re-sent (client plan §5).
- **G6 → S6b** — thinking replay on the Pia Cloud path.
- Source chips for Pia Cloud native search (`web_citations`).
- `knowledge_citations` has no client consumer either.
- `web_search_20260209` / `web_fetch_20260209` on 5.5-family rows.
- `display: "updates"` for 5.5-family progress notes.
