# Validating the UI-automation fixes (live WinWright re-run)

Re-ran the flows from `2026-08-16-ui-automation-gaps.md` against a Debug build of
`feature/ui-automation-a11y` at `c73458a0`, driving Pia v1.3.0.0 through WinWright MCP 3.1.0 on
2026-08-16. Every verdict below is first-hand unless the text says otherwise; source-derived
claims are marked as such.

Read-only against the real profile: dialogs were opened and cancelled, no persona or template was
saved or deleted, and the tool-approval flow was exercised with **Decline**.

## Verdicts against the gap list

| Gap | Was | Now | Verdict |
|---|---|---|---|
| 1.1 native `MessageBox` validation | Blocker | Save is gated on `CanSave`; no native box (`EnumWindows` shows only the main window) | **Fixed**, with a caveat |
| 1.2 sidebar not actionable | Blocker | `NavItem_*` Buttons with InvokePattern | **Fixed** |
| 1.3 tab content absent from UIA | Blocker | Full persona grid under the selected TabItem | **Fixed** |
| 1.4 chat + approval card absent | Blocker | Whole message subtree exposed; four `ToolApproval_*` buttons | **Fixed** |
| 1.5 icon buttons unnamed | Friction | Composer Send and grid Edit/Delete named; answer toolbar still unnamed | **Partly fixed** |
| 1.6 combos leak .NET types | Friction | Persona / ProviderChoice / ReasoningEffortChoice humanised; settings category list unchanged | **Partly fixed** |

### 1.1 — fixed

All three dialogs exercised.

*Edit Template* is the original repro: blanking **Generated Prompt** removes
`automationId=PrimaryButton` (count 0) and raises nothing — `ww_dialog(action=handle)` reports no
native box, only the in-window dialog whose sole button is *Cancel*. *Edit Persona* behaves the
same on a blanked Name, and restoring the name brings Save back enabled. `EnumWindows` over the
PID returned only the main window throughout the run — no `#32770`, so the stacking deadlock is
gone.

One wrinkle worth knowing: `Wpf.Ui.Controls.ContentDialog` **removes** the disabled primary button
instead of greying it. Functionally the gate holds, but the footer shows a lone full-width
*Cancel* with no hint why saving is unavailable, and an automation assertion of `enabled=false`
fails with `no_match` rather than `false`.

That wrinkle bites hardest on *Edit Provider*, which is worth a decision (see below).

### The Provider dialog can now be opened with no Save button and no explanation

Opening *Edit Provider* on the **Pia Cloud** provider shows Name `Pia Cloud`, an empty *Provider
Type*, no Endpoint field at all, and a footer with only *Cancel*. `CanSave` requires
`Name` **and** `Endpoint`; the Endpoint field is wrapped in
`Visibility="{Binding ProviderType, Converter={StaticResource ProviderTypeToVisibilityConverter}, ConverterParameter=Endpoint}"`,
so for this provider the field that blocks saving is not even on screen.

This is not a new *inability* — the removed `OnClosing` had the identical `Endpoint is required`
rule, so Save previously failed too. It is a regression in **communication**: before, clicking
Save said "Endpoint is required"; now the button silently does not exist and the field it refers
to is hidden. Options: relax `CanSave` to require Endpoint only for provider types that use one,
or keep the rule and surface the reason in the dialog (a `Wpf.Ui.Controls.InfoBar` above the
fields, which was suggestion 3 in the original gap doc and would also restore the "why" for the
persona and template cases).

### 1.2 — fixed

`ww_invoke automationId=NavItem_Memory` navigates. Every advertised id resolves except the two
that are conditional by design: `NavItem_Optimize` / `NavItem_History` (Optimize-mode windows) and
`NavItem_Assignments` (server-gated).

The buttons report a zero bounding rectangle while the sidebar is collapsed — they live inside the
item's hidden content. Invoke is unaffected; a mouse-fallback click could not work.

### 1.3 — fixed

