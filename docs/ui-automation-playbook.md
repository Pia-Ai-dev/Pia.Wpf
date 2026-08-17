# Driving Pia's UI with automation (WinWright / UIA)

Guidance for agents running UI walkthroughs or UI regression tests against a running Pia
instance. Read this before starting a run; it replaces guesswork with what is verified to work.
Companion to `docs/2026-08-16-ui-automation-gaps.md` (the findings that motivated the fixes) and
`docs/2026-08-16-ui-automation-validation.md` (the live re-run that confirmed them).

## Ground rules

- **Pia is UIA-driven.** Everything below works through UI Automation patterns — no pixel
  offsets, no screenshot arithmetic. If you find yourself computing coordinates, stop and look
  for an AutomationId or pattern instead.
- **Treat `ww_click` success as unverified.** It returns `{"success": true}` even for no-ops.
  Confirm the intended state change happened (snapshot diff, expected element appears).
- **Prefer `ww_invoke` / InvokePattern** over physical clicks; it works regardless of window
  foreground state.

## Stable AutomationIds

| Element | AutomationId |
|---|---|
| Sidebar items | `NavItem_Assistant`, `NavItem_AssistantHistory`, `NavItem_Memory`, `NavItem_Reminders`, `NavItem_Routines`, `NavItem_Todo`, `NavItem_Settings`, `NavItem_NewWindow`, `NavItem_ThemeToggle` |
| Sidebar items, conditional | `NavItem_Optimize` / `NavItem_History` (Optimize-mode windows only), `NavItem_Assignments` (only when the server offers the surface) |
| Chat input / scroller | `InputTextBox`, `MessageScrollViewer` |
| Tool-approval decisions | `ToolApproval_Decline`, `ToolApproval_AllowOnce`, `ToolApproval_AllowSession`, `ToolApproval_AlwaysAllow` |
| Personas / Templates grids | `Personas_AddButton`, `Templates_AddButton`, per-item `Persona_Edit_<guid>` / `Persona_Delete_<guid>` / `Persona_Duplicate_<guid>` / `Template_Edit_<guid>` / `Template_Delete_<guid>` / `Template_ViewPrompt_<guid>` / `Template_SetDefault_<guid>` |
| Settings categories | `Settings_CategoryList`, `SettingsCategory_General` / `_Providers` / `_Optimize` / `_Assistant` / `_Account` / `_Plugins` |
| Routines list / actions | `Routines_JobList`, `Routines_NewJob`, `Routines_Edit`, `Routines_Toggle`, `Routines_RunNow`, `Routines_Delete`, `Routines_StatusMessage`, `Routines_Detail_NextRun`, `Routines_RunHistory` |
| Routines editor | `Routines_Field_Name`, `_Goal`, `_Kind`, `_Recurrence`, `_DayOfWeek`, `_Month`, `_DayOfMonth`, `_Time`, `_Date`, `_Provider`, `_GrantedTools`, `_Quiet`, plus `Routines_Save` / `Routines_Cancel` |
| Persona dialog | `PersonaEdit_Name`, `PersonaEdit_SystemPrompt`, `PersonaEdit_Archetype`, `PersonaEdit_ModelType`, `PersonaEdit_ToolScope`, `PersonaEdit_PreferredProvider`, `PersonaEdit_ReasoningEffort` |
| Template dialog | `TemplateEdit_Name`, `TemplateEdit_StyleDescription`, `TemplateEdit_GeneratedPrompt` |
| Provider dialog | `ProviderEdit_Name`, `ProviderEdit_ProviderType`, `ProviderEdit_Endpoint` |
| Edit dialogs (shared) | `PrimaryButton` (Save), `CloseButton` (Cancel), `Dialog_RequiredHint` |

Built-in personas expose only `Persona_Duplicate_<guid>`; edit/delete exist for user personas
only. Use `automationId*=Persona_Edit_` to enumerate the editable ones.

## Navigation

Sidebar items are `Button`s with InvokePattern — `ww_invoke` on `automationId=NavItem_Memory`
just works.

