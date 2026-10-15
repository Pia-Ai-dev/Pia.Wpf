# Anthropic native block replay — checklist

**Status:** Draft — steps not started; gates G1–G4 and G6 open
**Owner:** Marco Altmann
**Written:** 2026-10-15
**Origin:** [2026-10-15-anthropic-native-block-replay-plan.md](2026-10-15-anthropic-native-block-replay-plan.md)

`S` steps live in the server repository `G:\Git\Pia`, `C` steps in this repository. A `C` box is ticked in
the commit that lands its step. An `S` step's commit lands in the other repository, so its box is ticked
here by hand once that commit exists, naming the Pia commit in the line.

## Scales

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface · `L` a week or more, a new subsystem.

**Value** — `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| G1 | Envelope size on a real 5-search round — client-carried sealed envelope or server-side store with a reference id? | S3, and every step after it |
| G2 | Replay within a turn only, or across user turns (persist + sync)? | C5 |
| G3 | Data Protection (shared keyring?) or HMAC with a dedicated secret? | S3 |
| G4 | Resume `pause_turn` in the server stream, or a distinct finish reason for the client? | S8 |
| G5 | Does the direct Anthropic provider in Pia.Wpf have the same flaw? | — (may spawn a third plan) |
| G6 | Replay thinking blocks at all, given `drop_block` is a beta and Pia's prefix is not byte-stable? | S6b |

## Steps

- [ ] **S0 — Measure and re-verify.** Capture one real native-search response (5 searches) on the e2e stack, record raw/compressed size, confirm an assistant turn with a `server_tool_use` pair and no thinking block is accepted, and re-check the live docs for server-tool replay and preserved thinking. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [ ] **S1 — Paragraph break after a search result.** A `text` block opening after a `web_search_tool_result` gets a leading `\n\n` in the stream and buffered paths; cited text blocks stay joined. *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **S2 — Native content accumulator.** `StreamState` rebuilds every content block from start/delta/stop events, and the buffered parser keeps `content` verbatim. *Deps:* — · *Effort:* S · *Value:* Enabler
- [ ] **S3 — Envelope seal and unseal.** Versioned, model-bound, compressed, protected payload without thinking blocks; failure means fallback. *Deps:* G1, G3 · *Effort:* S · *Value:* Enabler
- [ ] **S4 — Emit the envelope.** A choiceless `pia_native` chunk before `[DONE]` and a buffered `message.pia_native`, only when reconstruction cannot reproduce the content. *Deps:* S2, S3 · *Effort:* XS · *Value:* Enabler
- [ ] **S5 — Strip centrally.** `BuildChatJson` removes `pia_native` from every message and hands the unsealed blocks to the Anthropic handler alone; a no-leak test for every other handler. *Deps:* S3 · *Effort:* S · *Value:* High
- [ ] **S6a — Replay server-tool pairs and cited text.** `BuildAssistantContent` uses the native content verbatim when provider, model and tool-call ids match, else today's reconstruction. *Deps:* S5 · *Effort:* S · *Value:* High
- [ ] **S6b — Replay thinking.** The envelope keeps `thinking` / `redacted_thinking`, and requests that replay one send the `drop_block` binding and beta header. *Deps:* G6, S6a · *Effort:* S · *Value:* Med
- [ ] **S7 — Orchestrator carries the envelope.** In-loop rounds, the client handback, the final round and the cap path; the sensitive-hit model switch falls back cleanly. *Deps:* S4, S6a · *Effort:* S · *Value:* High
- [ ] **S8 — `pause_turn` continuation.** Resume a paused turn by re-sending its native content instead of ending it as `stop`. *Deps:* G4, S6a · *Effort:* S · *Value:* Med
- [ ] **S9 — Server gate.** The Pia repository's own build, test and doc rules. *Deps:* S1–S7 · *Effort:* XS · *Value:* High
- [ ] **C1 — Parse the envelope.** `PiaCloudChatClient` yields `pia_native` from stream and buffered responses as a dedicated `AIContent` subtype. *Deps:* S4 (contract) · *Effort:* XS · *Value:* Enabler
- [ ] **C2 — Send it back.** `SerializeMessages` writes it on the assistant message; a test proves `ToChatResponse()` keeps it through the tool loop. *Deps:* C1 · *Effort:* XS · *Value:* High
- [ ] **C3 — Hand-built assistant messages.** `HeadlessTurnExecutor`, `AgentToolCarryover`, `AgentToolExchangeSerializer` (row cap), `AgentContextCompactor` and `BackgroundAssistantTurnRunner` each carry the content or drop it on purpose. *Deps:* C2 · *Effort:* M · *Value:* High
- [ ] **C4 — Other chat clients tolerate it.** No other `IChatClient` throws on the content or serializes it. *Deps:* C1 · *Effort:* XS · *Value:* Enabler
- [ ] **C5 — Across user turns.** Persist the envelope with the chat and carry it through sync. *Deps:* G2, C3 · *Effort:* M · *Value:* Med
- [ ] **C6 — Release note.** One bullet in `docs/release_notes/RELEASE.md`. *Deps:* C2 · *Effort:* XS · *Value:* Med
- [ ] **C7 — Client gate.** Zero-warning rebuild in Debug and Release, test exe `Failed: 0`, and the MongoDB routine end to end on the e2e stack with native WEB on. *Deps:* C1–C4, C6, S9 · *Effort:* XS · *Value:* High

## Suggested order

1. **S0** first — it is the decisive one: the measured size settles G1 before anything is built on the envelope.
2. **S1** — independent quick win; ships on its own.
3. **S2 → S3 → S4 → S5 → S6a** — the server slice.
4. **C1 → C2 → C4** — the client slice; after C2 the within-turn loop from the bug report is fixed end to end.
5. **S7, C3** — orchestrator rounds and the agent-run / headless paths.
6. **S8**, then **G2 → C5**, then **G6 → S6b**.
7. **S9, C6, C7.**

## Not yet planned

- Source chips for Pia Cloud native search (`web_citations`).
- `knowledge_citations` has no client consumer either.
- `web_search_20260209` / `web_fetch_20260209` on 5.5-family rows.
- `display: "updates"` for 5.5-family progress notes.
- Forced tool choice (`required` → `any`) is a 400 on Opus 5.5 / Sonnet 5.5 — check the direct-provider path.