Settings → Assistant → Personas: `Personas_AddButton` resolves, and so does
`type=TabItem[name='Personas'] >> automationId=Personas_AddButton`, confirming the documented
scoping. `automationId*=Persona_Edit_` returns 4 (the user personas); built-ins expose Duplicate
only. A tree dump shows every card with its title, tagline and buttons.

Settings → Optimize covers the other grid: `Templates_AddButton` resolves, as do
`Template_ViewPrompt_` × 9, `Template_SetDefault_` × 9, and `Template_Edit_` / `Template_Delete_`
× 3 (the user templates). Every id in the playbook table is live.

### 1.4 — fixed, and this is the big one

Sent a message that triggers `create_todo`. The approval card appeared 2.9 s later with all four
`ToolApproval_*` buttons carrying accessible names. Invoking `ToolApproval_Decline` resolved the
card to *"Create Todo - Declined"* within 0.6 s.

The conditional subtree collapse is gone. Both while the card was live and after the decision, the
message list exposed the user's message text, the reasoning line ("Thought for 34s"), the card
title and description, and the answer toolbar.

Reply text is readable too: after `Reply with exactly: hello world`,
`ww_get_value automationId=MarkdownViewer` returned `hello world` via TextPattern. An empty read
earlier in the run was a genuinely empty reply (a turn that only called a tool), not a failure —
worth distinguishing, because the two look identical.

### 1.5 — partly fixed

`type=Button[name*='Send']` now matches (it matched zero before), and the persona grid's
Edit/Delete report `Edit` / `Delete`. Still nameless: the answer toolbar's **Copy, Read aloud,
Regenerate and Export** buttons, and `Personas_AddButton` / the `Duplicate` grid buttons. In
`PiaAnswerToolbar.xaml` only the two rating buttons received `AutomationProperties.Name`; the
other five wrap a StackPanel, so WPF derives no name from content. Same accessibility hole for
screen-reader users as before, just smaller.

### 1.6 — partly fixed

Verified from a UIA snapshot of the persona dialog:

| Control | Reported value |
|---|---|
| Composer persona picker | `Pia · Business` |
| Preferred Provider | `(Use mode default)` |
| Reasoning Effort | `(Provider default)` |
| Archetype / Model type / Tool Access | `visionary` / `general` / `Full` |
| Settings category list | `System.Windows.Controls.ListBoxItem` |

The three `ToString()` overrides all reach the accessibility tree. The settings category list is
untouched and still reports the raw type name.

Two things still block `ww_assert_value` here, neither of them the `ToString()` work:

- The persona dialog's five ComboBoxes have no AutomationId and no `Name`, so only the first is
  reachable by selector. Read them out of `ww_snapshot` instead.
- WinWright's `value` **selector filter reads ValuePattern only**, not SelectionPattern:
  `type=ComboBox[value='visionary']` resolves 0 elements while `ww_get_value` on that element
  returns `visionary`. That is a harness limitation, not an app gap.

## Follow-up: both new defects fixed

Everything in the next two sections was fixed after the run and re-verified live against a fresh
Debug build. `dotnet build -t:Rebuild` is clean in Debug and Release; `dotnet test` is
`4103 total / failed: 0`.

- The doubled markers are gone: the five XAML `*` TextBlocks were removed and
  `Dialog_TemplateEdit_GeneratedPrompt` gained the ` *` its siblings already carried, so all eight
  required labels are marked once, in one `Text` peer each. Checked in all three locales — every
  one of the eight keys ends in ` *` in en, de and fr, so dropping the XAML marker cannot leave a
  field unmarked in a translation.
