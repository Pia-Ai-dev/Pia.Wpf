# Driving Pia's UI with automation (WinWright / UIA)

Guidance for agents running UI walkthroughs or UI regression tests against a running Pia
instance. Read this before starting a run; it replaces guesswork with what is verified to work.
Companion to `2026-08-16-ui-automation-gaps.md` (the findings that motivated the fixes),
`2026-08-16-ui-automation-validation.md` (the live re-run that confirmed them), and
[2026-08-22-fill-uiautomation-gaps-checklist.md](2026-08-22-fill-uiautomation-gaps-checklist.md)
(the file-by-file work list for closing the "Known gaps" below).

## Ground rules

- **Pia is UIA-driven.** Everything below works through UI Automation patterns — no pixel
  offsets, no screenshot arithmetic. If you find yourself computing coordinates, stop and look
  for an AutomationId or pattern instead.
- **Treat `ww_click` success as unverified.** It returns `{"success": true}` even for no-ops.
  Confirm the intended state change happened (snapshot diff, expected element appears).
- **Prefer `ww_invoke` / InvokePattern** over physical clicks; it works regardless of window
  foreground state.
- **`ww_window action=activate` returns `{"success": true}` without foregrounding the window.** If a
  physical click, a key burst or a hover has to land, confirm the window really is foreground —
  `ww_inspect(action="attribute", property="HasKeyboardFocus")` on a control you just clicked is the
  cheap check. Windows refuses `SetForegroundWindow` from a background process, so a `ww_click` can be
  reported as successful while the click went to whatever was actually on top. Do **not** try to fix
  that with `ww_window action=state state=minimize`: Pia minimizes out of the window enumeration, and
  every later call fails with `no_match` until you restore it from outside WinWright.
- **An `x:Name` doubles as the AutomationId when none is set.** Handy — it is why `#PeekItems` and
  `#PART_VerticalScrollBar` resolve — but inside an `ItemTemplate` it means every row answers to the
  same id, and `ViewAutomationIdTests` cannot see it for a `TextBlock` (its sweep only walks
  `ButtonBase`/`ComboBox`/`TextBoxBase`/`PasswordBox`/`Slider`/`Expander`/`TabItem`). If a selector
  returns one hit per row, that is the reason. `tests/Pia.Wpf.Tests/Views/FlowRailCardAutomationTests.cs`
  walks the real peer tree to hold that line for the Flow rail.

## Stable AutomationIds

