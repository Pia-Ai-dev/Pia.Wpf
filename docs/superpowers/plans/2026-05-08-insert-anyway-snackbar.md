# Insert-Anyway Snackbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the Optimize window receives a hotkey-captured selection while its input field already contains text, show a snackbar with an inline **"Insert anyway"** hyperlink. Clicking the link replaces the input with the captured text — and, when the request originated from the Fast Path hotkey, also continues the fast-path pipeline (optimize → accept).

**Architecture:** Two call sites currently show the existing `Msg_SelectionNotPastedInputNotEmpty` snackbar:
1. `OptimizeViewModel.ApplyCapturedSelection` (regular Optimize hotkey).
2. `FastPathOptimizerService.RunAsync` — currently does NOT show this snackbar because `PrepareForFastPath()` auto-clears `InputText`. We change that: fast-path stops nuking the user's draft, instead falling through to the same "input not empty" path with a Fast-Path-aware Insert-anyway action.

A small, version-pinned helper (`SnackbarActionHelper`) constructs a `Wpf.Ui.Controls.Snackbar` with a custom `Content` that contains the message text plus a clickable `Hyperlink`. The helper is shared by the regular-hotkey path and the fast-path handle. Action semantics is **replace** (not append, not insert-at-cursor).

**Tech Stack:** C# 13, WPF (.NET 10), MVVM (CommunityToolkit.Mvvm), WPF-UI 4.2.0 (`Wpf.Ui.Controls.Snackbar`, `ISnackbarService`), xUnit + NSubstitute for tests.

---

## Behavioral pivot (load-bearing — read first)

`OptimizeViewModel.PrepareForFastPath` must STOP auto-clearing `InputText`. It still clears `OptimizedText`, `IsComparisonView`, and `ErrorMessage` (stale comparison state from a prior run must not survive). After this change, the existing "input not empty" branch becomes reachable from fast-path too, which is what the user asked for.

**Insert-anyway action semantics: REPLACE.** The captured text overwrites whatever is in the input field. This matches "do what the hotkey would have done if input had been empty." Do not append, do not concatenate, do not insert-at-cursor.

Two different callback shapes share the same snackbar visual:
- **Regular hotkey** (`OptimizeViewModel.ApplyCapturedSelection`): callback just sets `InputText = captured`. The user clicks Optimize manually afterwards.
- **Fast-path** (`FastPathOptimizerService`): callback sets `InputText = captured` AND continues the optimize→accept pipeline.

---

## File Structure

| File | Role | Change |
|------|------|--------|
| `src/Pia.Wpf/Helpers/SnackbarActionHelper.cs` | NEW. Static helper that constructs a `Wpf.Ui.Controls.Snackbar` with a `TextBlock`+`Hyperlink` Content and queues it on the snackbar service's presenter. | Create |
| `src/Pia.Wpf/Resources/Strings/MessageStrings.resx` | New key `Msg_SelectionNotPasted_InsertAnyway` (action label only — existing `Msg_SelectionNotPastedInputNotEmpty` body text stays). | Add 1 entry |
| `src/Pia.Wpf/Resources/Strings/MessageStrings.de.resx` | Same key, DE translation. | Add 1 entry |
| `src/Pia.Wpf/Resources/Strings/MessageStrings.fr.resx` | Same key, FR translation. | Add 1 entry |
| `src/Pia.Wpf/Services/Interfaces/IFastPathOptimizer.cs` | Extend `IOptimizeFastPathHandle` with `void ShowFastPathInsertAnywaySnackbar(string capturedText, Func<Task> onInsertAnyway)`. | Add method |
| `src/Pia.Wpf/ViewModels/OptimizeViewModel.cs` | (a) `PrepareForFastPath` stops clearing `InputText`. (b) Implement `ShowFastPathInsertAnywaySnackbar`. (c) `ApplyCapturedSelection` uses the new snackbar helper with a "replace" callback. | Modify |
| `src/Pia.Wpf/Services/FastPathOptimizerService.cs` | (a) Detect non-empty `InputText` after `PrepareForFastPath`; if so, show the Insert-Anyway snackbar instead of overwriting. (b) Extract optimize→accept body into `RunFastPathWithInputAsync`. (c) Insert-Anyway callback re-acquires the `_isRunning` guard, re-acquires the handle via `ShowOptimizeAndGetViewModelAsync`, and re-enters `RunFastPathWithInputAsync`. | Modify |
| `tests/Pia.Wpf.Tests/Services/FastPathOptimizerTests.cs` | (a) Extend `FakeFastPathHandle` with the new method capturing the callback. (b) Add 3 new tests: input-not-empty-shows-snackbar; insert-anyway-callback-runs-pipeline; insert-anyway-during-running-is-noop. | Modify |

