# Advanced Creation + templates parity — checklist

**Status:** Groups A, B and step C1 landed; C2/C3 fold into E1.
**Owner:** Marco Altmann
**Written:** 2026-09-13
**Origin:** Owner request, 2026-09-13 (advanced creation overlay; optimize templates
restyled to the routines/personas pattern).

| Group | Plan |
|---|---|
| A, B | [2026-09-13-templates-editor-parity.md](2026-09-13-templates-editor-parity.md) |
| C, D, E, F | [2026-09-13-advanced-creation.md](2026-09-13-advanced-creation.md) |

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a
new surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

## Decision gates

Do not tick a dependant of an open gate without revisiting it.

All four are answered (owner, 2026-09-13).

| Gate | Question it answers | Answer |
|---|---|---|
| G-1 | Does the templates editor stay embedded in Settings → Optimize, or become its own category? | **Embedded**, as `PersonasView` is in Assistant settings. |
| G-2 | Scrolling transcript, or one question card at a time? | **One card at a time**, with a collapsed summary line above it and the answered turns reachable behind it. |
| G-3 | Six ask-turns, at most three questions per turn? | **Yes.** |
| G-4 | Does *Advanced…* appear for every provider? | **Yes**, no context-length gate. |

G-2 leaves one thing under-specified that the mockup implies: a turn counter. The model
never says how many turns remain, so dots against the cap of six would read "2 of 6" when
the interview is one turn from done. The indicator therefore **grows** — one dot per turn
answered plus the current one — rather than drawing an unknown total up front.

## Group A — Templates editor parity

- [x] **A1 · Extract `TemplatesView`.** New UserControl carrying the master–detail–editor
      layout, embedded in `SettingsViews/OptimizeView.xaml` below the output settings.
      *Deps:* G-1 · *Effort:* M · *Value:* High
- [x] **A2 · Split `TemplatesSettingsViewModel` out of `OptimizeSettingsViewModel`.**
      `SelectedTemplate`, `IsEditorOpen`, `Editor`, `ShowsDetail`, `ShowsPlaceholder`,
      shaped like `PersonaSettingsViewModel`; registered in `Bootstrapper.cs`.
      *Deps:* G-1 · *Effort:* M · *Value:* High
- [x] **A3 · Extend `TemplateEditModel`.** It already existed; it gained `Description`,
      preserves `CreatedAt` across an edit and now stamps `ModifiedAt`.
      *Deps:* A2 · *Effort:* S · *Value:* Enabler
- [x] **A4 · Delete `TemplateEditContentDialog`.** Remove the XAML and code-behind and
      repoint `AddTemplateAsync` / `EditTemplateAsync` / `ViewTemplatePromptAsync` at the
      inline editor. *Deps:* A1, A2, A3 · *Effort:* S · *Value:* Med
- [x] **A5 · Automation ids and test rows.** `Templates_*` ids on every new control,
      per-row `StringFormat` ids in the `ListBox`, a `TemplatesView` `[InlineData]` row,
      and a recount of the `SettingsViews.OptimizeView` row (`:54`) now that the templates
      markup has moved out. Editor fields keep their `TemplateEdit_*` ids; list and
      detail controls take `Templates_*`. Correct the three
      `docs/ui_automation/` docs that describe these ids as living in a ContentDialog —
      nothing under `tests/ui-scripts/` references them, so no recording breaks.
      *Deps:* A4 · *Effort:* S · *Value:* High

## Group B — Templates draft parity

- [x] **B1 · Add `TemplateDraft` + `ParseTemplateDraft`.** `Name`, `Description`,
      `StyleDescription`, `Prompt`, parsed the way `ParsePersonaDraft` is, so all three
      subjects terminate in the same kind of record. *Deps:* A3 · *Effort:* S · *Value:* Enabler
- [x] **B2 · One-shot draft button in the new editor.** The existing
      `GeneratePromptAsync` behaviour, now returning a `TemplateDraft` that fills name and
      description too. *Deps:* B1 · *Effort:* S · *Value:* Med

## Group C — Overlay shell capability

- [x] **C1 · `MaxPanelHeight` + scrolling body.** New dependency property on
      `OverlayDialogPanel`; wrap the template's `ContentPresenter` in a `ScrollViewer`.
      *Deps:* G-2 · *Effort:* S · *Value:* Enabler
- [ ] **C2 · Inline confirm and error regions.** A discard confirmation and an error
      banner that render inside the panel, because `DialogOverlayHost` is single-slot and
      `IDialogService` would orphan the panel's `TaskCompletionSource`.
      *Deps:* C1 · *Effort:* S · *Value:* High
- [ ] **C3 · Escape override.** `OnEscapePressed` confirms instead of closing once at
      least one answer has been given. *Deps:* C2 · *Effort:* XS · *Value:* Med

## Group D — Interview engine

