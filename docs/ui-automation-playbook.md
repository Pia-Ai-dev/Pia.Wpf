# Driving Pia's UI with automation (WinWright / UIA)

Guidance for agents running UI walkthroughs or UI regression tests against a running Pia
instance. Read this before starting a run; it replaces guesswork with what is verified to work.
Companion to `docs/2026-08-16-ui-automation-gaps.md` (the findings that motivated the fixes).

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
| Sidebar items | `NavItem_Optimize`, `NavItem_Assistant`, `NavItem_History`, `NavItem_AssistantHistory`, `NavItem_Memory`, `NavItem_Reminders`, `NavItem_Assignments`, `NavItem_Todo`, `NavItem_Settings`, `NavItem_NewWindow`, `NavItem_ThemeToggle` |
| Chat input / scroller | `InputTextBox`, `MessageScrollViewer` |
| Tool-approval decisions | `ToolApproval_Decline`, `ToolApproval_AllowOnce`, `ToolApproval_AllowSession`, `ToolApproval_AlwaysAllow` |
| Personas / Templates grids | `Personas_AddButton`, `Templates_AddButton`, per-item `Persona_Edit_<guid>` / `Persona_Delete_<guid>` / `Persona_Duplicate_<guid>` / `Template_Edit_<guid>` / `Template_Delete_<guid>` / `Template_ViewPrompt_<guid>` / `Template_SetDefault_<guid>` |

## Navigation

Sidebar items are `Button`s with InvokePattern — `ww_invoke` (or `ww_click`) on
`automationId=NavItem_Memory` just works. If invoke ever fails, the fallback is `ww_focus` on
the item's parent `DataItem` followed by `Space` (keyboard activation routes correctly).

## Settings

1. Invoke `NavItem_Settings`.
2. Select the category in the left `ListBox` via SelectionItemPattern (match the `Text` child —
   the ListItem's own `Name` is the raw type name, a known cosmetic gap).
3. Select the inner tab via SelectionItemPattern on the `TabItem`. **The tab's content appears
   as children of the selected TabItem**, not as siblings of the tab headers — scope your
   searches accordingly.

## Chat and tool approval

- Assistant reply text: find `MarkdownViewer` (a `RichTextBox` Document) under
  `MessageScrollViewer` and read it via **TextPattern** — the bubble content is not a `Text`
  element.
- The tool-approval card exposes all four decisions as invokable buttons with the
  `ToolApproval_*` ids above. A full write-tool flow (send → approve/decline → resolved card)
  is automatable end-to-end.
- Send is name-addressable (`Send (Enter)`); setting `InputTextBox` via ValuePattern + invoking
  Send is the reliable path.

## Dialogs

- Edit dialogs (template/persona/provider) disable **Save** until the form is valid — there is
  no validation popup to handle. Required fields are marked `*`.
- `ww_inspect label_map` is the best first move inside a dialog: every field with its label and
  current value in one call.
- If a save/submit ever appears to do nothing, call `ww_dialog(action=handle)` **before**
  concluding anything — native `MessageBox` dialogs are invisible to window-scoped screenshots
  and `ww_list_windows`. Better: `ww_dialog(action=expect)` *before* the click to pre-register
  a handler and avoid the race. (Startup-failure is the one remaining native box, by design.)

## Cross-checks

- **Independent verification is cheap**: a PowerShell `EnumWindows` filtered to the app's PID
  definitively answers "is there a window I can't see?" (native dialogs don't appear in
  `ww_list_windows`, and it misreports modality).
- WinWright traps to remember: `ww_click` false-success (above), `ww_dialog handle` can report
  success without dismissing, and window-scoped screenshots never show native dialogs.
