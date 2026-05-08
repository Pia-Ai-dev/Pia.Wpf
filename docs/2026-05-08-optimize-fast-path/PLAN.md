# Plan: Optimize Fast-Path Hotkey

## Goal

Add a new, configurable global hotkey that runs the entire Optimize pipeline end-to-end without the user touching the UI:

1. Show the Optimize window with the optimizing overlay/dialog visible immediately.
2. Capture selected text from the foreground app (the existing `ISelectedTextService.CaptureAsync` flow).
3. Run optimize with the user's default template.
4. Run accept (executes `DefaultOutputAction`: clipboard / auto-type / paste-to-previous).
5. Hide the Optimize window so the user is back in their target app.

On any failure (no input captured, optimize failed, paste/auto-type failed), the window stays open in the natural state from that step (e.g., empty input + snackbar; or comparison view with the optimized text and an error snackbar). The user resolves it via the existing UI buttons.

## User-facing behavior

- Default: feature is **off**. The `FastPathHotkey` setting is `null`. No default chord is suggested or pre-registered.
- Settings UI gets a fourth hotkey row ("Fast Path") with the same Capture / Clear pattern as the existing three. Clearing the hotkey sets it back to `null` (disabled), unlike the existing Optimize hotkey which resets to a default. Suggested label inside the capture dialog can be empty / "Not set".
- Conflict detection: must not duplicate any of `OptimizeHotkey`, `AssistantHotkey`, `ResearchHotkey`. Same snackbar pattern as today.
- Cancellation: only via the existing **Cancel** button on the optimizing dialog (`CancelOptimizationCommand`). No hotkey-based cancellation.

## Architecture

### New service: `IFastPathOptimizer` / `FastPathOptimizer`

Lives in `src/Pia.Wpf/Services/`. Singleton. Owns the orchestration as a small state machine.

```csharp
public interface IFastPathOptimizer
{
    Task RunAsync();   // entry point called from TrayIconService when fast-path hotkey fires
}
```

States: `Idle → Running → (Success | Failed) → Idle`. `Running` is guarded so a second hotkey press while running is a no-op (logged at Debug).

Dependencies (constructor):
- `ILogger<FastPathOptimizer>`
- `IWindowManagerService`
- `IWindowTrackingService`
- `ISelectedTextService`
- `ISettingsService`
- `ILocalizationService`
- `Wpf.Ui.ISnackbarService`

### Orchestration (`RunAsync`)

```
1. If state != Idle → return.
2. State = Running.
3. Track foreground window: _windowTrackingService.TrackWindowAtCursor() — so paste/auto-type targets the user's app.
4. Show Optimize window: _windowManagerService.ShowWindowForFastPathAsync()
   - This must:
     a. Open / reuse the Optimize window.
     b. Trigger OptimizeViewModel readiness (templates loaded, default selected) — wait for the Ready signal.
     c. Set vm.IsOptimizing = true (or equivalent flag) so the existing optimizing-overlay UI shows immediately. The point is: the user sees the overlay BEFORE we have captured text.
5. Capture text: var captured = await _selectedTextService.CaptureAsync().
   - On null/empty: clear the temporary IsOptimizing flag, snackbar Msg_FastPath_NoContent, leave window visible and empty, transition to Idle, return.
6. Apply captured text to vm.InputText (overwrite — fast-path is single-shot).
7. Run optimize: await vm.RunFastPathOptimizeAsync(cancellationToken).
   - This is a new method on OptimizeViewModel that does what ExecuteOptimize does today, BUT does NOT show its own ShowOptimizingDialogAsync (because we already activated the overlay). It simply runs the optimization, sets OptimizedText / IsComparisonView / handles errors with existing snackbars.
8. After RunFastPathOptimizeAsync:
   - If vm.IsComparisonView == true (success): await vm.AcceptCommand.ExecuteAsync(null).
     - AcceptCommand on success clears InputText, OptimizedText, IsComparisonView. If those are all cleared, hide the window: _windowManagerService.HideWindow(WindowMode.Optimize).
     - If AcceptCommand fell back to clipboard with a paste-failed snackbar (DefaultOutputAction == PasteToPreviousWindow but no tracked window), it still cleared the state — but the user got a snackbar telling them what happened. Treat as success and hide window.
     - Distinguish "fully succeeded" from "fell back" by checking that the snackbar key was not Msg_Optimize_PasteFailed. Easiest implementation: have AcceptCommand return a bool / status and reuse it. If too invasive, just always hide on completion (the snackbar conveys the failure to the user) — acceptable per user direction.
   - If vm.IsComparisonView == false (optimize failed or cancelled): leave window visible. The optimize failure path already shows a snackbar and may have ErrorMessage set.
9. Finally: state = Idle.
```

