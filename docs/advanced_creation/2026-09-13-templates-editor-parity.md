# Optimize templates: master–detail parity with routines and personas

**Status:** Landed 2026-09-13. Templates became a second inner tab of Settings → Optimize
rather than a section below the output settings: a master-detail pane needs a bounded
height, which a page-level `ScrollViewer` does not give it.
**Owner:** Marco Altmann
**Written:** 2026-09-13
**Origin:** Owner request, 2026-09-13: "we should also change the whole design of the
optimize template side to the style we use for routines and personas". Sibling plan:
[2026-09-13-advanced-creation.md](2026-09-13-advanced-creation.md), which needs one
shared editor shape across all three subjects to hang an "Advanced" entry point on.

## Why

Three surfaces create the same kind of thing — a named, AI-authored artefact — and two of
them already agree on how. Routines and personas are master–detail: a list on the left, a
right pane that switches between placeholder, detail and inline editor. Optimize templates
are a card grid that opens a modal `ui:ContentDialog`. The odd one out costs twice: a user
learns the layout once and then meets a different one, and the Advanced Creation overlay
would need two different host shapes to attach to.

## What exists today

| | Routines | Personas | Templates |
|---|---|---|---|
| Surface | `src/Pia.Wpf/Views/RoutinesView.xaml` (top-level view) | `src/Pia.Wpf/Views/SettingsViews/PersonasView.xaml` (embedded in Assistant settings) | `src/Pia.Wpf/Views/SettingsViews/OptimizeView.xaml` (settings section) |
| Layout | 3-column grid, list `*` / `MinWidth=300` / `MaxWidth=440`, 14px gutter, `2*` pane (`RoutinesView.xaml:174`) | 3-column grid, `340` / 14 / `*` (`PersonasView.xaml:56`) | `controls:ColumnsPanel MaxColumns="3"` card grid |
| Edit | inline pane, `IsEditorOpen` | inline pane, `IsEditorOpen` | modal `Views/Dialogs/TemplateEditContentDialog.xaml` |
| Right-pane states | `ShowsPlaceholder` · `ShowsCatalog` · `ShowsDetail` · `IsEditorOpen` | `ShowsPlaceholder` · `ShowsDetail` · `IsEditorOpen` | — |
| Row style | `PiaMasterRowCardStyle` + `PiaMemoryRowItemStyle` | same | `PiaSettingsCardStyle` |
| Empty | `shared:PiaEmptyState` | `shared:PiaEmptyState` | — |

`PersonaSettingsViewModel` says so in its own summary: "pane with an inline editor,
mirroring `RoutinesViewModel`". That is the pattern to land on.

## Shape to build

Extract the templates half of the Optimize settings page into a
`src/Pia.Wpf/Views/SettingsViews/TemplatesView.xaml` UserControl and give it its own inner
tab of `SettingsViews/OptimizeView.xaml` — exactly the way `PersonasView` sits in a
`TabItem` of `SettingsViews/AssistantView`. That keeps the Settings navigation unchanged
(no new category, no new `SettingsCategory_*` automation id) and reuses a precedent that
already passes `ViewAutomationIdTests`.

A tab rather than a section below the output settings, because the height matters: every
other tab wraps its content in a `ScrollViewer`, but `PersonasView` does not — sitting
directly in a `TabItem` is what bounds it, so its list and its editor scroll separately
instead of stretching the page. Below a page-level `ScrollViewer` the `ListBox` would grow
to hold every template and the whole page would scroll as one.

```
┌ Templates ───────────────────────────────── [+ Add] ┐
│ ┌ list (340) ┬ 14 ┬ right pane (*) ──────────────┐  │
│ │ ▸ Email    │    │  placeholder | detail | editor│  │
│ │ ▸ Formal   │    │                               │  │
│ │ ▸ Summary  │    │                               │  │
│ └────────────┴────┴───────────────────────────────┘  │
└──────────────────────────────────────────────────────┘
```

- **List column**: star-sized with `MinWidth="300" MaxWidth="440"`, following
  `RoutinesView.xaml:175` rather than PersonasView's fixed `340`. The comment there says
  the fixed column "left the detail pane with almost nothing on a narrow" window, and the
  Settings page is narrower still.
- **List**: `ListBox` over `Templates`, `ItemContainerStyle` `PiaMemoryRowItemStyle`, row
  `Border` `PiaMasterRowCardStyle`. Row shows name, description, and a built-in chip for
  `IsBuiltIn`. `PiaEmptyState` behind it when the collection is empty.
