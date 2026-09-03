# Checklist — routines editor refresh

**Status:** steps 1–7 and 10–11 landed on `feature/routines-editor-refresh`; the live routine runs (8, 9)
are open.
**Owner:** Marco Altmann. **Written:** 2026-09-02.
**Origin:** [`2026-09-02-routines-editor-refresh.md`](2026-09-02-routines-editor-refresh.md), which is
the plan this tracks.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| G1 — the live runs (step 8) | Does a 320-character body still produce sourced, dated output, and does `meeting-followup` still extract sane action items without its evidence-quality step? | Step 9. A bad `meeting-followup` run means the 320 bar needs a carve-out for the one card that writes. |

## Steps

- [x] **1. Shorten the twenty bodies and split the guards.** Rewrite every body to ≤ 320 characters,
  shorten `WebSearchGuard` to one sentence, and add `ReadOnlyGuard` and `WriteGuard` so the clauses
  the six `your-data` cards repeat stop living in each body. Carry `GuardKey` on the record.
  *Deps:* — · *Effort:* S · *Value:* High
- [x] **2. Move the templates and slot defaults to resx.** `QueryTemplate` → `QueryKey`,
  `RoutineSlot.Default` → `DefaultKey`, guards to `Routines_Catalog_*`; add
  `RoutineBlueprintText.Resolve` and rework `ToCreateArgs` to take it. Write all 60 template strings
  and 42 slot defaults across en/de/fr.
  *Deps:* 1 · *Effort:* M · *Value:* High
- [x] **3. Move the catalog tests to per-locale.** Braces, declared slots, renders-from-defaults and
  the new length bar run for en/de/fr off `ViewStrings.ResourceManager`; delete
  `ATemplateThatQuotesItsOwnDefault_QuotesItVerbatim` with the placeholder branches it guarded, and
  leave the "search the web" phrase test `en`-only with a comment.
  *Deps:* 2 · *Effort:* S · *Value:* Enabler
- [x] **4. Required fields.** `CanSave` plus `[NotifyPropertyChangedFor]` on the six fields,
  `Routines_RequiredHint` with `PiaRequiredHintStyle`, `IsEnabled` on Save, and `*` in the three new
  editor-only label keys. Keep the URL and time-format checks in `SaveAsync`.
  *Deps:* — · *Effort:* XS · *Value:* High
- [x] **5. Two-column editor layout.** Kind|Recurrence, recurrence-detail|Time, Provider|Persona,
  Effort alone; drop the fixed 240 widths; Goal `MinHeight` 60 → 150.
  *Deps:* — · *Effort:* XS · *Value:* Med
- [x] **6. Generate with AI.** `RoutineDraft`, `GenerateRoutineDraftAsync` on
  `ITextOptimizationService`, the describe box and draft button in the editor, the prefill-only-unset
  rule, and a `catch` writing `Routines_Draft_Failed` — the gap the persona command has.
  *Deps:* 1 · *Effort:* M · *Value:* High
- [x] **7. Automation ids and the playbook.** A `Routines_*` id on every control steps 4–6 add, the
  matching `[InlineData]` rows in `ViewAutomationIdTests`, and the playbook lines.
  *Deps:* 4, 5, 6 · *Effort:* XS · *Value:* Enabler
- [ ] **8. The hand checks.** The UI half is **done** — a maximized throwaway-profile pass on 2026-09-02
  read the prefilled goal in en, de and fr, saw Save disable and `Routines_RequiredHint` appear on a
  cleared Name, and confirmed the draft command reports `Routines_Draft_Failed` rather than throwing when
  no provider is set up. Still owed, because a throwaway profile has no provider: **one real draft** from a
  sentence, and **one shortened web routine plus one shortened `your-data` routine fired for real** — per
  §10.5 of the plan, which also closes the never-ticked step 10 of the 2026-08-24 checklist. Also unchecked:
  the paired dropdowns at the *narrowest* pane width; the pass ran maximized.
  *Deps:* 7 · *Effort:* XS · *Value:* High
- [x] **10. Drafted tool grants.** `RoutineDraft.Tools`, the catalogue handed to the drafting model, the
  offer filtered by the same create-time rule the tool path applies, the reply intersected with the offer,
  and applied only when nothing is ticked. Five tests.
  *Deps:* 6 · *Effort:* S · *Value:* High
- [x] **11. Tie the slot field to the instruction.** Read-only coloured goal until clicked, plain box after.
  Chosen over a RichTextBox by the owner once the `TextBox` route was measured dead. Nine tests across the
  span and the two states.
  *Deps:* 5 · *Effort:* S · *Value:* High
- [ ] **9. Revisit `meeting-followup`.** It is the only card with a write grant and the only one whose
  cut removed a real quality step (state whether the transcript is complete and whether the speaker
  labels are real, before extracting). Decide from the live run whether `WriteGuard` covers it or the
  320 bar needs one carve-out.
  *Deps:* 8 (G1) · *Effort:* XS · *Value:* Med

## Not yet planned

- **Slot-first editor** — slots as the primary surface with the goal collapsed behind an expander.
  Owner chose "shorten only" on 2026-09-02, then step 11 answered the same complaint by tinting the slot
  value inside the instruction instead. Only revisit if that still does not land.
- **An always-editable coloured goal** — needs a `RichTextBox`, which has no plain string binding, so the
  text would be synced by hand in both directions. Declined 2026-09-02 for the caret, undo and paste
  behaviour that costs; the read-only-until-clicked swap gets the colour with none of it.
- **A second slot per blueprint** — declined on 2026-09-02 for translation cost. Revisit per card if
  a specific default keeps getting hand-edited in the goal box.
- **Localized search keywords** — still open from the 2026-08-24 checklist. Now cheaper to justify:
  once the goal text is localized, a German user typing "Aktien" failing to match
  "Börsenüberblick" is the last English-only surface left in the catalog.
- **Warn when the pinned provider cannot search** — step 11 of the 2026-08-24 checklist, untouched
  here.

## Suggested order

1, 2, 3 as one vertical slice (the bodies have to be shortened in the same edit that writes them to
resx, or all sixty strings get touched twice), then 4 and 5 which are independent and cheap, then 6,
then 7. 8 and 9 are the human round at the end.