Per the user's direction: **on any failure state the window stays open**. Failure states explicitly listed by the user:
- No input captured (clipboard empty, no selection in foreground app).
- No target window found in paste/auto-type mode.
- (Implicit) Optimize call failed or was cancelled.

For the "no target window in paste mode" case: the existing `ExecuteAcceptAsync` falls back to clipboard with a snackbar. Per user direction, treat this as a failure state for fast-path: do NOT auto-hide the window, let the user see the comparison view with the result still on screen and the snackbar explanation. Implement by checking `_windowTrackingService.HasTrackedWindow` before calling Accept when `DefaultOutputAction == PasteToPreviousWindow` — if no tracked window, surface a snackbar via `Msg_FastPath_NoTargetWindow` and return without Accept. Same check for `OutputAction.AutoType`.

### Changes to `OptimizeViewModel`

1. Expose a `Task ReadyAsync` (TaskCompletionSource) that completes once `OnNavigatedToAsync` finishes its first run successfully (templates loaded, `SelectedTemplateId` non-empty). Already-completed if `_isInitialized` is true at the time the property is accessed (initialize TCS in constructor; complete it at the end of `OnNavigatedToAsync`).
2. Add `Task<bool> RunFastPathOptimizeAsync(CancellationToken externalCt = default)`. Internally identical to `ExecuteOptimize` BUT:
   - Skips `_dialogService.ShowOptimizingDialogAsync` — the caller (`FastPathOptimizer`) is responsible for the overlay state.
   - Returns `true` on `IsComparisonView == true` after the run, `false` otherwise.
   - Propagates exceptions through the existing snackbar paths (truncation, generic failure) — same UX as today.
3. Optionally factor the body of `ExecuteOptimize` so both paths share the actual `RunOptimizationAsync` invocation.

For the "show overlay before capture" behavior: the overlay is currently driven by `_dialogService.ShowOptimizingDialogAsync` running concurrently with the optimize task. For fast-path, we want it visible BEFORE we have input text. Approach:

Option A (preferred): add a dedicated method on `IDialogService` (or use a VM flag bound in XAML) that shows a generic "Preparing..." overlay during fast-path's pre-optimize phases (capture). Once optimize starts, swap to the existing optimizing dialog.

Option B (simpler): set `IsOptimizing = true` on the VM at fast-path start; bind the existing overlay UI to `IsOptimizing` so the user sees the same visual immediately. Capture happens in parallel. Once capture completes and we call `RunFastPathOptimizeAsync`, `IsOptimizing` stays true throughout.

**Pick Option B.** Implementation:
- Currently `IsOptimizing` is set inside `ExecuteOptimize`. The optimizing-dialog visual is shown by `_dialogService.ShowOptimizingDialogAsync`, NOT bound to `IsOptimizing`. So we need a slight change: `FastPathOptimizer` opens the dialog itself BEFORE capture, with a generic "preparing" message; once optimize is about to run, dialog stays open through the optimization (if technically possible) OR we close+reopen with the optimize-specific messages.
- Simplest implementation: `FastPathOptimizer` calls `_dialogService.ShowOptimizingDialogAsync(messages, ct)` once at the start with generic preparing messages, and keeps it open until either (a) optimize completes (we cancel the dialog ourselves) or (b) failure.

Look at how `ShowOptimizingDialogAsync` is currently structured (in `OptimizeViewModel.ExecuteOptimize`). The dialog runs in a Task, watches a CancellationToken, and `RunOptimizationAsync` cancels it on completion. Replicate that pattern in `FastPathOptimizer`:

```
var dialogCts = new CancellationTokenSource();
var dialogTask = _dialogService.ShowOptimizingDialogAsync(messages, dialogCts.Token);
try
{
    // ... capture, set input, run optimize, run accept
}
finally
{
    dialogCts.Cancel();
    try { await dialogTask; } catch { }
    dialogCts.Dispose();
}
```