Note the buttons report a **zero bounding rectangle while the sidebar is collapsed** (the
icon-only default): the invoke button lives in the item's content, which is hidden in that
state. `ww_invoke` is unaffected; a mouse-fallback click cannot work. One more reason to prefer
invoke. The keyboard fallback (`ww_focus` the parent `DataItem`, then `Space`) still routes
correctly.

## Settings

1. Invoke `NavItem_Settings`.
2. Select the category with
   `ww_select(selector="automationId=Settings_CategoryList", optionText="Assistant")`.
   `ww_get_value` on the same list reports the selected category by name. (Don't click the item's
   `Text` child — that returns success and does nothing.)
3. Select the inner tab with `ww_select(selector="type=TabControl", optionText="Personas")`.
   **The tab's content appears as children of the selected TabItem**, not as siblings of the tab
   headers — scope searches as `type=TabItem[name='Personas'] >> ...`.

## Chat and tool approval

- Assistant reply text: `ww_get_value(selector="automationId=MarkdownViewer")` returns the
  rendered reply via **TextPattern** — the bubble content is not a `Text` element. An empty
  string means the reply really was empty (e.g. a turn that only called a tool), not a failure.
- The tool-approval card exposes all four decisions as invokable buttons with the
  `ToolApproval_*` ids above, each with a matching accessible `Name`. A full write-tool flow
  (send → approve/decline → resolved card) is automatable end-to-end; the message subtree stays
  fully exposed before, during and after the decision.
- Send is name-addressable (`type=Button[name*='Send']`); setting `InputTextBox` via
  ValuePattern and then invoking Send is the reliable path.

## Dialogs

- Edit dialogs (template/persona/provider) gate **Save** on a `CanSave` property — there is no
  validation popup to handle. Beware the observable: `Wpf.Ui.Controls.ContentDialog` **removes**
  the primary button from the tree rather than greying it out, so assert
  `ww_count("automationId=PrimaryButton") == 0` for the invalid case, not `enabled=false`
  (which fails with `no_match` and looks like a broken selector).
- Whenever Save is gone, `Dialog_RequiredHint` is present and says why. Read it with
  `ww_assert_value(property="name")` — it is a Text peer, and `ww_get_value` resolves it through
  LegacyIAccessible and returns an empty string.
- `ww_inspect label_map` is the best first move inside a dialog: every field with its label and
  current value in one call.
- If a save/submit ever appears to do nothing, call `ww_dialog(action=handle)` **before**
  concluding anything — native `MessageBox` dialogs are invisible to window-scoped screenshots
  and `ww_list_windows`. Better: `ww_dialog(action=expect)` *before* the click to pre-register
  a handler and avoid the race. (Startup-failure is the one remaining native box, by design.)

## Known gaps (don't burn time rediscovering these)

- **`ui:InfoBar` exposes no automation peer at all** in this Wpf.Ui version — it renders, but
  neither its `AutomationId` nor its message reaches UIA. Don't put anything an assertion needs
  inside one.
- **The Providers grid has no per-row AutomationIds.** Its Edit / Delete buttons do carry
  accessible names, so `type=Button[name='Edit']` works but matches every row; `ww_invoke` takes
  the first. Disambiguate by scoping to the row, or add ids if a test needs a specific provider.

## Cross-checks

- **Independent verification is cheap**: a PowerShell `EnumWindows` filtered to the app's PID
  definitively answers "is there a window I can't see?" (native dialogs don't appear in
  `ww_list_windows`, and it misreports modality).
- WinWright traps to remember:
  - `ww_click` false-success (above).
  - `ww_dialog handle` can report success without dismissing.
  - Window-scoped screenshots never show native dialogs.
  - The `value` **selector filter reads ValuePattern only**, not SelectionPattern:
    `type=ComboBox[value='visionary']` resolves 0 elements while `ww_get_value` on the same
    element returns `visionary`. Filter on snapshot output instead of on the selector.
- **If screenshots stop matching the UIA tree, suspect the GPU, not the app.** A WPF
  hardware-rendering stall leaves the window presenting a stale or blank surface while the
  dispatcher, UIA and the app itself keep working — `PrintWindow`, a screen grab and a resize all
  return the same dead frame. Confirm with a control app (Notepad renders fine) and, if needed,
  `HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration = 1` before relaunch. Remove the
  value afterwards.