- `ProviderEditModel.CanSave` no longer demands an endpoint from the built-in cloud provider, and
  *Edit Provider* on Pia Cloud now opens with Save enabled (`ww_count automationId=PrimaryButton`
  → 1). A fresh **Add Provider** still requires one — `PiaCloud` is enum 0, so a new model sits
  there before the user picks a type, and an `IsCloudProvider` flag set only by `FromProvider`
  keeps the two apart. Four new cases in `EditModelCanSaveTests` pin that.

  Saving that dialog is now reachable for the first time, so the round trip was checked by hand:
  `EnsureBuiltInProviderAsync` creates Pia Cloud with `Endpoint = ""`, so `ToProvider()` writes
  back exactly what was stored, and `ProviderService.UpdateProviderAsync` preserves
  `EncryptedApiKey` when no new key is supplied. The two properties `ToProvider()` never sets —
  `CreatedAt` and a null `ReasoningEffort` — are reset on every provider edit and always have been;
  nothing cloud-specific is lost.
- When Save is gated off, `Dialog_RequiredHint` now says why, in the dialog and in the UIA tree.
  It is a `Border`/`TextBlock` rather than the obvious `ui:InfoBar`: the InfoBar renders correctly
  but exposes **no automation peer at all**, so the reason would have been invisible to automation
  and to a screen reader.

The automation gaps the run turned up were closed in the same pass: `Settings_CategoryList` plus
per-category ids and Names (the category list is now `ww_select`-able by name and reports its
selection), AutomationIds on every field of the three edit dialogs, accessible Names on the answer
toolbar's Copy / Read aloud / Regenerate / Export and on the grid Add / Duplicate / View Prompt /
Set Default buttons. `type=Button[name='Copy']` and friends now resolve.

The sections below describe the defects as found.

## New defects found during the run

### Required-field markers render `**`

The three edit dialogs now append a red `*` TextBlock next to labels whose resx strings **already**
end in ` *`. Five fields are affected across the three dialogs:

| Dialog | Field | Renders | Observed |
|---|---|---|---|
| Persona | Name, System Prompt | `Name **`, `System Prompt **` | yes |
| Provider | Name | `Name **` | yes |
| Provider | Endpoint | `Endpoint **` | from source — the field is hidden for the provider opened |
| Template | Template Name | `Template Name **` | yes |

Template's *Generated Prompt* is correct — that resx string had no `*`, which is exactly the
labelling gap the change set out to fix (it renders as `Generated Prompt*`, with no space before
the marker, unlike every other label). Provider's *Provider Type* is also correct (no extra
TextBlock was added). Fixing it means either dropping the added TextBlocks for the five fields, or
stripping ` *` from the affected keys in all three resx files and letting the styled marker do the
work everywhere. The second is more consistent but touches en/de/fr parity.

### Settings category selection is not addressable

Not a regression — it was never in the gap list — but it blocked the playbook's own instructions.
The category ListBox carried no AutomationId, all six ListItems shared the Name
`System.Windows.Controls.ListBoxItem`, and clicking an item's `Text` child returned
`{"success": true}` while doing nothing. Fixed: the list is `Settings_CategoryList`, each item has
`SettingsCategory_<Name>` and an accessible Name, and
`ww_select(selector="automationId=Settings_CategoryList", optionText="Assistant")` works.

## A rendering stall that is not this branch's fault

Mid-run the window stopped presenting: screenshots kept returning an old frame while the UIA tree
advanced correctly (it showed the chat and the declined card while the pixels still showed the
persona dialog). Later launches painted only the window background.

Ruled out one at a time: an independent screen grab and `PrintWindow` returned the same dead frame,
so it was not a WinWright screenshot artifact; the process stayed responsive with a complete UIA
tree and a clean log; a `SetWindowPos` resize clipped the stale pixels without re-laying out; a
plain `Start-Process` launch reproduced it, so it was not `ww_launch`; and Notepad rendered
perfectly, so DWM and the capture path were fine.

Setting `HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration = 1` made the app render
correctly on the next launch — so it was WPF hardware rendering, i.e. a GPU/D3D device condition
on the machine, not app logic. The value has been removed again and the app renders normally with
hardware acceleration back on, so the condition cleared on its own.

No UIA result in this document depended on a screenshot, so none of the verdicts is affected. Two
lessons are in the playbook: suspect the GPU when pixels and the UIA tree disagree, and confirm
with a control app before blaming the harness.
