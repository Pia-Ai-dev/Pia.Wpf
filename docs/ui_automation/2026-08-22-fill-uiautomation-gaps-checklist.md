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

**Excluded, not just deferred** — `NavigationSidebarView`: its 12 `NavItem_*` buttons already all
carry ids (verified by hand), but they hang off `ui:NavigationView.MenuItems`, which
`LogicalTreeHelper` reports zero children for — a test row here would pass at a vacuous floor of
0 and catch nothing. `SettingsView.xaml` (the category shell): its one interactive element is
`Settings_CategoryList`, a `ListBox` — not one of the walker's seven types, so it's outside this
mechanism too, and it already has an id.

---

## A — Vault (`VaultView` is pure composition; every control lives in a nested `Pia*` control)

- [ ] **A1 · `PiaVaultHeader`.** Already has `Memory_Help`; audit for a "new topic"/add affordance.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **A2 · `PiaVaultSearchBar`.** Query box + any clear button.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **A3 · `PiaVaultCategoryCard`.** Per-item card in the left list — expand/collapse, likely a
  per-item id keyed on the category/topic slug.
  *Deps:* none · *Effort:* **S** · *Value:* **High** (the list you'd script against most)
- [ ] **A4 · `PiaVaultInspector`.** The detail pane for a selected memory item.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **A5 · `PiaInspectorHeader`.** Shared inspector header chrome (Vault uses it; check reuse).
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **A6 · `PiaVaultStatusBar`.** Bottom bar — likely just status text; confirm no dead buttons.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

## B — Todo

- [ ] **B1 · `TodoView` itself.** Unlike Vault/History/Reminders this view has ~43 direct
  interactive controls (filters, add box, list) — the biggest single-file item on this list.
  *Deps:* none · *Effort:* **M** · *Value:* **High**
- [ ] **B2 · `PiaTodoHeader`.** Already has `Todo_Help`; audit the rest.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **B3 · `PiaTodoSearchBar`.** Query box + clear.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **B4 · `TodoPanelControl`.** The right-side panel embedded in `AssistantView` — already
  listed as a nested-view gap there; fixing it here closes both.
  *Deps:* none · *Effort:* **S** · *Value:* **High** (shared by two surfaces)

## C — Reminders

- [ ] **C1 · `PiaRemindersHeader`.** Already has `Reminders_Help`; audit the rest.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **C2 · `PiaRemindersFilterBar`.** Filter chips/toggles.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **C3 · `PiaReminderRow`.** Per-item row — complete/snooze/delete, needs a per-item id keyed
  on the reminder's `Id`.
  *Deps:* none · *Effort:* **S** · *Value:* **High**
- [ ] **C4 · `PiaReminderGroupCard`.** Group header (Today/Upcoming/etc.) wrapping the rows.
  *Deps:* C3 · *Effort:* **XS** · *Value:* **Med**

## D — History (`HistoryView`, the Optimize-mode session list)

- [ ] **D1 · `PiaHistoryHeader`.** Already has `History_Help`; audit the rest.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **D2 · `PiaHistorySearchBar`.** Query box + clear.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **D3 · `PiaHistoryGroupCard`** and **`PiaHistorySessionRow`.** Per-session row — the
  equivalent of C3 for history entries.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **D4 · `PiaHistoryInspector`, `PiaHistoryInspectorHeader`, `PiaHistoryInspectorEmptyState`.**
  Detail pane for a selected session.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **D5 · `PiaHistoryStatusBar`.** Bottom bar; confirm no dead buttons.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

## E — Chat composer internals (nested under `AssistantView`, listed as gaps there)

- [ ] **E1 · `PiaAssistantMessage` → `PiaAnswerToolbar`.** The real location of the assistant
  bubble's Copy/Speak/Regenerate/Export/Suggestion/SwitchToAgent/ManageToolPermissions buttons
  (`PiaAssistantMessage.xaml` itself has no direct controls — it delegates here). Highest-traffic
  item in this whole checklist: every assistant reply renders this.
  *Deps:* none · *Effort:* **M** (313 lines, ~7 distinct actions) · *Value:* **High**
- [ ] **E2 · `RunProgressPanel`.** The pinned run-progress panel (agent run state, plan-approval).
  850 lines — audit before estimating further; likely several distinct action buttons.
  *Deps:* none · *Effort:* **M** · *Value:* **High**
- [ ] **E3 · `PiaChatTitleChip`.** 512 lines, chat-title editing chip in the composer header.
  *Deps:* none · *Effort:* **M** · *Value:* **Med**
- [ ] **E4 · `PiaChatQuickSwitcher`.** Chat switcher popup; only 1 interactive-type hit — likely
  cheap once opened.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **E5 · `VoiceModeOverlay`.** Voice-mode full overlay, 0 ids today.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **E6 · `DirectTranscriptionOverlay`.** 546 lines, 0 ids today — the largest overlay on this
  list.
  *Deps:* none · *Effort:* **M** · *Value:* **Med**
- [ ] **E7 · `AutocompletePopup`.** The `@`-command popup in the composer.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **E8 · `PiaSuggestionChips`, `PiaFileChip`, `PiaSourceChip`, `PiaChipOverflowPanel`,
  `PiaAgentModeChip`.** Small chip controls rendered inside assistant messages / the composer.
  *Deps:* E1 (same message surface) · *Effort:* **S** total · *Value:* **Med**
- [ ] **E9 · `PiaReasoningView`.** The collapsible chain-of-thought view (contains an `Expander`).
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**

## F — Assistant History (`AssistantHistoryView` already has 4 ids for import/export/help; the
nested rows do not)

