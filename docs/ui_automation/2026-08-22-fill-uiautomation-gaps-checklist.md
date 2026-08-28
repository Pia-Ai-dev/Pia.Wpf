# Implementation Checklist — Fill UI-Automation Gaps

**Status:** Open · **Owner:** unassigned · **Written:** 2026-08-22, rewritten 2026-08-28
**Origin:** [`2026-08-16-ui-automation-gaps.md`](2026-08-16-ui-automation-gaps.md), the field report from
driving Pia with WinWright. Tracks the "Known gaps" section of
[`ui-automation-playbook.md`](ui-automation-playbook.md).

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.

**Value** — `High` user-visible improvement or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

## What happened to the old list

The original 47 items (groups `A`–`J`, built and ticked between 2026-08-22 and 2026-08-27) are **closed and
deleted from this file**. Their reasoning is in the commits that landed them and their outcome is the
playbook's "Stable AutomationIds" table, which is the live inventory — this file is only the *open* list.

On **2026-08-28** the whole id surface was re-derived from scratch rather than re-read: every `.xaml` under
`src/Pia.Wpf` swept for interactive controls against declared ids, every C#-constructed control, every
`ControlTemplate`/`Style`-declared control, the `[InlineData]` set diffed against the lockable-view set, the
playbook table diffed both ways against source, and a fresh duplicate/prefix-shadowing pass. Each candidate
was then re-derived by an independent verifier; 11 were refuted and dropped.

That found **19 open gaps** — 17 items below, since the three edit-dialog findings merge into one — plus
four playbook drifts, which were fixed in the same commit that created this list and are not tracked here. Six of the 17 are surfaces this file already recorded as closed, either as a ticked
item or in its "already covered before this branch" preamble; the tick was wrong or incomplete, so they come
back as group `K` with the original item id named. The rest were never in scope: the scoping grep only ever
walked `<UserControl` roots for *walker-visible* controls, which structurally excluded
`AssistantHistoryView`'s own bar, every control built in C#, and every `ListBoxItem` container.

**Scope rule, unchanged:** an item earns a `[InlineData]` row only if its root is `<UserControl` **and** its
code-behind has a public parameterless constructor. A `Window` root or a host-taking constructor (every
`ui:ContentDialog`) makes a view ids-only. Check the `.xaml.cs` before planning a row; a grep settles it.

## Decision gates

Two questions cancel or reshape the steps under them. Do not tick a dependant without revisiting.

| Gate | Question it answers | Blocks | Resolution |
|---|---|---|---|
| **DG1 · rename or caveat?** | For the four prefix collisions in `P1`: rename the shadowed id, or document the caveat the way `DirectTrans_Save` already is? Renaming breaks any script pinned to the old id; a caveat leaves `automationId*=` returning 2–4 elements forever. | `P1` | **Renamed.** `MeetingAttendee_Save`→`_SaveTranscript`, `ChatChip_NewFolder`→`_AddFolder`, `AssistantHistory_Import`→`_ImportChats`, `NavItem_Assistant`→`_AssistantChat`. Confirmed no `tests/ui-scripts/` recording referenced any of the four old ids. |
| **DG2 · is `MarkdownViewer` load-bearing?** | `K6` makes the id per-message. The playbook prescribes the bare `automationId=MarkdownViewer` as *the* recipe for reading a reply in its "Chat and tool approval" section, and `tests/ui-scripts/` may use it. Decide whether to keep a stable alias or accept the break. | `K6` | **No alias kept.** Replaced with a per-message id (`MarkdownViewer_<messageId>` in chat, `MarkdownViewer_<Reference>` in the vault inspector); the playbook's reply-read recipe was rewritten to match. |

---

## K — Ticks that did not hold