- [ ] **D1 · `AdvancedCreationMode` + the three instances.** Subject preamble, draft-key
      block, extra context, localization keys. Routines pass `OfferableDraftTools()`.
      *Deps:* B1 · *Effort:* S · *Value:* Enabler
- [ ] **D2 · `IAdvancedCreationService` + envelope parser.** `StartAsync` / `AnswerAsync`
      over the existing streaming path, turn cap, empty-stream retry, tolerant JSON
      extraction. *Deps:* D1, G-3 · *Effort:* M · *Value:* High
- [ ] **D3 · Parser tests.** Empty, prose, fenced JSON, unknown `kind`, `done` without
      `draft`, `ask` without questions, over-long question list, turn-cap termination.
      Mirrors `RoutineDraftFailureTests`. *Deps:* D2 · *Effort:* S · *Value:* High

## Group E — Interview UI

- [ ] **E1 · `AdvancedCreationOverlayPanel`.** `OverlayDialogPanel` subclass in
      `Views/Dialogs/Overlay/`, copying `PolicyRestartOverlayPanel` — including its
      explicit `Style="{StaticResource {x:Type controls:OverlayDialogPanel}}"`, without
      which a subclass renders untemplated. Opening prompt, transcript, question region,
      live draft summary, Primary enabled only on `done`.
      *Deps:* C3, D2, G-2 · *Effort:* M · *Value:* High
- [ ] **E2 · Question `DataTemplateSelector`.** One template per `kind` — `text`,
      `longtext`, `choice`, `multichoice`, `sample` — shared by all three modes. This is
      what makes the look and feel identical. *Deps:* E1 · *Effort:* M · *Value:* High
- [ ] **E3 · Localization.** Every string through `loc:Str` into `ViewStrings.resx`,
      `.de.resx` and `.fr.resx` together. *Deps:* E2 · *Effort:* S · *Value:* High
- [ ] **E4 · Automation ids.** `AdvancedCreation_*` on fixed controls, the per-item
      `{Binding Id, StringFormat='AdvancedCreation_Q_{0}'}` form on question rows, plus
      the `ViewAutomationIdTests` row — which works here, because `Take` walks the
      logical tree and declared templates, not the unapplied `ControlTemplate`. The
      panel's Primary/Secondary/Close need no id: UIA falls back to their template
      `x:Name`, so they are already `PART_PrimaryButton` and friends.
      *Deps:* E2 · *Effort:* S · *Value:* High

## Group F — Wiring and close-out

- [ ] **F1 · `IAdvancedCreationLauncher` + the three entry buttons.** *Advanced…* beside
      each existing Sparkle button, never replacing it. *Deps:* E4, G-4 · *Effort:* S · *Value:* High
- [ ] **F2 · Apply through the existing paths.** Routine drafts go through
      `AcceptableDraftTools()` and `ApplyWebSearchGuard()`; personas through
      `PersonaEditModel`; templates through `TemplateEditModel`. *Deps:* F1 · *Effort:* S · *Value:* High
- [ ] **F3 · Apply-path tests.** Including an invented tool name dropped on the interview
      path — the one failure that can reach a stored grant. *Deps:* F2 · *Effort:* S · *Value:* High
- [ ] **F4 · Privacy-logging pass.** Opening sentence, questions, answers and drafts on
      `SensitiveDebug`; turn index, question count and `state` at normal level.
      *Deps:* F2 · *Effort:* XS · *Value:* High
- [ ] **F5 · Zero-warning rebuild, Debug and Release.** `dotnet build -t:Rebuild -v:n`
      and again with `-c Release`; read the count off the `N Warning(s)` line.
      *Deps:* F4 · *Effort:* XS · *Value:* High
- [ ] **F6 · Release notes.** Rewrite `docs/release_notes/RELEASE.md` in place per
      `docs/release_notes/README.md`. *Deps:* F5 · *Effort:* XS · *Value:* Med

## Not yet planned

- A `tests/ui-scripts/` recording driving one interview end to end.
- Resuming an abandoned interview — today Escape discards it.
- Reusing the interview to *revise* an existing template, routine or persona rather than
  only to create one.

## Suggested order

Cheapest decisive work first, then the vertical slices.

1. **G-1, G-2, G-3, G-4** — four answers that move a lot of markup.
2. **C1 → C2 → C3** — the overlay gaps. Small, self-contained, and every UI step below
   depends on them. Doing them first also proves the overlay can host a tall panel before
   anything is built on that assumption.
3. **A1 → A2 → A3 → A4 → A5** — the templates restyle. Standalone user-visible value even
   if the overlay slips, and it produces the third editor the launcher needs.
4. **B1 → B2** — `TemplateDraft`, which the interview's terminal turn needs anyway.
5. **D1 → D2 → D3** — the engine, testable without any UI.
6. **E1 → E2 → E3 → E4** — the panel.
7. **F1 → F2 → F3 → F4 → F5 → F6** — wiring, then the gate.
