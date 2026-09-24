# Agent-run visibility — checklist

**Status:** Done
**Owner:** Marco Altmann
**Written:** 2026-09-12
**Origin:** [2026-09-12-agent-run-visibility.md](2026-09-12-agent-run-visibility.md)

Tick a box in the commit that lands it.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.

**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## A — the blank bubble

- [x] **A1 Skip the empty headless row.** Do not persist a step reply whose visible text is empty;
      keep the model-context message so later steps still see the step ran.
      *Deps:* — · *Effort:* XS · *Value:* High
- [x] **A2 Collapse an empty shell in the transcript.** `AssistantMessage.IsEmptyShell` (no content,
      not streaming, no cards/sources/files/attachment) collapses the whole item template, avatar
      included, so rows already on disk stop rendering.
      *Deps:* — · *Effort:* XS · *Value:* High
- [x] **A3 Tests.** A1 against an empty and a non-empty exchange; A2 across the shell/streaming/
      content-bearing cases.
      *Deps:* A1, A2 · *Effort:* XS · *Value:* Med

## B — approval accounting

- [x] **B1 Pair a park row with its replay.** One merged row per approved call, keyed on `CallId`
      with a tool-name + `Seq` fallback within the step.
      *Deps:* — · *Effort:* S · *Value:* High
- [x] **B2 Label a paired row "Approved".** A person answered it; an unpaired `GrantedByName` stays
      auto-approved, because a scheduled job's envelope reaches the gate the same way.
      *Deps:* B1 · *Effort:* XS · *Value:* High
- [x] **B3 Recompute the pills off the merged list.** The summary counts what happened, not how many
      gate evaluations it took.
      *Deps:* B1 · *Effort:* XS · *Value:* High
- [x] **B4 Tests.** Park+replay pairs to one Approved row; a bare `GrantedByName` stays
      auto-approved; two same-tool calls with one park row do not over-merge.
      *Deps:* B1, B2, B3 · *Effort:* S · *Value:* High

## C — live insight

- [x] **C1 `CurrentActivity` on the run panel VM.** Latest event of the active step plus elapsed;
      before the first tool round, the step name and elapsed alone.
      *Deps:* — · *Effort:* S · *Value:* High
- [x] **C2 Auto-expand the activity band.** On the transition into an executing state only — a band
      the user collapsed mid-run stays collapsed.
      *Deps:* — · *Effort:* XS · *Value:* Med
- [x] **C3 Live pseudo-bubble in the chat.** Pinned below the transcript, bound to the same
      `CurrentActivity`, shown while `ForeignRunActive`.
      *Deps:* C1 · *Effort:* S · *Value:* High
- [x] **C4 Tests.** `CurrentActivity` projection and its empty state; the auto-expand transition
      fires once; the bubble's visibility gate.
      *Deps:* C1, C2, C3 · *Effort:* S · *Value:* Med

## Cross-cutting

- [x] **X1 Localize the hardcoded status.** `AssistantMessage.cs:22` ships English `"Thinking..."`
      although `Msg_Assistant_StatusThinking` exists in all three resx.
      *Deps:* — · *Effort:* XS · *Value:* Med
- [x] **X2 New strings in en/de/fr.** Parity is test-enforced.
      *Deps:* B2, C1 · *Effort:* XS · *Value:* Enabler
- [x] **X3 Gate + zero warnings.** Rebuild Debug and Release, run the test exe detached.
      *Deps:* all · *Effort:* XS · *Value:* High
- [x] **X4 Release notes.** Three user-visible changes into `docs/release_notes/RELEASE.md`.
      *Deps:* all · *Effort:* XS · *Value:* Med

## Suggested order

A1 → A2 → A3 (cheapest, and it removes the artefact the user is staring at), then X1 alongside.
B1 → B2 → B3 → B4 as one vertical slice — the pairing decides the labels and the pills, so splitting
the commits would land a half-honest summary.
C1 → C2 → C3 → C4 last: C1 is the shared data path, and C3 is only a second consumer of it.
X2 rides whichever slice introduces its keys; X3 and X4 close the branch.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| A1 | Does a step legitimately complete with no visible text, or is silence always a failure worth reporting? Resolved: legitimate — `emit_step_result{true}` needs no prose, so skip rather than synthesize. | A2 wording, A3 |
| B1 | Can a park row always be paired with its replay? `CallId` is reused by the replay, but the park arm writes one row for two same-tool calls. | B2, B3 |