- [x] **K1 · `CardDecisionBar`'s Flow-rail buttons have an empty id at runtime** (was `G1`, ticked as
  "confirmed already correct"). `CardDecisionBar.xaml:22` binds
  `AutomationProperties.AutomationId="{Binding AutomationId}"`, and `ActionCardInfo.cs:163,170,177,184` does
  set the four `ToolApproval_*` constants — but `FlowItemViewModel.BuildDecisions()` builds Deny/Approve/
  Snooze/Done at `FlowItemViewModel.cs:121,127,141,147` setting only `Label`/`Emphasis`/`Command`.
  `DecisionButton.AutomationId` is `string?` and stays null on that path, so approving or denying a parked
  tool call, or snoozing a reminder, is reachable only by index or localized text. `G1`'s audit checked the
  `ActionCardInfo` producer and not the Flow one. The `[InlineData]` row is **false-green**: the walker sees a
  `BindingExpression`, never a resolved value — lock this in `FlowItemViewModelTests` instead, the way
  `FollowUpChipAutomationIdTests` does for a binding the sweep cannot judge.
  *Deps:* none · *Effort:* **XS** · *Value:* **High**
- [x] **K2 · Vault / History / Reminders row containers carry no id and no name** (was `A3` / `D3` / `C4`).
  `PiaAssistantChatGroupCard.xaml:75-77` gets this right — a `BasedOn` style with
  `AutomationId="{Binding Id, StringFormat='AssistantChat_Row_{0}'}"` plus a `Name`. The other three apply
  `PiaMemoryRowItemStyle` raw (`PiaVaultCategoryCard.xaml:78`, `PiaHistoryGroupCard.xaml:70`,
  `PiaReminderGroupCard.xaml:79`) and that style (`Resources/Theme/PiaStyles.xaml:510`) sets no
  `AutomationProperties` at all. `PiaVaultRow`/`PiaHistorySessionRow` hold zero interactive controls, so there
  is no fallback: **row selection is the only route to `PiaVaultInspector`/`PiaHistoryInspector`**, which makes
  every `MemoryNote_*` and `HistorySession_*` id unreachable. `D3` weighed a container id and deliberately
  deferred it as "a separate design question" — that call was made *before* `D4`/`D5` added the inspector ids
  that now depend on it. Vault rows additionally report the record's full `Body` as their UIA name. Revise the
  playbook's Vault/History/Reminder row entries and its Known-gaps bullet when this lands.
  *Deps:* none · *Effort:* **S** · *Value:* **High**
- [x] **K3 · Four group-card `ListBox`es all answer to `ItemList`** (was `A3` / `C4` / `D3` / `F2`). None has
  an `AutomationId`, so each falls back to its `x:Name`: `PiaHistoryGroupCard.xaml:64`,
  `PiaAssistantChatGroupCard.xaml:64`, `PiaReminderGroupCard.xaml:73`, `PiaVaultCategoryCard.xaml:72`. One
  card renders per bucket, so N buckets on a page means N elements answering `automationId=ItemList`. This
  also blocks the fallback the playbook prescribes for id-less rows — scoping a name match inside one list —
  so it has to land with `K2`, not after it.
  **2026-08-28 close-out:** the first pass landed a literal `AutomationId="ItemList"` on
  `PiaHistoryGroupCard.xaml`, `PiaReminderGroupCard.xaml` and `PiaVaultCategoryCard.xaml` only, and
  skipped `PiaAssistantChatGroupCard.xaml:64` entirely — a review caught that the literal string was
  also a no-op regardless, since WPF's `x:Name` fallback already reported that exact value with no id
  set. A follow-up fix instead keyed each `ListBox` off its own bucket identity, matching the header
  toggle button one row up: `History_ItemList_<bucket>`, `Reminders_ItemList_<bucketKind>`,
  `Memory_ItemList_<type>`, `AssistantHistory_ItemList_<bucket>` (the last one new, closing the fourth
  card). All four are now distinct per bucket. Rebuilt clean (Debug + Release, 0/0) after the
  follow-up.
  *Deps:* none · *Effort:* **XS** · *Value:* **High**
