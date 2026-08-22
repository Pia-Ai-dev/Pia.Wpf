# UI parts that resist automation (WinWright)

Found while driving Pia v1.3.0.0 through WinWright MCP 3.1.0 on 2026-08-16 for the walkthrough that
produced `docs/user_questions/2026-08-16-ui-howto-questions.md`. Everything below was hit first-hand and verified,
not inferred from source.

Sorted by **who can fix it**, because that's what makes an entry actionable. These are mostly
barriers to driving the app with an automation agent rather than bugs a human would notice — which
matters if UI walkthroughs or UI regression tests are going to be repeatable. Two entries do have a
human-facing side, and say so: 1.1 (a required field that isn't marked required) and 1.5 (icon
buttons with no accessible name, which also affects screen-reader users).

The headline is **1.4**: the tool-approval card is invisible to automation, so no end-to-end test
can drive a flow that touches a write tool.

Severity is about automation impact only:

- **Blocker** — stops a run dead, and the cause isn't visible from inside the harness.
- **Trap** — the run continues but silently does the wrong thing, or reports success falsely.
- **Friction** — workable, just slower or uglier.

---

## 1. App-side (fixable in this repo)

### 1.1 Native `MessageBox.Show` for validation — **Blocker**

**What happened.** Saving the *Edit Template* dialog with an empty Generated Prompt raised
`MessageBox.Show(..., "Validation Error", ...)`. To the agent this was invisible: the message never
appeared in a window-scoped `ww_screenshot`, `ww_list_windows` reported only the main window, and
`ww_snapshot` showed nothing. The save just appeared to do nothing. Worse, the box is **modal**, so
every subsequent interaction with the main window was swallowed — and because a second failed save
stacks a *second* box rather than reusing the first, the app ended up with two modals queued. The
run was effectively dead from that point and I misdiagnosed it as "Save fails silently".

It took the user sending a screenshot to reveal it. Confirmed afterwards by enumerating top-level
windows with Win32 `EnumWindows`, which showed two `#32770` windows titled *Validation Error*
sitting above the WPF window.

**Sites.**

| File | Line | Purpose |
|---|---|---|
| `src/Pia.Wpf/Views/Dialogs/TemplateEditContentDialog.xaml.cs` | 45 | Validation error |
| `src/Pia.Wpf/Views/Dialogs/PersonaEditContentDialog.xaml.cs` | 51 | Validation error |
| `src/Pia.Wpf/Views/Dialogs/ProviderEditContentDialog.xaml.cs` | 104 | Validation error |
| `src/Pia.Wpf/App.xaml.cs` | 71 | Startup failure (arguably fine as a native box — it fires before any WPF UI exists) |

**Suggested fix.** For the three validation sites, don't raise a dialog at all. These are all
`Wpf.Ui.Controls.ContentDialog` subclasses validating in `OnClosing` and setting `args.Cancel = true`.
The stronger fix is to make the error unreachable:

