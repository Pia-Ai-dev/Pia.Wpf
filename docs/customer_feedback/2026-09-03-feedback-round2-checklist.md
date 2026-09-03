# Customer feedback round 2 — checklist

**Status:** In progress — A1, A2, B2, C1, E1, E2, G2a, H1, H2 landed; F1 half-done
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
| G1 | **ANSWERED 2026-09-03: the Assistant gains the toggle.** Extend the empty-composer dismiss rule to it rather than taking it away from Optimize. | A2 |
| G2 | **ANSWERED 2026-09-03: the server publishes its limits.** B1 reads the cap from a payload the client fetches, and needs a matching change in the server repo plus a version-skew fallback. | B1 |
| G3 | The command is not inert and the flow scrim that outranked the snackbar is fixed — does a live Dismiss all still show an X that does nothing? | F1 |
| G4 | What does the customer actually see when restoring after a minimize, and on which path? | G1a |
| G5 | **ANSWERED 2026-09-03: PdfPig.** Text extraction behind the existing `ReadResult` contract; the OS route has no text API and does not fit the chip path. | H1 |

## Steps

- [x] **A1 — Stop the hotkey repeating.** Pass `MOD_NOREPEAT` when registering a global
      hotkey so holding the combo fires once instead of toggling the window continuously.
      *Deps:* — · *Effort:* XS · *Value:* High
- [x] **A2 — Settle the Optimize/Assistant hotkey asymmetry.** Apply whichever direction G1
      picks to `CanDismissWithHotkey`, and cover it with a test per mode.
      *Deps:* A1, G1 · *Effort:* XS · *Value:* Med
- [x] **B2 — Localize the over-length rejection.** Map the proxy's 400 for an over-long
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
- [x] **G2a — A named offline failure.** Catch a name-resolution or connect failure ahead of
      the generic arm and surface a localized, actionable sentence instead of a raw English
      socket message in the chat bubble.
      *Deps:* — · *Effort:* S · *Value:* High
- [ ] **D1 — User-set chat titles.** Rename from the history row and the chat header through
      the existing title-only writer, with a hand-set title immune to auto-titling.
      *Deps:* — · *Effort:* S · *Value:* High
- [ ] **F1 — Repro the snackbar's close button.** One blocker is fixed (the flow scrim
      outranked the presenter); a live Reminders → Dismiss all still has to say whether that
      was the one the customer hit.
      *Deps:* G3 · *Effort:* S · *Value:* Med
- [ ] **G1a — Repro then fix restore-after-minimize.** Reproduce what the customer sees,
      then decide between leaving minimize as a real minimize and smoothing the
      hide-and-restore sequence.
      *Deps:* G4 · *Effort:* S · *Value:* Med
- [x] **H1 — PDF drop.** Whatever G5 picks, behind the existing `ReadResult` contract so the
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