- [x] **K4 · `Suggestion_Chip_<n>` / `AgentMode_Chip_<n>` repeat across replies** (was `E8`). The id binds
  `ItemsControl.AlternationIndex` (`PiaSuggestionChips.xaml:27`, `PiaAgentModeChip.xaml:32`), which restarts
  at 0 in every strip — and the strip is per-message, hosted at `PiaAssistantMessage.xaml:150,157`. `E8`
  recorded this as "arrival order within one reply, not globally unique", which reads as a numbering caveat;
  the real consequence is that two replies with follow-ups both sit in scrollback, so `Suggestion_Chip_0`
  matches two live buttons and tree order returns the **older** one. Key on message id + chip index.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **K5 · The Persona / Provider / Template edit dialogs are only half ided** (claimed under "Already
  covered before this branch"; group `H` was scoped to dialogs with *zero* ids, so it structurally skipped
  the partially-ided ones). Measured: `PersonaEditContentDialog.xaml` has 8 ids and **10 id-less controls** —
  Description `:42`, the **AI-draft button** `:49`, Tagline `:78`, Guardrails `:108`, OutputFormat `:124`,
  Expertise `:177`, Emoji field `:247`, AccentColor field `:322`, and the emoji/colour swatch buttons `:261`
  and `:307` (both `DataTemplate` roots inside an `ItemsControl`, so they need the per-item binding form).
  `ProviderEditContentDialog.xaml` has 8 ids and **8 id-less controls** — AzureDeploymentName `:114`,
  ReasoningEffort `:122`, WebSearch `:128`, MistralAgentId `:137`, a second WebSearch `:142`, and all three
  `ui:NumberBox` `:150`/`:159`/`:168` (timeout, context window, max output tokens). The two WebSearch
  `CheckBox`es are byte-identical and mutually exclusive only by *visibility*, and a collapsed element stays
  in the logical tree — so distinct ids are what disambiguates them, not a name match.
  `TemplateEditContentDialog.xaml:51`'s `GeneratePromptCommand` button is the third one. No test lock possible
  for any of them (`ContentDialog`).
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
  **2026-08-28 close-out:** landed as `PersonaEdit_Description/GenerateDraft/Tagline/Guardrails/
  OutputFormat/Expertise/EmojiText/AccentColor` plus per-item `PersonaEdit_EmojiSwatch_<n>` /
  `_ColorSwatch_<n>`, and `ProviderEdit_AzureDeploymentName/ReasoningEffort/WebSearch/
  MistralAgentId/Timeout/MaxContextWindow/MaxOutputTokens` plus `_MistralWebSearch` for the second
  checkbox, and `TemplateEdit_GeneratePrompt`. Not `PersonaEdit_Emoji` (would have prefix-shadowed
  its own `_EmojiSwatch_<n>` grid) or `ProviderEdit_WebSearchMistral` (would have prefix-shadowed
  `ProviderEdit_WebSearch`) — a review pass caught both before they shipped.
- [x] **K6 · `MarkdownViewer` is a literal id on a per-message control**
  (`MarkdownMessageControl.xaml:5-6`, claimed done as "`MarkdownViewer` id matches its `x:Name`"). Its hosts
  are `PiaAssistantMessage.xaml:48` — a per-message template — and `PiaVaultInspector.xaml:40`, so N replies
  on screen means N hits. Plumb the message id through the way `Answer_Copy_<id>` does, and revise the
  playbook's reply-read recipe when it lands.
  *Deps:* DG2 · *Effort:* **S** · *Value:* **Med**

## L — `AssistantHistoryView`: never in scope

The scoping grep looked for `<UserControl` roots *missing* ids. This file has six, so it never appeared —
and all six are on the header's import/export/help and the status bar, none on the filter bar.

- [x] **L1 · The view's own bar is entirely id-less.** Refresh `:44`, **Delete-all-chats `:83`** (destructive),
  search `TextBox` `:123`, the two `DatePicker`s `:134`/`:140`, provider `ComboBox` `:146`, state `ComboBox`
  `:162`, Clear-filters `:170` (which carries an `AutomationProperties.Name` and nothing else). A script
  cannot search, date-filter, provider-filter, refresh or bulk-delete on the chat-history page. The sibling
  Optimize-history surface ids all five equivalents in `Controls/History/PiaHistorySearchBar.xaml` — reuse
  those field names under an `AssistantHistory_` prefix. The `DatePicker`s get ids for scripts but contribute
  0 to the walker's count, same as `D2`'s pair.
  *Deps:* none · *Effort:* **S** · *Value:* **High**
  **2026-08-28 close-out:** landed as `AssistantHistory_Refresh/SearchQuery/StartDate/EndDate/
  ProviderFilter/StateFilter/ClearFilters` and `_BulkDeleteChats` for delete-all — not `_DeleteAll`,
  which a review pass caught prefix-shadowing the per-chat inspector's pre-existing
  `AssistantHistory_Delete`.
