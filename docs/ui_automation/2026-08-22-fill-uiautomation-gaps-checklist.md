# Implementation Checklist — Fill UI-Automation Gaps

Tracking file for closing the "Known gaps" section of
[`ui-automation-playbook.md`](ui-automation-playbook.md). One row per `UserControl` that
actually contains an interactive control the playbook's `ViewAutomationIdTests` walker can see
(`ButtonBase`, `ComboBox`, `TextBoxBase`/`RichTextBox`, `PasswordBox`, `Slider`, `Expander`,
`TabItem`). Tick as each control gets ids **and** a locking `[InlineData]` row in
`tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs`.

**Effort** — `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.

**Value** — `High` user-visible improvement or a real risk closed · `Med` worthwhile, not headline
· `Enabler` little standalone value, unblocks a High.

## How this list was built

Two greps, intersected: every `src/Pia.Wpf/**/*.xaml` file whose root is `<UserControl`, crossed
with every file containing one of the seven walker-recognized element types. That is the *exact*
remaining scope — not a guess. `FirstRunWizardWindow.xaml` (root `<Window`) and `MainWindow.xaml`
/ the `Resources/Styles/*.xaml` resource dictionaries are out of scope for this mechanism (the
walker does `Activator.CreateInstance` on the type, which only works for a parameterless
`UserControl`); they get ids by hand with no test lock. `MainWindow.xaml`'s 5 controls were done
that way on 2026-08-26 — `Setup_OpenSettings` / `Setup_RunWizard` (the setup-required overlay,
which covers any feature view until a provider is configured and so blocks a fresh-profile script
before it reaches anything else), `Update_RestartNow` / `Update_Dismiss`, `E2EE_OpenOnboarding`.
The setup pair is live-confirmed; the two bars need states a throwaway profile cannot reach.

**Already done** (slice 1 + slice 2 of this branch, both committed, gate green at each step):
`AssistantView` (composer toolbar, suggestion chips, weak-provider banner, persona picker,
chat/agent lever, send/run-in-background, per-message copy button), `MarkdownMessageControl`
(`MarkdownViewer` id matches its `x:Name`), `RoutinesView` (run-history open-chat link),
`MeetingAttendeeOverlay` (close, open-settings, save-to-vault, summarize, per-speaker rename),
`SettingsViews/PersonasView`. Already covered before this branch: `SettingsViews/GeneralView`,
`SettingsViews/AssistantView`, `SettingsViews/ProvidersView`, `SettingsViews/AccountView`,
`SettingsViews/OptimizeView`, and the Persona/Provider/Template edit dialogs.

**Slice 3** (previous session, committed, gate green): the cheap audits `A1`/`A2`/`C1`/`C2`/`D1`/
`D2`/`B2`/`B3`/`J1` (all literal ids, locked with `[InlineData]`), `J2` (audit only — dropped, see
below), and `E1` (`PiaAnswerToolbar`, per-item ids keyed on `AssistantMessage.Id` — see the id
table). `G1` was already done in slice 2.

**Slice 4** (this session, committed, gate green): the four per-item row types — `A3`
(`PiaVaultCategoryCard`), `C3`+`C4` (`PiaReminderRow` + `PiaReminderGroupCard`), `D3`
(`PiaHistoryGroupCard`; `PiaHistorySessionRow` had nothing to id), `F1`+`F2`
(`PiaAssistantChatRowContent` + `PiaAssistantChatGroupCard`). Found and documented (in the
playbook's Known gaps) a walker mechanism hazard along the way: a `DataTemplate` whose root is
itself a `UserControl` isn't flagged as a nested-view stop, so its controls get swept into the
ancestor group-card's inspected set — true for `PiaReminderGroupCard`/`PiaReminderRow` and
`PiaVaultCategoryCard`/`PiaVaultRow`, but not for `PiaAssistantChatGroupCard`/`PiaAssistantChatRow`
since the interesting control (`PiaAssistantChatRowContent`) sits one level deeper than the
template root.

**Slice 5** (this session, committed, gate green): `B1`+`B4`, the Todo view family. `TodoView`
turned out to hold 9 walker-visible controls, not the ~43 an earlier estimate guessed (that
number was never measured) — an add-todo bar (title/priority/due-date/record/add), a per-column
"..." menu plus its 3 context-menu items and a closed-column chevron (keyed on
`KanbanColumnViewModel.Id`), and per-todo complete/edit/delete (keyed on `TodoItem.Id`).
`TodoPanelControl` (the same board embedded read-only in `AssistantView`) got its own
`TodoPanel_`-prefixed ids — deliberately not `Todo_`, since both surfaces have a close/record/add/
new-title field and a shared prefix would make a script's `automationId*=Todo_` match rows on
whichever surface happened to be open. This closes the `TodoPanelControl` entry in the playbook's
`AssistantView` nested-view gap list (it is still a nested-view *stop* when walking `AssistantView`
itself — only its own `[InlineData]` row was missing).

**Slice 6** (this session, committed, gate green): the rest of the composer-adjacent group —
`E2`/`E4`/`E7`/`E9`, audited in parallel first per the "audit before estimating" rule
(`RunProgressPanel` turned out to hold 21 controls, not a guess; `PiaChatQuickSwitcher` exactly 1;
`AutocompletePopup` 0, so it got dropped like `J2` with no code change; `PiaReasoningView` exactly
1, and turned out to have no `Expander` despite the checklist's own description) — plus `E8`, whose
audit surfaced a real identity question resolved via 3 different mechanisms (own-DP
`ElementName=Root` binding for the two chip types that had a natural non-content field, a new
`GroupName` DP for the overflow panel's two-simultaneous-instances case, and a documented
skip-with-reasoning for the two chip types with no non-content identity at all — see `E8`'s entry).
Also fixed a correctness gap from slice 5: `TodoView`'s 3 `MenuItem` ids relied on unverified
`ContextMenu` DataContext inheritance; switched to `{Binding Tag.Id, RelativeSource={RelativeSource
Self}}`, which is correct regardless of whether that inheritance holds.

**Slice 7** (this session, committed, gate green): Cards & Flow — `G2`/`G3`/`G4`, audited in
parallel first. `ActionCardControl` (`G2`) needed a real model change (a new `Guid Id` on
`ActionCardInfo`) since it had no per-card identity at all and, unlike `G1`'s decision buttons, its
controls stay interactive on every already-resolved card, not just the one currently pending.
`FileDiffCard` (`G3`) surfaced a new walker-blind spot distinct from the known sweep hazard:
implicit per-type `DataTemplate`s declared in `ItemsControl.Resources` are invisible to the
mechanism, since it only reads `ItemsControl.ItemTemplateProperty`/`ContentTemplateProperty`/
`HeaderTemplateProperty` locally. `FlowView` (`G4`) confirmed the "measure, don't guess" rule
again — 5 distinct controls measured at 10 instances, because two of its templates are legitimately
applied at two separate sites (a real list plus a hidden arrival-peek clone). A same-session review
caught two more issues before they shipped: the header's literal ids would have repeated onto that
hidden clone, which `Visibility="Hidden"` does not remove from the UIA tree or block `InvokePattern`
for — fixed via a `Tag`-keyed `RelativeSource` binding, same shape as `PiaChipOverflowPanel`'s
`GroupName`; and the playbook's `ActionCardInfo.Id` row got a lifetime caveat, since it is a
per-render UI guid, not a persisted domain id like the other per-item keys on this list.

**Slice 8** (this session, committed, gate green): `H1`-`H4` plus `I5`. A single grep on the four
dialogs' `.xaml.cs` class declarations, checked before spinning up any audit, settled the whole
group at once: all four derive from Wpf.Ui's `ContentDialog` with parameterized constructors, not
`UserControl`, so none of them can ever get a `[InlineData]` row — this reclassifies `H1`-`H4` from
"content dialogs, effort XS-S" into the same "ids only, no test lock" shape as the `I` group,
before writing a single line of XAML. `AssignmentConsentContentDialog` (`H4`) needed a real
identity lookup for its per-record checkboxes (`AssignmentScopeItemViewModel.Item.EntityId`, a
`Guid` two levels down through the wrapped record). `AssignmentsView` (`I5`), unlike its sibling
dialogs, IS a plain `UserControl` and got a real `[InlineData]` row.

**Slice 9** (committed as "Give the assistant view's last id-less controls an AutomationId", gate
green): `E3`/`E5`/`E6` — the chat-title chip and the two overlays — plus, in the same commit,
`MainWindow`'s 5 hand-added ids, `MarkdownMessageControl`, and the `E8` reversal that ided
`PiaSuggestionChips`/`PiaAgentModeChip` on a container index and locked them with the second test,
`FollowUpChipAutomationIdTests`. That leaves `AssistantView` itself with no id-less control left at
any depth.

**Since slice 9** — three changes landed ids without touching this file, so it drifted:
`AssistantChat_Open_<id>` and the `AssistantChat_Row_<id>` container id on
`PiaAssistantChatRowContent` (see `F1`); a new `PiaRoutinesSearchBar` control, ided
(`Routines_SearchQuery`) and locked with its own `[InlineData]` row the same day it was added, with
`RoutinesView` growing 15 → 17 walker-visible controls as the Teams-scheduling work landed; and
`OptOutConfirmContentDialog`, a new `ui:ContentDialog` that arrived with its one id
(`OptOutConfirm_DontAskAgain`) already on it. None of the three is an open gap — they are recorded
here so the "exact remaining scope" claim above stays true.

**Slice 10** (2026-08-27, the last slice — every remaining item): `A4`/`A6`, `D4`/`D5`, `F3`/`F4`,
`H5`, and the whole of group `I`. Run as seven parallel units, each implemented and then re-derived
from scratch by an independent adversarial verifier; all seven came back CLEAN, with the base types
of every `ui:` control resolved out of Wpf.Ui 4.3.0's IL rather than assumed (`ToggleSwitch` →
`ToggleButton`, `HyperlinkButton`/`Button` → `System.Windows.Controls.Button`, `PasswordBox` →
`ui:TextBox` → `TextBox`; `InfoBar`/`ProgressRing`/`TitleBar`/`SymbolIcon` all excluded). 14 new
`[InlineData]` rows, 74 new ids, 4 views deliberately dropped with zero ids
(`PiaHistoryInspectorEmptyState`, `PiaChatStateBadge`, `ModesOverviewStep`, `ReadyStep` — all
vacuous at floor 0). Two corrections to this file's own guesses: `PiaVaultStatusBar` was NOT "just
status text", and the group `I` heading's "no test-lock mechanism" was wrong for four of its five
entries. The cross-unit sweep afterwards found no new duplicate literal id anywhere in `src/` and
one class of hygiene defect worth fixing before commit — four same-surface prefix shadowings, where
`automationId*=X` would have returned two controls that are on screen at the same moment:
`OptimizeWindow_Optimize` ⊂ `_OptimizedText`, `OptimizeWindow_Language` ⊂ `_LanguageItem_<id>`,
`WizardAccount_SignIn` ⊂ `_SignInGoogle`/`_SignInMicrosoft`/`_SignInEntraId`, and `WizardProfile_Name`
⊂ `_NameVoice` (with `_Nickname` and `_Location` the same). Renamed to `_ResultText`,
`_LanguagePicker`, `_SignInLocal` and `WizardProfile_Voice<Field>` respectively. `WizardE2EE_*`
containing the substring `E2EE_` was left alone: `automationId*=` is a prefix match, no field name
is shared with `E2EE_OpenOnboarding`, and the two surfaces never render together.

**Excluded, not just deferred** — `NavigationSidebarView`: its 12 `NavItem_*` buttons already all
carry ids (verified by hand), but they hang off `ui:NavigationView.MenuItems`, which
`LogicalTreeHelper` reports zero children for — a test row here would pass at a vacuous floor of
0 and catch nothing. `SettingsView.xaml` (the category shell): its one interactive element is
`Settings_CategoryList`, a `ListBox` — not one of the walker's seven types, so it's outside this
mechanism too, and it already has an id.

---

## A — Vault (`VaultView` is pure composition; every control lives in a nested `Pia*` control)

- [x] **A1 · `PiaVaultHeader`.** Already has `Memory_Help`; audited and ided its 5 buttons
  (Back/Home/Refresh/OpenFolder/ShowHelp) — no separate "new topic" affordance exists here.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **A2 · `PiaVaultSearchBar`.** Query box ided (`Memory_SearchQuery`); no clear button exists.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **A3 · `PiaVaultCategoryCard`.** Ided the expand/collapse toggle, per-category:
  `Memory_CategoryToggle_<type>` keyed on `MemoryGroupViewModel.Type`. `PiaVaultRow` (the
  ItemTemplate's root, `<mem:PiaVaultRow/>`) has zero walker-recognized controls, so this reduces
  to just the one button; confirmed there's no `PiaVaultRow` case to add. `PiaTypeChip` inside it
  shows up as a nested-view stop, correctly, since it's one level deeper than the template root
  (see the playbook's new "root-of-DataTemplate" hazard note).
  *Deps:* none · *Effort:* **S** · *Value:* **High** (the list you'd script against most)
- [x] **A4 · `PiaVaultInspector`.** 3 walker-visible controls, measured: the raw-markdown editor
  `MemoryNote_Body` plus the `MemoryNote_Cancel` / `_Save` pair, all three inside `Visibility`-bound
  borders that exist only while `IsEditing`. Shares `PiaInspectorHeader`'s `MemoryNote_` prefix on
  purpose — it is one pane, and no field name collides with that control's four. Row 3 / 0 /
  `MarkdownMessageControl,PiaInspectorHeader`; both nested stops already hold their own rows, so the
  read-mode body stays addressable as `MarkdownViewer`.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **A5 · `PiaInspectorHeader`.** Shared inspector header chrome (Vault uses it; check reuse).
  Landed with the Obsidian button: `MemoryNote_Edit` / `_Copy` / `_Delete` / `_OpenObsidian`, on its own
  prefix so the page header's `Memory_*` buttons stay prefix-disjoint. Only Vault uses it — no other
  reuse to check. `PiaTypeChip` is its one nested-view stop.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **A6 · `PiaVaultStatusBar`.** The "likely just status text" guess did not hold: it owns one
  real `Button` bound to `RegenerateEmbeddingsCommand`, now `MemoryStatus_RegenerateEmbeddings`, so
  it earns a row (1 / 0 / empty) instead of being dropped. The state dot and its label are an
  `Ellipse` and a `Run`-composed `TextBlock` driven by `Style` triggers the walker never expands.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

## B — Todo

- [x] **B1 · `TodoView` itself.** Unlike Vault/History/Reminders this view has direct interactive
  controls of its own — measured at 9, not the ~43 an earlier unmeasured estimate guessed. Add-todo
  bar (`Todo_NewTitle`/`_NewPriority`/`_NewDueDate`/`_Record`/`_AddTodo`), per-column
  (`KanbanColumnViewModel.Id`): `Todo_ColumnMenu_<id>` plus its 3 context-menu items and
  `Todo_ExpandColumn_<id>`, per-todo (`TodoItem.Id`): `Todo_Complete_<id>`/`_Edit_<id>`/
  `_Delete_<id>`. `Todo_AddTodo`, not `Todo_Add`, to keep it disjoint from B2's `Todo_AddColumn`
  under a prefix match.
  *Deps:* none · *Effort:* **M** · *Value:* **High**
- [x] **B2 · `PiaTodoHeader`.** Already has `Todo_Help`; ided `AddColumn`/`Refresh`.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **B3 · `PiaTodoSearchBar`.** Query box ided (`Todo_SearchQuery`); no clear button exists.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **B4 · `TodoPanelControl`.** The right-side panel embedded in `AssistantView` — was already
  listed as a nested-view gap there; fixing it here closes both. `TodoPanel_Close`/`_NewTitle`/
  `_Record`/`_Add`/`_OpenFullView`, per-todo (`TodoItem.Id`): `TodoPanel_Complete_<id>`. Prefixed
  `TodoPanel_`, not `Todo_` — this panel and `TodoView` both have a record/add/new-title field.
  *Deps:* none · *Effort:* **S** · *Value:* **High** (shared by two surfaces)

## C — Reminders

- [x] **C1 · `PiaRemindersHeader`.** Already has `Reminders_Help`; ided the 4 bulk-action buttons.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **C2 · `PiaRemindersFilterBar`.** Ided the 5 static filter `RadioButton`s (not an
  `ItemsControl`, so literal ids are correct here — no per-item mechanism needed).
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **C3 · `PiaReminderRow`.** Ided the 4 hover-action buttons (ToggleEnable/Snooze/Dismiss/
  Delete), per-reminder keyed on `Reminder.Id`: `Reminders_ToggleEnable_<id>` / `_Snooze_<id>` /
  `_Dismiss_<id>` / `_Delete_<id>`. Given its own standalone `[InlineData]` row (floor 4, all 4
  per-item, nested `PiaReminderStatusChip`).
  *Deps:* none · *Effort:* **S** · *Value:* **High**
- [x] **C4 · `PiaReminderGroupCard`.** Ided the header expand/collapse toggle, per-bucket:
  `Reminders_GroupToggle_<bucketKind>` keyed on `ReminderGroupViewModel.BucketKind`. Given its
  own `[InlineData]` row too, not skipped: `PiaReminderGroupCard`'s `ItemTemplate` root is the
  literal `<rem:PiaReminderRow/>`, which the walker's nested-view check doesn't flag (see the
  playbook's new hazard note) — so testing it standalone sweeps in all 4 of C3's row buttons
  (floor 5 total, 5 per-item, nested `PiaReminderStatusChip` — confirmed empirically, not
  guessed: an earlier assumption that this would stay at an empty nested list was wrong).
  *Deps:* C3 · *Effort:* **XS** · *Value:* **Med**

## D — History (`HistoryView`, the Optimize-mode session list)

- [x] **D1 · `PiaHistoryHeader`.** Already has `History_Help`; ided `Refresh`/`DeleteAll`.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **D2 · `PiaHistorySearchBar`.** Ided the query box, the Templates `ComboBox` and the clear
  button (all walker-visible); also ided the two `DatePicker`s for script use even though
  `Activator.CreateInstance` never triggers their `OnApplyTemplate`, so the walker can't see or
  demand ids on them (confirmed: they contributed 0 to the measured control count).
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **D3 · `PiaHistoryGroupCard`** and **`PiaHistorySessionRow`.** `PiaHistorySessionRow` has
  zero walker-recognized controls (three `TextBlock`s only, no buttons at all — row selection is
  a bare `ListBoxItem` click), so this reduced to just `PiaHistoryGroupCard`'s header
  expand/collapse toggle, per-bucket: `History_GroupToggle_<bucket>` keyed on
  `SessionGroupViewModel.Bucket` (floor 1, 1 per-item, no nested views — the swept-controls
  hazard from C4/A3 doesn't apply here since the swept-in row itself has nothing to sweep). A
  ListBoxItem-level id for picking one session row by identity was considered and deliberately
  not added — no test lock possible (same class of gap as the History `DatePicker`s), and the
  shared `PiaMemoryRowItemStyle` is reused across Vault/Reminders/History/AssistantChat, so it'd
  need checking every other item type actually exposes a usable identity too; treat as a
  separate design question if a script ever needs it.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **D4 · `PiaHistoryInspector`, `PiaHistoryInspectorHeader`, `PiaHistoryInspectorEmptyState`.**
  Prefix `HistorySession_`, deliberately not the page-level `History_`. The inspector measured 6 —
  two segmented `RadioButton` tabs, two copy buttons, two read-only `TextBox`es, one half of each
  pair collapsed at a time — row 6 / 0 / `PiaHistoryInspectorHeader`; the header measured 1
  (`HistorySession_Delete`), row 1 / 0 / empty. `PiaHistoryInspectorEmptyState` is
  `Border` → `StackPanel` → icon + two `TextBlock`s: zero controls, so no ids and no row, the same
  vacuous-floor call as `J2` and `E7`. Anchor a "nothing selected" assertion on the absence of
  `HistorySession_*` rather than on an id of its own.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **D5 · `PiaHistoryStatusBar`.** One `Button`, `HistoryStatus_LoadMore`; row 1 / 0 / empty. It
  is collapsed while a load is in flight and present-but-disabled once every session is loaded, so a
  script asserts presence plus `IsEnabled`, never clickability.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

## E — Chat composer internals (nested under `AssistantView`, listed as gaps there)

- [x] **E1 · `PiaAssistantMessage` → `PiaAnswerToolbar`.** The real location of the assistant
  bubble's Copy/Speak/Regenerate/RegenerateOptions/Export/RateUp/RateDown buttons
  (`PiaAssistantMessage.xaml` itself has no direct controls — it delegates here). One
  `PiaAnswerToolbar` instance is reused per assistant reply, so a literal id would collide across
  messages the same way `Assistant_CopyMessage_<guid>` was designed to avoid — used the per-item
  binding form keyed on `CommandParameter.Id` (`AssistantMessage.Id`) instead: `Answer_Copy_<id>`
  / `_Speak_` / `_Regenerate_` / `_RegenerateOptions_` / `_Export_` / `_RateUp_` / `_RateDown_`.
  Deliberately **not** prefixed `Assistant_` — that would collide with the existing
  `Assistant_CopyMessage_<guid>` (user bubble) under a prefix-match enumeration. Also ided the 3
  regenerate-style `MenuItem`s — deliberately a *different* prefix,
  `Answer_RegenerateStyle_Shorten`/`_Detailed`/`_Exportable`, since reusing
  `Answer_RegenerateOptions_` would make a prefix-match enumeration hit the chevron button plus
  all three menu items once opened. Literal ids are fine here since only one context menu is ever
  open at a time; not walker-visible, no test lock.
  Suggestion/SwitchToAgent live in `PiaSuggestionChips`/`PiaAgentModeChip` (E8) and
  ManageToolPermissions in `ActionCardControl` (G2) — both closed separately, not by this item.
  Highest-traffic item in this whole checklist: every assistant reply renders this.
  *Deps:* none · *Effort:* **M** (313 lines, 7 distinct actions) · *Value:* **High**
- [x] **E2 · `RunProgressPanel`.** 850 lines, 21 walker-visible controls — measured, not guessed.
  11 root-level literal ids (`Run_Pause`/`_Continue`/`_DenyTool`/`_RejectPlan`/`_Publish`,
  `Run_CardToggle`, `Run_NudgeText`, `Run_ShowEarlierSteps`/`_ShowLaterSteps`,
  `Run_TimelineToggle`/`_ChildrenToggle`), 9 per-step (keyed on `StepRowViewModel.StepId`):
  `Run_StepEdit_<id>`/`_StepInsertBelow_<id>`/`_StepMoveUp_<id>`/`_StepMoveDown_<id>`/
  `_StepSkip_<id>`/`_StepEditTitle_<id>`/`_StepEditIntent_<id>`/`_StepEditCancel_<id>`/
  `_StepEditSave_<id>`, and 1 per-child-run (keyed on `ChildRunRowViewModel.RunId`):
  `Run_ChildToggle_<id>`. `PiaPersonaAvatar` is a genuine nested-view stop one level below the
  Steps template root, not the sweep hazard. A second `ItemsControl` (`LastStepView`) reuses the
  Steps template via a `Binding`, not a literal `DataTemplate`, so the walker never expands it —
  the ids on the Steps template cover it for free at runtime with no separate handling needed.
  *Deps:* none · *Effort:* **M** · *Value:* **High**
- [x] **E3 · `PiaChatTitleChip`.** The chat-history picker at the top-left of the Assistant view,
  measured at 11 walker-visible controls. Own prefix `ChatChip_`, deliberately not `AssistantChat_`
  — the flyout's rows embed `PiaAssistantChatRowContent`, so a shared prefix would make one
  enumeration return the chip's chrome and the row buttons together. 9 literal
  (`ChatChip_Toggle`/`_Search`/`_WorkingDir`/`_NewChat`/`_ShowAllChats`/`_NewFolder`/
  `_NewFolderName`/`_NewFolderConfirm`/`_NewFolderCancel`) and 2 per-item: `ChatChip_Resume_<chatId>`
  keyed on `ChatChipItemViewModel.Id` and `ChatChip_Crumb_<index>` keyed on
  `WorkingDirectoryCrumb.Index` (0 = root — `Name` is a user-chosen folder name, so it stays out of
  a permanent enumerable property). The `WorkingDirEntries` `ListBox` got a literal
  `ChatChip_FolderEntries` with no per-row ids: its items are bare folder-name strings, so a
  container id could only key on the name itself. `PiaAssistantChatRowContent` is a genuine
  nested-view stop. Live-confirmed through both popups.
  *Deps:* none · *Effort:* **M** · *Value:* **Med**
- [x] **E4 · `PiaChatQuickSwitcher`.** Measured at exactly 1 control (`QueryBox`, the search input);
  its `ListBox`/`ListBoxItem` match list is not a walker-recognized type, same exclusion reasoning
  as `NavigationSidebarView`. Ided `QuickSwitcher_Query` — its own prefix, not `AssistantChat_`,
  since nothing else needs disambiguating from it.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **E5 · `VoiceModeOverlay`.** Exactly 3 controls, all literal: `VoiceMode_Done` (Listening
  only), `VoiceMode_Stop` (Speaking only), `VoiceMode_End` (always). `RecordingIndicator` is a
  nested-view stop with zero interactive controls of its own. Test-locked only — entering the
  overlay needs `_ttsService.HasVoiceLoaded`, which a throwaway profile has no way to satisfy.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **E6 · `DirectTranscriptionOverlay`.** 12 walker-visible controls, prefix `DirectTrans_` to
  match its resource keys and stay disjoint from `MeetingAttendee_`. 11 literal — `_ToggleStats`,
  `_Close`, `_DisclaimerAccept` (a `ui:ToggleSwitch`, which is a `ButtonBase` and so IS swept),
  `_DisclaimerClose`, `_Start`, `_FooterClose`, `_Stop`, `_Resume`, `_Save`, `_SaveToVault`,
  `_Summarize` — plus per-bubble `DirectTrans_RenameSpeaker_<speakerLabel>`, mirroring
  `MeetingAttendee_RenameSpeaker_<speakerLabel>` and keyed on the raw diarizer label rather than
  the renamed display name. Three separate buttons bind `CloseCommand` in mutually visible
  regions, hence the `_Close` / `_DisclaimerClose` / `_FooterClose` split. The consent chip is a
  `Border` with a `ContextMenu` — no automation peer, so its two `MenuItem`s got
  `DirectTrans_ChipRename_<label>` / `_ChipRevoke_<label>` with no test lock. Live-confirmed
  through the disclaimer, running and stopped states.
  *Deps:* none · *Effort:* **M** · *Value:* **Med**
- [x] **E7 · `AutocompletePopup`.** Audited, dropped — same shape as `J2`. Its only interactive
  surface is a `ListBox`/`ListBoxItem` match list, neither a walker-recognized type; zero
  walker-visible controls, so a test row would sit at a vacuous floor of 0. No ids added, no row.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- **E8 · `PiaSuggestionChips`, `PiaFileChip`, `PiaSourceChip`, `PiaChipOverflowPanel`,
  `PiaAgentModeChip`.** Small chip controls rendered inside assistant messages / the composer.
  All five done, though the last two took a second pass — the audit surfaced a real identity
  question, not just mechanics.
  - [x] `PiaFileChip`. 3 buttons (open default / open in VS Code / reveal), none inside an
    `ItemsControl` — each `PiaFileChip` instance is itself one file, so the ambiguity is across
    *sibling instances* (a message with several file chips), not template rows. Keyed on the
    control's own `FileName` DP via `ElementName=Root` (not `AbsolutePath`, even though that DP
    exists and the chip's own `ToolTip` already shows it — a tooltip is transient, an
    `AutomationId` is a permanent enumerable property, so it doesn't bake a full local filesystem
    path into a UIA-visible surface): `FileChip_Open_<fileName>`, `_OpenVsCode_<fileName>`,
    `_Reveal_<fileName>`. Caveat: two attachments with the same filename in different directories
    collide — accepted, same class as the tool-name-not-unique caveat elsewhere.
  - [x] `PiaSourceChip`. 1 button, keyed on the control's own `Number` DP (an int, the per-message
    citation number) via `ElementName=Root`: `SourceChip_Open_<number>`.
  - [x] `PiaChipOverflowPanel`. 1 button (`MoreButton`). `PiaAssistantMessage.xaml` renders TWO
    instances of this control simultaneously in one message (Sources overflow at line ~92, Files
    overflow at line ~106, independently visible) — a literal id would be a genuine two-hit
    ambiguity, not just a documented caveat, so it needed a real fix: added a `GroupName` DP the
    call site sets (`"Sources"` / `"Files"`), bound via `ElementName=Root`:
    `ChipOverflow_More_<groupName>`.
  - [x] `PiaSuggestionChips`, `PiaAgentModeChip` — **ided 2026-08-26, reversing the earlier
    audited skip.** The item is content all the way down (a raw `string`, or an
    `AgentModeSuggestion`'s model-generated `Goal`/`Reason`), so the id keys on the CONTAINER
    instead: `{Binding (ItemsControl.AlternationIndex), RelativeSource={RelativeSource
    AncestorType=ContentPresenter}, StringFormat='Suggestion_Chip_{0}'}`, and `AgentMode_Chip_{0}`.
    `AlternationCount="20"` is required — without it every row reports index 0. The original
    rejection was right about the mechanism and wrong about the conclusion: `LoadContent()` builds
    the template with no generator behind it, so the sweep really would go green on a binding that
    resolves to nothing. That is why this one is locked by a SECOND test,
    `FollowUpChipAutomationIdTests`, which renders both lists through a real layout pass and asserts
    the resolved strings (`Suggestion_Chip_0/1/2`, `AgentMode_Chip_0/1`) rather than the presence of
    a `Binding`. The "a script has to match on the text anyway" argument does not survive either: an
    index id is positional, so a script can press the first follow-up chip without knowing what the
    model wrote. Caveat: the index is arrival order within one reply, not globally unique, and it
    wraps past `AlternationCount`. Not live-confirmed — a follow-up chip needs a real provider reply.
  *Deps:* E1 (same message surface) · *Effort:* **S** total · *Value:* **Med**
- [x] **E9 · `PiaReasoningView`.** No `Expander` despite the description — the collapse is hand-rolled
  via a bool + `Visibility` triggers. Exactly 1 control, the collapsed-state toggle button, reused
  per assistant reply (same DataContext as `PiaAnswerToolbar`/E1) — keyed on `AssistantMessage.Id`:
  `Reasoning_Toggle_<id>`. Own prefix, not `Answer_`, since it is a materially different affordance
  from the reply toolbar it sits beside.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

## F — Assistant History (`AssistantHistoryView` already has 4 ids for import/export/help; the
nested rows do not)

- [x] **F1 · `PiaAssistantChatRow`, `PiaAssistantChatRowContent`.** `PiaAssistantChatRow` itself
  has no direct controls — it just hosts `PiaAssistantChatRowContent`, a second nested
  `UserControl`, which is a genuine (one-level-deeper) nested-view stop, not the swept-in hazard
  case. So the fix landed on `PiaAssistantChatRowContent`'s delete button, ided per-chat
  keyed on `AssistantChatRowViewModel.Id` (`Chat.Id`): `AssistantChat_Delete_<id>`. Given its own
  standalone `[InlineData]` row (nested `PiaChatStateBadge`). A later change ("Let a script open a
  named past chat, not only delete one") added the sibling `AssistantChat_Open_<id>`, taking this
  row to floor 2, 2 per-item — the control is now 2 buttons, not the 1 this entry originally landed
  — and gave the row CONTAINER an `AssistantChat_Row_<id>` through
  `PiaAssistantChatGroupCard`'s `ItemContainerStyle` (a `ListBoxItem`, so it is not walker-visible
  and `F2`'s floor is unchanged).
  *Deps:* none · *Effort:* **S** · *Value:* **High**
- [x] **F2 · `PiaAssistantChatGroupCard`.** Bundled with F1 since it's the same header-toggle
  shape as A3/C4/D3. Ided the header expand/collapse toggle, per-bucket:
  `AssistantHistory_GroupToggle_<bucket>`. Bound to `AssistantChatGroupViewModel.Bucket` (a
  non-null `HistoryDateBucket`) rather than its `GroupKey` (a `string?` that happens to always be
  set by the one construction site today, but isn't guaranteed non-null by the type) — same
  identity, no nullability risk. Own `[InlineData]` row: floor 1, 1 per-item, nested
  `PiaAssistantChatRowContent` (confirmed — testing the group card standalone correctly stops at
  the row-content control, since it's one level deeper than the swept-in `PiaAssistantChatRow`
  template root, not the hazard case).
  *Deps:* F1 · *Effort:* **XS** · *Value:* **Med**
- [x] **F3 · `PiaAssistantChatInspector`.** Three were missing beside the `AssistantHistory_ExportArchive`
  it already carried; continued the same prefix with `_Resume`, `_ExportMarkdown`, `_Delete`. Row
  4 / 0 / `PiaAssistantMessage,PiaPersonaAvatar` — the only new row whose nested list comes out of
  an expanded `ItemTemplate` rather than the plain logical tree. `AssistantHistory_Delete` acts on
  the SELECTED chat and is a different affordance from the row's own `AssistantChat_Delete_<id>`;
  the transcript below holds no ids, so message actions stay on `PiaAnswerToolbar`.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **F4 · `PiaChatStateBadge`.** Settled: display-only, so dropped rather than ided. The whole
  markup is `Border` → `StackPanel` → `ui:SymbolIcon` + `TextBlock`, with no command, no click
  handler, and a code-behind that is the generated constructor alone. No ids, no row — same call as
  `J2` and `E7`. It stays visible to the suite as a nested-view stop in the
  `PiaAssistantChatRowContent` and `FlowView` rows; read its UIA name to assert state.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

## G — Cards & Flow

- [x] **G1 · `CardDecisionBar`.** Confirmed already correct — its `ItemTemplate` binds
  `AutomationProperties.AutomationId="{Binding AutomationId}"` straight from
  `DecisionButton.AutomationId` (`ActionCardInfo.cs`'s `ToolApproval_*` constants). No XAML
  change; added the `[InlineData]` row to lock it in.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **G2 · `ActionCardControl`.** 3 controls beyond `CardDecisionBar`: the details
  expand/collapse chevron and 2 "Manage" `ui:HyperlinkButton`s (a `ButtonBase` subclass) — one
  per mutually-exclusive layout (plain vs. diff card), same affordance, so both get the same id
  formula. `ActionCardInfo` had no per-card identity at all (only `PluginId`, shared across every
  card from that plugin, and a non-unique `ToolName`) — unlike `G1`'s `Decisions`, whose 4 buttons
  are semantically fixed per decision type and only ever interactive on the one card currently
  `Pending`, these two controls stay interactive on every already-resolved card, and several
  resolved cards commonly sit in scrollback at once. Rather than accept the collision or key on
  the content-derived `Title` (not guaranteed unique), added a real `Guid Id` to `ActionCardInfo`
  (UI-only, defaulted, the single construction site in `ActionCardBuilder` needed no change):
  `ActionCard_ToggleDetails_<id>`, `ActionCard_Manage_<id>`.
  *Deps:* G1 · *Effort:* **S** · *Value:* **Med**
- [x] **G3 · `FileDiffCard`.** 247 lines (close to the ~246 estimate). Exactly 1 walker-reachable
  control, the header chevron — keyed on `ActionCardInfo.FilePath` (not the new `Id` from `G2`;
  `FilePath` is already shown in the card header, so it stays human-readable in a script and two
  simultaneous diffs to the same path is a rare, accepted corner case, same class as a non-unique
  tool name): `ActionCard_DiffToggle_<filePath>`. New mechanism finding for the playbook: the
  card's `DiffLine`/`CollapsedDiffRun` `DataTemplate`s are declared inside
  `ItemsControl.Resources` as *implicit* per-type templates, never assigned to
  `ItemsControl.ItemTemplateProperty` — so `ReadLocalValue` on that property returns
  `UnsetValue` and the walker never opens either template. The collapsed-run's own "N unchanged
  lines" button is therefore invisible to the mechanism entirely, distinct from the already-known
  DataTemplate-root-is-a-UserControl sweep hazard.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **G4 · `FlowView`.** 489 lines, 5 distinct controls but 2 resource-keyed `DataTemplate`s
  (`FlowItemCardTemplate`, `FlowHeaderTemplate`) are each applied at TWO separate sites (the real
  rail list plus an arrival-peek/spacer clone reusing the identical template object) — measured
  at 10 walker-visible control *instances*, not 5, confirming the "measure, don't guess" rule
  again. Per-item (`FlowItemCardTemplate`, keyed on `FlowItemViewModel.Item.Id`): `Flow_ActionLink_
  <id>`, `Flow_Dismiss_<id>` — safe as per-item at both sites since the peek clone only ever holds
  one transient item. Header (`FlowHeaderTemplate`, `DataContext` = the one `FlowViewModel`): the
  hidden peek-spacer clone of the header is NOT automation-inert — `Visibility="Hidden"` blocks
  hit-testing but not `InvokePattern`, so a literal id would collide with a genuinely invokable
  duplicate, not just a cosmetic one. Fixed by giving each of the two `ContentControl` hosts a
  `Tag` ("Real"/"Peek") and keying the header's 3 buttons on it via `RelativeSource
  AncestorType=ContentControl`: `Flow_ClearAll_<host>`, `Flow_PinToggle_<host>`,
  `Flow_Collapse_<host>`. Nested views: `CardDecisionBar` (`G1`) and `PiaChatStateBadge`, both
  genuine stops one level below the card template's root, not swept.
  *Deps:* none · *Effort:* **M** · *Value:* **High**

## H — Content dialogs with zero ids beyond the shared `PrimaryButton`/`CloseButton`

`H1`-`H4` all derive from Wpf.Ui's `ContentDialog`, not `UserControl`, with constructors that take a
`ContentDialogHost` (and more) — so `Activator.CreateInstance(viewType)` fails and none of these
can ever get a `[InlineData]` row: ids only, no test lock, no measured floor.

`H5` was missed when this list was built — the original scoping grep only walked files whose root is
`<UserControl`, which is right for the test mechanism but wrong for "which dialogs need ids". A
re-scan of `src/Pia.Wpf/Views/Dialogs/` on 2026-08-27 found exactly one id-less dialog with an
interactive control of its own; the other seven id-less ones (`FolderMove`, `HotkeyCapture`,
`MissedScheduledJob`, `ModelDownload`, `Optimizing`, `Recording`, `Transcribing`) are progress or
message dialogs whose only buttons are `ContentDialog`'s shared `PrimaryButton`/`CloseButton`, which
this group excludes by definition.

- [x] **H1 · `TodoEditContentDialog`.** `TodoEdit_Title`, `_Notes`, `_Priority`, `_DueDate` (the
  `DatePicker`, no test lock possible anyway).
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **H2 · `RecoveryCodeContentDialog`.** `RecoveryCode_Copy`, `_Confirm`.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **H3 · `MeetingSaveContentDialog`.** Feeds "Save to vault" from the meeting overlay —
  pairs naturally with the meeting-attendee work already done this branch. `MeetingSave_Title`,
  `_Attendees`, `_Tags`, `_Project`, `_Notes`.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **H4 · `AssignmentConsentContentDialog`.** 175 lines — the consent flow gating background
  assignments. `AssignmentConsent_Skill`, `_Prompt`, `_Affirm`; per-record (keyed on
  `AssignmentScopeItemViewModel.Item.EntityId`): `AssignmentConsent_Record_<id>`.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **H5 · `VaultHelpContentDialog`.** `VaultHelp_OpenFolder` on its one `ui:HyperlinkButton`; the
  other six elements are `TextBlock`s. Ctor is `(ContentDialogHost, string vaultRoot)`, so no row —
  exactly the `H1`-`H4` shape.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

## I — Wizard, top-level Optimize, and remaining Settings gaps

This group's original heading said "no test-lock mechanism; ids only". That was wrong for four of
its five entries and is corrected here: only `FirstRunWizardWindow` itself is a `Window` with a
parameterized constructor. The seven `WizardSteps/`, `OptimizeView`, `PluginsView` and
`E2EEOnboardingView` are all plain `UserControl`s with a public parameterless constructor
(verified 2026-08-27), so `Activator.CreateInstance` reaches them and each one can hold a real
`[InlineData]` row — same as `I5` already does.

- [x] **I1 · `FirstRunWizardWindow` + all of `WizardSteps/`.** The pack-URI worry was investigated
  first, as this entry demanded, and it does not stand: `FirstRunWizardWindowParseTests` already
  builds the window under the same `WpfStaHost` and asserts its binding paths, so nothing here is
  unparseable today. The window is a `ui:FluentWindow` whose ctor takes three services — ids only:
  `Wizard_TitleBar` (the close button lives in its `ControlTemplate`, reachable only as a
  descendant), `_Skip`, `_Back`, `_Next`. The seven progress dots are bare `Ellipse`es with no
  automation peer, so a script reads the step from its content. Five of the seven steps got ids AND
  a row: `WelcomeStep` 1 / 0, `UserProfileStep` 8 / 0, `ProviderSetupStep` 7 / 0, `AccountSetupStep`
  8 / 0 / `E2EEOnboardingView` (it really does host that control), `E2EESetupStep` 5 / 0.
  `ModesOverviewStep` and `ReadyStep` hold no interactive control at all and stayed bare.
  *Deps:* none · *Effort:* **M** · *Value:* **Low**
- [x] **I2 · Top-level `OptimizeView.xaml`** (the Optimize hotkey window, distinct from
  `SettingsViews/OptimizeView`). 13 walker-visible controls measured, floor set to 12 — the one row
  in this slice with deliberate headroom. Prefix `OptimizeWindow_`, and the composer box is
  `OptimizeWindow_Input`, so the literal `InputTextBox` now resolves to `AssistantView`'s composer
  alone, as this entry required. Row 12 / 0 / `TodoPanelControl`. The language flags carry the one
  binding-form id here (`OptimizeWindow_LanguageItem_<EN|DE|FR>`, a pathless
  `{Binding StringFormat=…}` onto the bare string item) but they are `Image`s, not one of the seven,
  so the per-item floor is honestly 0.
  *Deps:* none · *Effort:* **M** (555 lines) · *Value:* **Med**
- [x] **I3 · `SettingsViews/PluginsView.xaml`.** `Plugins_GoToAccount` on the disconnected-state
  button, plus per-plugin `Plugins_Toggle_<guid>` keyed on `PluginItemViewModel.Id` — a projection
  of `SyncPlugin.Id`, not the display name. Row 2 / 1 / empty.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **I4 · `SettingsViews/E2EEOnboardingView.xaml`.** 8 controls across five mutually exclusive
  stages that all live in one tree, so the affordance that repeats across two stages got
  stage-qualified names rather than one shared id (`_ShowRecoveryEntry` vs
  `_WaitingShowRecoveryEntry`) — the split `DirectTranscriptionOverlay`'s three close buttons
  needed. Row 8 / 0 / empty. `AccountView`'s existing row is unaffected: this control was already a
  nested stop there.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [x] **I5 · `AssignmentsView.xaml`.** Header: `Assignments_Refresh`, `_New` (already had
  `Assignments_Help`). Per-row (keyed on `AssignmentRowViewModel.Id`): `Assignments_OpenChat_<id>`,
  `_Cancel_<id>`. Unlike H1-H4 this one IS a `UserControl` with a parameterless constructor, so it
  got its own `[InlineData]` row (floor 4, 2 per-item, nested `PiaEmptyState,PiaHelpHint`).
  *Deps:* H4 (shares the consent dialog) · *Effort:* **S** · *Value:* **Med**

## J — Small remaining pieces

- [x] **J1 · `CodeBlockControl`.** Ided the copy button (`CodeBlock_Copy`) and the read-only
  `RichTextBox` viewer (`CodeBlock_Content`, `TextBoxBase`-derived so the walker demands an id on
  it too). Literal ids: the control is built procedurally per fence with no per-block identity
  plumbed through, so two code blocks in one reply repeat the same id — same class of caveat the
  playbook already documents for tool-name rows; combine with ordinal indexing if it matters.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [x] **J2 · `PersonaGlyph`, `PiaPersonaAvatar`.** Confirmed neither has a walker-visible control
  (`Path`/`EmojiPresenter`/`Border` only — `EmojiPresenter : Image`). Dropped from the playbook's
  nested-view gap list; no `[InlineData]` row added (would sit at a vacuous floor of 0, same
  reasoning as `NavigationSidebarView`'s exclusion).
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

---

## Suggested order

Cheapest decisive work first, then the highest-traffic vertical slices.

```
G1 → A1 → A2 → C1 → C2 → D1 → D2 → B2 → B3 → J1 → J2   # DONE (slice 2 + slice 3)
E1                                                      # DONE (slice 3) — highest-traffic single item
A3 → C3 → C4 → D3 → F1 → F2                             # DONE (slice 4) — the four per-item row types
B1 → B4                                                 # DONE (slice 5) — Todo view + its embedded panel
E2 → E4 → E7 → E9 → E8                                  # DONE (slice 6) — remaining composer-adjacent controls
G2 → G3 → G4                                            # DONE (slice 7) — Cards & Flow
H1 → H2 → H3 → H4 → I5                                  # DONE (slice 8) — dialogs + Assignments
E3 → E5 → E6                                            # DONE (slice 9) — chat-title chip + the two overlays
A4 · A6 · D4 · D5 · F3 · F4 · H5 · I1 · I2 · I3 · I4    # DONE (slice 10) — everything that was left
```

Every item on this list is now closed. What remains is not planning work — it is the live
confirmation below, which needs a Windows desktop.

## Open: live confirmation on Windows

**Nothing in slice 10 was executed.** It was written on macOS, where `net10.0-windows` compiles
(`dotnet build -p:EnableWindowsTargeting=true`, clean rebuild, `0 Warning(s)` / `0 Error(s)` in both
Debug and Release) but no test can run and no window can open. Two things are therefore outstanding,
in this order.

**1. Run the gate.** `dotnet test` on Windows, no filter, bar is `failed: 0`. The 14 new rows have
never executed. Their assertions fail in characteristic ways, so read the message before touching
the XAML:

- *"only N interactive controls were inspected … below the non-vacuity floor of M"* — a floor is
  one too high. Eleven of the 14 rows use the exact measured count with no headroom (only
  `Pia.Views.OptimizeView` at 12-of-13 has slack), so this is the likeliest failure. The fix is to
  drop that row's floor to N, not to add an id. Most exposed: `ProviderSetupStep` at 7, whose only
  unverified arm is `ui:PasswordBox` deriving from `TextBoxBase` — if that is wrong the row is 6.
- *`Assert.Equal` on the nested-view list* — an exact comparison, so it fails loudly rather than
  degrading. `PiaVaultInspector`'s `"MarkdownMessageControl,PiaInspectorHeader"` depends on
  `ScrollViewer` reporting its `Content` as a logical child, and `PiaAssistantChatInspector`'s
  `"PiaAssistantMessage,PiaPersonaAvatar"` is the one list that comes out of an expanded
  `ItemTemplate` rather than the plain tree.
- *An exception rather than an assertion* — a view that cannot be constructed at all. Five wizard
  steps and `Pia.Views.OptimizeView` have never been instantiated standalone under `WpfStaHost`.
  `IOException("Cannot locate resource")` here means a re-introduced authority-only pack URI, not a
  missing id.

**2. Confirm the ids reach the live UIA tree** with WinWright, per
[`ui-automation-playbook.md`](ui-automation-playbook.md). A green test row only proves an id is
present on a constructed object; it says nothing about whether a script can drive it. Ranked by what
a walkthrough would hit first, and by how hard the state is to reach:

| Surface | What to confirm | State needed |
|---|---|---|
| First-run wizard (`Wizard_*`, `Wizard<Step>_*`) | Drive the whole flow end to end with ids only, no pixel offsets. Cheapest single signal that the ids reached the live tree: `Wizard_Back` is absent on step 0 and present after. `WizardE2EE_Enable` is a `ui:ToggleSwitch` with no InvokePattern — `ww_set_checked`, not a click. | A fresh `PIA_DATA_DIR`. Re-reachable afterwards through `Setup_RunWizard` without wiping again. |
| Optimize hotkey window (`OptimizeWindow_*`) | Input state, then the comparison state after a real optimization. `OptimizeWindow_LanguageItem_<EN\|DE\|FR>` is the one id whose binding cannot be checked without opening the dropdown. `OptimizeWindow_TargetAssistant` lives in a context-menu popup — re-scan, do not search the window subtree. Confirm `InputTextBox` now matches only the Assistant composer. | A configured provider. |
| Vault inspector + status bar (`MemoryNote_*`, `MemoryStatus_*`) | `MemoryNote_Body` / `_Cancel` / `_Save` exist only while editing; in read mode the body must be `MarkdownViewer` instead. | A profile whose vault has at least one note. Do not invoke `MemoryStatus_RegenerateEmbeddings` outside a throwaway profile — it re-embeds. |
| History inspector + status bar (`HistorySession_*`, `HistoryStatus_*`) | Flipping the tab swaps which copy button and which text box is visible; they overlap in one grid cell, so a script that picks the wrong one hits a collapsed element. `HistoryStatus_LoadMore` needs more sessions than one page to be enabled. | A profile with saved optimize sessions. |
| Assistant chat-history inspector (`AssistantHistory_*`) | `automationId*=AssistantHistory_Export` must return exactly three (`ExportAll`, `ExportArchive`, `ExportMarkdown`) — the shape a script must use the full id for. `AssistantHistory_Delete` acts on the selected chat, not the hovered row. | A profile with at least one saved chat. Delete only against a throwaway chat. |
| Settings → Plugins (`Plugins_*`) | Enumerate `automationId*=Plugins_Toggle_`: one hit per plugin, each ending in a distinct guid. That is what proves the per-item binding resolved instead of collapsing to one shared id. | A signed-in cloud account with at least one synced plugin. |
| E2EE onboarding (`E2EEOnboarding_*`) | After `_StartApproval`, the Initial pair must go Offscreen/Collapsed while the Waiting pair appears — the check that justifies the stage-qualified names. | A sync account on an E2EE-enabled server with this device not yet approved. `_ErrorTryAgain` needs a forced activation failure and may only be verifiable by inspection. |
| Vault help dialog (`VaultHelp_OpenFolder`) | That the id survives the `ContentDialogHost` overlay — it has no test row behind it. Do not invoke unattended; it launches Explorer. | Any profile. |