- [ ] **F1 · `PiaAssistantChatRow`, `PiaAssistantChatRowContent`.** Per-chat row in the history
  list — needs a per-item id keyed on chat `Id`.
  *Deps:* none · *Effort:* **S** · *Value:* **High**
- [ ] **F2 · `PiaAssistantChatGroupCard`.** Group header wrapping the rows.
  *Deps:* F1 · *Effort:* **XS** · *Value:* **Med**
- [ ] **F3 · `PiaAssistantChatInspector`.** Already has 1 id; audit the rest (export archive
  button is already covered per the playbook table — confirm nothing else is missing).
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **F4 · `PiaChatStateBadge`.** Small status badge; confirm whether it has any interactive
  part at all (may be display-only, in which case drop it from this list).
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

## G — Cards & Flow

- [ ] **G1 · `CardDecisionBar`.** Already routes per-button ids through `DecisionButton.AutomationId`
  (see `ActionCardInfo.cs`'s `ToolApproval_*` constants) — likely already fully correct. Add the
  `[InlineData]` row to lock it in; only touch XAML if the audit finds a gap.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **G2 · `ActionCardControl`.** The card host rendering `CardDecisionBar` plus any other
  action-card chrome (370 lines) — audit for controls outside the decision bar.
  *Deps:* G1 · *Effort:* **S** · *Value:* **Med**
- [ ] **G3 · `FileDiffCard`.** Inline diff card (246 lines) shown for file-writing tool calls.
  *Deps:* none · *Effort:* **S** · *Value:* **Med**
- [ ] **G4 · `FlowView`.** The Flow notification/action surface (489 lines) — biggest unaudited
  file after `RunProgressPanel`; scope it before estimating further.
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

- [ ] **J1 · `CodeBlockControl`.** The copy-code button on rendered markdown code fences.
  *Deps:* none · *Effort:* **XS** · *Value:* **Med**
- [ ] **J2 · `PersonaGlyph`, `PiaPersonaAvatar`.** Confirm whether either has an actual interactive
  element (they render an avatar/glyph) — if not, drop them from the playbook's nested-view gap
  list instead of chasing ids that were never needed.
  *Deps:* none · *Effort:* **XS** · *Value:* **Enabler**

---

## Suggested order

Cheapest decisive work first, then the highest-traffic vertical slices.

```
G1 → A1 → A2 → C1 → C2 → D1 → D2 → B2 → B3 → J1 → J2   # cheap audits, several already near-done
E1                                                      # highest-traffic single item — every reply renders it
A3 → C3 → D3 → F1                                       # the four per-item row types
B1 → B4                                                 # Todo view + its embedded panel
E2 → E4 → E7 → E9 → E8                                  # remaining composer-adjacent controls
G2 → G3 → G4                                            # Cards & Flow
H1 → H2 → H3 → H4 → I5                                  # dialogs + Assignments (shares H4's dialog)
I3 → I4                                                 # the long-standing Settings gaps
E3 → E5 → E6 → I2 → I1                                  # remaining overlays + the wizard, lowest value/traffic
```