1. Mark every genuinely-required field with `*` — Generated Prompt currently isn't, which is the
   actual user-facing bug behind this (see doc #1, note 1).
2. Bind `IsPrimaryButtonEnabled` to a validity property on the edit model, so Save is disabled until
   the form is valid.
3. If a message is still wanted, render it *inside* the dialog — a `Wpf.Ui.Controls.InfoBar` above
   the fields, or per-field validation text. That keeps it in the visual tree, so it's visible to
   screenshots and to a screen reader, and it can't stack.

That change removes a blocker, fixes the labelling gap, and improves accessibility in one move.

### 1.2 Sidebar navigation items are not actionable via UIA — **Blocker**

**What happened.** `ww_click` on the Memory / Chat history / Todo sidebar entries did nothing —
while returning `{"success": true}`. Inspecting the element explains it:

```
ControlType:  DataItem          AutomationId: (empty)
ClassName:    ItemsControlItem  Name:         Memory
patterns:     LegacyIAccessiblePattern     <-- that's the entire list
```

No `InvokePattern`, no `SelectionItemPattern`. `ww_invoke` on the same element fails honestly with
`pattern_not_supported`. The physical-mouse fallback didn't help either, because the window was
never foreground for synthetic input.

These are `Wpf.Ui.Controls.NavigationViewItem`s hosted in an `ItemsControl` (`AutomationId`
`NavigationItems`), and the item container surfaces as a bare `DataItem`.

**Workaround that does work:** `ww_focus` on the item, then send `Space`. The items report
`IsKeyboardFocusable: True`, and keyboard activation routes correctly. This is how the Memory view
was eventually reached.

**Suggested fix.** Give the item containers an `AutomationProperties.AutomationId` (e.g.
`NavItem_Memory`) via an `ItemContainerStyle` setter, and expose an invokable or selectable
automation peer so the container isn't a dead `DataItem`. Stable per-item AutomationIds would also
remove the need for the brittle `type=DataItem[name='Memory']` selectors used throughout this pass.

### 1.3 Tab content is absent from the UIA tree — **Blocker**

**What happened.** With Settings → Assistant → **Personas** on screen and a full grid of persona
cards visible, `ww_snapshot` at depth 30 / 400 elements returned the five `TabItem` headers and
**nothing underneath them**. `ww_inspect find_by_description "Add Persona button"` scanned 81
elements and never saw it. The persona cards, their Duplicate / edit / delete buttons, and the
*Add Persona* button were all unreachable by selector.

Same for the Optimize templates grid.

**Workaround:** click by pixel offset from a known-good anchor element (`ww_click` with
`offsetX`/`offsetY` relative to e.g. `automationId=PrimaryButton`), reading the target coordinates
off a screenshot. This works but is fragile — it breaks on any layout change, window resize, or DPI
difference, and it's the single biggest source of slowness in a run.

Curiously, the *dialogs* launched from these tabs expose their contents fine — `ww_inspect
label_map` returned every field of both edit dialogs cleanly. So the problem is specific to the tab
content host, not to the controls themselves.

**Suggested fix.** Worth a focused look at why the tab's content presenter yields no automation
peer children. Adding `AutomationProperties.AutomationId` to the *Add Persona* / *Add Template*
buttons and to the item containers in those grids would cover the common cases even if the root
cause is deeper.

### 1.4 Chat message content — including the tool-approval card — is absent from the UIA tree — **Blocker**

This is the most consequential entry in this document: **tool approval cannot be automated at all.**

**What happened.** A `create_todo` call raised an inline approval card in the chat with four buttons
— Decline / Allow once / Allow this session / Always allow. None of them exists in the automation
tree. `type=Button[name='Allow once']` returns 0 matches; `ww_inspect find_by_description` scanned
84 elements without finding it; `automationId=MessageScrollViewer >> type=Button` returns 0, as does
`>> type=Text`.

A full `ww_dump_tree` shows the message list containing exactly two childless items:

```
[Pane] AutomationId: MessageScrollViewer
  [List] ClassName: ItemsControl
    [DataItem] Name: Pia.Models.AssistantMessage   Bounds: (990,421,1422x101)
    [DataItem] Name: Pia.Models.AssistantMessage   Bounds: (990,522,1422x171)
```

No `Text`, no `Button`, nothing. The message bubbles, the reasoning line, the approval card and its
buttons are all invisible.

**The subtree collapse is conditional, which makes it worse.** Earlier in the same session, while
the assistant was still streaming, the identical `DataItem`s *did* expose children — `PiaPersonaAvatar`,
`PiaAssistantMessage`, `PiaReasoningView`, a `Text` reading "Thinking...", the user's message text,
and a copy `Button`. Something about the rendered approval state collapses the whole list's peer
tree. So an agent can read chat content at some moments and not others, with no signal about which.

**Why it matters beyond this one card.** Any end-to-end test that exercises a write tool will hit an
approval gate it cannot answer, and no test can assert on assistant reply text.

**Workaround:** pixel-offset physical click, anchored on `automationId=InputTextBox`. Verified
working — a click at `offsetX=83, offsetY=-415` hit **Decline** and the card resolved to
*"Create Todo - Declined"*. Fragile for all the usual reasons, and it requires a screenshot plus
arithmetic on every run.

**Suggested fix.** Give the approval card's four buttons `AutomationProperties.AutomationId`
(e.g. `ToolApproval_AllowOnce`) and an `AutomationProperties.Name`. More broadly, this looks like the
same root cause as 1.3 — content hosted inside an items control not yielding automation peers — so
the two are probably worth investigating together.

### 1.5 Icon-only buttons have no accessible `Name` — **Friction**

The composer's buttons (clear, record, attach, join meeting, live transcription, **Send**) all
surface with an empty UIA `Name`. `ww_snapshot` shows a friendly `label` for some of them, but that
is WinWright synthesising a label from tooltips or `AutomationId` — it is *not* the `Name` property,
so `type=Button[name*='Send']` matches zero elements. Selecting Send by name is impossible; I had to
fall back to focusing the textbox and sending `Enter`.

**Suggested fix.** Add `AutomationProperties.Name` (or `ToolTip` where it isn't already set) to the
icon buttons. This is also a straightforward accessibility win — a screen-reader user currently gets
nothing on these controls.

### 1.6 ComboBoxes expose raw .NET types as their value — **Friction**

Reading combo values back gives internal representations rather than what's on screen:

| Control | Value reported |
|---|---|
| Composer persona picker | `Pia.Models.Persona` |
| Preferred Provider | `ProviderChoice { Id = , Name = (Use mode default) }` |
| Reasoning Effort | `ReasoningEffortChoice { Value = , Display = (Provider default) }` |
| Settings category list | `System.Windows.Controls.ListBoxItem` |

The first and last are useless — no `ToString()` override, so assertions can't check which persona
is selected. The middle two leak a C# record's synthesised `ToString()` into the accessibility tree.

**Suggested fix.** Override `ToString()` on `Persona` / `ProviderChoice` / `ReasoningEffortChoice`,
or set `AutomationProperties.Name` on the ComboBox from the selected item's display text. Cheap, and
it makes `ww_assert_value` usable against these controls.

---

## 2. WinWright-side (not fixable here — worth reporting upstream)

### 2.1 `ww_click` reports success for a no-op — **Trap**

`ww_click` defaults to `useInvokePattern=true`. When the target has no `InvokePattern`, it appears
to fall through to a physical click; when that also fails to take effect, the call still returns
`{"success": true}`. Six consecutive nav clicks returned success while nothing happened. `ww_invoke`
on the identical element correctly returns `pattern_not_supported`.

This is the single most expensive behaviour in this list — it converts a hard failure into a silent
one and sends the agent hunting for the wrong cause.

### 2.2 `ww_list_windows` misses native dialogs and misreports modality — **Trap**

With two modal `#32770` *Validation Error* boxes open above the main window, `ww_list_windows`
returned exactly one window — the main one — with `"isModal": false`. Win32 `EnumWindows` over the
same process ID returned all three. An agent has no way to learn from the harness that its target
window is blocked.

### 2.3 `ww_dialog handle` reported success without dismissing — **Trap**

`ww_dialog(action=handle, button=OK)` returned `{"success": true, "buttonClicked": "OK"}` twice, and
`captureText` really did return the dialog's message — so it clearly *found* the dialogs. But both
boxes were still open afterwards, which the user confirmed visually and `EnumWindows` verified. They
had to be closed by sending `WM_CLOSE` directly.

Also worth noting: `captureText` returned the dialog message concatenated with the entire main
window's text content, rather than just the dialog's, which makes it awkward to assert on.

### 2.4 Window-scoped screenshots can't see native dialogs — **Blocker**, by design

`ww_screenshot` captures the target window, so a separate-HWND dialog never appears. Combined with
2.2 this is what made 1.1 invisible. A whole-screen capture mode would close the gap.

---

## 3. Agent technique (no code change needed)

These cost time in this pass purely because the technique wasn't known up front. Worth folding into
whatever guidance drives future UI runs.

- **After any Save / OK / submit that appears to do nothing, call `ww_dialog(action=handle)`
  before concluding anything.** It's the only tool that sees native dialogs, and nothing in the
  normal discovery path (screenshot → `ww_list_windows` → `ww_snapshot`) hints that one exists.
  Better still, call `ww_dialog(action=expect)` *before* the click to auto-handle it.
- **Prefer `ww_dialog(action=expect)` over reacting.** It pre-registers a handler and avoids the
  timing race entirely.
- **Treat `ww_click` success as unverified.** Confirm the intended state change (screenshot, or
  `ww_snapshot` diff) rather than trusting the return value.
- **When a click won't take, try `ww_focus` + `Space`/`Enter`.** Keyboard activation reached the
  sidebar when neither UIA invoke nor physical click would.
- **Independent verification is available and cheap.** A PowerShell `EnumWindows` filtered to the
  app's PID definitively answers "is there a window I can't see?" in one call.
- **`ww_inspect label_map` is the best first move inside a dialog.** It returned every field of both
  edit dialogs with their labels and current values in one call, where `ww_snapshot` needed several
  attempts and a raised element cap.

---

## Suggested order of work

If any of this gets picked up, this is the ordering by value-per-effort:

1. **1.1 MessageBox → in-dialog validation + disable Save until valid.** Fixes a real user-facing
   labelling gap, removes an automation blocker, and helps screen readers. Also the user's own
   suggestion.
2. **1.2 Sidebar AutomationIds + actionable peer.** Small change, unblocks navigation, kills the
   most brittle selectors in the suite.
3. **1.5 / 1.6 accessible names and `ToString()` overrides.** Trivial, and both double as
   accessibility improvements.
4. **1.3 / 1.4 tab and message-list content visibility.** Highest value for testing the persona/template grids, but needs
   investigation first — the cause isn't yet known.
5. **Report 2.1 and 2.3 upstream to WinWright.** False-positive success returns are worth a bug
   report; they mislead every agent that uses the tool, not just this one.

Not included here: the `@`-command picker. It never rendered during this pass, but whether it works
at all for a human is still unknown, so filing it as an automation gap would assume the conclusion.
It stays an open question in `docs/user_questions/2026-08-16-ui-howto-questions.md` pending one manual check.
