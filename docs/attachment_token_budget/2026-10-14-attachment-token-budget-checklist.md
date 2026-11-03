# Attachment token budget — checklist

**Status:** Implemented on `feature/files-tool-pdf`; in review as a PR into `develop`
**Owner:** Marco Altmann
**Written:** 2026-10-14
**Origin:** [2026-10-14-attachment-token-budget-plan.md](2026-10-14-attachment-token-budget-plan.md)

Tick a box when its step's tests are green.

## Scales

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface · `L` a week or more, a new subsystem.

**Value** — `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| D1 | **CLOSED 2026-10-14 — run-based heuristic, letter weight calibrated under a 5 % rule (pins it at 1.0).** Which estimator decides? | A1 |
| D2 | **CLOSED 2026-10-14 — 25 % of the input window per chat, 2K–50K, fair shares per drop.** Where does the budget come from? | A2, B2, B4 |
| D3 | **CLOSED 2026-10-14 — at drop, against the persona-aware provider; no re-cut at send.** When is a file cut? | A3, B4 |
| D4 | **CLOSED 2026-10-14 — percentage snackbar, chip badge while pending, highlighted save, note in lines with a gated read_file hint.** What do user and model see? | B1, C2, D1, D2 |
| D5 | **CLOSED 2026-10-14 — yes, same magnitude in tokens, no provider coupling.** Do the @Files preview and read_file caps move to tokens too? | E1 |
| D6 | **CLOSED 2026-10-15 — develop: worktree `G:\Git\Pia-attachment-docs`, branch `feature/attachment-token-budget-docs` (`--no-track`, from `origin/develop` 6261eafd).** Which Pia-repo branch takes the Pia.Docs change? | F2 |

## Steps

- [x] **A1 — `TokenEstimator` and reference corpus.** Run-based estimate and line-boundary cut, held to o200k by a checked-in corpus (Pia.Docs EN/DE/FR, BMF table extract, C#, XAML, CJK, generated TSV) with the test-only `O200kBase` package and the `Microsoft.Bcl.Memory` 10.0.11 lift. *Deps:* — · *Effort:* S · *Value:* Enabler
- [x] **A2 — `AttachmentBudget`.** Per-chat budget from the provider's input window and the water-filling split across one drop. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **A3 — `AssistantTurnProvider`.** The persona-over-default rule in one place, used by `StartTurnAsync` now and by the drop in B4. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **B1 — Cut on the chip, note in lines.** `AttachmentCut` replaces `Truncated`/`OriginalCharCount`; the composer writes "Showing lines 1-N of M" with an optional read_file pointer; seven test files migrated. *Deps:* — · *Effort:* S · *Value:* Med
- [x] **B2 — Stage by budget.** Two-phase `TryStageAsync`: read everything, then distribute, cut at line boundaries, skip slivers under 200 tokens. *Deps:* A1, A2, B1 · *Effort:* S · *Value:* High
- [x] **B3 — Strings.** `Msg_File_Truncated` takes a percentage; `Msg_File_AttachBudget` names the chat budget and suggests removing a file or a new chat — neutral, `.de`, `.fr`, and the placeholder test row. *Deps:* B2 · *Effort:* XS · *Value:* High
- [x] **B4 — Drop reads the real budget.** The view model passes a callback: persona-aware provider's budget minus what the live chat already re-sends. *Deps:* A3, B2 · *Effort:* S · *Value:* High
- [x] **C1 — `ToReadFilePath`.** The path read_file accepts for a dropped or saved file inside the chat's working root, or null. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **C2 — Note built where tools are known.** `StartTurnAsync(…, files)` builds a provisional block, then the final one after `PrepareTurn`, naming read_file only on a turn that offers it; parked-run answers still refuse files. *Deps:* B1, C1 · *Effort:* S · *Value:* High
- [x] **C3 — Substitute migration.** The eighth `StartTurnAsync` matcher in six view-model test files. *Deps:* C2 · *Effort:* XS · *Value:* Enabler
- [x] **D1 — Chip badge.** "shortened · ~N %" with a tooltip on a cut pending chip; three new view strings. *Deps:* B1 · *Effort:* XS · *Value:* High
- [x] **D2 — Save asks for attention.** The save button turns `Caution` with its own tooltip on a cut file; real-app check with a long PDF/xlsx (owner's go to launch). Checked 2026-10-15 with a 5,000-row xlsx for drop, chip, snackbar and save; the send + `read_file` leg is not verified — the throwaway profile has no provider. *Deps:* D1 · *Effort:* XS · *Value:* High
- [x] **E1 — @Files and read_file by tokens.** Preview capped at ~3,000 and read windows at ~25,000 estimated tokens. *Deps:* A1 · *Effort:* XS · *Value:* Med
- [x] **F1 — Release note.** One bullet under `## Assistant`, after the untouched PDF bullet. *Deps:* B2, D1 · *Effort:* XS · *Value:* Med
- [x] **F2 — User guide.** Pia.Docs `attachments.mdx` EN/DE/FR: the "whole text" and "never a problem" claims corrected, a "How much of a file reaches the AI" subsection. *Deps:* D6, B2, D1 · *Effort:* XS · *Value:* High
- [x] **G1 — Gate.** Comment pass, Debug and Release rebuild at 0 warnings, full exe run `Failed: 0`, hand-off to the owner. *Deps:* every step above · *Effort:* XS · *Value:* High

## Suggested order

1. **A1** first — it is the decisive one: if the real BMF extract lands outside the band, the owner decides before anything builds on the estimator.
2. **A2, A3, C1** — small, independent enablers.
3. **B1 → B2 + B3 → B4** — the drop slice; after B4 a dropped file already arrives whole far more often.
4. **C2 + C3** — the model-facing slice.
5. **D1 + D2** — the chip slice.
6. **E1**, then **F1**, **F2** (after D6), **G1**.

## Not yet planned

- `AgentContextCompactor` still charges text at Length/4 — a numeric attachment pinned in an agent step is undercounted ~2.6×.
- The interactive tool loop has no context budget, so several large `read_file` results in one turn can overflow a small window.
- A "shortened" marker on the sent chip (needs `AttachedFileRef` and the sync DTO).
- Re-cutting at send when the provider changed after the drop.
- A token budget for Optimize / Advanced Creation drops (`DroppedFileImporter`).
- `read_file` cannot return part of one over-long line, so the note's `offset: N` for a file cut mid-line fails whenever that line exceeds the ~25K-token read window — the usual case on 128K+ models.
- The cut chip's tooltip and highlighted save button promise "Pia can read the rest" even on a turn without file tools (no tool calling, a no-tools persona, file tools switched off).