- [x] **L2 · It has no `[InlineData]` row** — the only lockable `UserControl` with walker-visible controls
  that lacks one (`AssistantHistoryView.xaml.cs` is a public parameterless ctor). That absence is precisely
  why `L1` survived ten slices of this work. Floor 7 (the `DatePicker`s do not count); nested stops are the
  group card and the status bar, which hold their own rows.
  *Deps:* L1 · *Effort:* **XS** · *Value:* **High**

## M — Controls built in C#

The mechanism only ever saw XAML. The app sets **zero** AutomationIds from C# today; `RoutinesViewModel.cs`'s
two `AutomationId` properties are view-model strings bound from XAML, not `AutomationProperties.SetAutomationId`
calls.

- [x] **M1 · `DialogService.ShowInputDialogAsync` builds a bare `new TextBox`** with no id and no name
  (`Services/DialogService.cs:239`). It is the rename field behind four flows whose *entry* buttons are
  documented as scriptable — `TodoViewModel.cs`, `DirectTranscriptionViewModel.cs`,
  `MeetingAttendeeViewModel.cs`. It works today only via an undocumented `type=Edit` match, and breaks the
  moment the dialog grows a second field. One `AutomationProperties.SetAutomationId` call.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **M2 · Markdown hyperlinks are procedural and id-less** —
  `Controls/Markdown/PiaMarkdownRenderer.cs:364,386`. That includes **wiki-links between vault notes**, which
  is in-app navigation over deterministic user content, not an external URL. The playbook's procedural-render
  caveat covers only the code-fence case. Key on the href or an ordinal.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
  **2026-08-28 close-out:** landed as `Markdown_Link_<n>`, a per-render ordinal (the checklist's own
  "key on the href or an ordinal" was satisfied by the ordinal option). Not per-message — the ordinal
  restarts at 0 for every render call, so two different messages/notes each containing links produce
  colliding ids, the same class of defect `K4` fixed for the suggestion chips in this same round.
  `MarkdownMessageControl.AutomationIdSuffix` (added by `K6` in this same round) already carries the
  right discriminator end-to-end but was not threaded into the renderer — left as a follow-up, not
  done here, since it means changing the renderer's public `Render` signature and every call site.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**
- [x] **M3 · The tray-icon menu is built with `Header` + `Click` only** —
  `Services/TrayIconService.cs:84,87,90`. `Tray_OpenOptimize` / `Tray_OpenAssistant` / `Tray_Exit` are
  localization keys, not ids, so reaching the app from the notification area means matching localized text,
  which [`2026-08-19-ui-testability-prompts.md`](2026-08-19-ui-testability-prompts.md) rules out. Both windows
  are openable another way, so this is low-traffic.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

## N — Remaining single controls

- [x] **N1 · `x:Name="TodoTitle"` inside the per-todo template** (`TodoView.xaml:338`,
  `TodoPanelControl.xaml:125`). One hit per card, first silently wins — the same defect that produced
  `Flow_Title_<id>`. The sibling checkbox at `TodoView.xaml:318` already keys on `Id`, so the identity is
  right there. A `TextBlock` is outside the walker's seven types, so no row demands it.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **N2 · The sidebar's new-window `ContextMenu` has two id-less `MenuItem`s**
  (`NavigationSidebarView.xaml:229,246`), whose `Header`s reuse the `Nav_Optimize` / `Nav_Assistant` keys the
  nav buttons publish as their UIA **Name** — an exact name collision. It is the only path to opening a
  second window in a chosen mode: `NavItem_NewWindow` at `:218` carries no `Command`. Not covered by the
  documented `NavigationView.MenuItems` exclusion, which is about the test lock only.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
  **2026-08-28 close-out:** landed as `NavItem_OpenOptimizeWindow` / `NavItem_OpenAssistantWindow` —
  not `NavItem_NewWindow_Optimize` / `_Assistant`, which a review pass caught prefix-shadowing the
  menu's own host button, `NavItem_NewWindow` at `:218`.
