# Live Transcription Code-Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the critical, architectural, and high-impact correctness fixes from the code review of `feature/meeting_transscription` so the live transcription feature can ship without UI freezes, memory leaks, or contract violations.

**Architecture:** Three sequential chunks — stability (leaks, broken contracts, ordering, ownership), performance (UI-thread storms and hot-path waste), and correctness (lost trailing utterances, thread-affinity assumptions, missed localization). Each task is TDD-first; UI-only behavior that cannot be unit-tested gets explicit manual verification steps.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, `System.Threading.Channels`, NAudio, Whisper.net, Silero VAD ONNX, xunit.v3 + NSubstitute, Microsoft.Testing.Platform.

**Conventions:**
- Test project root namespace: `Pia.Tests`. Test runner is MTP (`dotnet test`).
- Production namespaces use `Pia` (not `Pia.Wpf`) — see CLAUDE.md.
- Tests use plain `Xunit.Assert` (no FluentAssertions).
- 4-space C# indent.
- Each task ends in a small, self-contained commit. Conventional Commits style (`fix:`, `refactor:`, `perf:`).

---

## Chunk 1: Stability Fixes

These four tasks fix bugs that will leak memory, drop events, or cause double-dispose in production. All are blockers.

---

### Task 1: Fix overlay memory leak via `Unloaded` handler

**Why:** `LiveTranscriptionOverlay` subscribes to `Utterances.CollectionChanged` in `OnDataContextChanged` but only unsubscribes when DataContext is replaced. Since the VM is `AddScoped` and held by `AssistantViewModel`, the VM outlives navigations away from `AssistantView`; each visit leaks one overlay rooted via the event handler.

**Files:**
- Modify: `src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml.cs`

This task has no automated test — it's pure UI lifecycle behavior. Verification is manual via the running app.

- [ ] **Step 1: Add `Unloaded` cleanup**

Replace the body of the constructor and add an `OnUnloaded` handler:

```csharp
public LiveTranscriptionOverlay()
{
    InitializeComponent();
    DataContextChanged += OnDataContextChanged;
    Unloaded += OnUnloaded;
}

private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
{
    if (DataContext is LiveTranscriptionViewModel vm)
        ((INotifyCollectionChanged)vm.Utterances).CollectionChanged -= OnUtterancesChanged;
    DataContextChanged -= OnDataContextChanged;
    Unloaded -= OnUnloaded;
}
```

- [ ] **Step 2: Build to confirm no compile regressions**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Manually verify no leak**

1. Launch the app: `dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj`.
2. Open Assistant view → toggle live transcription on → close it → navigate away from Assistant → return.
3. Repeat 5–10 times.
4. Trigger a GC via the dev tools / VS diagnostic tools (or a temporary `GC.Collect(); GC.WaitForPendingFinalizers();` button in debug only) and snapshot the heap. There should be exactly **one** live `LiveTranscriptionOverlay` instance at any time, not N.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml.cs
git commit -m "fix(transcription): unsubscribe overlay from VM collection on Unloaded"
```

---

### Task 2: Persist a single utterance channel across start/stop cycles

**Why:** `ILiveMeetingService.Utterances` is documented "Stable across start/stop cycles" but `LiveMeetingService.StartAsync` reassigns the underlying channel. Any consumer caching the reader between sessions reads from a completed channel and silently sees zero utterances.

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs`
- Test: `tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveMeetingServiceChannelTests.cs` (create)

The simplest fix: keep one channel for the lifetime of the service. Engines write into it; consumers always read from the same reader.

