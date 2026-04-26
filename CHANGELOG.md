# Changelog

All notable changes to Pia are documented here.
## [Unreleased]

### Features

- Add design spec for todo panel UX enhancements

Covers: visual redesign (Clean Elevated style), expand/collapse animation,
drag-and-drop reordering with SortOrder sync, voice input via Whisper,
and completion animation (strikethrough + fade + collapse).

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
- Add implementation plan for todo panel UX enhancements

16 tasks across 4 chunks: data model, visual redesign,
voice/drag-drop/animation, and integration verification.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
- Feat(todo): add SortOrder property to TodoItem and SyncTodo DTO

Backwards compatible - int defaults to 0, old clients ignore the field.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
- #3 Add SortOrder column to SQLite schema and migration

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- #7 Add theme resources for panel shadow

Add PanelShadowColor and PanelShadowOpacity resources to Dark.xaml (0.3)
and Light.xaml (0.15), with sys namespace for Double type.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- #9 Add TodoPanel_Record localization key in EN/DE/FR

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- #11 Add voice input and reorder commands to TodoViewModel

- Inject IVoiceInputService; add RecordTodoCommand that appends transcription to NewTodoTitle
- Replace priority-based GetInsertIndex with append-to-end (SortOrder-based)
- Add ReorderTodosAsync to move items, recalculate SortOrder, persist via UpdateSortOrderAsync, and revert on failure

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- Feat(todo): wire drag-drop reorder and add completion animation

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>

### Other

- #4 Update TodoService to read/write SortOrder

- Add SortOrder to all SELECT column lists (GetAsync, GetAllAsync, GetPendingAsync, GetCompletedAsync, GetCompletedTodayAsync)
- Change GetPendingAsync ORDER BY to SortOrder ASC, CreatedAt ASC
- Map SortOrder from reader index 10 in MapTodoItem
- Include SortOrder in AddTodoParameters
- Assign SortOrder = max+1 on CreateAsync before INSERT
- Include SortOrder in CreateAsync INSERT and ImportAsync INSERT OR REPLACE
- Add SortOrder = @SortOrder to UpdateAsync SET clause
- Implement UpdateSortOrderAsync with transaction support
- Add UpdateSortOrderAsync to ITodoService interface

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- #5 update SyncMapper to include SortOrder in todo sync mappings

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- #8 Redesign TodoPanelControl XAML with Clean Elevated style

- Replace flat border with rounded corners (12,0,0,12) and drop shadow effect
- Remove PanelPriorityDotStyle; replace priority dots with colored left borders
- Add fade-in animation and drag grip icon (visible on hover) to todo items
- Add record button (RecordTodoCommand) to quick-add input area (3-column layout)
- Use TertiaryBackgroundBrush for item backgrounds

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- #10 Implement panel expand/collapse animation in host views

Replace BooleanToVisibilityConverter with DataTrigger storyboards on
TodoPanelControl in AssistantView, OptimizeView, and ResearchView.
Width animates 0→280px (EaseOut, 0.25s) on open and 280→0px (EaseIn,
0.2s) on close, with Visibility toggled at the correct keyframe.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- #12 Create DragDropReorderBehavior attached behavior

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>

## [1.3.8] - 2026-03-17

### Bug Fixes

- Fix OptimizeView: auto-focus input on hotkey and prevent LLM preamble in output

1. Subscribe to parent window's Activated event so the input textbox
   receives focus whenever the window is shown via hotkey and the input
   is empty (not in comparison view).

2. Add explicit "output only" instruction to the optimization prompt so
   LLMs don't wrap results with commentary like "Here is the adjusted
   content:".

https://claude.ai/code/session_01UBLGackmcfmBf1CN5d9cvV
- Fix version mismatch between update notification and title bar

The title bar used Assembly.GetName().Version (defaults to 1.0.0.0)
while the update notification used Velopack's semantic version from
GitHub releases. Now both use Velopack's CurrentVersion when installed,
falling back to assembly version only in development.

https://claude.ai/code/session_0123VVw3hqNqQbD78Jd9mJVs

### Other

- #26 mistral added as provider, default provider url
- #31 refine optimize view hotkey handling and target defintion

## [1.3.7] - 2026-03-17

### Bug Fixes

- Fix duplicate providers when navigating to ProviderView

Clear the Providers collection at the start of InitializeAsync() before
adding providers. Previously, each navigation to SettingsView would call
InitializeAsync() and append all providers again without clearing,
causing duplicates to accumulate in the dropdown and list.

https://claude.ai/code/session_01HojgKguMQnqfK9QQBihb63

## [1.3.5] - 2026-03-17

### Bug Fixes

- Fix Whisper native library not found in GitHub release installer

The CI workflow used IncludeNativeLibrariesForSelfExtract=true, bundling
native DLLs inside the single-file exe. At runtime, .NET extracted them
to a flat temp directory without the runtimes/{rid}/ structure that
Whisper.net expects, causing the "Native Library not found" error.

- Set IncludeNativeLibrariesForSelfExtract=false in CI to keep native
  DLLs as separate files alongside the exe (matching the local build script)