- [x] **N3 · Transcript-bubble context menus have no ids** — `DirectTranscriptionOverlay.xaml:468,473` and
  `MeetingAttendeeOverlay.xaml:390-397` — while the consent-chip menu in the same file does
  (`DirectTrans_ChipRename_{0}` at `:230`, `_ChipRevoke_{0}` at `:238`). Both menus expose identically-named
  items and only one pair is addressable, so right-clicking a bubble (the affordance the UI actually offers)
  is unscriptable. Rename has an ided alternative; revoke needs `ConsentChips.Count > 0`.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
  **2026-08-28 close-out:** `MeetingAttendeeOverlay`'s bubble menu landed as
  `MeetingAttendee_ChipRename_<speakerLabel>`, deliberately distinct from that same file's consent-chip
  `MeetingAttendee_RenameSpeaker_<speakerLabel>`. `DirectTranscriptionOverlay`'s second menu first
  landed reusing the consent-chip's own `DirectTrans_ChipRename_<speakerLabel>` / `_ChipRevoke_<...>` —
  a review pass caught that this made two separate `MenuItem`s answer to one id, and it was renamed to
  `DirectTrans_BubbleChipRename_<speakerLabel>` / `_BubbleChipRevoke_<speakerLabel>`.
- [x] **N4 · `SnackbarActionHelper.ShowSubtleWithAction` bypasses `ISnackbarService`** —
  `Helpers/SnackbarActionHelper.cs` constructs `new Snackbar(presenter)` and calls `Show()` directly, with a
  `StackPanel` + `Hyperlink` body carrying no id or name. Live from `BackgroundChatNotificationSurface` and
  `AssignmentNotificationSurface` when the Assistant window is foreground. The playbook says flatly "there is
  no snackbar to read" — true for `ISnackbarService.Show`, false for this path. Mitigated: the same action is
  also published as `Flow_ActionLink_<id>`, so this is ids-plus-a-doc-fix, not a blocker. Revise the playbook's
  snackbar bullet when it lands.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**
- [x] **N5 · The kanban column-resize `Thumb` has no id and no `x:Name`** (`TodoView.xaml:441`, one per
  column; the style adds none). A script cannot resize a named column or even locate the grip's rect. `Thumb`
  is outside both the walker's seven types and the CLAUDE.md enumeration, so no rule was broken here.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

## P — Prefix collisions

- [x] **P1 · Four same-surface prefix shadowings the playbook lists flatly**, unlike `DirectTrans_Save` ⊂
  `_SaveToVault` which it does caveat. `automationId*=` is a prefix match, so each returns 2–4 elements:
  `MeetingAttendee_Save` ⊂ `_SaveToVault` (same `StackPanel`, identical `IsRunning` gate —
  `MeetingAttendeeOverlay.xaml:443,451`); `ChatChip_NewFolder` ⊂ `_NewFolderName` / `_Confirm` / `_Cancel`
  (the header button never collapses — `PiaChatTitleChip.xaml:306` vs `:339,349,362`);
  `AssistantHistory_Import` ⊂ `_ImportStatus` / `_ImportProgress` while an import runs
  (`AssistantHistoryView.xaml:64` vs `:296,304`); `NavItem_Assistant` ⊂ `NavItem_AssistantHistory`, both
  permanently visible (`NavigationSidebarView.xaml:50,92`). This is the class of defect slice 10 found and
  fixed four of; these four survived because they span two files or two states.
  *Deps:* DG1 · *Effort:* **XS** as a caveat, **S** as renames · *Value:* **Med**
  **2026-08-28 close-out:** all four renamed per DG1 (see Decision gates table above). A review pass
  of the whole batch then caught four NEW prefix collisions this same round of fixes had introduced
  elsewhere — `AssistantHistory_DeleteAll` ⊂ `AssistantHistory_Delete`, `PersonaEdit_Emoji` ⊂
  `PersonaEdit_EmojiSwatch_<n>`, `ProviderEdit_WebSearchMistral` ⊃ `ProviderEdit_WebSearch`, and
  `NavItem_NewWindow_Optimize`/`_Assistant` ⊃ `NavItem_NewWindow` — plus a true duplicate-id
  regression in `DirectTranscriptionOverlay.xaml`'s new bubble menu (see L1, K5, N2 and N3 close-out
  notes above for the final names). Same class of defect as `P1` itself; worth checking for on any
  future automation-id batch, not just this one.