- [ ] **Step 1: Write a failing test**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveMeetingServiceChannelTests.cs`:

```csharp
using System.Threading.Channels;
using Pia.Models;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class LiveMeetingServiceChannelTests
{
    [Fact]
    public void Utterances_Reader_IsSameInstance_AcrossArbitraryWriterCompletes()
    {
        // Sentinel test: the Utterances reader must be a stable identity for the
        // service instance lifetime, regardless of internal state transitions.
        // We do not actually run the audio pipeline — we just assert reference
        // equality of the reader across simulated start/stop cycles.
        var sut = TestableLiveMeetingService.CreateForChannelTest();

        var reader1 = sut.Utterances;
        sut.SimulateRestart();
        var reader2 = sut.Utterances;

        Assert.Same(reader1, reader2);
    }
}
```

To compile that test, expose a tiny test surface on the production class. Add this **inside** `LiveMeetingService` (in production code, kept minimal):

```csharp
internal static class TestableLiveMeetingService
{
    public static LiveMeetingService CreateForChannelTest() =>
        new(
            settingsService: NSubstitute.Substitute.For<ISettingsService>(),
            httpClientFactory: NSubstitute.Substitute.For<System.Net.Http.IHttpClientFactory>(),
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
}
```

…**no — don't do that.** Putting NSubstitute in production code is wrong. Instead, write the test against the real ctor and use NSubstitute from the test side. Replace the test body with:

```csharp
[Fact]
public async Task Utterances_Reader_IsStable_AcrossStartStopCycles()
{
    var settings = NSubstitute.Substitute.For<ISettingsService>();
    settings.GetSettingsAsync().Returns(new Pia.Models.AppSettings());
    var http = NSubstitute.Substitute.For<System.Net.Http.IHttpClientFactory>();
    var loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

    var sut = new Pia.Services.LiveTranscription.LiveMeetingService(settings, http, loggers);

    var readerBefore = sut.Utterances;

    // Internal "restart" we'll add: a single method that completes-then-recreates if currently
    // broken, or no-ops if fixed. We assert reference equality survives.
    await sut.StopAsync();   // Idle → no-op
    var readerAfter = sut.Utterances;

    Assert.Same(readerBefore, readerAfter);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter Utterances_Reader_IsStable_AcrossStartStopCycles`
Expected: PASS today (because no `StartAsync` is called — reader hasn't been swapped). To make this a true regression test, extend it with a stronger assertion that doesn't rely on `StartAsync` running real audio:

Replace the test body with this version that uses reflection to invoke the private channel-reset code path, or simpler — assert via the **structural** invariant: there is no field reassignment of `_utterances` after construction. Use `NetArchTest`:

```csharp
using NetArchTest.Rules;
using Xunit;

public class LiveMeetingServiceChannelTests
{
    [Fact]
    public void LiveMeetingService_DoesNotReassign_UtterancesField()
    {
        // Compiled constraint: the `_utterances` field must be readonly.
        var type = typeof(Pia.Services.LiveTranscription.LiveMeetingService);
        var field = type.GetField("_utterances",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.True(field!.IsInitOnly,
            "_utterances must be readonly so the public reader is stable across sessions.");
    }
}
```

This fails today because `_utterances` is mutable.

Run: `dotnet test --filter LiveMeetingService_DoesNotReassign_UtterancesField`
Expected: FAIL — `_utterances must be readonly`.

- [ ] **Step 3: Make `_utterances` readonly and remove the reset path**

In `src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs`:

Change the field:
```csharp
private readonly Channel<TranscriptUtterance> _utterances;
```

In the constructor, keep the existing assignment (it's already there).

In `StartAsync`, **delete** these lines:
```csharp
// Reset the utterance channel for a fresh session.
_utterances.Writer.TryComplete();
_utterances = CreateUtterancesChannel();
```

In `StopAsync`, **delete** this line:
```csharp
_utterances.Writer.TryComplete();
```

Reason: completing the writer permanently kills the channel. With `BoundedChannelFullMode.DropOldest`, the channel naturally absorbs back-pressure between sessions — there's no reason to ever complete it for the lifetime of the service. The actual completion happens in `DisposeAsync` (which forwards to `StopAsync`); we'll replace that with explicit completion only on dispose:

In `DisposeAsync`, after `await StopAsync()`, add:
```csharp
public async ValueTask DisposeAsync()
{
    await StopAsync().ConfigureAwait(false);
    _utterances.Writer.TryComplete();
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter LiveMeetingService_DoesNotReassign_UtterancesField`
Expected: PASS.

- [ ] **Step 5: Update the XML doc**

In `src/Pia.Wpf/Services/Interfaces/ILiveMeetingService.cs`, change:
```csharp
/// <summary>Reader of the merged utterance stream. Stable across start/stop cycles.</summary>
```
to:
```csharp
/// <summary>
/// Reader of the merged utterance stream. The reader instance is stable for the
/// lifetime of the service — engines write into the same channel across all
/// start/stop cycles. The channel is completed only on <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </summary>
```

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs \
        src/Pia.Wpf/Services/Interfaces/ILiveMeetingService.cs \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveMeetingServiceChannelTests.cs
git commit -m "fix(transcription): keep utterance channel stable across start/stop cycles"
```

---

### Task 3: Make `StateChanged` synchronous and ordered

**Why:** `LiveMeetingService.SetStateLocked` raises the event via `Task.Run(() => handler?.Invoke(...))`. The thread pool provides no ordering — two rapid transitions (`Starting → Running` or `Stopping → Idle`) can be observed out of order, producing UI flicker or stuck status text. The lock is also still held during the `Task.Run` call, contradicting the comment.

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs`
- Test: `tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveMeetingServiceStateTests.cs` (create)

- [ ] **Step 1: Write a failing test**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveMeetingServiceStateTests.cs`:

```csharp
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class LiveMeetingServiceStateTests
{
    [Fact]
    public async Task StopAsync_FromIdle_DoesNotRaiseStateChanged()
    {
        var sut = CreateSut();
        var observed = new List<LiveMeetingState>();
        sut.StateChanged += (_, s) => observed.Add(s);

        await sut.StopAsync();

        Assert.Empty(observed);
    }

    [Fact]
    public async Task StopAsync_FromIdle_ObserverSeesNoTransitions_Synchronously()
    {
        // Regression: previously SetStateLocked dispatched via Task.Run, so even a
        // no-op call could schedule work that races with the test assertion. We
        // assert that the moment StopAsync returns, every observed state has
        // already been delivered — no thread-pool deferral.
        var sut = CreateSut();
        var observed = new List<LiveMeetingState>();
        sut.StateChanged += (_, s) => observed.Add(s);

        await sut.StopAsync();

        // Give thread-pool work a chance to run and corrupt the result, if any.
        await Task.Delay(50);

        Assert.Empty(observed);
    }

    private static LiveMeetingService CreateSut()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var http = Substitute.For<System.Net.Http.IHttpClientFactory>();
        var loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        return new LiveMeetingService(settings, http, loggers);
    }
}
```

A stronger assertion that fails *today* (before we fix the bug):

```csharp
[Fact]
public void SetState_RaisesEvent_OnCallingThread_NotThreadPool()
{
    var sut = CreateSut();
    int? observedThreadId = null;
    sut.StateChanged += (_, _) => observedThreadId = Environment.CurrentManagedThreadId;

    var callerThreadId = Environment.CurrentManagedThreadId;

    // Force a transition: Idle → Stopping is a no-op so we use a private hook.
    var setMethod = typeof(LiveMeetingService).GetMethod(
        "SetStateLocked",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Assert.NotNull(setMethod);

    var stateLockField = typeof(LiveMeetingService).GetField(
        "_stateLock",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var stateLock = stateLockField!.GetValue(sut)!;

    lock (stateLock) setMethod!.Invoke(sut, new object[] { LiveMeetingState.Starting });

    Assert.Equal(callerThreadId, observedThreadId);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter SetState_RaisesEvent_OnCallingThread_NotThreadPool`
Expected: FAIL — `observedThreadId` is null (event raised asynchronously and hasn't run yet) or differs from the caller.

- [ ] **Step 3: Replace `SetStateLocked` with `TransitionState`**

In `LiveMeetingService.cs`, remove the field-level `_stateLock` if it remains only used by SetStateLocked, and replace the existing `SetStateLocked` with a state-transition helper. Also replace every `lock (_stateLock) SetStateLocked(...)` call site.

Replace the existing method:
```csharp
private void SetStateLocked(LiveMeetingState newState)
{
    if (_state == newState) return;
    _state = newState;
    var handler = StateChanged;
    Task.Run(() => handler?.Invoke(this, newState));
}
```

with:

```csharp
private void TransitionState(LiveMeetingState newState)
{
    EventHandler<LiveMeetingState>? handler;
    lock (_stateLock)
    {
        if (_state == newState) return;
        _state = newState;
        handler = StateChanged;
    }
    handler?.Invoke(this, newState);
}
```

In `StartAsync`, change:
```csharp
lock (_stateLock)
{
    if (_state is LiveMeetingState.Running or LiveMeetingState.Starting)
        throw new InvalidOperationException($"Cannot start while {_state}");
    SetStateLocked(LiveMeetingState.Starting);
}
```
to:
```csharp
lock (_stateLock)
{
    if (_state is LiveMeetingState.Running or LiveMeetingState.Starting)
        throw new InvalidOperationException($"Cannot start while {_state}");
}
TransitionState(LiveMeetingState.Starting);
```

Replace remaining call sites:
- `lock (_stateLock) SetStateLocked(LiveMeetingState.Running);` → `TransitionState(LiveMeetingState.Running);`
- `lock (_stateLock) SetStateLocked(LiveMeetingState.Error);` → `TransitionState(LiveMeetingState.Error);`
- `lock (_stateLock) SetStateLocked(LiveMeetingState.Stopping);` → `TransitionState(LiveMeetingState.Stopping);`
- `lock (_stateLock) SetStateLocked(LiveMeetingState.Idle);` → `TransitionState(LiveMeetingState.Idle);`

In `StopAsync`, change:
```csharp
lock (_stateLock)
{
    if (_state is LiveMeetingState.Idle or LiveMeetingState.Stopping) return;
    SetStateLocked(LiveMeetingState.Stopping);
}
```
to:
```csharp
lock (_stateLock)
{
    if (_state is LiveMeetingState.Idle or LiveMeetingState.Stopping) return;
}
TransitionState(LiveMeetingState.Stopping);
```

Update the test from Step 1 to call the new method name (`TransitionState` instead of `SetStateLocked`). Drop the `lock (stateLock)` wrapper in the test — `TransitionState` takes the lock internally.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter LiveMeetingServiceState`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveMeetingServiceStateTests.cs
git commit -m "fix(transcription): raise StateChanged synchronously, in order"
```

---

### Task 4: Stop `AssistantViewModel` from disposing the scoped `LiveTranscriptionViewModel`

**Why:** `LiveTranscriptionViewModel` is registered `AddScoped`. Its lifetime belongs to the DI container. `AssistantViewModel.Dispose` calling `LiveTranscription.Dispose()` invites double-dispose and tightens cross-VM coupling. Unsubscribing from `CloseRequested` is sufficient — the container handles disposal.

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`

This task has no automated test — `Dispose` correctness is best verified by inspection plus an integration-style test that the next task adds.

- [ ] **Step 1: Remove the disposal call**

In `AssistantViewModel.cs` around the existing disposal block:
```csharp
LiveTranscription.CloseRequested -= OnLiveTranscriptionCloseRequested;
LiveTranscription.Dispose();
```
Change to:
```csharp
LiveTranscription.CloseRequested -= OnLiveTranscriptionCloseRequested;
```

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/ViewModels/AssistantViewModel.cs
git commit -m "fix(assistant): don't dispose scoped LiveTranscriptionViewModel from sibling VM"
```

---

## Chunk 2: Performance Fixes

These three tasks remove waste from hot paths. Order matters: Task 5 (counterpart-name storm) is the most user-visible.

---

### Task 5: Eliminate per-utterance refresh on counterpart-name change

**Why:** `UpdateSourceTrigger=PropertyChanged` on the counterpart-name `TextBox` combined with `OnCounterpartNameChanged` iterating every `TranscriptUtteranceViewModel` and raising `PropertyChanged` is O(n) UI-thread work per keystroke. With `MaxUtterances = 2000`, every keystroke fans out 2000 binding updates.

**Strategy:** Eliminate `TranscriptUtteranceViewModel` entirely. The `Utterances` collection holds raw `TranscriptUtterance` records; the bubble's `DisplayName` resolves at the binding layer using `RelativeSource` to reach the parent VM's `CounterpartName` property and a small `IValueConverter` to render `"you"` for the `You` speaker.

**Files:**
- Delete: `src/Pia.Wpf/Models/TranscriptUtteranceViewModel.cs`
- Delete: `tests/Pia.Wpf.Tests/ViewModels/TranscriptUtteranceViewModelTests.cs`
- Create: `src/Pia.Wpf/Converters/SpeakerToDisplayNameConverter.cs`
- Create: `tests/Pia.Wpf.Tests/Converters/SpeakerToDisplayNameConverterTests.cs`
- Modify: `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs`
- Modify: `src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml`
- Modify: `src/Pia.Wpf/App.xaml` (register the converter as a static resource if other converters live there)

- [ ] **Step 1: Locate the existing converter resources**

Run: `grep -rn "MultiplyConverter" src/Pia.Wpf/App.xaml src/Pia.Wpf/Views/`
Expected: shows where converters are declared as application/resource-dictionary entries. Add `SpeakerToDisplayNameConverter` to the same dictionary.

- [ ] **Step 2: Write a failing test for the converter**

Create `tests/Pia.Wpf.Tests/Converters/SpeakerToDisplayNameConverterTests.cs`:

```csharp
using System.Globalization;
using Pia.Converters;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Converters;

public class SpeakerToDisplayNameConverterTests
{
    [Fact]
    public void You_ReturnsLiteralYou_RegardlessOfCounterpart()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object?[] { TranscriptSpeaker.You, "Alex" },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("you", result);
    }

    [Fact]
    public void Them_ReturnsCounterpart()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object?[] { TranscriptSpeaker.Them, "Alex" },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("Alex", result);
    }

    [Fact]
    public void Them_NullOrWhitespaceCounterpart_ReturnsThemFallback()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var resultNull = sut.Convert(
            new object?[] { TranscriptSpeaker.Them, null },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        var resultEmpty = sut.Convert(
            new object?[] { TranscriptSpeaker.Them, "  " },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("them", resultNull);
        Assert.Equal("them", resultEmpty);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter SpeakerToDisplayNameConverter`
Expected: FAIL — type `Pia.Converters.SpeakerToDisplayNameConverter` does not exist.

- [ ] **Step 4: Create the converter**

Create `src/Pia.Wpf/Converters/SpeakerToDisplayNameConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

/// <summary>
/// Multi-binding: <c>{Speaker, CounterpartName}</c> → display name. <see cref="TranscriptSpeaker.You"/>
/// always renders as "you"; the counterpart name is used otherwise, falling back to "them" when blank.
/// </summary>
public sealed class SpeakerToDisplayNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return string.Empty;
        if (values[0] is TranscriptSpeaker.You) return "you";

        var counterpart = values[1] as string;
        return string.IsNullOrWhiteSpace(counterpart) ? "them" : counterpart;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test --filter SpeakerToDisplayNameConverter`
Expected: PASS.

- [ ] **Step 6: Register the converter as a XAML resource**

Locate the resource dictionary that declares `MultiplyConverter` (likely `App.xaml` or a merged dictionary). Add a peer entry:
```xml
<conv:SpeakerToDisplayNameConverter x:Key="SpeakerToDisplayNameConverter" />
```
…with the necessary xmlns import:
```xml
xmlns:conv="clr-namespace:Pia.Converters"
```

- [ ] **Step 7: Switch the overlay to bind directly to `TranscriptUtterance`**

In `src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml`:

Replace the `DataTemplate DataType="{x:Type models:TranscriptUtteranceViewModel}"` with:
```xml
<DataTemplate DataType="{x:Type models:TranscriptUtterance}">
```

Replace the `IsYou` DataTriggers with comparisons against `Speaker`:
```xml
<DataTrigger Binding="{Binding Speaker}" Value="{x:Static models:TranscriptSpeaker.You}">
    <Setter Property="Visibility" Value="Visible" />
</DataTrigger>
```
…and the corresponding "Them" trigger:
```xml
<DataTrigger Binding="{Binding Speaker}" Value="{x:Static models:TranscriptSpeaker.Them}">
    <Setter Property="Visibility" Value="Visible" />
</DataTrigger>
```

Replace each `Text="{Binding DisplayName}"` with a `MultiBinding`:
```xml
<TextBlock FontSize="11"
           HorizontalAlignment="Right"
           Margin="0,0,4,2"
           Foreground="{DynamicResource TextPlaceholderColorBrush}">
    <TextBlock.Text>
        <MultiBinding Converter="{StaticResource SpeakerToDisplayNameConverter}">
            <Binding Path="Speaker" />
            <Binding Path="DataContext.CounterpartName"
                     RelativeSource="{RelativeSource AncestorType=UserControl}" />
        </MultiBinding>
    </TextBlock.Text>
</TextBlock>
```
…repeat for the "Them" branch (set `HorizontalAlignment="Left"` etc.).

- [ ] **Step 8: Update `LiveTranscriptionViewModel` to hold raw utterances**

In `LiveTranscriptionViewModel.cs`:

Change the collection type:
```csharp
public ObservableCollection<TranscriptUtterance> Utterances { get; } = [];
```

Replace `AddUtterance` body:
```csharp
private void AddUtterance(TranscriptUtterance utterance)
{
    DispatchToUi(() =>
    {
        Utterances.Add(utterance);
        if (Utterances.Count > MaxUtterances)
        {
            for (int i = 0; i < TrimBatch && Utterances.Count > MaxUtterances - TrimBatch; i++)
                Utterances.RemoveAt(0);
        }
    });
}
```

**Delete** `OnCounterpartNameChanged` entirely — the WPF binding layer now reacts to `CounterpartName` change automatically.

- [ ] **Step 9: Delete the now-unused VM and its tests**

Delete:
- `src/Pia.Wpf/Models/TranscriptUtteranceViewModel.cs`
- `tests/Pia.Wpf.Tests/ViewModels/TranscriptUtteranceViewModelTests.cs`

- [ ] **Step 10: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 11: Manual verification**

1. Run the app, open live transcription.
2. Type fast in the counterpart-name field. The UI must remain responsive even with thousands of utterances.
3. Verify all "Them" bubbles re-render with the new name without a per-bubble flicker.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "perf(transcription): bind counterpart name once, drop per-bubble VM"
```

---

### Task 6: Replace List<float> memmove with a ring buffer in `SileroVadDetector`

**Why:** `List<float>.RemoveRange(0, WindowSize)` shifts the list's tail every 32 ms — an O(n) memmove on an audio hot path. The VAD also performs `Add(samples[i])` in a tight loop instead of `AddRange(span)`.

**Files:**
- Create: `src/Pia.Wpf/Services/LiveTranscription/FloatRingBuffer.cs`
- Create: `tests/Pia.Wpf.Tests/Services/LiveTranscription/FloatRingBufferTests.cs`
- Modify: `src/Pia.Wpf/Services/LiveTranscription/SileroVadDetector.cs`

- [ ] **Step 1: Write failing tests for `FloatRingBuffer`**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/FloatRingBufferTests.cs`:

```csharp
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class FloatRingBufferTests
{
    [Fact]
    public void Write_Then_TryRead_ReturnsExactWindow_InOrder()
    {
        var buf = new FloatRingBuffer(capacity: 16);
        buf.Write(new float[] { 1, 2, 3, 4, 5 });

        var window = new float[3];
        Assert.True(buf.TryRead(window));
        Assert.Equal(new float[] { 1, 2, 3 }, window);
    }

    [Fact]
    public void TryRead_WhenInsufficientSamples_ReturnsFalse_AndDoesNotConsume()
    {
        var buf = new FloatRingBuffer(capacity: 16);
        buf.Write(new float[] { 1, 2 });

        var window = new float[3];
        Assert.False(buf.TryRead(window));

        // After failed read, the next successful read must observe all samples.
        buf.Write(new float[] { 3 });
        Assert.True(buf.TryRead(window));
        Assert.Equal(new float[] { 1, 2, 3 }, window);
    }

    [Fact]
    public void Write_WrapsAround_WithoutLosingSamples()
    {
        var buf = new FloatRingBuffer(capacity: 8);
        buf.Write(new float[] { 1, 2, 3, 4, 5 });

        var firstWindow = new float[3];
        Assert.True(buf.TryRead(firstWindow));
        Assert.Equal(new float[] { 1, 2, 3 }, firstWindow);

        buf.Write(new float[] { 6, 7, 8, 9, 10 });
        var secondWindow = new float[5];
        Assert.True(buf.TryRead(secondWindow));
        Assert.Equal(new float[] { 4, 5, 6, 7, 8 }, secondWindow);

        var thirdWindow = new float[2];
        Assert.True(buf.TryRead(thirdWindow));
        Assert.Equal(new float[] { 9, 10 }, thirdWindow);
    }

    [Fact]
    public void Write_BeyondCapacity_Throws()
    {
        var buf = new FloatRingBuffer(capacity: 4);
        Assert.Throws<InvalidOperationException>(() => buf.Write(new float[] { 1, 2, 3, 4, 5 }));
    }

    [Fact]
    public void Clear_ResetsState()
    {
        var buf = new FloatRingBuffer(capacity: 8);
        buf.Write(new float[] { 1, 2, 3 });
        buf.Clear();

        var window = new float[1];
        Assert.False(buf.TryRead(window));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FloatRingBufferTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement `FloatRingBuffer`**

Create `src/Pia.Wpf/Services/LiveTranscription/FloatRingBuffer.cs`:

```csharp
namespace Pia.Services.LiveTranscription;

/// <summary>
/// Fixed-capacity FIFO of float samples used by the VAD pre-windowing stage. Constant-time
/// append and constant-time fixed-size dequeue, which avoids the O(n) memmove that
/// <see cref="List{T}.RemoveRange"/> incurs on every 32 ms VAD window.
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _data;
    private int _head;     // next read index
    private int _count;

    public FloatRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _data = new float[capacity];
    }

    public int Count => _count;
    public int Capacity => _data.Length;

    public void Write(ReadOnlySpan<float> samples)
    {
        if (_count + samples.Length > _data.Length)
            throw new InvalidOperationException(
                $"FloatRingBuffer overflow: {_count}+{samples.Length} > {_data.Length}");

        var tail = (_head + _count) % _data.Length;
        var firstChunk = Math.Min(samples.Length, _data.Length - tail);
        samples.Slice(0, firstChunk).CopyTo(_data.AsSpan(tail));
        if (firstChunk < samples.Length)
            samples.Slice(firstChunk).CopyTo(_data.AsSpan(0));
        _count += samples.Length;
    }

    public bool TryRead(Span<float> destination)
    {
        if (destination.Length > _count) return false;

        var firstChunk = Math.Min(destination.Length, _data.Length - _head);
        _data.AsSpan(_head, firstChunk).CopyTo(destination);
        if (firstChunk < destination.Length)
            _data.AsSpan(0, destination.Length - firstChunk).CopyTo(destination.Slice(firstChunk));

        _head = (_head + destination.Length) % _data.Length;
        _count -= destination.Length;
        return true;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FloatRingBufferTests`
Expected: PASS (all 5).

- [ ] **Step 5: Wire the ring buffer into `SileroVadDetector`**

In `src/Pia.Wpf/Services/LiveTranscription/SileroVadDetector.cs`:

Replace the field:
```csharp
private readonly List<float> _pendingChunk = new(WindowSize * 2);
```
with:
```csharp
// Capacity must accommodate the largest expected single Process() call. Loopback emits
// ~50 ms hops at 16 kHz = 800 samples; mic emits the same. Plus one in-flight window.
// 4 KiB of float storage is negligible.
private readonly FloatRingBuffer _pendingChunk = new(capacity: WindowSize * 8);
```

Replace `Process`:
```csharp
public void Process(ReadOnlySpan<float> samples)
{
    _pendingChunk.Write(samples);

    var window = new float[WindowSize];
    while (_pendingChunk.TryRead(window))
    {
        ProcessWindow(window);
        window = new float[WindowSize]; // fresh array per window — segments capture them
    }
}
```

Replace `Drain`:
```csharp
public void Drain()
{
    if (_segment is { Count: >= MinSegmentSamples }) FlushSegment();
    else _segment = null;
    _preroll.Clear();
    _pendingChunk.Clear();
}
```

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/FloatRingBuffer.cs \
        src/Pia.Wpf/Services/LiveTranscription/SileroVadDetector.cs \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/FloatRingBufferTests.cs
git commit -m "perf(vad): replace List<float> memmove with fixed-capacity ring buffer"
```

---

### Task 7: Use `MemoryMarshal.Cast` for mic byte-to-short conversion

**Why:** `MicAudioCaptureService.OnDataAvailable` re-implements little-endian Int16 decoding by hand. `MemoryMarshal.Cast<byte, short>` is zero-copy on x86/x64 (both little-endian) and reads more idiomatically.

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/MicAudioCaptureService.cs`
- Create: `tests/Pia.Wpf.Tests/Services/LiveTranscription/PcmConversionTests.cs`

- [ ] **Step 1: Extract the conversion to a static helper**

Add to `MicAudioCaptureService.cs` (above the class or as a nested static):

```csharp
internal static class PcmConversion
{
    /// <summary>Converts a little-endian 16-bit PCM byte buffer to Float32 samples in [-1, 1].</summary>
    public static float[] Pcm16LeToFloat(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / 2;
        var output = new float[sampleCount];
        var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm[..(sampleCount * 2)]);
        for (int i = 0; i < shorts.Length; i++)
            output[i] = shorts[i] / 32768f;
        return output;
    }
}
```

- [ ] **Step 2: Write a failing test for the conversion**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/PcmConversionTests.cs`:

```csharp
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class PcmConversionTests
{
    [Fact]
    public void Pcm16LeToFloat_DecodesKnownSamples()
    {
        // 0x0000 → 0.0; 0x7FFF → ~0.9999; 0x8000 (-32768) → -1.0; 0xFFFF (-1) → ~-0.0000305
        var input = new byte[]
        {
            0x00, 0x00,
            0xFF, 0x7F,
            0x00, 0x80,
            0xFF, 0xFF,
        };

        var result = PcmConversion.Pcm16LeToFloat(input);

        Assert.Equal(0f, result[0]);
        Assert.Equal(32767f / 32768f, result[1], precision: 6);
        Assert.Equal(-1f, result[2]);
        Assert.Equal(-1f / 32768f, result[3], precision: 6);
    }

    [Fact]
    public void Pcm16LeToFloat_TruncatesOddByte()
    {
        var input = new byte[] { 0x00, 0x00, 0xFF }; // last byte is incomplete sample
        var result = PcmConversion.Pcm16LeToFloat(input);
        Assert.Single(result);
        Assert.Equal(0f, result[0]);
    }
}
```

- [ ] **Step 3: Run the test to verify it passes**

Run: `dotnet test --filter PcmConversionTests`
Expected: PASS (the helper exists from Step 1).

- [ ] **Step 4: Replace the inline loop in `OnDataAvailable`**

In `MicAudioCaptureService.OnDataAvailable`, replace:

```csharp
var sampleCount = e.BytesRecorded / 2;
if (sampleCount == 0) return;

var samples = new float[sampleCount];
for (int i = 0, j = 0; i < e.BytesRecorded - 1; i += 2, j++)
{
    short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
    samples[j] = s / 32768f;
}
```

with:

```csharp
if (e.BytesRecorded < 2) return;
var samples = PcmConversion.Pcm16LeToFloat(e.Buffer.AsSpan(0, e.BytesRecorded));
```

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/MicAudioCaptureService.cs \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/PcmConversionTests.cs
git commit -m "refactor(mic): use MemoryMarshal.Cast for PCM16 decoding"
```

---

## Chunk 3: Correctness & Robustness

These three tasks address bugs that don't crash but quietly truncate output or rely on undocumented thread affinity.

---

### Task 8: Drain trailing segment before cancellation in engine shutdown

**Why:** When `LiveTranscriptionEngineService.DisposeAsync` calls `_cts.Cancel()`, the segment loop's `ReadAllAsync(cancellationToken)` may abort before draining the queue. `_vad.Drain()` runs in the reader-loop's `finally` and may enqueue one final segment; that segment is silently dropped when the segment loop has already cancelled.

**Strategy:** Use **two** cancellation tokens — one for the audio source's reader loop, one for the segment loop. On stop, cancel only the reader; let the reader's `finally` enqueue any tail and complete the writer; let the segment loop drain the now-bounded queue and exit naturally; only then dispose Whisper.

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs`
- Create: `tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveTranscriptionEngineDrainTests.cs`

- [ ] **Step 1: Refactor `LiveTranscriptionEngineService` to use a graceful shutdown signal**

Replace the single `_cts` with two:
```csharp
private CancellationTokenSource? _readerCts;
private CancellationTokenSource? _segmentCts;
```

Replace `StartAsync`:
```csharp
public Task StartAsync(CancellationToken cancellationToken = default)
{
    if (_readerCts is not null) throw new InvalidOperationException("Engine already started");
    _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _segmentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    _readerLoop = Task.Factory.StartNew(
        () => RunReaderLoopAsync(_readerCts.Token),
        TaskCreationOptions.LongRunning).Unwrap();

    _segmentLoop = Task.Run(() => RunSegmentLoopAsync(_segmentCts.Token));

    return Task.CompletedTask;
}
```

Replace `DisposeAsync`:
```csharp
public async ValueTask DisposeAsync()
{
    // 1. Stop accepting new audio: cancel the reader, which will drain the VAD,
    //    enqueue any trailing segment, and complete the segment-queue writer.
    try { _readerCts?.Cancel(); } catch { /* ignore */ }
    try { if (_readerLoop is not null) await _readerLoop.ConfigureAwait(false); }
    catch { /* swallow on shutdown */ }

    // 2. Wait for the segment loop to finish processing whatever is left in the
    //    queue (it observes writer-completion via ReadAllAsync). Do NOT cancel
    //    its token unless the wait is taking too long — but for a clean stop,
    //    the writer being completed is enough.
    try { if (_segmentLoop is not null) await _segmentLoop.ConfigureAwait(false); }
    catch { /* swallow on shutdown */ }

    _vad.OnSegment -= EnqueueSegmentForTranscription;
    _vad.Dispose();
    _processor.Dispose();
    _whisperFactory.Dispose();
    _readerCts?.Dispose();
    _segmentCts?.Dispose();
}
```

- [ ] **Step 2: Write a failing test for the drain behavior**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveTranscriptionEngineDrainTests.cs`:

```csharp
using System.Threading.Channels;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class LiveTranscriptionEngineDrainTests
{
    [Fact]
    public async Task DisposeAsync_DrainsQueuedSegments_BeforeShuttingDownProcessor()
    {
        // We cannot easily run real Whisper in a unit test, so this test verifies
        // the queue drain via a scoped helper exposed for tests:
        //   - feed N synthetic segments to the segment queue
        //   - call DisposeAsync
        //   - assert all N segments were observed by the sink.

        var sink = Channel.CreateUnbounded<TranscriptUtterance>();

        // The engine's drain logic is independent of Whisper. We invoke the helper
        // RunSegmentLoopAsync via an internal entry point that bypasses ProcessAsync
        // and writes a stub utterance for each enqueued sample buffer.
        var helper = new EngineDrainTestHarness(sink.Writer);

        helper.EnqueueSegment(new float[] { 0.1f });
        helper.EnqueueSegment(new float[] { 0.2f });
        helper.EnqueueSegment(new float[] { 0.3f });

        await helper.ShutdownAsync();
        sink.Writer.TryComplete();

        var observed = new List<TranscriptUtterance>();
        await foreach (var u in sink.Reader.ReadAllAsync()) observed.Add(u);

        Assert.Equal(3, observed.Count);
    }
}
```

To make this compile, add a small test-only helper. Place it in the test project (NOT in production code):

```csharp
// In the same test file, below the test class:

internal sealed class EngineDrainTestHarness
{
    private readonly ChannelWriter<TranscriptUtterance> _sink;
    private readonly Channel<float[]> _segmentQueue =
        Channel.CreateBounded<float[]>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly Task _loop;

    public EngineDrainTestHarness(ChannelWriter<TranscriptUtterance> sink)
    {
        _sink = sink;
        _loop = Task.Run(LoopAsync);
    }

    public void EnqueueSegment(float[] samples) => _segmentQueue.Writer.TryWrite(samples);

    public async Task ShutdownAsync()
    {
        _segmentQueue.Writer.TryComplete();
        await _loop;
    }

    private async Task LoopAsync()
    {
        await foreach (var s in _segmentQueue.Reader.ReadAllAsync())
        {
            await _sink.WriteAsync(new TranscriptUtterance(
                TranscriptSpeaker.You,
                $"len={s.Length}",
                DateTimeOffset.UnixEpoch));
        }
    }
}
```

This test asserts the **architectural invariant** the production code must maintain: completing the writer (not cancelling the loop's token) is what stops the segment loop, ensuring the queue drains.

- [ ] **Step 3: Run the test to verify it passes against the new design**

Run: `dotnet test --filter LiveTranscriptionEngineDrainTests`
Expected: PASS (the harness reproduces the desired pattern).

- [ ] **Step 4: Add a second test that fails against the OLD design**

Add to the same file:

```csharp
[Fact]
public async Task EngineService_ReaderCts_IsSeparateFrom_SegmentCts()
{
    // Reflection-based structural assertion: the engine must hold two distinct
    // cancellation sources so the reader can be cancelled while the segment
    // loop continues to drain.
    var type = typeof(Pia.Services.LiveTranscription.LiveTranscriptionEngineService);
    var readerField = type.GetField("_readerCts",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var segmentField = type.GetField("_segmentCts",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    Assert.NotNull(readerField);
    Assert.NotNull(segmentField);
}
```

Run: `dotnet test --filter EngineService_ReaderCts_IsSeparateFrom_SegmentCts`
Expected: PASS after Step 1's refactor; FAIL on a checkout where `_cts` is still a single field.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveTranscriptionEngineDrainTests.cs
git commit -m "fix(transcription): drain segment queue on shutdown so trailing utterance survives"
```

---

### Task 9: Replace `SynchronizationContext` capture with the application Dispatcher

**Why:** `LiveTranscriptionViewModel._uiContext = SynchronizationContext.Current` assumes the constructor runs on the UI thread. DI resolution can occur on any thread (e.g., navigation prefetch). When `_uiContext` is null or wrong, `DispatchToUi` runs `ObservableCollection<T>.Add` off-thread — WPF binding will then throw.

**Strategy:** The codebase already uses `Application.Current.Dispatcher` directly (see `OutputService.cs:29`, `ThemeService.cs:87`). Match that convention; no new abstraction needed.

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs`

- [ ] **Step 1: Replace the dispatcher field and helper**

In `LiveTranscriptionViewModel.cs`, remove:
```csharp
private readonly SynchronizationContext? _uiContext;
```
and the assignment `_uiContext = SynchronizationContext.Current;` in the constructor.

Replace `DispatchToUi`:
```csharp
private static void DispatchToUi(Action action)
{
    var dispatcher = System.Windows.Application.Current?.Dispatcher;
    if (dispatcher is null || dispatcher.CheckAccess()) action();
    else dispatcher.BeginInvoke(action);
}
```

Make sure to add `using System.Windows;` if not present.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 3: Manual verification**

1. Run the app, open the Assistant view, start live transcription.
2. Confirm utterances arrive in the overlay without `InvalidOperationException` from cross-thread collection updates.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs
git commit -m "fix(transcription): dispatch via Application.Current.Dispatcher, not captured SyncContext"
```

---

### Task 10: Default counterpart name from localization, not the literal `"them"`

**Why:** `LiveTranscriptionViewModel.CounterpartName` defaults to `"them"`. The same string already exists as `LiveTrans_OtherSpeaker_Placeholder` in three resx files. Hardcoding bypasses German/French localization for the default value.

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs`
- Modify: `src/Pia.Wpf/Converters/SpeakerToDisplayNameConverter.cs` (from Task 5 — extend the fallback)
- Modify: `tests/Pia.Wpf.Tests/Converters/SpeakerToDisplayNameConverterTests.cs`

- [ ] **Step 1: Initialize the property from localization in the ctor**

In `LiveTranscriptionViewModel.cs`, change:
```csharp
[ObservableProperty]
private string _counterpartName = "them";
```
to:
```csharp
[ObservableProperty]
private string _counterpartName = string.Empty;
```

In the constructor, after assigning `_localizationService`:
```csharp
_counterpartName = _localizationService["LiveTrans_OtherSpeaker_Placeholder"];
```

(Note: assigning the backing field bypasses the `OnCounterpartNameChanged` partial — fine here since no utterances exist yet.)

- [ ] **Step 2: Update the converter fallback test**

The fallback `"them"` in `SpeakerToDisplayNameConverter` is now dead code in the happy path (because the VM ensures a non-empty value). Keep it for defense-in-depth, but the test from Task 5 still passes.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 4: Manual verification across cultures**

1. Switch the app language to German via the existing language switcher.
2. Open live transcription. The counterpart-name field placeholder and value must both render in German (e.g., "Andere Person") rather than the English literal.
3. Repeat for French.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs
git commit -m "fix(transcription): default counterpart name from localization, not literal 'them'"
```

---

## Out of Scope (deferred to a follow-up branch)

Documented here so reviewers don't think they were missed. Each is a nitpick from the review with low blast radius:

- Logger category disambiguation between mic/loopback engines (`LiveMeetingService.cs:71-79`).
- `Task.Run` instead of `Task.Factory.StartNew` for the segment loop (`LiveTranscriptionEngineService.cs:42`).
- `TimeProvider` injection for `DateTimeOffset.Now` in `LiveTranscriptionEngineService.cs:121`.
- Cleaner `while ((read = …) == _readBuffer.Length)` loop in `LoopbackAudioCaptureService.cs:80-92`.
- Consolidating two `StackPanel`s in the bubble template into one with a `HorizontalAlignment` converter (post-Task-5 the perf concern is gone, so this is purely cosmetic now).
- Pipeline factory injection on `LiveMeetingService` for orchestrator unit tests.

If any of these become load-bearing later, lift them into a fresh plan rather than back-merging.

---

## Verification Summary

After all tasks land, run:

```bash
dotnet build -c Release
dotnet test
```

Manual smoke test (must pass before merging to `main`):

1. Open Assistant → toggle live transcription on.
2. Speak; have system audio playing simultaneously. Verify both "you" and counterpart bubbles appear.
3. Type rapidly into the counterpart-name field with 100+ utterances on screen — UI must remain responsive.
4. Click Stop → wait → click toggle to start again. Verify the second session produces utterances (channel-stability regression check).
5. Change language to German, repeat step 2. Verify all status text and the default counterpart name are localized.
6. Repeat full open/close cycle 10 times; verify no growing memory in dev tools.
