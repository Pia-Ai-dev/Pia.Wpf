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
`UserControl`); they get ids by hand with no test lock.

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
- [ ] **A4 · `PiaVaultInspector`.** The detail pane for a selected memory item.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **A5 · `PiaInspectorHeader`.** Shared inspector header chrome (Vault uses it; check reuse).
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **A6 · `PiaVaultStatusBar`.** Bottom bar — likely just status text; confirm no dead buttons.
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
- [ ] **D4 · `PiaHistoryInspector`, `PiaHistoryInspectorHeader`, `PiaHistoryInspectorEmptyState`.**
  Detail pane for a selected session.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **D5 · `PiaHistoryStatusBar`.** Bottom bar; confirm no dead buttons.
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
  ManageToolPermissions in `ActionCardControl` (G2) — both still open, not closed by this item.
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
- [ ] **E3 · `PiaChatTitleChip`.** 512 lines, chat-title editing chip in the composer header.
  *Deps:* none · *Effort:* **M** · *Value:* **Med**
- [x] **E4 · `PiaChatQuickSwitcher`.** Measured at exactly 1 control (`QueryBox`, the search input);
  its `ListBox`/`ListBoxItem` match list is not a walker-recognized type, same exclusion reasoning
  as `NavigationSidebarView`. Ided `QuickSwitcher_Query` — its own prefix, not `AssistantChat_`,
  since nothing else needs disambiguating from it.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **E5 · `VoiceModeOverlay`.** Voice-mode full overlay, 0 ids today.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **E6 · `DirectTranscriptionOverlay`.** 546 lines, 0 ids today — the largest overlay on this
  list.
  *Deps:* none · *Effort:* **M** · *Value:* **Med**
- [x] **E7 · `AutocompletePopup`.** Audited, dropped — same shape as `J2`. Its only interactive
  surface is a `ListBox`/`ListBoxItem` match list, neither a walker-recognized type; zero
  walker-visible controls, so a test row would sit at a vacuous floor of 0. No ids added, no row.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- **E8 · `PiaSuggestionChips`, `PiaFileChip`, `PiaSourceChip`, `PiaChipOverflowPanel`,
  `PiaAgentModeChip`.** Small chip controls rendered inside assistant messages / the composer.
  Split three done, two audited-and-skipped — the audit surfaced a real identity question, not
  just mechanics.
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
  - [x] `PiaSuggestionChips`, `PiaAgentModeChip` — **audited, no id added, no test row (same shape
    as `E7`/`J2`).** Each has exactly one control (a `Button`) inside a literal `ItemsControl`
    template, but the bound item is either a raw `string` (`Suggestions`) or a
    `(Goal, Reason)` record with both fields explicitly documented as model-generated content
    (`AgentModeSuggestion`) — there is no non-content field to key a per-item id on. A synthetic
    index (`ItemsControl.AlternationIndex`) was considered and rejected: the test walker's
    `LoadContent()` builds the `DataTemplate` in isolation with no real `ItemsControl` generator
    behind it, so a `RelativeSource AncestorType=ContentPresenter` binding would register as a
    valid per-item `Binding` (test green) while resolving to nothing at that moment — passing a
    test that doesn't check what it claims to. The localization argument that motivates ids
    elsewhere doesn't apply here either: this text is dynamic model output, never a fixed
    localized string, so a script already has to match on `Content`/text, making a content-derived
    id redundant with what's already there.
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
  case. So the fix landed on `PiaAssistantChatRowContent`'s one delete button, ided per-chat
  keyed on `AssistantChatRowViewModel.Id` (`Chat.Id`): `AssistantChat_Delete_<id>`. Given its own
  standalone `[InlineData]` row (floor 1, 1 per-item, nested `PiaChatStateBadge`).
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
- [ ] **F3 · `PiaAssistantChatInspector`.** Already has 1 id; audit the rest (export archive
  button is already covered per the playbook table — confirm nothing else is missing).
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **F4 · `PiaChatStateBadge`.** Small status badge; confirm whether it has any interactive
  part at all (may be display-only, in which case drop it from this list).
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

- [ ] **H1 · `TodoEditContentDialog`.**
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **H2 · `RecoveryCodeContentDialog`.**
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **H3 · `MeetingSaveContentDialog`.** Feeds "Save to vault" from the meeting overlay —
  pairs naturally with the meeting-attendee work already done this branch.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **H4 · `AssignmentConsentContentDialog`.** 175 lines — the consent flow gating background
  assignments.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**

## I — Wizard, top-level Optimize, and remaining Settings gaps (no test-lock mechanism; ids only)

- [ ] **I1 · `FirstRunWizardWindow` + all of `WizardSteps/`.** Root is a `Window` — a prior UI
  round already found it unparseable via pack URI; investigate that before adding ids, not after.
  Runs once per install, lowest traffic on this list.
  *Deps:* none · *Effort:* **M** · *Value:* **Low**
- [ ] **I2 · Top-level `OptimizeView.xaml`** (the Optimize hotkey window, distinct from
  `SettingsViews/OptimizeView`). **Do not reuse the literal id `InputTextBox`** — its composer box
  shares the `x:Name` with `AssistantView`'s, and a script targeting "the chat input" would then
  match two elements across window types.
  *Deps:* none · *Effort:* **M** (555 lines) · *Value:* **Med**
- [ ] **I3 · `SettingsViews/PluginsView.xaml`.** Long-standing known gap.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **I4 · `SettingsViews/E2EEOnboardingView.xaml`.** Long-standing known gap.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **I5 · `AssignmentsView.xaml`.** Has 1 id (`Assignments_Help`); audit the run list / consent
  entry points.
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
H1 → H2 → H3 → H4 → I5                                  # dialogs + Assignments — next up (shares H4's dialog)
I3 → I4                                                 # the long-standing Settings gaps
E3 → E5 → E6 → I2 → I1                                  # remaining overlays + the wizard, lowest value/traffic
```