For `RunFastPathOptimizeAsync` to NOT spawn its own dialog, parametrize: the existing `ExecuteOptimize` always runs both tasks (optimize + dialog) in parallel. The new `RunFastPathOptimizeAsync` runs only the optimize task — same exception handling, same final state.

### Changes to `IWindowManagerService` / `WindowManagerService`

Add:
```csharp
Task<OptimizeViewModel> ShowOptimizeAndGetViewModelAsync();
```

Implementation:
- Calls existing `ShowWindow(WindowMode.Optimize)`.
- Resolves `OptimizeViewModel` from the Optimize scope.
- Awaits `vm.ReadyAsync` (with a timeout of e.g. 3 seconds; on timeout return what we have — orchestrator handles fallback).
- Returns the VM.

This avoids exposing the scope mechanics to the orchestrator while keeping the VM accessible for fast-path.

### Changes to `TrayIconService`

1. Track a fast-path hotkey separately from the per-mode dictionary. Store it under a sentinel key: introduce private `private INativeHotkeyService? _fastPathHotkeyService;` and an integer hotkey ID `private const int FastPathHotkeyId = 100;` (existing IDs are 0/1/2 = WindowMode values, which RegisterHotKey uses to distinguish).
2. In `RegisterAllHotkeysAsync`: if `settings.FastPathHotkey != null`, register fast-path hotkey via `_hotkeyServiceFactory.Create(FastPathHotkeyId, settings.FastPathHotkey)` and wire `HotKeyPressed += OnFastPathHotkeyPressed`.
3. Add `void UpdateFastPathHotkey(KeyboardShortcut? shortcut)` to `ITrayIconService` (mirrors `UpdateHotkey` for the mode-based ones). Used by Settings VM.
4. `OnFastPathHotkeyPressed`: simple — kick off `_ = _fastPathOptimizer.RunAsync()` (fire-and-forget, awaiting the result is not the tray service's job; logging on failure happens inside the orchestrator). The orchestrator's reentrancy guard prevents double-runs.

### DI registration (`Bootstrapper.cs`)

Add:
```csharp
services.AddSingleton<IFastPathOptimizer, FastPathOptimizer>();
```
TrayIconService gains a constructor parameter for `IFastPathOptimizer` (or resolved on demand to break the cycle if needed — try ctor injection first; if it creates a cycle, use `Lazy<IFastPathOptimizer>`).

### `AppSettings`

Add:
```csharp
public KeyboardShortcut? FastPathHotkey { get; set; }    // default null = disabled
```

JSON serialization: nullable property should serialize naturally with the existing System.Text.Json setup (verify via existing `AssistantHotkey` / `ResearchHotkey` pattern — they're nullable and serialize fine).

### `KeyboardShortcut` model

No changes needed. (User specifically said "no default hotkey".) Do NOT add a `DefaultCtrlAltF()` factory.

### `GeneralSettingsViewModel`

Add observable property + commands mirroring the existing pattern:

```csharp
[ObservableProperty]
private string _fastPathHotkeyDisplayText = "";   // = Msg_Settings_HotkeyNotSet when null

private KeyboardShortcut? _fastPathHotkey;

[RelayCommand]
private async Task CaptureFastPathHotkeyAsync()
{
    var shortcut = await _dialogService.ShowHotkeyCaptureDialogAsync();
    if (shortcut != null && !HasInternalConflict(shortcut, /* sentinel mode? */))
    {
        _fastPathHotkey = shortcut;
        FastPathHotkeyDisplayText = shortcut.DisplayText;
        await SaveSettingsAsync();
        _trayIconService.UpdateFastPathHotkey(_fastPathHotkey);
    }
}

[RelayCommand]
private async Task ClearFastPathHotkeyAsync()
{
    _fastPathHotkey = null;
    FastPathHotkeyDisplayText = _localizationService["Msg_Settings_HotkeyNotSet"];
    await SaveSettingsAsync();
    _trayIconService.UpdateFastPathHotkey(null);
}
```

Conflict detection: `HasInternalConflict` currently iterates a dictionary keyed by `WindowMode`. Refactor it slightly to take an arbitrary "currently-being-edited" identifier (could be `WindowMode?` plus a `bool isFastPath` flag, or extract a list-based check). Simplest: add the fast-path hotkey to the comparison list with a sentinel name "FastPath" and emit `Msg_Settings_HotkeyAlreadyAssigned` with that sentinel name (localized).

`InitializeAsync`: load `_fastPathHotkey = settings.FastPathHotkey`; set `FastPathHotkeyDisplayText = _fastPathHotkey?.DisplayText ?? _localizationService["Msg_Settings_HotkeyNotSet"]`.

`SaveSettingsAsync`: persist `settings.FastPathHotkey = _fastPathHotkey`.

### `GeneralView.xaml`

Add a fourth `ui:Card` row inside the hotkey StackPanel (after the Research card), mirroring the Assistant/Research pattern (with `Dismiss24` icon for clear since there's no default to reset to). Bind to `FastPathHotkeyDisplayText`, `CaptureFastPathHotkeyCommand`, `ClearFastPathHotkeyCommand`. Label: `{loc:Str Settings_Hotkey_FastPath}`.

### Localization

Add new keys to all three of `MessageStrings.{resx,de.resx,fr.resx}` and `ViewStrings.{resx,de.resx,fr.resx}`:

**ViewStrings:**
- `Settings_Hotkey_FastPath`:
  - EN: "Fast Path"
  - DE: "Schnellpfad"
  - FR: "Voie rapide"
- `Settings_Hotkey_FastPath_Description` (optional helper text under the row, omit if it crowds the UI):
  - EN: "Capture, optimize and apply in one keystroke"
  - DE: "Erfassen, optimieren und anwenden mit einem Tastendruck"
  - FR: "Capturer, optimiser et appliquer en une seule frappe"

**MessageStrings:**
- `Msg_FastPath_NoContent`:
  - EN: "Nothing to optimize. Select text first, then press the fast-path hotkey."
  - DE: "Nichts zu optimieren. Markieren Sie zuerst Text und drücken Sie dann den Schnellpfad-Hotkey."
  - FR: "Rien à optimiser. Sélectionnez d'abord du texte, puis appuyez sur le raccourci de la voie rapide."
- `Msg_FastPath_NoTargetWindow`:
  - EN: "No target window for paste/auto-type. The optimized text is shown in the window — apply it manually."
  - DE: "Kein Zielfenster für Einfügen/Auto-Eingabe. Der optimierte Text wird im Fenster angezeigt — wenden Sie ihn manuell an."
  - FR: "Aucune fenêtre cible pour coller / saisie automatique. Le texte optimisé est affiché dans la fenêtre — appliquez-le manuellement."

Also wire the new keys into `MessageStrings.Designer.cs` (auto-generated) or the existing extension mechanism — match the pattern used by neighbouring keys.

### Tests

`tests/Pia.Wpf.Tests/Services/FastPathOptimizerTests.cs` (new):
- Reentrancy: two parallel `RunAsync` calls — second is a no-op.
- No captured text: snackbar shown, window not hidden, no Optimize/Accept invoked.
- Captured text + optimize success + accept success: window hidden.
- Captured text + optimize failure: window stays visible, no Accept invoked.
- DefaultOutputAction == PasteToPreviousWindow with no tracked window: snackbar shown, no Accept invoked, window stays visible with comparison view.

Use `NSubstitute` (or whatever the project uses — check existing tests). Mock `IWindowManagerService`, `ISelectedTextService`, `ISettingsService`, `IDialogService`, `IWindowTrackingService`, `ISnackbarService`, `ILocalizationService`. Stub a fake `OptimizeViewModel`-like contract — if `OptimizeViewModel` is hard to mock directly (concrete class with many deps), introduce a thin `IOptimizeFastPathHandle` interface that `OptimizeViewModel` implements with just the methods/properties fast-path needs (`InputText`, `IsComparisonView`, `OptimizedText`, `RunFastPathOptimizeAsync`, `AcceptCommand` / `RunAcceptAsync`, `ReadyAsync`). Cleaner separation, easier tests.

Confirm by checking existing test helpers under `tests/Pia.Wpf.Tests/Services/`.

## Files to modify

1. `src/Pia.Wpf/Models/AppSettings.cs` — add `FastPathHotkey`
2. `src/Pia.Wpf/Services/Interfaces/IFastPathOptimizer.cs` — NEW
3. `src/Pia.Wpf/Services/FastPathOptimizer.cs` — NEW
4. `src/Pia.Wpf/Services/Interfaces/IWindowManagerService.cs` — add `ShowOptimizeAndGetViewModelAsync` (or alternative scope-access method)
5. `src/Pia.Wpf/Services/WindowManagerService.cs` — implement it
6. `src/Pia.Wpf/Services/Interfaces/ITrayIconService.cs` — add `UpdateFastPathHotkey`
7. `src/Pia.Wpf/Services/TrayIconService.cs` — register fast-path hotkey, route press to `IFastPathOptimizer`
8. `src/Pia.Wpf/ViewModels/OptimizeViewModel.cs` — add `ReadyAsync`, `RunFastPathOptimizeAsync`, optional `IOptimizeFastPathHandle`
9. `src/Pia.Wpf/Bootstrapper.cs` — register `IFastPathOptimizer`
10. `src/Pia.Wpf/ViewModels/GeneralSettingsViewModel.cs` — Capture/Clear/Display + extend `HasInternalConflict`
11. `src/Pia.Wpf/Views/SettingsViews/GeneralView.xaml` — add fourth hotkey card
12. `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` (+ `.de.resx`, `.fr.resx`) — `Settings_Hotkey_FastPath`(+ description)
13. `src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs` — auto-generated; regenerate via the existing tooling, OR add manually to match existing patterns
14. `src/Pia.Wpf/Resources/Strings/MessageStrings.resx` (+ `.de.resx`, `.fr.resx`) — `Msg_FastPath_NoContent`, `Msg_FastPath_NoTargetWindow`
15. `src/Pia.Wpf/Resources/Strings/MessageStrings.Designer.cs` — same note as above
16. `tests/Pia.Wpf.Tests/Services/FastPathOptimizerTests.cs` — NEW

## Hard constraints

- Do **not** add a default hotkey for fast-path. Setting starts as `null`.
- Do **not** call `RegisterHotKey` for fast-path when `settings.FastPathHotkey == null`.
- Do **not** auto-hide the window on any failure path. Hide only on full Accept success.
- Do **not** change the existing single-press Optimize hotkey behavior.
- Do **not** introduce double-tap / LL keyboard hooks. The whole double-tap idea is rejected.
- Reuse existing services (`ISelectedTextService`, `IWindowTrackingService`, `IOutputService` via VM) — do not duplicate copy/paste/clipboard plumbing.
- Follow existing code style: 4-space indent, `[ObservableProperty]`/`[RelayCommand]`, namespaces use `Pia` (not `Pia.Wpf`), privacy-first logging (use `SensitiveDebug` for any user-content-bearing log line).

## Definition of Done (for Codex)

- All listed files modified or created.
- `dotnet build -c Release` succeeds with zero warnings (existing warning level).
- `dotnet test` passes including the new tests.
- Manual smoke test instructions documented at the bottom of this file (Codex appends what to test).
- Commit on branch `feature/optimize_fast_path` with a single commit message: `Add optimize fast-path hotkey`.

## Manual Smoke Test

1. Start Pia and open Settings > General > Hotkeys.
2. In the Fast Path row, click Change, press an unused shortcut, and confirm that the displayed shortcut updates. Use Clear to verify it returns to Not set, then assign it again.
3. In Settings > General > Appearance, leave Auto Capture Selected Text enabled.
4. Success path: in another app, select editable text, press the Fast Path hotkey, and verify the Optimize window opens with the optimizing overlay immediately, captures the selection, optimizes it, applies the configured Default Output Action, and hides only after the output succeeds.
5. No-selection path: click in another app with no text selected, press the Fast Path hotkey, and verify the Optimize window stays visible with empty input and a snackbar saying there is nothing to optimize.
6. No-target-window path for PasteToPreviousWindow: set Default Output Action to Paste to previous window, close or invalidate the previously tracked target window, select text somewhere, press the Fast Path hotkey, and verify the Optimize window remains visible in comparison view with the optimized text and a snackbar saying there is no target window for paste/auto-type.
7. Return to Settings > General > Hotkeys and clear the Fast Path hotkey if the feature should remain disabled by default for the profile.