| Element | AutomationId |
|---|---|
| Sidebar items | `NavItem_Assistant`, `NavItem_AssistantHistory`, `NavItem_Memory`, `NavItem_Reminders`, `NavItem_Routines`, `NavItem_Todo`, `NavItem_Settings`, `NavItem_NewWindow`, `NavItem_ThemeToggle` |
| Sidebar items, conditional | `NavItem_Optimize` / `NavItem_History` (Optimize-mode windows only), `NavItem_Assignments` (only when the server offers the surface) |
| Chat input / scroller | `InputTextBox`, `MessageScrollViewer`, `MarkdownViewer` (assistant bubble text, via `PiaAssistantMessage` → `MarkdownMessageControl`) |
| Chat composer toolbar | per-group `Assistant_Suggestion_<Group>` (empty-state chips; 3 of `Memory`, `Recall`, `Todo`, `Reminder`, `Routine`, `Plan`, `Chats` are drawn per visit, so match on the prefix rather than one id), `Assistant_ClearConversation`, `Assistant_CancelStreaming`, `Assistant_ToggleRecording`, `Assistant_AttachFile`, `Assistant_RemoveAttachment`, `Assistant_ToggleMeetingAttendee`, `Assistant_ToggleDirectTranscription`, `Assistant_RunAssignment`, `Assistant_PersonaPicker`, `Assistant_Mode_Chat` / `_Agent`, `Assistant_RunInBackground`, `Assistant_Send`, `Assistant_WeakProvider_Continue` / `_ChooseProvider` / `_StayInChat`, per-message `Assistant_CopyMessage_<guid>` (user bubble, keyed by message `Id`) |
| Tool-approval decisions | `ToolApproval_Decline`, `ToolApproval_AllowOnce`, `ToolApproval_AllowSession`, `ToolApproval_AlwaysAllow` |
| Personas / Templates grids | `Personas_AddButton`, `Templates_AddButton`, per-item `Persona_Edit_<guid>` / `Persona_Delete_<guid>` / `Persona_Duplicate_<guid>` / `Template_Edit_<guid>` / `Template_Delete_<guid>` / `Template_ViewPrompt_<guid>` / `Template_SetDefault_<guid>` |
| Settings categories | `Settings_CategoryList`, `SettingsCategory_General` / `_Providers` / `_Optimize` / `_Assistant` / `_Account` / `_Plugins` |
| Settings inner tabs | `Settings_General_Tab_Application` / `_Hotkeys` / `_Speech` / `_Privacy`, `Settings_Assistant_Tab_General` / `_Personas` / `_ToolPermissions` / `_Meeting` / `_Agent` |
| Settings → General | Application: `Settings_General_UiLanguage`, `_LaunchAtStartup`, `_StartMinimized`, `_AutoCaptureSelectedText`, `_ExportDiagnostics`, `_ResetAppData`. Hotkeys: `_CaptureOptimizeHotkey` / `_ClearOptimizeHotkey`, `_CaptureFastPathHotkey` / `_ClearFastPathHotkey`, `_CaptureAssistantHotkey` / `_ClearAssistantHotkey`. Speech: `_SttEngine`, `_WhisperModel`, `_DownloadWhisperModel`, `_DownloadParakeetModel`, `_SttLanguage`, per-voice `_DownloadVoice_<voiceKey>` / `_SelectVoice_<voiceKey>`. Privacy: `_TokenizationEnabled`, `_NewKeywordInput`, `_NewKeywordCategory`, `_AddPiiKeyword`, per-row `_KeywordCategory_<keyword>` / `_RemoveKeyword_<keyword>` |
| Settings → Assistant | General: `Settings_Assistant_GoToProvidersTab`, `_DefaultWindowMode`, `_SuggestionsEnabled`, `_FilesFolder`, `_ChangeFilesFolder`, `_FileToolsEnabled`, `_GitToolsEnabled`, `_DefaultWorkingDirectory`, `_ChatHistoryEnabled`, `_ChatHistoryToolsEnabled`, `_ChatHistoryRetentionDays`, `_ChatAutoTitleEnabled`, `_DeleteAllChatHistory` — `Settings_Assistant_ChatHistory` is an ambiguous prefix across those three, so match the full id. Tool access: `_ToolPermissions_AutoApproveBuiltInWrites`, `_ToolCatalog`, per-tool `_ForgetSession_<toolName>` / `_Revoke_<toolName>` / `_AllowedForSession_<toolName>` / `_AllowedAlways_<toolName>`. Meeting: `_EnableMeetingDiarization`, `_MeetingSmartSpeakerDetection`, `_MeetingSuppressSpeakerLabels`, `_SpeakerEmbeddingThreshold`, `_MeetingMaxSpeakers`, `_MeetingMinSpeechSeconds`, `_MeetingBrowser`, `_MeetingAttendeeShowBrowserWindow`. Agent runs: `_AgentMaxSteps`, `_MaxToolRoundsPerStep`, `_AgentWallClockMinutes`, `_AgentMaxReplans`, `_AgentPlanReasoningTurnEnabled`, `_Agent_AutoApproveBuiltInWrites`, per-persona `_AgentRoster_<guid>`, `_ScheduledMaxSteps`, `_ScheduledWallClockMinutes`, `_ScheduledMaxReplans`, `_MaxParallelBackgroundRuns` |
| Settings → Providers | `Settings_Providers_UseSameProviderForAllModes`, `_OptimizeProvider`, `_AssistantProvider`, `_AddProvider`, `_GoToCloudSync`, per-row `Provider_Test_<guid>` / `Provider_Edit_<guid>` / `Provider_Delete_<guid>` |
| Settings → Account | `Settings_Account_ServerUrl`, `_TrustSelfSignedCertificates`, `_LoginEmail`, `_LoginPassword`, `_LoginWithPassword`, `_OpenRegistrationPage`, `_OpenForgotPassword`, `_LoginWithGoogle`, `_LoginWithMicrosoft`, `_LoginWithEntraId`, `_SyncNow`, `_SyncLogout`, `_IsE2EEEnabled`, `_CheckForPendingDevices` |
| Settings → Optimize | `Settings_Optimize_GoToProvidersTab`, `_OutputAction`, `_AutoTypeDelayMs`; the template list uses the `Templates_*` / `Template_*` ids above |
| Routines list / actions | `Routines_JobList`, `Routines_Row_<id>` (the row container, whose UIA name is the routine's name), `Routines_NewJob`, `Routines_Edit`, `Routines_Toggle`, `Routines_RunNow`, `Routines_Delete`, `Routines_StatusMessage`, `Routines_Detail_NextRun`, `Routines_RunHistory` |
| Routines editor | `Routines_Field_Name`, `_Goal`, `_Kind`, `_Recurrence`, `_DayOfWeek`, `_Month`, `_DayOfMonth`, `_Time`, `_Date`, `_Provider`, `_Persona`, `_Effort`, `_Quiet`, plus `Routines_Save` / `Routines_Cancel`. The detail pane's `Routines_Detail_Tools` reads back what the selected routine may use |
| Routines tool picker | `Routines_Tools_Picker` is an expander, collapsed by default, so expand it before reaching anything inside. `Routines_Tools_Summary` reads back what is ticked, or names the launcher default when nothing is. Per tool, `ww_set_checked` on `Routines_Tools_Allow_<toolName>`, whose UIA name is the tool name. Two plugins can expose one tool name, so a prefix match on `Routines_Tools_Allow_` may return more than one box for a single grant — ticking either ticks both. Nothing ticked means `{write_file}` for an agent routine and `{}` for a research one |
| Routines blueprint catalog | `Routines_NewJob` opens it (as does an empty routine list, by itself) and `Routines_BrowseCatalog` reopens it from the placeholder pane: `Routines_SearchQuery`, `Routines_StartBlank`, `Routines_CatalogClose`, `Routines_BlueprintList`, per-group `Routines_Category_<key>` (`ready` / `your-data`, the second collapsed until searched), per-card `Routines_Blueprint_<key>` (UIA name is the card title) |
| Persona dialog | `PersonaEdit_Name`, `PersonaEdit_SystemPrompt`, `PersonaEdit_Archetype`, `PersonaEdit_ModelType`, `PersonaEdit_ToolScope`, `PersonaEdit_PreferredProvider`, `PersonaEdit_ReasoningEffort` |
| Template dialog | `TemplateEdit_Name`, `TemplateEdit_StyleDescription`, `TemplateEdit_GeneratedPrompt` |
| Provider dialog | `ProviderEdit_Name`, `ProviderEdit_ProviderType`, `ProviderEdit_Endpoint`, `ProviderEdit_ApiKey`, `ProviderEdit_ModelName`, `ProviderEdit_FetchModels`. The key box is always blank on an edit — the saved key is never loaded into it — and Fetch Models falls back to the stored key, so leave it empty unless you are testing a rotation. |
| Meeting attendee overlay | `MeetingAttendee_Url`, `_DisplayName`, `_Consent`, `_Join`, `_Stop`, `_Save`, `_SpeakerDisclaimer`, `_Close`, `_OpenSettings`, `_SaveToVault`, `_Summarize`, per-bubble `_RenameSpeaker_<speakerLabel>` (shared across every bubble from the same speaker — renaming applies to the label, not one utterance). Open the overlay with the composer button named "Join a meeting and transcribe". Bubble labels carry no id — read them as `Text` elements, which is what `Invoke-MeetingReplay.ps1` does to check the numbering. |
| Edit dialogs (shared) | `PrimaryButton` (Save), `CloseButton` (Cancel), `Dialog_RequiredHint`. **Wpf.Ui’s own `SimpleContentDialog`, the one `ShowConfirmationDialogAsync` raises, carries the same two ids** — verified on the export-diagnostics confirmation, where they are named `Yes`/`No`. |
| Chat history import/export | `AssistantHistory_Import`, `AssistantHistory_ExportAll`, `AssistantHistory_LoadMore` (header/list), `AssistantHistory_ExportArchive` (inspector, needs a selected chat), `AssistantHistory_ImportStatus` / `AssistantHistory_ImportProgress` (status bar, present only while an import runs) |
| Chat title chip + history flyout (`PiaChatTitleChip`) | The picker at the top-left of the Assistant view. `ChatChip_Toggle` opens it; inside, `ChatChip_Search`, `ChatChip_NewChat`, `ChatChip_ShowAllChats`, and per-chat `ChatChip_Resume_<chatId>` (keyed on `ChatChipItemViewModel.Id`). Each row's hover trash is `AssistantChat_Delete_<chatId>` — the row body is the shared `PiaAssistantChatRowContent`, and its `AssistantChat_Open_<id>` sibling is **collapsed here** because the chip passes only `DeleteCommand`, so resume through `ChatChip_Resume_<chatId>`. The flyout is `StaysOpen="False"`: query it in the call right after the invoke, with no `ww_window activate` in between. |
| Working-directory picker (inside that flyout) | `ChatChip_WorkingDir` opens the drill-down; per-crumb `ChatChip_Crumb_<index>` keyed on `WorkingDirectoryCrumb.Index` (0 = root). `ChatChip_NewFolder` reveals the inline row: `ChatChip_NewFolderName`, `ChatChip_NewFolderConfirm`, `ChatChip_NewFolderCancel`. The child-folder list is `ChatChip_FolderEntries`; its rows are bare folder-name strings with no non-content field to key on, so scope a name match inside that list rather than expecting per-row ids. |
| Voice mode overlay (`VoiceModeOverlay`) | `VoiceMode_Done` (Listening only), `VoiceMode_Stop` (Speaking only), `VoiceMode_End` (always). Entering the overlay needs a loaded TTS voice (`CanEnterVoiceMode`), so a throwaway profile cannot reach it — these are test-locked, not walkthrough-confirmed. |
| Direct transcription overlay (`DirectTranscriptionOverlay`) | Header: `DirectTrans_ToggleStats` (hidden while the disclaimer shows), `DirectTrans_Close`. Disclaimer: `DirectTrans_DisclaimerAccept` (a `ui:ToggleSwitch` — `ww_set_checked`, it has no InvokePattern), `DirectTrans_DisclaimerClose`, `DirectTrans_Start`. Footer, after Start: `DirectTrans_FooterClose`, `_Stop` (running), `_Resume` / `_Save` / `_SaveToVault` / `_Summarize` (stopped). `DirectTrans_Save` is a prefix of `_SaveToVault`, so match the full id — same caveat as `Settings_Assistant_ChatHistory`. Per-bubble `DirectTrans_RenameSpeaker_<speakerLabel>`, keyed on the raw diarizer label, not the renamed display name. |
| Direct transcription consent chip | The chip is a `Border` with a `ContextMenu` — no `AutomationId`, no InvokePattern. Right-click it, then `DirectTrans_ChipRename_<speakerLabel>` / `DirectTrans_ChipRevoke_<speakerLabel>`. `MenuItem`s, so no test lock. |
| Follow-up chips (`PiaSuggestionChips` / `PiaAgentModeChip`) | `Suggestion_Chip_<n>` and `AgentMode_Chip_<n>`, keyed on the container's `ItemsControl.AlternationIndex` — arrival order within one reply, not globally unique, and it wraps past `AlternationCount` (20). Both bound items are model-generated content (a raw suggestion string; `AgentModeSuggestion.Goal`/`Reason`), so there is no stable field to key on. Distinctness is locked by `FollowUpChipAutomationIdTests`, which renders both lists through a real layout pass — the sweep alone would go green on a binding that resolves to nothing. `automationId*=Suggestion_` also matches the empty-state `Assistant_Suggestion_<Group>` chips, but never at the same time: that panel is `Collapsed` (out of the UIA tree, not merely hidden) once the chat has messages, and a follow-up chip only exists on a reply. |
| Reply PII context menu (`MarkdownMessageControl`) | Right-click an assistant reply: `PiiMenu_Open`, then `PiiMenu_Person` / `_Nickname` / `_Email` / `_Phone` / `_Address` / `_Date` / `_Custom`. `MenuItem`s, so no test lock — same caveat as the reply toolbar's regenerate menu. |
| Main-window chrome (`MainWindow`) | Setup-required overlay (covers any feature view until a provider is configured, so a fresh-profile script hits it first): `Setup_OpenSettings`, `Setup_RunWizard`. Update bar: `Update_RestartNow`, `Update_Dismiss`. E2EE bar: `E2EE_OpenOnboarding`. `MainWindow`'s constructor takes DI parameters, so `Activator.CreateInstance` fails and it can never get an `[InlineData]` row — same class as the content dialogs below. |
| Assistant chat history row / group card | Per-chat **open** and delete buttons, both on `PiaAssistantChatRowContent` and both revealed by hovering the row (keyed on `AssistantChatRowViewModel.Id`, i.e. `Chat.Id`): `AssistantChat_Open_<id>`, `AssistantChat_Delete_<id>`. The row CONTAINER carries `AssistantChat_Row_<id>` and reports the chat title as its UIA name. `AssistantChat_Open_<id>` resumes the named chat in one invoke - selecting the row does not open it, that needs the inspector's Resume button. Group header expand/collapse, per-bucket: `AssistantHistory_GroupToggle_<bucket>`, keyed on `AssistantChatGroupViewModel.Bucket` (its nullable `GroupKey` string was skipped in favor of this non-null enum, same identity the group is actually built from). |
| Page-header help hints | `Routines_Help`, `Assignments_Help`, `History_Help`, `AssistantHistory_Help`, `Memory_Help`, `Todo_Help`, `Reminders_Help` |
| Settings help hints | `Settings_ToolPermissions_Page_Help` (tab intro), `Settings_ToolPermissions_Session_Help` (session tier), `Settings_ToolPermissions_Help` (always-allowed list), `Settings_MeetingBrowser_Help`, `Settings_Agent_Roster_Help`, `Settings_Scheduled_Help` |
| Vault (Memory) header / search | `Memory_Back`, `_Home`, `_Refresh`, `_OpenFolder`, `_ShowHelp` (distinct from the `Memory_Help` hover hint above), `Memory_SearchQuery` |
| Vault category card (`PiaVaultCategoryCard`) | Expand/collapse toggle, per-category: `Memory_CategoryToggle_<type>`, keyed on `MemoryGroupViewModel.Type` (the category/topic slug). `PiaVaultRow` (the per-item row inside) has no walker-recognized controls of its own — nothing else to id there. |
| Reminders header / filters | `Reminders_Refresh`, `_DismissAll`, `_DisableAll`, `_DeleteAll`; filter bar (static `RadioButton`s, not per-item): `Reminders_Filter_All` / `_Active` / `_Snoozed` / `_Disabled` / `_Completed` |
| Reminder row / group card | Per-reminder (keyed on `Reminder.Id`): `Reminders_ToggleEnable_<id>`, `_Snooze_<id>`, `_Dismiss_<id>`, `_Delete_<id>`. Group header expand/collapse, per-bucket: `Reminders_GroupToggle_<bucketKind>`, keyed on `ReminderGroupViewModel.BucketKind`. |
| History (Optimize sessions) header / search | `History_Refresh`, `_DeleteAll`, `_SearchQuery`, `_TemplateFilter`, `_ClearFilters`; `_StartDate` / `_EndDate` on the two `DatePicker`s carry ids too but have no test lock (see Known gaps) |
| History group card (`PiaHistoryGroupCard`) | Expand/collapse toggle, per-bucket: `History_GroupToggle_<bucket>`, keyed on `SessionGroupViewModel.Bucket`. `PiaHistorySessionRow` (the per-item row inside) has no walker-recognized controls of its own — nothing else to id there. |
| Todo header / search | `Todo_AddColumn`, `_Refresh`, `_SearchQuery` |
| Todo kanban board (`TodoView`) | Add-todo bar: `Todo_NewTitle`, `_NewPriority`, `_NewDueDate` (no test lock, same `DatePicker` caveat as History), `_Record`, `_AddTodo`. Per-column (keyed on `KanbanColumnViewModel.Id`): `Todo_ColumnMenu_<id>` (the "..." button) plus its 3 context-menu items `Todo_ColumnMenu_SetDefault_<id>` / `_Rename_<id>` / `_Delete_<id>` (no test lock, same `MenuItem` caveat as `PiaAnswerToolbar`), and `Todo_ExpandColumn_<id>` (the closed-column chevron). Per-todo (keyed on `TodoItem.Id`): `Todo_Complete_<id>`, `Todo_Edit_<id>`, `Todo_Delete_<id>`. |
| Todo panel (`TodoPanelControl`, embedded in `AssistantView`) | `TodoPanel_Close`, `_NewTitle`, `_Record`, `_Add`, `_OpenFullView`; per-todo (keyed on `TodoItem.Id`): `TodoPanel_Complete_<id>`. Prefixed `TodoPanel_`, not `Todo_`, so a script targeting one surface's fields never prefix-matches the other's. |
| Assistant reply toolbar (`PiaAnswerToolbar`) | Per-reply, keyed by `AssistantMessage.Id`: `Answer_Copy_<id>`, `_Speak_<id>`, `_Regenerate_<id>`, `_RegenerateOptions_<id>`, `_Export_<id>`, `_RateUp_<id>`, `_RateDown_<id>`. Deliberately not prefixed `Assistant_` — that already means the user-bubble copy button (`Assistant_CopyMessage_<guid>`). The regenerate-style context menu's 3 items are literal and deliberately a *different* prefix, `Answer_RegenerateStyle_Shorten` / `_Detailed` / `_Exportable` — reusing `Answer_RegenerateOptions_` here would make `automationId*=Answer_RegenerateOptions_` match the chevron button plus all three menu items once opened, the same collision the `Assistant_`/`Answer_` split above exists to avoid. No test lock on the menu items. |
| Markdown code block (`CodeBlockControl`) | `CodeBlock_Copy`, `CodeBlock_Content` — literal; a message with two+ code fences repeats these ids (see Known gaps) |
| Reasoning trace toggle (`PiaReasoningView`) | Per-reply, keyed by `AssistantMessage.Id`: `Reasoning_Toggle_<id>`. Own prefix, not `Answer_` — a different affordance from the reply toolbar it sits beside. No `Expander` involved despite appearances; the collapse is hand-rolled. |
| Chat quick switcher (`PiaChatQuickSwitcher`) | `QuickSwitcher_Query` (the search box). Its match list is a `ListBox`, not a walker-recognized type — no id needed or possible on individual matches. |
| Run progress panel (`RunProgressPanel`) | Root-level: `Run_Pause`, `_Continue`, `_DenyTool`, `_RejectPlan`, `_Publish`, `_CardToggle`, `_NudgeText`, `_ShowEarlierSteps`, `_ShowLaterSteps`, `_TimelineToggle`, `_ChildrenToggle`. Per-step, keyed on `StepRowViewModel.StepId`: `Run_StepEdit_<id>`, `_StepInsertBelow_<id>`, `_StepMoveUp_<id>`, `_StepMoveDown_<id>`, `_StepSkip_<id>`, `_StepEditTitle_<id>`, `_StepEditIntent_<id>`, `_StepEditCancel_<id>`, `_StepEditSave_<id>`. Per-child-run, keyed on `ChildRunRowViewModel.RunId`: `Run_ChildToggle_<id>`. |
| File chip (`PiaFileChip`) | Keyed on the chip's own `FileName` (not `AbsolutePath` — that would put a full local filesystem path into a permanent, enumerable UIA property): `FileChip_Open_<fileName>`, `_OpenVsCode_<fileName>`, `_Reveal_<fileName>`. Two attachments sharing a filename collide — same caveat class as a non-unique tool name. |
| Source chip (`PiaSourceChip`) | `SourceChip_Open_<n>`, keyed on `SourceRef.Ordinal` — arrival order within the message, not globally unique. Deliberately not the vault/chat `Target`: a user-named page would land in a permanent, enumerable UIA property (same rule as the file chip above). For a web-only answer it still equals the visible citation number; a vault or chat chip shifts it. |
| Chip overflow "+N" button (`PiaChipOverflowPanel`) | `ChipOverflow_More_<groupName>`. `PiaAssistantMessage` renders two instances per message (Sources, FileRefs) that can both be visible at once, so the control now exposes a `GroupName` DP the call site sets (`"Sources"` / `"Files"`) rather than leaving it a literal id that would collide. |
| Action card chrome (`ActionCardControl`) | Keyed on `ActionCardInfo.Id` (a `Guid` added for this — the model had no per-card identity before): `ActionCard_ToggleDetails_<id>`, `ActionCard_Manage_<id>` (same id on both mutually-exclusive "Manage" layouts). `CardDecisionBar`'s own 4 buttons (`ToolApproval_*`, table above) are unchanged — they stay semantic-per-decision-type, not per-card. **Lifetime caveat:** `Id` defaults to `Guid.NewGuid()` at `ActionCardBuilder.Build` time — it is NOT a persisted domain id like `TodoItem.Id`/`AssistantMessage.Id`. Action cards are never serialized to chat history and `ActionCardBuilder.Build` runs exactly once per real tool call, so the id is stable for the in-memory lifetime of that card (including a DataContext re-host, since the same `AssistantMessage`/`ActionCardInfo` object is reused, not rebuilt) — but discover it at runtime, never hardcode one in a script. |
| File diff card (`FileDiffCard`) | `ActionCard_DiffToggle_<filePath>`, keyed on `ActionCardInfo.FilePath` rather than the `Id` above — already shown in the card header, so it stays human-readable, and a same-path collision is a rare accepted corner case. |
| Flow notification rail (`FlowView`) | Per-item, keyed on `FlowItemViewModel.Item.Id`: `Flow_Card_<id>` (the `DataItem` container, set through the rail list's `ItemContainerStyle`; its UIA name is the card title), `Flow_Title_<id>`, `Flow_Body_<id>`, `Flow_ActionLink_<id>`, `Flow_Dismiss_<id>`, `Flow_Decisions_<id>` (same formula covers both the real rail and the transient single-item arrival-peek clone, which reuses the identical template — except `Flow_Card_<id>`, which only the real rail sets, so the two can never collide during a peek). Header (`FlowHeaderTemplate`, `DataContext` is the one `FlowViewModel`): `Flow_ClearAll_<host>`, `Flow_PinToggle_<host>`, `Flow_Collapse_<host>`, keyed on a `Tag` ("Real"/"Peek") set on each of the two `ContentControl` hosts and read back via `RelativeSource AncestorType=ContentControl` — needed because the peek clone's `Visibility="Hidden"` does NOT remove it from the UIA tree or block `InvokePattern` (only the hit-test path), so a literal id here would have been a genuine, invokable ambiguity, not a cosmetic one. |
| Todo edit dialog (`TodoEditContentDialog`) | `TodoEdit_Title`, `_Notes`, `_Priority`, `_DueDate`. No test lock — see Known gaps. |
| Recovery code dialog (`RecoveryCodeContentDialog`) | `RecoveryCode_Copy`, `_Confirm`. No test lock. |
| Meeting save dialog (`MeetingSaveContentDialog`) | `MeetingSave_Title`, `_Attendees`, `_Tags`, `_Project`, `_Notes`. No test lock. |
| Assignment consent dialog (`AssignmentConsentContentDialog`) | `AssignmentConsent_Skill`, `_Prompt`, `_Affirm`; per-record (keyed on `AssignmentScopeItemViewModel.Item.EntityId`): `AssignmentConsent_Record_<id>`. No test lock. |
| Opt-out confirm dialog (`OptOutConfirmContentDialog`) | `OptOutConfirm_DontAskAgain`. Generic over the caller's strings, so the title is the only thing that names which confirm it is; raised by "Run in background". |
| Assignments list (`AssignmentsView`) | `Assignments_Refresh`, `_New` (plus the existing `Assignments_Help`); per-row, keyed on `AssignmentRowViewModel.Id`: `Assignments_OpenChat_<id>`, `_Cancel_<id>`. |

Import and Export open a native file picker, which is not reliably scriptable: `ww_dialog handle_file`
returns `{"success": true}` without confirming the dialog, and re-invoking the button just stacks up
more pickers. In a DEBUG build set `PIA_DEBUG_CHAT_IMPORT_FILE` / `PIA_DEBUG_CHAT_EXPORT_FILE` to a
path and the picker is skipped entirely, so one `ww_invoke` runs the whole import/export.

Built-in personas expose only `Persona_Duplicate_<guid>`; edit/delete exist for user personas
only. Use `automationId*=Persona_Edit_` to enumerate the editable ones.

Per-item ids interpolate the row's identity. Two families end in a guid you have to discover at
runtime — provider rows use `AiProvider.Id`, roster rows the persona guid — so enumerate with a
prefix match (`automationId*=Provider_Edit_`, verified to return one hit per row) and read the id
back instead of hardcoding one. The
rest are stable strings you can write literally: the voice key (`en_US-lessac-medium`), the tool
name, and for the PII rows the keyword you just typed. Tool name is *not* unique, so two plugins
exposing the same tool produce the same id twice.

The help hints (`PiaHelpHint`) render their text only in a hover tooltip, which is a separate
popup HWND — a window-scoped `ww_screenshot` will not contain it, and `ww_list_windows` does not
enumerate it. Read the text with `ww_inspect(action="attribute", property="HelpText")`, which
returns it without hovering at all. If you do need the rendered tooltip in an image, the window
must be foreground (`ww_window activate`) and the mouse must *enter* the element, so hover
somewhere else first; capture the screen region rather than the window.

Some settings ids are absent from the tree, not merely hidden, until the state that renders them:
`Settings_General_WhisperModel` / `_DownloadWhisperModel` need Whisper selected in
`Settings_General_SttEngine`, `_DownloadParakeetModel` needs Parakeet, each voice row exposes
`_DownloadVoice_` or `_SelectVoice_` but never both, and the tool-catalog checkboxes only exist
once `Settings_Assistant_ToolCatalog` (a `CardExpander`) is expanded. Note also that
`Settings_Assistant_ToolPermissions_AutoApproveBuiltInWrites` and
`Settings_Assistant_Agent_AutoApproveBuiltInWrites` are one property rendered on two tabs; either
one moves the setting, and both share an accessible name.

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
3. Select the inner tab. `ww_select(selector="type=TabControl", optionText="Personas")` works but
   matches a localized header;
   `ww_select(selector="type=TabControl", optionSelector="automationId=Settings_General_Tab_Speech")`
   is language-independent and works live — **but a recording of it cannot be replayed.** The
   recorder folds `optionText` and `optionSelector` into one `extra` field, and on replay that
   resolves 0 elements and errors, deterministically. So: `optionSelector` when you are driving
   interactively, `optionText` in anything destined for `tests/ui-scripts/` (whose fixture pins
   `uiLanguage: 0` for exactly this reason). **The tab's content appears as children of the
   selected TabItem**, not as siblings of the tab headers, so scope name-based searches as
   `type=TabItem[name='Personas'] >> ...`; an `automationId=` is unique within the view and
   resolves on its own once the tab is selected.

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
- **A `ContentDialog` surfaces as a nested `Window` peer named by its title**, so
  `ww_dump_tree(selector="type=Window")` reads the whole thing — title, body and both buttons — in
  one call, and `ww_count("type=Window")` is a reliable is-it-up check: it resolves **0** when no
  dialog is open, because the selector is scoped to the main window and does not match it.
- If a save/submit ever appears to do nothing, call `ww_dialog(action=handle)` **before**
  concluding anything — native `MessageBox` dialogs are invisible to window-scoped screenshots
  and `ww_list_windows`. Better: `ww_dialog(action=expect)` *before* the click to pre-register
  a handler and avoid the race. (Startup-failure is the one remaining native box, by design.)

## Recording a walkthrough (`ww_record`)

Committed recordings, the settings fixture they start from and the replay harness live in
`tests/ui-scripts/` (read its README before adding one). Full evaluation of the feature in
`2026-08-18-winwright-recording-eval.md`. The short version:

- The recorder captures **your tool calls**, so the script is only as good as the selectors you
  typed. Pass `record: false` on discovery calls; read-only tools are never recorded.
- Wrap scenarios in `test_start` / `test_end`, and use **`ww_assert_value`** for checks —
  `ww_assert` is not recorded. On a `CheckBox`, `property=value` with `On`/`Off` works
  (TogglePattern).
- **Never call `stop` to peek**: it ends the session, `pop` then refuses, and `start` clears the
  buffer. `pop`'s `remaining` count is the only mid-run read.
- Replay is **not** in the MCP surface — it is a CLI verb on the same binary:
  `%LOCALAPPDATA%\WinWright\Civyk.WinWright.Mcp.exe run <script.json> --format junit`. It launches
  the app from the script's `launchPath`, evaluates the embedded assertions and exits 0/1.
- A script that changes persisted app state **only passes once**: give it a seeded
  `settings.json` (recording captures actions, never preconditions). Flipping *Start minimized*
  makes the next run fail with "No main window found".
- `ww_heal_script` / `winwright heal` only validate the steps whose targets are on screen *right
  now*, and they ignore elements that have no AutomationId — so they do cover the five settings
  views, but not Plugins or the E2EE onboarding screen.

## Known gaps (don't burn time rediscovering these)

- **`ChatChip_Toggle` went out of phase with the flyout — FIXED 2026-08-27.** The flyout is still
  `StaysOpen="False"` and still closes on its own, but `ChipButton_Click` now decides from
  `FlyoutPopup.IsOpen` instead of the view-model flag, and both popups' `Closed` handlers push that flag
  back down, so one invoke always toggles. Same fix on `ChatChip_WorkingDir` / `IsPickerOpen`. Verified
  live: flyout open → another window activated → one `ww_invoke` reopens it. Drop the invoke-twice
  recipe. Still true, and not a bug: **`ww_screenshot` closes the flyout when Pia is not the foreground
  window** (UIA read calls do not) — capture the image last, or foreground Pia first. Also unchanged and
  separate: a physical click on the chip while the flyout is open does **not** close it. The popup
  dismisses itself on the mouse-DOWN and the button's Click arrives on the mouse-UP, a dispatcher turn
  later, so the button cannot tell that click apart from a fresh one. Close it with `Escape` or an
  outside click.
- **The working-directory picker opens inside the chat's CURRENT working directory, not at the root**, so
  `ChatChip_FolderEntries` is legitimately empty for a leaf folder — that is not a UIA gap, and its rows
  expose their names normally once the folder has children. Send `Backspace` to ascend, then arrow +
  `Enter`. **Those no longer have to be separate `ww_keyboard` calls — FIXED 2026-08-27**: the picker's
  `FocusFirstEntry` runs synchronously from the key handler now (its `UpdateLayout` is what makes that
  safe), so `["Backspace","Down","Down","Enter"]` in ONE call works. Measured both ways from
  `\Alpha\Beta`, same profile and same call: **before** it landed on `\Alpha\Delta` — the posted focus
  move swallowed the FIRST arrow, so one `Down` of two survived, which is why the old symptom read as
  "the pill keeps the old folder" rather than an error. **After**, `\Alpha\Epsilon`. Only
  `WorkingDirPopup_Opened` still posts, because the popup's content is mid-connection when it fires.
  Read the chosen folder back off `ChatChip_WorkingDir`'s own `Text` child.
- **The Flow rail's cards had no per-card identity — FIXED 2026-08-27.** Every card's `DataItem`
  container now carries `Flow_Card_<id>` and reports its title as its UIA name (via the list's
  `ItemContainerStyle`), and title/body/decisions are per-item ids — `Flow_Title_<id>`, `Flow_Body_<id>`,
  `Flow_Decisions_<id>` — beside the `Flow_ActionLink_<id>` / `Flow_Dismiss_<id>` that were already there.
  Before this, title and body were addressable only through their shared `x:Name`, so `#TitleText`
  resolved to **one hit per card** and silently returned the first; the DataItem answered to no id at all
  and reported `Pia.ViewModels.Flow.FlowItemViewModel` as its name. That, not a realization failure, is
  what made "the card for run X" unaddressable. Scope inside a card with
  `automationId=Flow_Card_<id> >> automationId=Flow_ActionLink_<id>`.
  **The reported flakiness ("no children at all", varying between calls) did not reproduce.** With 4 and
  8 seeded cards, every card exposed every id on every consecutive `ww_dump_tree`, pinned and in overlay
  mode, scrolled past the viewport, after dismissals, and after a collapsed-rail arrival-peek cycle. The
  suspected cause — `FlowItemCardTemplate` being bound at two hosts — is **refuted**: `OnItemArrived`
  returns early while the rail is open, `OnPeekCompleted` nulls `PeekItems.ItemsSource`, and an
  in-process peer walk with the same item in both hosts resolves both correctly. The one variable not
  reproduced is a UI thread saturated by streaming runs, which is what the e2e session had; cross-process
  UIA marshals every call to that dispatcher, so suspect that before suspecting the ids.
- **A `ListBoxItem` container CAN carry a per-item id and name** - through the list's `ItemContainerStyle`, as
  the chat-history and Routines lists now do. "Not a walker-recognized type" below means the
  `ViewAutomationIdTests` sweep cannot see it (it only walks controls declared inside a `DataTemplate`), not
  that an id is impossible. Without one a row reports its view-model's `ToString()` and can only be reached by
  index; `tests/Pia.Wpf.Tests/Views/RowContainerAutomationTests.cs` holds the two that were fixed.
- **`ui:InfoBar` exposes no automation peer at all** in this Wpf.Ui version — it renders, but
  neither its `AutomationId` nor its message reaches UIA. Don't put anything an assertion needs
  inside one.
- **There is no snackbar to read — `RootSnackbarPresenter` is never driven.** `ISnackbarService` is
  bound to `FlowSnackbarService`, so every `Show(…)` becomes a Flow item instead. What that means for
  a script: a **Danger/Warning** notice is `FlowLifetime.Persistent` and is fully readable at leisure
  (`Flow_Title_<id>` / `Flow_Body_<id>` inside `Flow_Card_<id>`, plus `Flow_Dismiss_<id>`), while a
  **Success/Info** notice is `FlowLifetime.Transient` — a peek that expires before one MCP round trip
  can read it. Four attempts on a confirmed-successful action caught nothing. Assert the side effect,
  never the success notice.
- **The Flow rail’s collapsed handle cannot be invoked.** It is a `Border` with a `MouseBinding`, so it
  has no `AutomationId` and no InvokePattern. Open it with a real mouse click on the bell glyph inside
  it, after `ww_window action=activate`:
  `ww_click(selector="automationId=RootFlowView >> type=Text", useInvokePattern=false)` — the bell is
  that subtree’s first `Text`. While collapsed the rail also exposes an unread **count badge** as a
  plain `Text` (`"1"`, `"2"`, …), which is a cheap “something was published” probe that needs no click
  at all — and once open, the header’s `Flow_ClearAll_Real` / `Flow_PinToggle_Real` /
  `Flow_Collapse_Real` confirm you got there.
- **`PluginsView.xaml` and the E2EE onboarding screen hosted inside Account still have no ids.**
  The General, Assistant, Providers, Account and Optimize *settings* views are id-addressable
  throughout, inner tab headers included, and `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs`
  fails `dotnet test` if that stops being true. It is a test, not a build error — a missing id
  compiles fine.
- **`AssistantView`, `MeetingAttendeeOverlay`, `RoutinesView`, `SettingsViews/PersonasView`'s own
  controls are covered and locked in `ViewAutomationIdTests`** (composer toolbar, suggestion
  chips, weak-provider banner, persona picker, chat/agent lever, send/run-in-background, the
  meeting overlay's close/settings/save-to-vault/summarize/rename-speaker, the run-history
  open-chat link — table above), and the walk that backs that test stops at every nested
  `UserControl`, so each of those needs its own row. **Every one now has it**: `PiaChatTitleChip`,
  `VoiceModeOverlay`, `DirectTranscriptionOverlay`, `TodoPanelControl`, `RunProgressPanel`,
  `PiaChatQuickSwitcher` (table above). A `ww_dump_tree` of a running Assistant window reports an
  `AutomationId` on every `Button`/`Edit`/`ComboBox`/`RadioButton` in the states that were actually
  rendered — empty state, setup overlay, composer, chip flyout with a chat row, working-directory
  picker, direct-transcription overlay — leaving only `Text`, `Separator`, `Image` and the item
  containers bare. The voice/meeting overlays, run-progress panel, todo panel, quick switcher,
  action cards and the expanded Flow rail were NOT in those dumps; they rest on their test rows.
  `AutocompletePopup` never had a gap to close here in
  the first place: its only interactive surface is a `ListBox`/`ListBoxItem` match list, neither a
  walker-recognized type — same exclusion as `NavigationSidebarView`, zero ids possible or needed.
  `PiaAssistantMessage` itself declares no
  direct controls (pure composition, same shape as `VaultView` below); its
  Copy/Speak/Regenerate/Export/rate buttons are now ided per-reply via `PiaAnswerToolbar`
  (`Answer_*_<messageId>`, table above), and its reasoning-trace toggle via `PiaReasoningView`
  (`Reasoning_Toggle_<messageId>`, table above). `PiaFileChip`/`PiaSourceChip`/
  `PiaChipOverflowPanel`/`ActionCardControl`/`FileDiffCard` are also closed (table above).
  `PiaSuggestionChips` / `PiaAgentModeChip` were long left open because the bound item is content
  only (a raw suggestion string, or an `AgentModeSuggestion`'s model-generated `Goal`/`Reason`) with
  no field to key a per-item id on. They are now closed by binding the id to the CONTAINER's
  `ItemsControl.AlternationIndex` instead of anything on the item — reach for that whenever a
  template's item is content all the way down, and set `AlternationCount` or every row silently
  reports index 0.
- **`PiaHistorySearchBar`'s two `DatePicker`s, `PiaAnswerToolbar`'s regenerate-style context menu,
  `MarkdownMessageControl`'s `PiiMenu_*` items and `DirectTranscriptionOverlay`'s `DirectTrans_Chip*`
  items carry ids the test cannot lock in.** `Activator.CreateInstance` never runs a layout pass,
  so a `DatePicker`'s `OnApplyTemplate` never fires and its internal `DatePickerTextBox` (part of
  the `ControlTemplate`, not the logical tree) never appears to the walker — confirmed empirically
  (they contribute 0 to the measured control count). A `ContextMenu`'s `MenuItem`s aren't one of
  the walker's seven recognized types either. Both got ids anyway for scripts; neither regresses
  silently, so don't be surprised `dotnet test` stays green if one is later removed.
- **`CodeBlockControl`'s ids are literal, not per-block.** It's built procedurally per fence in
  `PiaMarkdownRenderer.RenderCodeCard` with no index or other identity plumbed through, so a reply
  with two or more code blocks repeats `CodeBlock_Copy` / `CodeBlock_Content`. Same class of
  caveat as the tool-name rows below; disambiguate with ordinal indexing if a script needs a
  specific block.
- **`VaultView`, `HistoryView` and `RemindersView` are pure composition** — the top-level view
  itself declares zero interactive controls; every button/search-box/row lives in a nested
  `Pia<Area><Thing>` control. The header, search-bar, and now the per-item row/group-header
  controls all have ids (table above): `PiaVaultCategoryCard`'s category toggle,
  `PiaReminderRow`'s four hover actions plus `PiaReminderGroupCard`'s bucket toggle,
  `PiaHistoryGroupCard`'s bucket toggle, and (same shape, under `AssistantHistoryView`)
  `PiaAssistantChatRowContent`'s delete button plus `PiaAssistantChatGroupCard`'s bucket toggle.
  Still open in this family: the inspector panes (`PiaVaultInspector`, `PiaHistoryInspector*`)
  and the status bars. A test row or an id on the top-level view accomplishes nothing; the fix
  has to happen one nested control at a time. `TodoView` is the exception — unlike Vault/
  History/Reminders it declares interactive controls of its own (kanban add-bar, per-column and
  per-todo actions, table above), not pure composition; it turned out to be 9 controls, not the
  ~43 an earlier estimate guessed. Same nested-control shape is true
  of the top-level `OptimizeView` (the Optimize hotkey window, distinct from
  `SettingsViews/OptimizeView`), the first-run wizard (`FirstRunWizardWindow` and all of
  `WizardSteps/`), and most content dialogs beyond the shared `PrimaryButton` / `CloseButton` /
  `Dialog_RequiredHint` ids. See the checklist doc below for the file-by-file work list.
  **Every content dialog derives from Wpf.Ui's `ContentDialog`, not `UserControl`, with a
  constructor that takes a `ContentDialogHost`** — so `Activator.CreateInstance` fails and none of
  them can ever get a `[InlineData]` row. Check a dialog's base type and constructor before
  planning one as a test-locked item; it never is.
- **The walker's nested-view check misses a `UserControl` that is the literal root of a
  `DataTemplate`.** `Collect()` re-anchors its `root` parameter to whatever `LoadContent()`
  returns, so when an `ItemTemplate`'s root is itself a `UserControl` (e.g. `PiaReminderRow` as
  `PiaReminderGroupCard`'s row template), `!ReferenceEquals(element, root) && element is
  UserControl` is trivially false for that instance and the walk keeps descending — its controls
  get swept into the *group card's* `[InlineData]` row instead of stopping as a nested view. A
  `UserControl` one level deeper than the template root (e.g. `PiaReminderStatusChip` inside
  `PiaReminderRow`, or `PiaAssistantChatRowContent` inside `PiaAssistantChatRow`) is unaffected
  and still stops the walk normally. If a group-card row's inspected/per-item counts look higher
  than the header alone would explain, or its nested-view list contains something one level
  deeper than expected, this is why — confirmed across the Vault/Reminders/History/AssistantChat
  group-card-and-row pairs.
- **An *implicit* per-type `DataTemplate` — one declared in `ItemsControl.Resources` rather than
  assigned to `ItemsControl.ItemTemplateProperty` — is invisible to the walker, a different blind
  spot than the sweep hazard above.** `Collect()` only expands a template it finds via
  `element.ReadLocalValue(property)` for the three `DeclaredTemplates` properties; an implicit
  template is never set as a local value of any of them, so that read returns `UnsetValue` and the
  walk never opens it. `FileDiffCard`'s `CollapsedDiffRun` row template is the confirmed case: its
  one button (the "N unchanged lines" fold toggle) has no id today and the mechanism cannot demand
  one — not a sweep, not a genuine stop, just unreachable. Suspect this whenever a `DataTemplate`
  is declared with `DataType="{x:Type ...}"` and no `x:Key`/local property assignment.
- **A `StaticResource`-keyed `DataTemplate`/`ContentTemplate` assigned at more than one XAML site
  is walked once per site, not once per template.** Two call sites setting the identical resource
  object are two separate local-value reads, so `LoadContent()` runs twice and the inspected-control
  count doubles for whatever that template contains — confirmed on `FlowView`, whose
  `FlowItemCardTemplate`/`FlowHeaderTemplate` each back a real list/header AND a hidden
  arrival-peek clone that reuses the same resource. Measure via the test rather than counting
  `<DataTemplate>` declarations in the XAML.
- **`NavigationSidebarView`'s 12 `NavItem_*` buttons already all have ids** (verified by hand),
  but `ViewAutomationIdTests` cannot lock that in: they're set via
  `<ui:NavigationViewItem.Content>` inside `ui:NavigationView.MenuItems`, and `LogicalTreeHelper`
  reports zero children for that collection, so the walker's non-vacuity floor would trivially
  pass at 0 either way. Don't add a row for it — there's nothing for the walk to catch a
  regression with.

## Cross-checks

- **Independent verification is cheap**: a PowerShell `EnumWindows` filtered to the app's PID
  definitively answers "is there a window I can't see?" (native dialogs don't appear in
  `ww_list_windows`, and it misreports modality).
- WinWright traps to remember:
  - `ww_click` false-success (above).
  - `ww_dialog handle` can report success without dismissing.
  - Window-scoped screenshots never show native dialogs.
  - `ww_snapshot`'s `label` is **not** a selectable `name` — it is inferred from neighbouring text,
    so a `[name='<that label>']` selector can resolve 0 elements.
  - `ww_get_tree_path` emits ordinal paths (`type=Pane[0] >> type=ComboBox[0]`) that the selector
    parser **rejects**, despite the tool calling them "suitable for use as a selector".
  - `ww_inspect label_map` pairs the settings checkboxes with the *previous* row's description
    (`SpatialAbove` off-by-one) — plausible-looking and wrong.
  - The `value` **selector filter reads ValuePattern only**, not SelectionPattern:
    `type=ComboBox[value='visionary']` resolves 0 elements while `ww_get_value` on the same
    element returns `visionary`. Filter on snapshot output instead of on the selector.
- **If screenshots stop matching the UIA tree, suspect the GPU, not the app.** A WPF
  hardware-rendering stall leaves the window presenting a stale or blank surface while the
  dispatcher, UIA and the app itself keep working — `PrintWindow`, a screen grab and a resize all
  return the same dead frame. Confirm with a control app (Notepad renders fine) and, if needed,
  `HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration = 1` before relaunch. Remove the
  value afterwards.

## Traps that read as product bugs

Two found the hard way, neither of them WinWright's fault. They are here because each costs a
session before it is recognised for what it is.

### A `Focus()` inside `IsVisibleChanged` silently does nothing

- **What looks fine.** A pane flips visible and its `IsVisibleChanged` handler calls
  `SomeTextBox.Focus()` on a control inside it. Clean build, no warning, no log line.
- **What happens.** `UIElement.Focus()` returns a `bool` and returns `false` when the target is not
  focusable *at that instant* — it neither throws nor logs, and callers ignore the return. At the
  moment the pane's own event fires the descendant is not focusable yet — the shipped handler's own
  comment says `IsVisible` has not reached the target that early, and the previously collapsed
  subtree has not been laid out either. Focus stays wherever it was.
- **To a script this reads as lost input.** Click what opens the pane, then `ww_type` / `ww_keyboard`
  with no target, and the keystrokes land on whatever had focus before — indistinguishable from the
  harness dropping them. Name the target instead: `Routines_Field_Name` for the routine editor,
  `QuickSwitcher_Query` for Ctrl+H.
- **What the two shipped call sites do instead** — defer one dispatcher turn at
  `DispatcherPriority.Input`, then call **both** `Focus()` (logical) and `Keyboard.Focus(target)`
  (keyboard focus): `RoutinesView.EditorPane_IsVisibleChanged` → `EditorNameBox`, and
  `PiaChatQuickSwitcher.OnIsVisibleChanged` → `QueryBox`. So the focus arrives a turn *after* the
  click: an assertion in the same beat reads the pre-focus state. Wait or re-snapshot first.

### Seeding logs in console format makes every diagnostics-export entry 0 bytes

- **Symptom.** The export zip's `README.txt`, `manifest.json` and `environment.json` are all correct,
  and whatever the running app logged itself has content, but every log file you seeded by hand
  comes out **0 bytes**. It looks exactly like the redactor eating the file. It is not a product bug.
- **Mechanism** (`src/Pia.Wpf/Logging/LogRedactor.cs`). `Redact` walks the file line by line and
  starts with `dropping = true` on purpose, so a file beginning mid-payload cannot leak that
  fragment; a non-record line is written only while not dropping, so nothing at all is emitted before
  the first record. `IsRecord` demands the sink's exact shape — `Split('\t', 5)` yielding five fields,
  field 0 an ISO-8601 timestamp, field 1 one of `TRCE`/`DBUG`/`INFO`/`WARN`/`FAIL`/`CRIT`, fields 2
  and 3 bracketed. Console format (`info: Some.Category[0]` plus an indented message line) satisfies
  none of that, so a file made only of it legitimately emits nothing.
  `LogRedactorTests.AStreamThatStartsMidRecord_EmitsNothingBeforeItsFirstRecord` is that behaviour,
  green today.
- **Seed the tab-delimited form**, one record per line — `{ISO timestamp}`, `{LEVEL}`,
  `[{Category}]`, `[{EventId}]`, `{message}`, with a real tab between each. Build it rather than
  typing it, so the separators cannot come out as spaces:

  ```powershell
  $fields = '2026-08-24T12:24:51.9219150+02:00', 'INFO', '[Bootstrapper]', '[0]', 'Started'
  ($fields -join "`t") | Add-Content "$env:PIA_LOCAL_DATA_DIR/Logs/pia-2026-08-24.log"
  ```

  Seed at `INFO` or above: a correctly shaped `DBUG`/`TRCE` file is just as useless, because those
  bodies are replaced by `<debug-payload-dropped>` by design.
- **Where.** `pia*.log` in the logs directory the export was pointed at — under a throwaway profile
  that is `%PIA_LOCAL_DATA_DIR%\Logs`, not your real `%LOCALAPPDATA%\Pia\Logs`. Whatever the running
  app wrote itself exports fine — the sink holds that file open and the export deliberately opens it
  `FileShare.ReadWrite` — so one healthy entry beside a directory of 0-byte seeds is what makes this
  read as a partial failure rather than a fixture mistake.
