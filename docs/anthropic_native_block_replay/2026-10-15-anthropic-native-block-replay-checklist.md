# Anthropic native block replay — checklist

**Status:** Planned — steps not started; G1 decided (A, server-side store), G3 moot; G2a, G2b, G4, G5, G6 open
**Owner:** Marco Altmann
**Written:** 2026-10-15
**Origin:** [2026-10-15-anthropic-native-block-replay-plan.md](2026-10-15-anthropic-native-block-replay-plan.md)

Client detail (C steps, file and test level, cold-start header):
[2026-10-15-anthropic-native-block-replay-client-plan.md](2026-10-15-anthropic-native-block-replay-client-plan.md).
Client issue: https://github.com/Pia-Ai-dev/Pia.Wpf/issues/70 · branch `feature/anthropic-native-replay`.
Server: Azure DevOps #7836 · Pia branch `feature/7836`, plan `docs/plans/2026-10-15-anthropic-native-block-replay.md`.

`S` steps live in the server repository `G:\Git\Pia`, `C` steps in this repository. A `C` box is ticked in
the commit that lands its step. An `S` step's commit lands in the other repository, so its box is ticked
here by hand once that commit exists, naming the Pia commit in the line.

## Scales

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface · `L` a week or more, a new subsystem.

**Value** — `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| G1 | Client-carried sealed envelope or server-side store? **Decided: A** — in-process store keyed by the `toolu_` id, size-bounded, sliding TTL; prod runs one server instance per tenant. | — |
| G2a | Replay across the steps of one agent run? Under A no client code: carried calls keep their ids. Server defines the replay rule for a message with `tool_calls` but no text. Client recommends yes. | TTL choice |
| G2b | Replay across chat user turns? Under A finished turns carry no tool calls, so there is no anchor. Client recommends defer. | — (would need a new step) |
| G3 | Data Protection or HMAC? **Moot under A.** | — |
| G4 | Resume `pause_turn` in the server stream, or a distinct finish reason for the client? | S8 |
| G5 | Does the direct Anthropic provider in Pia.Wpf have the same flaw? Client finding: yes — the SDK adapter drops both web-search blocks and citations on the way back; thinking survives. | — (recommend a third plan) |
| G6 | Replay thinking blocks at all, given `drop_block` is a beta and Pia's prefix is not byte-stable? | S6b |

## Steps

- [ ] **S0 — Measure and re-verify.** Capture one real native-search response (5 searches) on the e2e stack, confirm an assistant turn with a `server_tool_use` pair and no thinking block is accepted, record the pending-search case (parallel `web_search` + client tool → `stop_reason` `tool_use` with a pending `server_tool_use`; the next response starts with the orphan result), and re-check the live docs. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [ ] **S1 — Paragraph break after a search result.** A `text` block opening after a `web_search_tool_result` gets a leading `\n\n` in the stream and buffered paths; cited text blocks stay joined. *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **S2 — Native content accumulator.** `StreamState` rebuilds every content block from start/delta/stop events, including a pending `server_tool_use` and the orphan result that opens the next response; the buffered parser keeps `content` verbatim. *Deps:* — · *Effort:* S · *Value:* Enabler
- [ ] **S3 — Native-content store.** In-process store keyed by the `toolu_` id, size-bounded, sliding TTL, kill switch; a miss falls back to reconstruction. *Deps:* — · *Effort:* S · *Value:* Enabler
- [ ] **S4 — Capture into the store.** Each response's native content is stored under its client tool call ids when reconstruction cannot reproduce it. *Deps:* S2, S3 · *Effort:* XS · *Value:* Enabler
- [ ] **S5 — Pair integrity.** A pass over the outbound request keeps every `server_tool_use` / result and `tool_use` / `tool_result` pair whole, and strips the server-internal `pause_turn` resume marker from client input. *Deps:* S3 · *Effort:* S · *Value:* High
- [ ] **S6a — Replay server-tool pairs and cited text.** `BuildAssistantContent` uses the stored native content when provider, model and tool-call ids match, else today's reconstruction. *Deps:* S4, S5 · *Effort:* S · *Value:* High
- [ ] **S6b — Replay thinking.** The store keeps `thinking` / `redacted_thinking`, and requests that replay one send the `drop_block` binding and beta header. *Deps:* G6, S6a · *Effort:* S · *Value:* Med
- [ ] **S7 — Orchestrator rounds.** Server-consumed rounds, the client handback, the final round and the cap path capture into the store; the sensitive-hit model switch falls back cleanly. *Deps:* S4, S6a · *Effort:* S · *Value:* High
- [ ] **S8 — `pause_turn` continuation.** Resume a paused turn by re-sending its native content instead of ending it as `stop`. *Deps:* G4, S6a · *Effort:* S · *Value:* Med
- [ ] **S9 — Server gate.** The Pia repository's own build, test and doc rules. *Deps:* S1–S7 · *Effort:* XS · *Value:* High
- [ ] **C1 — Pin the id guarantee.** Tests that every `tool_calls[].id` goes back verbatim on every client path — tool loop, tokenization, in-step compaction, carry-over, exchange save/restore, headless resume (audit in the client plan §2). *Deps:* — · *Effort:* S · *Value:* High
- [ ] **C2 — Duplicate id after a headless resume.** Prove or refute that an approved parked call goes out twice under one id (carried `Paused:` pair plus the seeded replay); fix by filtering the carried pair. *Deps:* — · *Effort:* S · *Value:* High
- [ ] **C3 — Measure what the client cannot see.** Log the compactor's estimate against the provider-reported input tokens per round (counts only), so S0/C7 can size the replayed input. *Deps:* — · *Effort:* XS · *Value:* Med
- ~~**C4 — Other chat clients tolerate it.**~~ Dropped under A (no new content type); kept as B-C4 in the client plan's Appendix B.
- ~~**C5 — Across user turns.**~~ Dropped under A: no persistence or sync is needed. Cross-turn replay (G2b) would be a new step.
- [ ] **C6 — Release-note text.** Drafted in the client plan, applied at the release cut (`RELEASE.md` is edited only on `release/*` / `hotfix/*`). *Deps:* C2 · *Effort:* XS · *Value:* Med
- [ ] **C7 — Client gate.** Zero-warning rebuild in Debug and Release, test exe `Failed: 0`, and the MongoDB routine end to end on the e2e stack with native WEB on. *Deps:* C1–C3, C6, S9 · *Effort:* XS · *Value:* High

## Suggested order

1. **S0** first — measures the real size and confirms the pending-search shape before the store is built.
2. **S1** and **C2** — independent quick wins; C2 is a live defect on `develop`.
3. **S2 → S3 → S4 → S5 → S6a** — the server slice; it fixes the reported loop for shipped clients too.
4. **C1** alongside — locks the id guarantee the server slice relies on.
5. **S7**, then **G2a**, **S8**, **G6 → S6b**.
6. **S9, C3, C6, C7.**

## Not yet planned

- Source chips for Pia Cloud native search (`web_citations`).
- `knowledge_citations` has no client consumer either.
- `web_search_20260209` / `web_fetch_20260209` on 5.5-family rows.
- `display: "updates"` for 5.5-family progress notes.
- Direct Anthropic provider replay (G5): convert the SDK's response-side web-search blocks back to `*Param` blocks and keep citations — a third plan.
- Forced tool choice on the direct path: the tool-call probe (`AiClientService.cs:1364`, `RequireAny`) 400s with adaptive thinking, returns `Unknown`, and `Unknown` is never cached — a paid probe per turn.