- **Placeholder**: `PiaEmptyState`, `Symbol="DocumentText24"`, visible when nothing is
  selected and the editor is closed.
- **Detail**: read-only view of name, description, style description and prompt, with
  Edit / Duplicate / Set-default / Delete. Mirrors the persona detail pane, including
  the read-only handling that `CanDeleteTemplate` already encodes for built-ins.
- **Editor**: the fields currently in `TemplateEditContentDialog` — name, style
  description, the Sparkle *Generate prompt* button, generated prompt — plus the
  `PiaRequiredHintStyle` "why Save is disabled" hint the dialog already carries, and
  Save / Cancel in the pane rather than dialog buttons.

`TemplateEditContentDialog.xaml` and its code-behind are deleted in the same change, and
`OptimizeSettingsViewModel.AddTemplateAsync` (`:157`), `EditTemplateAsync` (`:183`) and
`ViewTemplatePromptAsync` (`:170`) stop calling `IDialogService` and drive
`IsEditorOpen` / `SelectedTemplate` instead.

## View-model work

`OptimizeSettingsViewModel` currently owns both the optimize *settings* (output action,
auto-type delay) and the template list. Split the template half into a
`TemplatesSettingsViewModel` shaped like `PersonaSettingsViewModel`:

- `SelectedTemplate`, `IsEditorOpen`, `Editor`, `ShowsDetail`, `ShowsPlaceholder`
- an editor model `TemplateEditModel` alongside `ViewModels/Models/PersonaEditModel.cs`,
  carrying `Name`, `Description`, `StyleDescription`, `GeneratedPrompt`, `CanSave`,
  `IsGeneratingPrompt` and the `GeneratePromptCommand` that today lives on
  `OptimizeSettingsViewModel`
- `OnSelectedTemplateChanged` closes an open editor when the selection moves, the way
  `PersonaSettingsViewModel:67` does

Registration goes next to the other settings view-models in `Bootstrapper.cs`;
`OptimizeSettingsViewModel` exposes it as a property so `OptimizeView.xaml` can bind the
embedded control's `DataContext`, mirroring `ProvidersVm`.

## Constraints this change has to respect

- **Policy locks.** `OptimizeSettingsViewModel` reads `IPolicyService` for
  `IsOutputActionEnforced` / `IsAutoTypeDelayEnforced` and exposes a `PolicyLock`. The
  template half must keep whatever lock applies to managed templates; check
  `CanDeleteTemplate` and the built-in guard before assuming none applies.
- **Automation ids.** The editor fields keep the ids the dialog already uses —
  `TemplateEdit_Name`, `TemplateEdit_StyleDescription`, `TemplateEdit_GeneratePrompt`,
  `TemplateEdit_GeneratedPrompt` — so moving them out of the dialog does not rename
  anything. New list and detail controls take `Templates_*`, which
  `Templates_AddButton` already establishes. Do not invent a third prefix: `Templates_`
  and `TemplateEdit_` both match a `automationId*="Template"` prefix search, and a script
  reaching for one must not find rows of the other, so keep list ids under
  `Templates_Row_*` via `{Binding Id, StringFormat='Templates_Row_{0}'}`.
  `TemplatesView` gets its own `[InlineData]` row in
  `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs`, and the existing
  `SettingsViews.OptimizeView` row (`:54`, currently `6, 4`) has to be recounted once the
  templates markup leaves it.
- **Docs that name these ids.** `docs/ui_automation/ui-automation-playbook.md`,
  `docs/ui_automation/2026-08-16-ui-automation-validation.md` and
  `docs/ui_automation/2026-08-22-fill-uiautomation-gaps-checklist.md` all reference
  `TemplateEdit_*` or `Templates_AddButton` as living in a ContentDialog. Nothing under
  `tests/ui-scripts/` does, so no recording breaks — but the playbook has to be corrected
  in the same change. 
- **Localization.** Strings already exist for the dialog fields
  (`Dialog_TemplateEdit_*`, `Templates_Add`, `Templates_Description`,
  `Settings_Tab_Templates`). Renaming a key means touching `ViewStrings.resx`,
  `.de.resx` and `.fr.resx` together — prefer reusing the existing keys.
- **Zero-warning policy.** Rebuild Debug and Release; WPF re-reports `src/` warnings under
  the generated `_wpftmp.csproj`.

## Out of scope

Making templates a top-level navigable view like Routines. Personas is the closer
sibling and it lives in Settings; moving templates out is a navigation change with its
own cost and no bearing on look and feel.