- Add fallback in ConfigureNativeLibraryPath to search for whisper.dll
  directly in flat extraction directories

https://claude.ai/code/session_01Tb4om32jYKLHfxZsrRHkhS

## [1.3.3] - 2026-03-17

### Bug Fixes

- Fix Whisper native library not found in MSI installed variant

Whisper.net uses a custom NativeLibraryLoader that only searches
runtimes/{rid}/ subdirectories. With IncludeNativeLibrariesForSelfExtract=true,
native DLLs were bundled in the single-file exe and extracted flat to a temp
directory at runtime — without the runtimes/ structure Whisper.net expects.

- Set IncludeNativeLibrariesForSelfExtract=false so native libs remain as
  separate files with their runtimes/ directory structure in the publish output
  (Velopack packages them into the installer regardless)
- Add RuntimeOptions.LibraryPath fallback in TranscriptionService that searches
  NATIVE_DLL_SEARCH_DIRECTORIES for single-file deployment scenarios

https://claude.ai/code/session_019maHLZRV5eNkLhK2Ls1wnh

## [1.3.1] - 2026-03-16

### Features

- Feature/26 optimize updates (#27)

* Fix OptimizeView: auto-focus input on hotkey and prevent LLM preamble in output

1. Subscribe to parent window's Activated event so the input textbox
   receives focus whenever the window is shown via hotkey and the input
   is empty (not in comparison view).

2. Add explicit "output only" instruction to the optimization prompt so
   LLMs don't wrap results with commentary like "Here is the adjusted
   content:".

* Fix version mismatch between update notification and title bar

The title bar used Assembly.GetName().Version (defaults to 1.0.0.0)
while the update notification used Velopack's semantic version from
GitHub releases. Now both use Velopack's CurrentVersion when installed,
falling back to assembly version only in development.

* #26 mistral added as provider, default provider url ([#27](https://github.com/Pia-Ai-dev/Pia.Wpf/pull/27))

## [1.2.36] - 2026-03-13

### Bug Fixes

- #21 fix height

### Other

- #20 first shot
- #20 settings adjustments
- #21 Make optimize input responsive and remove text wrapping

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
- #22 Show app version in the title bar

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
- #22 use clean version

## [1.2.32] - 2026-03-11

### Other

- #18 velopack polish

## [1.2.30] - 2026-03-11

### Other

- #15 sync logging and ui updates
- #15 sync tunning

## [1.2.28] - 2026-03-11

### Other

- #15 check for remote revoked client
- #15 e2ee fixes and debug log

## [1.2.26] - 2026-03-11

### Features

- #11 add sync now button and last sync metadata to SyncView

Add SyncNow command, LastSyncText/LastSyncItemsText/IsSyncing properties
to SettingsViewModel. Add sync status section with relative timestamp,
item counts, and Sync Now button with spinner to SyncView.xaml. Add
localized strings in EN/DE/FR.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
- #11 add SyncClientService tests for SyncResult null cases

Test that SyncNowAsync returns null when not logged in and when sync
is disabled.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>

### Other

- #11 return SyncResult from SyncNowAsync with push/pull counts

Add SyncResult record. Change SyncNowAsync to return SyncResult? with
pushed/pulled counts and decryption errors. Update PushChangesAsync and
PullChangesAsync to return counts.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>

## [1.2.22] - 2026-03-11

### Other

- #10 move update bar below window title

## [1.2.20] - 2026-03-11

### Other

- #4 e2ee checks, log to file

## [1.2.19] - 2026-03-10

### Other

- #4 e2ee onboading issue

## [1.2.17] - 2026-03-10

### Other

- #4 merge commit
- #4 property merge issue

## [1.2.14] - 2026-03-10

### Features

- Add missing E2EE shared DTOs for server build

Add E2EEStatusResponse, DeviceStatusResponse classes and
OnboardingSessionId property to DeviceInfo.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>

## [1.2.13] - 2026-03-10

### Features

- Feature/5 first run (#7) ([#7](https://github.com/Pia-Ai-dev/Pia.Wpf/pull/7))

## [1.2.12] - 2026-03-10

### Features

- #3 add to pii from assistant result

### Other

- #4 e2ee fixes

## [1.2.10] - 2026-03-09

### Bug Fixes

- #1 Fix build action branch

### Features

- #1 Add MdXaml as git submodule
- #1 Add WPF client project (renamed Pia → Pia.Wpf)
- #1 Add Pia.Shared library
- #1 Add test project (renamed Pia.Tests → Pia.Wpf.Tests)
- #1 Add .gitignore and remove tracked bin/obj artifacts
- #1 Add Pia.Wpf solution file
- #1 Add Directory.Build.props and dotnet tools manifest
- #1 Add version.json at 1.2 (avoid collision with private repo)
- #1 Add build-velopack.ps1 adapted for Pia.Wpf
- #1 Add MIT license
- #1 Add CLAUDE.md for public repo
- #1 Add build-and-release workflow adapted for Pia.Wpf

### Other

- #1 Update GitHub URLs to point to Pia.Wpf public repo
- #1 Update from prepared develop
- #1 update readme.md and .gitignore

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
- #1 Update license