## Known-accepted — no action

Real, documented, and deliberately left. Listed so a future audit does not re-file them.

- `FileDiffCard.xaml:217` — the `CollapsedDiffRun` fold toggle sits inside an *implicit* per-type
  `DataTemplate`, which the walker cannot see and cannot demand an id on. Still open at HEAD.
- `NavigationSidebarView`'s 12 `NavItem_*` ids cannot be test-locked: `ui:NavigationView.MenuItems` reports
  zero `LogicalTreeHelper` children, so a row would pass at a vacuous floor of 0.
- All 17 `ui:ContentDialog` subclasses are unlockable — host-taking constructors.
- `CodeBlock_Copy` / `CodeBlock_Content` are literal per fence, so two fences in one reply repeat them.
- `ToolApproval_*` repeat if two approval cards are pending — deliberately semantic-per-decision-type.
- `AutocompletePopup` and `PiaChatQuickSwitcher` match lists — `ListBox`/`ListBoxItem`, arrow-key driven.
- `DirectTrans_Save` ⊂ `_SaveToVault`, `Settings_Assistant_ChatHistory`, `ActionCard_Manage_<id>` on two
  mutually-exclusive layouts — all three already carry caveats.
- `FlowView`'s `StaticResource` templates are walked once per site, so its count is 10 instances of 5
  controls; `DatePicker` internals never reach the walker.

## Suggested order

Cheapest decisive work first, then the vertical slices.

```
K1                          # XS, restores tool-approval and reminder actions from the Flow rail
K3 → K2                     # ItemList ids first, then the row containers they scope
L1 → L2                     # one surface, and the row that stops it regressing again
M1 · N1 · N2 · N5           # single-control XS work, no shared state
K5 · K6 · K4                # dialogs, then the two per-message id collisions (K6 gated on DG2)
N3 · M2 · N4 · M3           # lower-traffic surfaces
P1                          # after DG1
```

Every item revises whatever playbook line describes its surface, in the commit that lands it — that is part
of the item, not a step of its own.

`K1` and `K3` are the two that pay for themselves immediately: one is four initializers, the other four
attributes, and between them they unblock the Flow rail and every group-card row list.

---

## Open: live confirmation on Windows

**None of the id work from slice 10 (2026-08-27) was executed, and neither was the 2026-08-28 audit.** Both
were done on macOS, where `net10.0-windows` compiles (`dotnet build -p:EnableWindowsTargeting=true`, clean
rebuild, `0 Warning(s)` / `0 Error(s)` in Debug and Release) but no test runs and no window opens.

**1. Run the gate.** `dotnet test` on Windows, no filter, bar is `failed: 0`. Fourteen rows have never
executed. They fail in characteristic ways — read the message before touching XAML:

- *"only N interactive controls were inspected … below the non-vacuity floor of M"* — a floor is one too
  high. Eleven of the fourteen use the exact measured count with no headroom (only `Pia.Views.OptimizeView`
  at 12-of-13 has slack), so this is the likeliest failure. Drop the floor to N; do not add an id. Most
  exposed: `ProviderSetupStep` at 7, whose only unverified arm is `ui:PasswordBox` deriving from
  `TextBoxBase` — if that is wrong the row is 6.
- *`Assert.Equal` on the nested-view list* — exact, so it fails loudly. `PiaVaultInspector`'s
  `"MarkdownMessageControl,PiaInspectorHeader"` depends on `ScrollViewer` reporting its `Content` as a
  logical child; `PiaAssistantChatInspector`'s `"PiaAssistantMessage,PiaPersonaAvatar"` is the one list that
  comes out of an expanded `ItemTemplate`.