No XAML changes. No new DI registrations. No `Designer.cs` regeneration (the localization service goes through `ResourceManager.GetString(key, culture)` directly).

---

## API verification step (do before writing the helper)

WPF-UI 4.2.0's `Wpf.Ui.Controls.Snackbar` and `ISnackbarService.SnackbarPresenter` API surface varies between minor versions. **Before** writing `SnackbarActionHelper`, decompile/inspect the installed `Wpf.Ui.dll` (under `~/.nuget/packages/wpf-ui/4.2.0/lib/...`) to confirm:

1. `Snackbar` has a public constructor that takes a `SnackbarPresenter`.
2. `Snackbar.Show()` is the method that enqueues it (or use `presenter.AddToQue(snackbar)` if `Show()` doesn't exist).
3. `Snackbar.Content` accepts arbitrary `object` (FrameworkElement).
4. `ISnackbarService.SnackbarPresenter` is a public property exposing the presenter.

**If the API differs**, fall back to: append a separate inline action button to the comparison view (for fast-path) and toast a normal `_snackbarService.Show(...)` snackbar with no link; for the regular-hotkey path, surface "Insert anyway" as a `Button` in a one-line banner inside `OptimizeView.xaml` bound to a new VM command. Document the fallback in a code comment if used.

For the rest of this plan, assume the primary API works as expected.

---

## Task 1: Add localization keys

**Files:**
- Modify: `src/Pia.Wpf/Resources/Strings/MessageStrings.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/MessageStrings.de.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/MessageStrings.fr.resx`

- [ ] **Step 1: Add EN entry**

In `MessageStrings.resx`, after the existing `Msg_SelectionNotPastedInputNotEmpty` entry:

```xml
<data name="Msg_SelectionNotPasted_InsertAnyway" xml:space="preserve"><value>Insert anyway</value></data>
```

- [ ] **Step 2: Add DE entry**

In `MessageStrings.de.resx`, in the same position relative to `Msg_SelectionNotPastedInputNotEmpty`:

```xml
<data name="Msg_SelectionNotPasted_InsertAnyway" xml:space="preserve"><value>Trotzdem einfügen</value></data>
```

- [ ] **Step 3: Add FR entry**

In `MessageStrings.fr.resx`:

```xml
<data name="Msg_SelectionNotPasted_InsertAnyway" xml:space="preserve"><value>Insérer quand même</value></data>
```

- [ ] **Step 4: Build to verify resx is valid**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj -c Debug`
Expected: build succeeds (resx schema validation passes).

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Resources/Strings/MessageStrings.resx src/Pia.Wpf/Resources/Strings/MessageStrings.de.resx src/Pia.Wpf/Resources/Strings/MessageStrings.fr.resx
git commit -m "Add Insert-anyway action label to MessageStrings"
```

---

## Task 2: Verify WPF-UI Snackbar API

**Files:** none (investigation only)

- [ ] **Step 1: Inspect installed Wpf.Ui assembly**

Run (PowerShell):
```powershell
$dll = "$env:USERPROFILE\.nuget\packages\wpf-ui\4.2.0\lib\net8.0-windows7.0\Wpf.Ui.dll"
[System.Reflection.Assembly]::LoadFile($dll).GetType("Wpf.Ui.Controls.Snackbar").GetConstructors() | ForEach-Object { $_.ToString() }
[System.Reflection.Assembly]::LoadFile($dll).GetType("Wpf.Ui.Controls.Snackbar").GetMethods() | Where-Object Name -in 'Show','Hide' | ForEach-Object { $_.ToString() }
[System.Reflection.Assembly]::LoadFile($dll).GetType("Wpf.Ui.ISnackbarService").GetProperties() | ForEach-Object { $_.ToString() }
```

Expected: a constructor `.ctor(Wpf.Ui.Controls.SnackbarPresenter)`; a `Show()` method; an `ISnackbarService.SnackbarPresenter { get; }` property. Record the actual signatures in a comment in the helper.

- [ ] **Step 2: Decision**

If the signatures match the expected pattern, proceed with the helper as written below. If not, adapt the helper accordingly OR fall back to the in-view inline action button (see "API verification step" above) and document the deviation in `docs/superpowers/plans/2026-05-08-insert-anyway-snackbar.md` under a "Deviations" section.

---

## Task 3: Create SnackbarActionHelper

**Files:**
- Create: `src/Pia.Wpf/Helpers/SnackbarActionHelper.cs`

- [ ] **Step 1: Write the helper**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Pia.Helpers;

/// <summary>
/// Constructs a Snackbar with an inline hyperlink action.
/// </summary>
public static class SnackbarActionHelper
{
    public static void ShowWithAction(
        ISnackbarService snackbarService,
        string title,
        string message,
        string actionText,
        Action onAction,
        ControlAppearance appearance,
        TimeSpan timeout)
    {
        var presenter = snackbarService.SnackbarPresenter;
        if (presenter is null)
            return;

        var content = BuildContent(message, actionText, onAction);

        var snackbar = new Snackbar(presenter)
        {
            Title = title,
            Content = content,
            Appearance = appearance,
            Timeout = timeout,
        };

        snackbar.Show();
    }

    private static FrameworkElement BuildContent(string message, string actionText, Action onAction)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        textBlock.Inlines.Add(new Run(message));
        textBlock.Inlines.Add(new Run("  "));

        var hyperlink = new Hyperlink(new Run(actionText));
        hyperlink.Click += (_, _) =>
        {
            try
            {
                onAction();
            }
            catch
            {
                // Action errors are surfaced by the caller's logging path; swallow here so
                // an exception in user-supplied code does not crash the snackbar host.
            }
        };
        textBlock.Inlines.Add(hyperlink);

        return textBlock;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Helpers/SnackbarActionHelper.cs
git commit -m "Add SnackbarActionHelper for inline hyperlink actions"
```

---

## Task 4: Stop auto-clearing input in PrepareForFastPath

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/OptimizeViewModel.cs:203-210`

- [ ] **Step 1: Update PrepareForFastPath**

Replace:

```csharp
public void PrepareForFastPath()
{
    InputText = string.Empty;
    OptimizedText = string.Empty;
    IsComparisonView = false;
    ErrorMessage = null;
    OptimizeCommand.NotifyCanExecuteChanged();
}
```

with:

```csharp
public void PrepareForFastPath()
{
    // Do NOT clear InputText — fast-path now respects the existing draft and surfaces
    // an "Insert anyway" snackbar via FastPathOptimizerService when input is non-empty.
    OptimizedText = string.Empty;
    IsComparisonView = false;
    ErrorMessage = null;
    OptimizeCommand.NotifyCanExecuteChanged();
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 3: Commit (defer until Task 5; combined commit improves diff readability)**

No commit yet — combined with the next change in Task 5.

---

## Task 5: Add ShowFastPathInsertAnywaySnackbar to handle interface and VM

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IFastPathOptimizer.cs`
- Modify: `src/Pia.Wpf/ViewModels/OptimizeViewModel.cs`

- [ ] **Step 1: Extend IOptimizeFastPathHandle**

In `IFastPathOptimizer.cs`, add the new method to the interface:

```csharp
public interface IOptimizeFastPathHandle
{
    Task ReadyAsync { get; }
    string InputText { get; set; }
    string OptimizedText { get; }
    bool IsComparisonView { get; }
    bool IsOptimizing { get; set; }
    void PrepareForFastPath();
    Task ShowOptimizingDialogAsync(CancellationToken cancellationToken);
    Task<bool> RunFastPathOptimizeAsync(CancellationToken externalCt = default);
    Task<bool> RunFastPathAcceptAsync();
    void ShowFastPathSnackbar(string messageKey);
    void ShowFastPathInsertAnywaySnackbar(string capturedText, Func<Task> onInsertAnyway);
}
```

- [ ] **Step 2: Implement on OptimizeViewModel**

In `OptimizeViewModel.cs`, after the existing `ShowFastPathSnackbar` method (around line 367):

```csharp
public void ShowFastPathInsertAnywaySnackbar(string capturedText, Func<Task> onInsertAnyway)
{
    Pia.Helpers.SnackbarActionHelper.ShowWithAction(
        _snackbarService,
        _localizationService["Msg_Warning"],
        _localizationService["Msg_SelectionNotPastedInputNotEmpty"],
        _localizationService["Msg_SelectionNotPasted_InsertAnyway"],
        () =>
        {
            // Fire-and-forget; FastPathOptimizerService logs failures internally.
            onInsertAnyway().SafeFireAndForget(_logger);
        },
        Wpf.Ui.Controls.ControlAppearance.Caution,
        TimeSpan.FromSeconds(8));
}
```

Note: timeout extended from 3s to 8s so the user has time to actually click the link.

- [ ] **Step 3: Update ApplyCapturedSelection (regular-hotkey path)**

Replace the existing `ApplyCapturedSelection` method (around line 618):

```csharp
private void ApplyCapturedSelection(string text)
{
    if (string.IsNullOrEmpty(InputText))
    {
        InputText = text;
        ShouldFocusInput = true;
        return;
    }

    Pia.Helpers.SnackbarActionHelper.ShowWithAction(
        _snackbarService,
        _localizationService["Msg_Warning"],
        _localizationService["Msg_SelectionNotPastedInputNotEmpty"],
        _localizationService["Msg_SelectionNotPasted_InsertAnyway"],
        () =>
        {
            InputText = text;
            ShouldFocusInput = true;
            OptimizeCommand.NotifyCanExecuteChanged();
        },
        Wpf.Ui.Controls.ControlAppearance.Caution,
        TimeSpan.FromSeconds(8));
}
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 5: Commit (combined with Task 4)**

```bash
git add src/Pia.Wpf/Services/Interfaces/IFastPathOptimizer.cs src/Pia.Wpf/ViewModels/OptimizeViewModel.cs
git commit -m "Show Insert-anyway snackbar instead of dropping captured text"
```

---

## Task 6: Update FakeFastPathHandle for new interface method

**Files:**
- Modify: `tests/Pia.Wpf.Tests/Services/FastPathOptimizerTests.cs:114-166`

- [ ] **Step 1: Add capture fields and method to the fake**

Add to `FakeFastPathHandle`:

```csharp
public string? InsertAnywayCapturedText { get; private set; }
public Func<Task>? InsertAnywayCallback { get; private set; }
public int InsertAnywaySnackbarShownCount { get; private set; }

public void ShowFastPathInsertAnywaySnackbar(string capturedText, Func<Task> onInsertAnyway)
{
    InsertAnywaySnackbarShownCount++;
    InsertAnywayCapturedText = capturedText;
    InsertAnywayCallback = onInsertAnyway;
    LastSnackbarKey = "Msg_SelectionNotPastedInputNotEmpty";
}
```

The `LastSnackbarKey` is set so existing assertions that check it (none today, but future-proof) continue to work.

- [ ] **Step 2: Build the test project**

Run: `dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -c Debug`
Expected: build succeeds. Existing tests should compile against the new fake.

- [ ] **Step 3: Run existing tests**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -c Debug --filter FullyQualifiedName~FastPathOptimizerTests`
Expected: all 5 existing tests pass — the behavior change in the production service has not been made yet, so the existing happy-path tests still apply.

- [ ] **Step 4: Commit (deferred to Task 8 with the rest of the test additions)**

No commit yet.

---

## Task 7: Refactor FastPathOptimizerService to support Insert-Anyway

**Files:**
- Modify: `src/Pia.Wpf/Services/FastPathOptimizerService.cs`

- [ ] **Step 1: Restructure RunAsync into entry guard + extracted pipeline**

Replace the current `RunAsync` body with:

```csharp
public async Task RunAsync()
{
    if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
    {
        _logger.LogDebug("Fast-path optimize ignored because another run is already active");
        return;
    }

    try
    {
        _windowTrackingService.TrackWindowAtCursor();
        var captured = await _selectedTextService.CaptureAsync();

        var handle = await _windowManagerService.ShowOptimizeAndGetViewModelAsync();
        handle.PrepareForFastPath();

        if (string.IsNullOrWhiteSpace(captured))
        {
            handle.ShowFastPathSnackbar("Msg_FastPath_NoContent");
            return;
        }

        if (!string.IsNullOrEmpty(handle.InputText))
        {
            handle.ShowFastPathInsertAnywaySnackbar(captured, () => RunInsertAnywayContinuationAsync(captured));
            return;
        }

        await RunFastPathWithInputAsync(handle, captured);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Fast-path optimize failed");
    }
    finally
    {
        Interlocked.Exchange(ref _isRunning, 0);
    }
}
```

- [ ] **Step 2: Extract the optimize→accept pipeline**

Add private method:

```csharp
private async Task RunFastPathWithInputAsync(IOptimizeFastPathHandle handle, string captured)
{
    using var dialogCts = new CancellationTokenSource();
    Task? dialogTask = null;

    try
    {
        handle.InputText = captured;
        handle.IsOptimizing = true;
        dialogTask = handle.ShowOptimizingDialogAsync(dialogCts.Token);

        var optimized = await handle.RunFastPathOptimizeAsync();
        if (!optimized || !handle.IsComparisonView)
            return;

        var settings = await _settingsService.GetSettingsAsync();
        if (RequiresTrackedTarget(settings.DefaultOutputAction) && !_windowTrackingService.HasTrackedWindow)
        {
            handle.ShowFastPathSnackbar("Msg_FastPath_NoTargetWindow");
            return;
        }

        var accepted = await handle.RunFastPathAcceptAsync();
        if (accepted && !handle.IsComparisonView && string.IsNullOrWhiteSpace(handle.InputText) && string.IsNullOrWhiteSpace(handle.OptimizedText))
            _windowManagerService.HideWindow(WindowMode.Optimize);
    }
    finally
    {
        handle.IsOptimizing = false;
        dialogCts.Cancel();
        if (dialogTask is not null)
        {
            try { await dialogTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "Fast-path optimizing dialog ended with an error"); }
        }
    }
}
```

- [ ] **Step 3: Add the Insert-Anyway continuation**

Add private method:

```csharp
private async Task RunInsertAnywayContinuationAsync(string captured)
{
    if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
    {
        _logger.LogDebug("Fast-path Insert-anyway ignored because another run is already active");
        return;
    }

    try
    {
        // Re-acquire the handle: the user may have hidden/closed/reopened the window
        // between the snackbar and the click. Calling Show... again ensures a live VM.
        var handle = await _windowManagerService.ShowOptimizeAndGetViewModelAsync();
        handle.PrepareForFastPath();
        await RunFastPathWithInputAsync(handle, captured);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Fast-path Insert-anyway continuation failed");
    }
    finally
    {
        Interlocked.Exchange(ref _isRunning, 0);
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 5: Commit (deferred — combined with Task 8 commit)**

No commit yet.

---

## Task 8: Add tests for the new behavior

**Files:**
- Modify: `tests/Pia.Wpf.Tests/Services/FastPathOptimizerTests.cs`

- [ ] **Step 1: Test — non-empty input shows Insert-Anyway snackbar instead of running pipeline**

Add to the test class:

```csharp
[Fact]
public async Task RunAsync_WhenInputAlreadyHasText_ShowsInsertAnywaySnackbarAndDoesNotOptimize()
{
    var handle = new FakeFastPathHandle { InputText = "existing draft" };
    _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
    _selectedText.CaptureAsync().Returns("captured selection");

    await CreateSut().RunAsync();

    Assert.Equal(1, handle.InsertAnywaySnackbarShownCount);
    Assert.Equal("captured selection", handle.InsertAnywayCapturedText);
    Assert.NotNull(handle.InsertAnywayCallback);
    Assert.Equal(0, handle.OptimizeCalls);
    Assert.Equal(0, handle.AcceptCalls);
    Assert.Equal("existing draft", handle.InputText); // input untouched until user clicks
    _windowManager.DidNotReceive().HideWindow(WindowMode.Optimize);
}
```

- [ ] **Step 2: Test — Insert-Anyway callback runs the pipeline**

```csharp
[Fact]
public async Task InsertAnywayCallback_ReplacesInputAndRunsPipeline()
{
    var handle = new FakeFastPathHandle { InputText = "existing draft", OptimizeResult = true, AcceptResult = true };
    _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
    _selectedText.CaptureAsync().Returns("captured selection");
    _settings.GetSettingsAsync().Returns(new AppSettings { DefaultOutputAction = OutputAction.CopyToClipboard });

    var sut = CreateSut();
    await sut.RunAsync();
    Assert.NotNull(handle.InsertAnywayCallback);

    await handle.InsertAnywayCallback!();

    Assert.Equal("captured selection", handle.CapturedInput);
    Assert.Equal(1, handle.OptimizeCalls);
    Assert.Equal(1, handle.AcceptCalls);
    _windowManager.Received(2).ShowOptimizeAndGetViewModelAsync(); // initial + continuation
    _windowManager.Received(1).HideWindow(WindowMode.Optimize);
}
```

- [ ] **Step 3: Test — Insert-Anyway callback respects re-entrancy guard**

```csharp
[Fact]
public async Task InsertAnywayCallback_WhenAnotherRunActive_IsNoOp()
{
    var handle = new FakeFastPathHandle { InputText = "existing draft", OptimizeResult = true, AcceptResult = true };
    _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
    _selectedText.CaptureAsync().Returns("captured selection");
    _settings.GetSettingsAsync().Returns(new AppSettings { DefaultOutputAction = OutputAction.CopyToClipboard });

    var sut = CreateSut();
    await sut.RunAsync();
    Assert.NotNull(handle.InsertAnywayCallback);

    // Start a second RunAsync that blocks inside CaptureAsync — keeps _isRunning held.
    var captureGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var captureCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    _selectedText.CaptureAsync().Returns(_ =>
    {
        captureCalled.TrySetResult(true);
        return captureGate.Task;
    });

    var blocking = sut.RunAsync();
    await captureCalled.Task;

    // While the blocking run holds the guard, the Insert-Anyway click should be a no-op.
    var beforeOptimizeCalls = handle.OptimizeCalls;
    var beforeAcceptCalls = handle.AcceptCalls;
    await handle.InsertAnywayCallback!();

    Assert.Equal(beforeOptimizeCalls, handle.OptimizeCalls);
    Assert.Equal(beforeAcceptCalls, handle.AcceptCalls);

    captureGate.SetResult(null);
    await blocking;
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -c Debug --filter FullyQualifiedName~FastPathOptimizerTests`
Expected: all 8 tests pass (5 original + 3 new).

- [ ] **Step 5: Run full test suite**

Run: `dotnet test`
Expected: all tests pass.

- [ ] **Step 6: Build Release with no new warnings**

Run: `dotnet build -c Release`
Expected: build succeeds with no NEW warnings (existing warning level preserved).

- [ ] **Step 7: Commit Tasks 4–8 together**

```bash
git add src/Pia.Wpf/Services/FastPathOptimizerService.cs tests/Pia.Wpf.Tests/Services/FastPathOptimizerTests.cs
git commit -m "Refactor fast-path service to support Insert-Anyway continuation"
```

---

## Manual Smoke Test

1. Start Pia, focus an external app with a non-empty text selection.
2. **Regular Optimize hotkey, input empty:** Press the Optimize hotkey. Window opens with the captured text in the input. (Unchanged behavior — sanity check.)
3. **Regular Optimize hotkey, input non-empty:** Type something into the Optimize input first. Switch to another app, select different text, press the Optimize hotkey. The Optimize window opens, the input field still shows the original draft, and a snackbar appears with "Selected text not pasted because the input already contains text." plus a clickable **Insert anyway** link. Click the link — the input is replaced with the captured selection. The user clicks Optimize manually.
4. **Fast Path hotkey, input empty:** Press the Fast Path hotkey while a selection exists in the foreground app. Verify it runs end-to-end as before (window shows briefly, captured text is optimized, default output action runs, window hides). (Unchanged behavior — sanity check.)
5. **Fast Path hotkey, input non-empty (the new path):** Type something into the Optimize input first. Switch to another app, select different text, press the Fast Path hotkey. The Optimize window opens with the original draft preserved, optimizing dialog NOT shown, and a snackbar appears with the **Insert anyway** link. Click the link — the input is replaced with the captured selection, the optimizing dialog appears, optimization runs, default output action runs, window hides on success.
6. **Fast Path hotkey, input non-empty, no click:** Repeat step 5 but let the snackbar time out (8 seconds). The window stays open with the original draft. No optimization runs.
7. **Fast Path hotkey, input non-empty, click during another run:** Trigger Fast Path with non-empty input (snackbar appears with link). Before clicking, press Fast Path hotkey again to start a fresh run. Then click the OLD Insert-Anyway link — verify the click is a no-op (no double-pipeline) and the in-flight run continues normally.

---

## Hard constraints

- Do **not** change `ApplyCapturedSelection` in `AssistantViewModel` or `ResearchViewModel`. Out of scope.
- Do **not** change the regular Optimize hotkey's behavior when input is empty.
- Do **not** auto-clear `InputText` in `PrepareForFastPath` — the whole feature depends on this.
- Do **not** widen the snackbar timeout for the existing single-message snackbars (`Msg_FastPath_NoContent`, `Msg_FastPath_NoTargetWindow`). The 8s timeout applies only to action-bearing snackbars.
- Do **not** introduce a new DI registration. `SnackbarActionHelper` is a static helper.
- Do **not** skip the WPF-UI API verification step. If the API differs from the assumed shape, follow the documented fallback.
- Follow existing code style: 4-space indent, `[ObservableProperty]`/`[RelayCommand]`, namespaces use `Pia` (not `Pia.Wpf`), privacy-first logging.

---

## Definition of Done

- All listed files modified or created.
- `dotnet build -c Release` succeeds with no new warnings.
- `dotnet test` passes including the 3 new tests.
- All 7 manual smoke tests pass.
- Three logical commits on branch `feature/optimize_fast_path` (resx → snackbar helper → behavior+tests).

## Deviations

- WPF-UI 4.2.0 exposes `ISnackbarService.GetSnackbarPresenter()` instead of the planned `ISnackbarService.SnackbarPresenter` property. `SnackbarActionHelper` uses the verified method while keeping the planned `Snackbar(SnackbarPresenter)` and `Show()` path.
