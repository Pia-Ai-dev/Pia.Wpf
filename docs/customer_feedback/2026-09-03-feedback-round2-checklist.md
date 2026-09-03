# Customer feedback round 2 — checklist

**Status:** In progress — the five ungated XS steps have landed
**Owner:** Marco Altmann
**Written:** 2026-09-03
**Origin:** [2026-09-03-feedback-round2-plan.md](2026-09-03-feedback-round2-plan.md)

Tick a box in the commit that lands it.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

## Decision gates

Do not tick a step below an open gate without revisiting it.

| Gate | Question it answers | Blocks |
|------|--------------------|--------|
| G1 | Should a second press of the Optimize hotkey still close the window, or should it only ever show and focus, like every other mode? | A2 |
| G2 | Does the 10,000-character cap stay a client constant mirroring the server default, or does the server start telling the client its limit? | B1 |
| G3 | On a live run, is the snackbar's X unclickable or clickable-but-inert? | F1 |
| G4 | What does the customer actually see when restoring after a minimize, and on which path? | G1a |
| G5 | PDF via PdfPig text extraction, via `Windows.Data.Pdf` page rendering, or not at all? | H1 |

## Steps

- [x] **A1 — Stop the hotkey repeating.** Pass `MOD_NOREPEAT` when registering a global
      hotkey so holding the combo fires once instead of toggling the window continuously.
      *Deps:* — · *Effort:* XS · *Value:* High
- [ ] **A2 — Settle the Optimize/Assistant hotkey asymmetry.** Apply whichever direction G1
      picks to `CanDismissWithHotkey`, and cover it with a test per mode.
      *Deps:* A1, G1 · *Effort:* XS · *Value:* Med
- [ ] **B2 — Localize the over-length rejection.** Map the proxy's 400 for an over-long
      Optimize payload to a sentence that names the limit and the text's length.
      *Deps:* — · *Effort:* XS · *Value:* High
- [ ] **B1 — Character counter on the Optimize composer.** Show a live count once the text
      nears the cap, in the danger brush past it, only for a Pia Cloud provider.
      *Deps:* B2, G2 · *Effort:* S · *Value:* Med
- [x] **C1 — Unclip the template card's buttons.** Let the card footer wrap so Edit and
      Delete stay reachable on a user-created template at every column count.
      *Deps:* — · *Effort:* XS · *Value:* High
- [x] **E2 — Keep a column's expansion across a reload.** Preserve `IsExpanded` by column id
      when `LoadTodosAsync` rebuilds the board, so adding or removing a task no longer
      collapses Closed.
      *Deps:* — · *Effort:* XS · *Value:* High
- [x] **E1 — Lock in restore-to-default-column.** A test that a reopened task lands in the
      `IsDefaultView` column, not the first one.
      *Deps:* — · *Effort:* XS · *Value:* Enabler
- [x] **H2 — Name the size limit in the rejection message.** The too-large strings state the
      cap, the way Outlook's does; the image path keeps its generic wording until its
      failure reasons are separated.
      *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **G2a — A named offline failure.** Catch a name-resolution or connect failure ahead of
      the generic arm and surface a localized, actionable sentence instead of a raw English
      socket message in the chat bubble.
      *Deps:* — · *Effort:* S · *Value:* High
- [ ] **D1 — User-set chat titles.** Rename from the history row and the chat header through
      the existing title-only writer, with a hand-set title immune to auto-titling.
      *Deps:* — · *Effort:* S · *Value:* High
- [ ] **F1 — Repro then fix the snackbar's close button.** Drive Reminders → Dismiss all in
      a live run, establish which half of G3 is true, and fix that half.
      *Deps:* G3 · *Effort:* S · *Value:* Med
- [ ] **G1a — Repro then fix restore-after-minimize.** Reproduce what the customer sees,
      then decide between leaving minimize as a real minimize and smoothing the
      hide-and-restore sequence.
      *Deps:* G4 · *Effort:* S · *Value:* Med
- [ ] **H1 — PDF drop.** Whatever G5 picks, behind the existing `ReadResult` contract so the
      chip, the wrapper and the caps are untouched.
      *Deps:* G5 · *Effort:* S · *Value:* High
- [ ] **C2 — Optimize templates as master–detail.** Rebuild the templates section in the
      Personas/Routines shape: row list, then placeholder / detail / inline editor.
      *Deps:* C1 · *Effort:* M · *Value:* Med
- [ ] **C3 — Providers as master–detail.** The same rebuild for the Providers tab.
      *Deps:* C2 · *Effort:* M · *Value:* Med

## Not yet planned

- Scanned PDFs (no text layer) — needs page rendering plus vision, or OCR.
- Separating `ImageAttachmentProcessor.TryPrepare`'s failure reasons so the image
  rejection can name its own limit.
- The server-side half of G2, if the answer is that the server should publish its limits.

## Suggested order

The cheap decisive fixes first, each one landable on its own:

1. **A1**, **E2**, **C1**, **H2** — four XS fixes, four separate reports closed, no gates.
2. **B2**, **E1** — still XS, and B2 makes B1 optional rather than urgent.
3. **G2a**, **D1** — the two S items with no gate between them and the user.
4. **F1**, **G1a** — one live session covers both repros; fix whatever it shows.
5. **B1**, **H1** — once G2 and G5 are answered.
6. **C2**, **C3** — the redesign, last, because C1 already bought the relief.