- *An exception rather than an assertion* — a view that cannot be constructed. Five wizard steps and
  `Pia.Views.OptimizeView` have never been instantiated standalone under `WpfStaHost`.
  `IOException("Cannot locate resource")` means a re-introduced authority-only pack URI, not a missing id.

**2. Settle what the audit could not decide statically.** Six claims above are derived from source, not from
a dump. Each is a short check against a running app:

| Check | How | Confirms |
|---|---|---|
| `Flow_Decisions_<id>` subtree on an open reminder card | `ww_dump_tree` | `K1` — that the null `AutomationId` really reaches UIA as empty rather than falling back |
| Vault row UIA names | `ww_dump_tree` on an expanded category | `K2` — whether the positional record's `ToString()` (including `Body`) is what the peer reports |
| `AssistantHistoryView.xaml:44` / `:83` names | `ww_snapshot` | `L1` — what a content-less `ui:Button` with neither id nor `Name` resolves to |
| Both collapsed `EnableWebSearch` boxes | name match while one is `Collapsed` | `K5` — that a collapsed element really does answer, per the playbook's own note |
| A foreground-window snackbar | trigger a background chat completion | `N4` — that `TryFindForegroundSnackbarPresenter` resolves in a real Assistant window |
| An added `AssistantHistoryView` row | `dotnet test` | `L2` — lockability is measured, a passing floor is not |

**3. Confirm the slice-10 ids reach the live tree** with WinWright, per
[`ui-automation-playbook.md`](ui-automation-playbook.md). A green row proves an id is present on a constructed
object; it says nothing about whether a script can drive it. Ranked by what a walkthrough hits first:

| Surface | What to confirm | State needed |
|---|---|---|
| First-run wizard (`Wizard_*`, `Wizard<Step>_*`) | Drive the flow end to end with ids only. Cheapest signal: `Wizard_Back` absent on step 0, present after. `WizardE2EE_Enable` is a `ui:ToggleSwitch` with no InvokePattern — `ww_set_checked`. | A fresh `PIA_DATA_DIR`; re-reachable afterwards through `Setup_RunWizard`. |
| Optimize hotkey window (`OptimizeWindow_*`) | Input state, then comparison state after a real optimization. `OptimizeWindow_LanguageItem_<EN\|DE\|FR>` needs the dropdown open; `_TargetAssistant` lives in a context-menu popup, so re-scan rather than searching the window subtree. Confirm `InputTextBox` now matches only the Assistant composer. | A configured provider. |
| Vault inspector + status bar (`MemoryNote_*`, `MemoryStatus_*`) | `MemoryNote_Body` / `_Cancel` / `_Save` exist only while editing; read mode must show `MarkdownViewer` instead. | A vault with at least one note. Do not invoke `MemoryStatus_RegenerateEmbeddings` outside a throwaway profile — it re-embeds. |
| History inspector + status bar (`HistorySession_*`, `HistoryStatus_*`) | Flipping the tab swaps which copy button and text box is visible; they overlap in one grid cell. `HistoryStatus_LoadMore` needs more sessions than one page. | A profile with saved optimize sessions. |
| Assistant chat-history inspector (`AssistantHistory_*`) | `automationId*=AssistantHistory_Export` must return exactly three (`ExportAll`, `ExportArchive`, `ExportMarkdown`). `AssistantHistory_Delete` acts on the selected chat, not the hovered row. | A profile with a saved chat; delete only a throwaway one. |
| Settings → Plugins (`Plugins_*`) | Enumerate `automationId*=Plugins_Toggle_`: one hit per plugin, each ending in a distinct guid — that is what proves the per-item binding resolved. | A signed-in cloud account with a synced plugin. |
| E2EE onboarding (`E2EEOnboarding_*`) | After `_StartApproval` the Initial pair must go Offscreen/Collapsed as the Waiting pair appears — the check that justifies the stage-qualified names. | A sync account on an E2EE server, device not yet approved. `_ErrorTryAgain` needs a forced failure. |
| Vault help dialog (`VaultHelp_OpenFolder`) | That the id survives the `ContentDialogHost` overlay — no test row behind it. | Any profile. Do not invoke unattended; it launches Explorer. |
