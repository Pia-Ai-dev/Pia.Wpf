# First-Run Wizard E2EE Setup Step Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a wizard page that explains end-to-end encryption in consumer-friendly language and gives the user the explicit choice to enable it (default on), with inline recovery-code reveal and a soft opt-out confirmation.

**Architecture:** A new `E2EESetupStepViewModel` drives a new `E2EESetupStep` user control inserted as step 2 of the first-run wizard. The step is only shown when the user has logged in to Pia Cloud and the cloud account does not yet have E2EE enabled. It reuses `IDeviceManagementService.BootstrapFirstDeviceAsync` and the same recovery-code confirmation pattern as `RecoveryCodeContentDialog`. Sync start is deferred until the user finishes this step (either via bootstrap-success or explicit opt-out).

**Tech Stack:** WPF (.NET 10), CommunityToolkit.Mvvm, WPF-UI (`ui:` controls), xunit.v3 + `Xunit.Assert` + NSubstitute, NReco.Logging.

**Spec:** `docs/superpowers/specs/2026-05-02-wizard-e2ee-setup-design.md`

---

## File Structure

### New files

- `src/Pia.Wpf/Models/E2EESetupState.cs` — public enum with the 5 states.
- `src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs` — the step view model.
- `src/Pia.Wpf/Views/WizardSteps/E2EESetupStep.xaml` (+ `.xaml.cs`) — the step view.
- `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs` — unit tests for the new view model.
- `tests/Pia.Wpf.Tests/ViewModels/FirstRunWizardViewModelTests.cs` — wizard-level integration tests for the new step's visibility and navigation.

### Modified files

- `src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs` — bump `TotalSteps`, route step 2, defer sync start, add visibility flag.
- `src/Pia.Wpf/Views/FirstRunWizardWindow.xaml` — insert new step view, shift existing dots/views, add conditional dot, hide Skip on step 2 when E2EE step is visible, disable Back post-bootstrap.
- `src/Pia.Wpf/Bootstrapper.cs` — register `E2EESetupStepViewModel`.
- `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` (+ `.de.resx`, `.fr.resx`) — new `Wizard_E2EE_*` keys.
- `src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs` — auto-regenerated; commit alongside the resx changes.

---

## Chunk 1: Foundations — enum, view model, unit tests

### Task 1: Add `E2EESetupState` enum

**Files:**
- Create: `src/Pia.Wpf/Models/E2EESetupState.cs`

- [ ] **Step 1: Create the enum file**

```csharp
namespace Pia.Models;

/// <summary>
/// State of the first-run wizard's E2EE setup step.
/// </summary>
public enum E2EESetupState
{
    /// <summary>Initial state — toggle visible, default on.</summary>
    Choice,

    /// <summary>User toggled off and pressed Next; soft-confirm opt-out is visible.</summary>
    ConfirmingOptOut,

    /// <summary>BootstrapFirstDeviceAsync is in flight.</summary>
    Bootstrapping,

    /// <summary>Bootstrap complete; recovery code is shown and must be confirmed.</summary>
    SavingRecoveryCode,

    /// <summary>Confirmation done; ready to advance the wizard.</summary>
    Completed,
}
```

- [ ] **Step 2: Build to confirm enum compiles**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds with no new warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Models/E2EESetupState.cs
git commit -m "Add E2EESetupState enum for wizard E2EE step"
```

---

### Task 2: Write failing tests for `E2EESetupStepViewModel` initial state and toggle

**Files:**
- Create: `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs`

- [ ] **Step 1: Write the failing test file**

```csharp
namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

public class E2EESetupStepViewModelTests
{
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly ISyncClientService _syncService;
    private readonly IOutputService _outputService;

    public E2EESetupStepViewModelTests()
    {
        _deviceMgmt = Substitute.For<IDeviceManagementService>();
        _deviceKeys = Substitute.For<IDeviceKeyService>();
        _syncService = Substitute.For<ISyncClientService>();
        _outputService = Substitute.For<IOutputService>();

        _deviceKeys.GetFingerprint().Returns("ABCD-1234");
    }

    private E2EESetupStepViewModel CreateSut() => new(
        _deviceMgmt, _deviceKeys, _syncService, _outputService,
        NullLogger<E2EESetupStepViewModel>.Instance);

    [Fact]
    public void InitialState_ShouldBeChoice_WithToggleOn()
    {
        var sut = CreateSut();

        Assert.Equal(E2EESetupState.Choice, sut.State);
        Assert.True(sut.ShouldEnableE2EE);
        Assert.Null(sut.ErrorMessage);
        Assert.False(sut.IsBusy);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~E2EESetupStepViewModelTests"`
Expected: build error — `E2EESetupStepViewModel` does not exist.

- [ ] **Step 3: Stub the view model to make the test compile and pass**

Create `src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class E2EESetupStepViewModel : ObservableObject
{
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly ISyncClientService _syncService;
    private readonly IOutputService _outputService;
    private readonly ILogger<E2EESetupStepViewModel> _logger;

    [ObservableProperty]
    private E2EESetupState _state = E2EESetupState.Choice;

    [ObservableProperty]
    private bool _shouldEnableE2EE = true;

    [ObservableProperty]
    private string? _recoveryCode;

    [ObservableProperty]
    private bool _hasConfirmedRecoveryCode;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public E2EESetupStepViewModel(
        IDeviceManagementService deviceMgmt,
        IDeviceKeyService deviceKeys,
        ISyncClientService syncService,
        IOutputService outputService,
        ILogger<E2EESetupStepViewModel> logger)
    {
        _deviceMgmt = deviceMgmt;
        _deviceKeys = deviceKeys;
        _syncService = syncService;
        _outputService = outputService;
        _logger = logger;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~E2EESetupStepViewModelTests"`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs
git commit -m "Add E2EESetupStepViewModel with initial state"
```

---

### Task 3: Add `ProceedAsync` — toggle on path triggers bootstrap

**Files:**
- Modify: `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs`
- Modify: `src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs`

- [ ] **Step 1: Add the failing test**

Append to `E2EESetupStepViewModelTests.cs`:

```csharp
[Fact]
public async Task Proceed_FromChoice_WithToggleOn_ShouldBootstrapAndEnterRecoveryState()
{
    _deviceMgmt.BootstrapFirstDeviceAsync().Returns("XXXX-XXXX-XXXX-XXXX-XXXX-XXXX");

    var sut = CreateSut();
    Assert.True(sut.ShouldEnableE2EE);

    await sut.ProceedCommand.ExecuteAsync(null);

    await _deviceMgmt.Received(1).BootstrapFirstDeviceAsync();
    Assert.Equal(E2EESetupState.SavingRecoveryCode, sut.State);
    Assert.Equal("XXXX-XXXX-XXXX-XXXX-XXXX-XXXX", sut.RecoveryCode);
    Assert.False(sut.IsBusy);
    Assert.Null(sut.ErrorMessage);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~Proceed_FromChoice_WithToggleOn"`
Expected: build error — no `ProceedCommand`.

- [ ] **Step 3: Add `ProceedCommand` with the toggle-on path**

Add to `E2EESetupStepViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.Input;
```

And inside the class:

```csharp
/// <summary>
/// Raised when the wizard should advance to the next step.
/// The bool indicates whether E2EE was enabled (true) or skipped (false).
/// </summary>
public event Action<bool>? AdvanceRequested;

[RelayCommand]
private async Task ProceedAsync()
{
    switch (State)
    {
        case E2EESetupState.Choice when ShouldEnableE2EE:
            await BootstrapAsync();
            break;
        case E2EESetupState.Choice when !ShouldEnableE2EE:
            State = E2EESetupState.ConfirmingOptOut;
            break;
        case E2EESetupState.ConfirmingOptOut:
            AdvanceRequested?.Invoke(false);
            break;
        case E2EESetupState.SavingRecoveryCode when HasConfirmedRecoveryCode:
            await CompleteEnableAsync();
            break;
    }
}

private async Task BootstrapAsync()
{
    try
    {
        IsBusy = true;
        ErrorMessage = null;
        State = E2EESetupState.Bootstrapping;

        var code = await _deviceMgmt.BootstrapFirstDeviceAsync();
        RecoveryCode = code;
        State = E2EESetupState.SavingRecoveryCode;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "E2EE bootstrap failed during wizard");
        ErrorMessage = ex.Message;
        State = E2EESetupState.Choice;
    }
    finally
    {
        IsBusy = false;
    }
}

private async Task CompleteEnableAsync()
{
    State = E2EESetupState.Completed;
    try
    {
        await _syncService.PerformFirstSyncMigrationAsync();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "First sync after E2EE bootstrap failed in wizard");
    }
    _syncService.StartBackgroundSync();
    AdvanceRequested?.Invoke(true);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~Proceed_FromChoice_WithToggleOn"`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs
git commit -m "Implement bootstrap path in E2EESetupStepViewModel.ProceedAsync"
```

---

### Task 4: Add toggle-off path → opt-out confirmation

**Files:**
- Modify: `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs`

- [ ] **Step 1: Add the failing tests**

```csharp
[Fact]
public async Task Proceed_FromChoice_WithToggleOff_ShouldEnterConfirmingOptOut()
{
    var advanceRaisedWith = (bool?)null;

    var sut = CreateSut();
    sut.ShouldEnableE2EE = false;
    sut.AdvanceRequested += enabled => advanceRaisedWith = enabled;

    await sut.ProceedCommand.ExecuteAsync(null);

    Assert.Equal(E2EESetupState.ConfirmingOptOut, sut.State);
    await _deviceMgmt.DidNotReceive().BootstrapFirstDeviceAsync();
    Assert.Null(advanceRaisedWith);
}

[Fact]
public async Task Proceed_FromConfirmingOptOut_ShouldSignalAdvanceWithoutEnabling()
{
    var advanceRaisedWith = (bool?)null;

    var sut = CreateSut();
    sut.ShouldEnableE2EE = false;
    sut.AdvanceRequested += enabled => advanceRaisedWith = enabled;

    await sut.ProceedCommand.ExecuteAsync(null); // → ConfirmingOptOut
    await sut.ProceedCommand.ExecuteAsync(null); // → AdvanceRequested(false)

    Assert.False(advanceRaisedWith);
    await _deviceMgmt.DidNotReceive().BootstrapFirstDeviceAsync();
    _syncService.DidNotReceive().StartBackgroundSync();
}

[Fact]
public async Task OptOutGoBack_FromConfirmingOptOut_ShouldReturnToChoice()
{
    var sut = CreateSut();
    sut.ShouldEnableE2EE = false;
    await sut.ProceedCommand.ExecuteAsync(null);
    Assert.Equal(E2EESetupState.ConfirmingOptOut, sut.State);

    sut.OptOutGoBackCommand.Execute(null);

    Assert.Equal(E2EESetupState.Choice, sut.State);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~E2EESetupStepViewModelTests"`
Expected: 1 pass, 3 fail (no `OptOutGoBackCommand`; existing tests still work).

- [ ] **Step 3: Add `OptOutGoBackCommand`**

Add to `E2EESetupStepViewModel.cs`:

```csharp
[RelayCommand]
private void OptOutGoBack()
{
    if (State == E2EESetupState.ConfirmingOptOut)
    {
        State = E2EESetupState.Choice;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~E2EESetupStepViewModelTests"`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs
git commit -m "Add opt-out confirmation path in E2EESetupStepViewModel"
```

---

### Task 5: Add recovery-code confirmation gate

**Files:**
- Modify: `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs`

- [ ] **Step 1: Add the failing tests**

```csharp
[Fact]
public async Task Proceed_FromSavingRecoveryCode_WithoutCheckbox_ShouldNotAdvance()
{
    _deviceMgmt.BootstrapFirstDeviceAsync().Returns("CODE");
    var advanceRaised = false;

    var sut = CreateSut();
    sut.AdvanceRequested += _ => advanceRaised = true;
    await sut.ProceedCommand.ExecuteAsync(null); // → SavingRecoveryCode

    Assert.Equal(E2EESetupState.SavingRecoveryCode, sut.State);
    Assert.False(sut.HasConfirmedRecoveryCode);

    await sut.ProceedCommand.ExecuteAsync(null);

    Assert.False(advanceRaised);
    Assert.Equal(E2EESetupState.SavingRecoveryCode, sut.State);
    _syncService.DidNotReceive().StartBackgroundSync();
}

[Fact]
public async Task Proceed_FromSavingRecoveryCode_WithCheckbox_ShouldSignalAdvanceAndStartSync()
{
    _deviceMgmt.BootstrapFirstDeviceAsync().Returns("CODE");
    bool? advanceRaisedWith = null;

    var sut = CreateSut();
    sut.AdvanceRequested += enabled => advanceRaisedWith = enabled;
    await sut.ProceedCommand.ExecuteAsync(null); // → SavingRecoveryCode
    sut.HasConfirmedRecoveryCode = true;

    await sut.ProceedCommand.ExecuteAsync(null);

    Assert.True(advanceRaisedWith);
    Assert.Equal(E2EESetupState.Completed, sut.State);
    await _syncService.Received(1).PerformFirstSyncMigrationAsync();
    _syncService.Received(1).StartBackgroundSync();
}
```

- [ ] **Step 2: Run tests to verify they pass**

The `ProceedAsync` already covers both branches (the checkbox check is in the `when` clause). The first test verifies the no-op case; the second verifies the happy path.

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~E2EESetupStepViewModelTests"`
Expected: 6 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs
git commit -m "Cover recovery-code confirmation gate in E2EESetupStepViewModel"
```

---

### Task 6: Add bootstrap-failure path

**Files:**
- Modify: `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs`

- [ ] **Step 1: Add the failing test**

```csharp
[Fact]
public async Task Bootstrap_Failure_ShouldStayInChoice_WithErrorMessage()
{
    _deviceMgmt.BootstrapFirstDeviceAsync().ThrowsAsync(new InvalidOperationException("server unreachable"));

    var sut = CreateSut();
    await sut.ProceedCommand.ExecuteAsync(null);

    Assert.Equal(E2EESetupState.Choice, sut.State);
    Assert.NotNull(sut.ErrorMessage);
    Assert.Contains("server unreachable", sut.ErrorMessage);
    Assert.False(sut.IsBusy);
    _syncService.DidNotReceive().StartBackgroundSync();
}
```

- [ ] **Step 2: Run test to verify it passes**

The error path is already implemented in `BootstrapAsync`'s catch block.

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~Bootstrap_Failure"`
Expected: 1 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs
git commit -m "Test bootstrap failure stays in Choice with error message"
```

---

### Task 7: Add CopyRecoveryCode command + DeviceFingerprint exposure

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs`
- Modify: `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs`

- [ ] **Step 1: Add failing test**

```csharp
[Fact]
public async Task CopyRecoveryCode_ShouldDelegateToOutputService()
{
    _deviceMgmt.BootstrapFirstDeviceAsync().Returns("MY-CODE");
    var sut = CreateSut();
    await sut.ProceedCommand.ExecuteAsync(null);

    await sut.CopyRecoveryCodeCommand.ExecuteAsync(null);

    await _outputService.Received(1).CopyToClipboardAsync("MY-CODE");
}
```

- [ ] **Step 2: Run test, expect failure**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~CopyRecoveryCode"`
Expected: build error — no `CopyRecoveryCodeCommand`.

- [ ] **Step 3: Add the command**

In `E2EESetupStepViewModel.cs`:

```csharp
[RelayCommand]
private Task CopyRecoveryCodeAsync()
    => RecoveryCode is null ? Task.CompletedTask : _outputService.CopyToClipboardAsync(RecoveryCode);
```

Also expose the device fingerprint for the recovery view:

```csharp
public string DeviceFingerprint => _deviceKeys.GetFingerprint();
```

- [ ] **Step 4: Run test, expect pass**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~E2EESetupStepViewModelTests"`
Expected: 8 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs
git commit -m "Add CopyRecoveryCode command and DeviceFingerprint accessor"
```

---

### Task 8: Add `CanGoBack` flag for wizard Back-button gating

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs`
- Modify: `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs`

- [ ] **Step 1: Add failing test**

```csharp
[Fact]
public void CanGoBack_ShouldBeTrue_InChoice_AndConfirmingOptOut_ShouldBeFalse_AfterBootstrap()
{
    var sut = CreateSut();

    Assert.True(sut.CanGoBack);

    sut.State = E2EESetupState.ConfirmingOptOut;
    Assert.True(sut.CanGoBack);

    sut.State = E2EESetupState.Bootstrapping;
    Assert.False(sut.CanGoBack);

    sut.State = E2EESetupState.SavingRecoveryCode;
    Assert.False(sut.CanGoBack);

    sut.State = E2EESetupState.Completed;
    Assert.False(sut.CanGoBack);
}
```

- [ ] **Step 2: Run test, expect failure**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~CanGoBack"`
Expected: build error — no `CanGoBack` property.

- [ ] **Step 3: Add `CanGoBack`**

In `E2EESetupStepViewModel.cs`, add a computed property and notify when `State` changes:

```csharp
public bool CanGoBack => State is E2EESetupState.Choice or E2EESetupState.ConfirmingOptOut;

partial void OnStateChanged(E2EESetupState value)
{
    OnPropertyChanged(nameof(CanGoBack));
    ProceedCommand.NotifyCanExecuteChanged();
}
```

- [ ] **Step 4: Run all view-model tests**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~E2EESetupStepViewModelTests"`
Expected: 9 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs
git commit -m "Expose CanGoBack on E2EESetupStepViewModel for wizard gating"
```

---

## Chunk 2: Wire-up — DI, FirstRunWizardViewModel, integration tests

### Task 9: Register `E2EESetupStepViewModel` in DI

**Files:**
- Modify: `src/Pia.Wpf/Bootstrapper.cs:303` (after `E2EEOnboardingViewModel`)

- [ ] **Step 1: Add registration**

After the `services.AddScoped<E2EEOnboardingViewModel>();` line, add:

```csharp
        services.AddScoped<E2EESetupStepViewModel>();
```

- [ ] **Step 2: Build to confirm**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds. (DI validation runs in DEBUG via `ValidateOnBuild = true`.)

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Bootstrapper.cs
git commit -m "Register E2EESetupStepViewModel in DI"
```

---

### Task 10: Add `_cloudAccountHasE2EE` capture in `FirstRunWizardViewModel`

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs`

- [ ] **Step 1: Inject `E2EESetupStepViewModel` and add a backing field**

In the constructor parameters, add `E2EESetupStepViewModel e2eeSetupViewModel` (after `E2EEOnboardingViewModel onboardingViewModel`).

Add field:

```csharp
public E2EESetupStepViewModel E2EESetupViewModel { get; }
```

Set it in the constructor body:

```csharp
E2EESetupViewModel = e2eeSetupViewModel;
```

Add a private flag:

```csharp
private bool _cloudAccountHasE2EE;
```

- [ ] **Step 2: Add `IsE2EESetupVisible` computed property**

After `VisibleStepCount`:

```csharp
/// <summary>
/// Show the E2EE setup step only when the user is signed in to cloud
/// and the cloud account does not yet have E2EE enabled.
/// </summary>
public bool IsE2EESetupVisible => IsLoggedIn && !IsE2EEOnboardingRequired && !_cloudAccountHasE2EE;
```

Notify when its dependencies change (extend the existing attributes on `_isLoggedIn` and `_isE2EEOnboardingRequired`):

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(VisibleStepCount))]
[NotifyPropertyChangedFor(nameof(HasProviderConfigured))]
[NotifyPropertyChangedFor(nameof(AccountSummary))]
[NotifyPropertyChangedFor(nameof(IsE2EESetupVisible))]
private bool _isLoggedIn;

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(VisibleStepCount))]
[NotifyPropertyChangedFor(nameof(IsE2EESetupVisible))]
private bool _isE2EEOnboardingRequired;
```

- [ ] **Step 3: Build to confirm**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs
git commit -m "Inject E2EESetupStepViewModel and expose IsE2EESetupVisible"
```

---

### Task 11: Update step count + insert step 2 routing in `FirstRunWizardViewModel`

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs`

- [ ] **Step 1: Bump TotalSteps and renumber step references**

Change `public const int TotalSteps = 6;` to `public const int TotalSteps = 7;`.

Update `VisibleStepCount` logic:

```csharp
/// <summary>Visible step count: 5 when E2EE already on, 6 otherwise (one of E2EE or Provider hidden).</summary>
public int VisibleStepCount => IsLoggedIn
    ? (_cloudAccountHasE2EE || IsE2EEOnboardingRequired ? 5 : 6)
    : 6;
```

In `CanExecuteNextOrFinish`:

```csharp
private bool CanExecuteNextOrFinish()
{
    if (IsCompleting) return false;
    if (CurrentStep == 1 && IsE2EEOnboardingRequired) return false;
    // E2EE setup step: blocked while busy or while in SavingRecoveryCode without confirmation
    if (CurrentStep == 2 && IsE2EESetupVisible)
    {
        if (E2EESetupViewModel.IsBusy) return false;
        if (E2EESetupViewModel.State == E2EESetupState.SavingRecoveryCode
            && !E2EESetupViewModel.HasConfirmedRecoveryCode) return false;
    }
    // Provider step has moved from index 2 to index 3
    if (CurrentStep == 3 && !IsLoggedIn && !ConnectionTestPassed) return false;
    return true;
}
```

In `ExecuteNext`:

```csharp
private void ExecuteNext()
{
    if (CurrentStep >= TotalSteps - 1) return;

    // On step 1 → 2: skip E2EE step if not visible (skip-login or E2EE already on)
    if (CurrentStep == 1 && !IsE2EESetupVisible)
    {
        // Skip both E2EE step (2) and Provider step (3) if logged in
        CurrentStep = IsLoggedIn ? 4 : 3;
    }
    // On step 2 (E2EE) → 3: route through the step view model; if it's not visible, skip
    else if (CurrentStep == 2)
    {
        // The wizard's Next button on this step is wired to E2EESetupViewModel.ProceedCommand
        // (see XAML). When E2EESetupViewModel.AdvanceRequested fires we call AdvanceFromE2EEStep.
        // Direct hits here only happen when the step is hidden — skip Provider too if logged in.
        CurrentStep = IsLoggedIn ? 4 : 3;
    }
    // On step 3 (Provider) → 4: skip Provider if logged in
    else if (CurrentStep == 3 && IsLoggedIn)
    {
        CurrentStep = 4;
    }
    else
    {
        CurrentStep++;
    }

    NotifyNavigationChanged();
}

private void AdvanceFromE2EEStep(bool e2eeEnabled)
{
    // Called by E2EESetupViewModel.AdvanceRequested. Always coming from CurrentStep == 2.
    CurrentStep = IsLoggedIn ? 4 : 3;
    NotifyNavigationChanged();
}
```

In `ExecuteBack`:

```csharp
private void ExecuteBack()
{
    if (CurrentStep <= 0) return;

    // From step 2 (E2EE): blocked when E2EESetupViewModel.CanGoBack is false; that's enforced in CanExecuteBack
    if (CurrentStep == 2)
    {
        CurrentStep = 1;
    }
    // From step 3 (Provider): if logged in, this step is hidden — go back to step 2 (E2EE) if visible, else step 1
    else if (CurrentStep == 3 && IsLoggedIn)
    {
        CurrentStep = IsE2EESetupVisible ? 2 : 1;
    }
    // From step 4 (Modes): if E2EE step or Provider step are hidden, skip back appropriately
    else if (CurrentStep == 4)
    {
        if (IsLoggedIn)
            CurrentStep = IsE2EESetupVisible ? 2 : 1;
        else
            CurrentStep = 3;
    }
    else
    {
        CurrentStep--;
    }

    NotifyNavigationChanged();
}
```

Update `CanExecuteBack`:

```csharp
private bool CanExecuteBack()
{
    if (IsFirstStep || IsCompleting) return false;
    if (CurrentStep == 2 && IsE2EESetupVisible && !E2EESetupViewModel.CanGoBack) return false;
    return true;
}
```

- [ ] **Step 2: Wire `AdvanceRequested`**

In the constructor (near where `OnboardingCompleted` is wired):

```csharp
E2EESetupViewModel.AdvanceRequested += AdvanceFromE2EEStep;
```

Subscribe `BackCommand`/`NextOrFinishCommand` notifications when E2EE step state changes by adding a property-change handler:

```csharp
E2EESetupViewModel.PropertyChanged += (_, e) =>
{
    if (e.PropertyName is nameof(E2EESetupStepViewModel.State)
        or nameof(E2EESetupStepViewModel.IsBusy)
        or nameof(E2EESetupStepViewModel.HasConfirmedRecoveryCode)
        or nameof(E2EESetupStepViewModel.CanGoBack))
    {
        NextOrFinishCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }
};
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs
git commit -m "Bump TotalSteps to 7 and route through E2EE step in wizard navigation"
```

---

### Task 12: Defer post-login sync until E2EE step finishes

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs:438-451` (`HandlePostLoginSyncAsync`)

- [ ] **Step 1: Update `HandlePostLoginSyncAsync`**

Replace the method body with:

```csharp
/// <summary>
/// Check E2EE status before deciding what to do after login.
/// - E2EE enabled on account but UMK missing locally → show inline onboarding (existing flow).
/// - E2EE NOT enabled on account → defer first sync until the E2EE setup step decides.
/// - E2EE already on and UMK available → start sync immediately.
/// </summary>
private async Task HandlePostLoginSyncAsync()
{
    var e2eeStatus = await _deviceManagement.CheckE2EEStatusAsync();

    if (e2eeStatus is { IsEnabled: true } && !_deviceManagement.IsInitialized())
    {
        _logger.LogInformation("E2EE enabled on account but UMK not available; showing onboarding in wizard");
        IsE2EEOnboardingRequired = true;
        _syncClientService.NotifyE2EEOnboardingRequired();
        return;
    }

    if (e2eeStatus is { IsEnabled: false })
    {
        _logger.LogInformation("E2EE not enabled on account; deferring first sync until E2EE setup step decides");
        _cloudAccountHasE2EE = false;
        OnPropertyChanged(nameof(IsE2EESetupVisible));
        OnPropertyChanged(nameof(VisibleStepCount));
        // Do NOT start sync here — the E2EE step will start it when the user makes a choice.
        return;
    }

    // E2EE already on and UMK available — start sync.
    _cloudAccountHasE2EE = true;
    OnPropertyChanged(nameof(IsE2EESetupVisible));
    OnPropertyChanged(nameof(VisibleStepCount));
    await _syncClientService.PerformFirstSyncMigrationAsync();
    _syncClientService.StartBackgroundSync();
}
```

Note: when the user opts out of E2EE on the new step, sync is started by `E2EESetupStepViewModel`'s opt-out path (added in step 2 below).

- [ ] **Step 2: In `E2EESetupStepViewModel`, also start sync when opting out**

Modify `ProceedAsync`'s opt-out branch:

```csharp
case E2EESetupState.ConfirmingOptOut:
    await CompleteOptOutAsync();
    break;
```

Add `CompleteOptOutAsync`:

```csharp
private async Task CompleteOptOutAsync()
{
    try
    {
        await _syncService.PerformFirstSyncMigrationAsync();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "First sync after E2EE opt-out failed in wizard");
    }
    _syncService.StartBackgroundSync();
    AdvanceRequested?.Invoke(false);
}
```

Update the existing `Proceed_FromConfirmingOptOut` test to reflect that sync now starts:

```csharp
[Fact]
public async Task Proceed_FromConfirmingOptOut_ShouldStartSyncAndSignalAdvance()
{
    bool? advanceRaisedWith = null;

    var sut = CreateSut();
    sut.ShouldEnableE2EE = false;
    sut.AdvanceRequested += enabled => advanceRaisedWith = enabled;

    await sut.ProceedCommand.ExecuteAsync(null); // → ConfirmingOptOut
    await sut.ProceedCommand.ExecuteAsync(null); // → CompleteOptOutAsync → AdvanceRequested(false)

    Assert.False(advanceRaisedWith);
    await _deviceMgmt.DidNotReceive().BootstrapFirstDeviceAsync();
    await _syncService.Received(1).PerformFirstSyncMigrationAsync();
    _syncService.Received(1).StartBackgroundSync();
}
```

(Replace the older `Proceed_FromConfirmingOptOut_ShouldSignalAdvanceWithoutEnabling` test with this one.)

- [ ] **Step 3: Run all view-model tests**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~E2EESetupStepViewModelTests"`
Expected: 9 passed.

- [ ] **Step 4: Build to confirm wizard side**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs
git commit -m "Defer first sync until E2EE step decides; start sync on opt-out"
```

---

### Task 13: Wizard-level integration tests (visibility + navigation)

**Files:**
- Create: `tests/Pia.Wpf.Tests/ViewModels/FirstRunWizardViewModelTests.cs`

- [ ] **Step 1: Write the test fixture and visibility tests**

```csharp
namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.ViewModels;
using Xunit;

public class FirstRunWizardViewModelTests
{
    private readonly ISettingsService _settings;
    private readonly IMemoryService _memory;
    private readonly IVoiceInputService _voice;
    private readonly ILocalizationService _loc;
    private readonly IAuthService _auth;
    private readonly IProviderService _providers;
    private readonly ISyncClientService _sync;
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly IOutputService _output;
    private readonly E2EEOnboardingViewModel _onboardingVm;
    private readonly E2EESetupStepViewModel _e2eeSetupVm;

    public FirstRunWizardViewModelTests()
    {
        _settings = Substitute.For<ISettingsService>();
        _memory = Substitute.For<IMemoryService>();
        _voice = Substitute.For<IVoiceInputService>();
        _loc = Substitute.For<ILocalizationService>();
        _auth = Substitute.For<IAuthService>();
        _providers = Substitute.For<IProviderService>();
        _sync = Substitute.For<ISyncClientService>();
        _deviceMgmt = Substitute.For<IDeviceManagementService>();
        _deviceKeys = Substitute.For<IDeviceKeyService>();
        _output = Substitute.For<IOutputService>();

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _deviceKeys.GetFingerprint().Returns("FP");

        _onboardingVm = new E2EEOnboardingViewModel(
            _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(),
            _sync, _settings, NullLogger<E2EEOnboardingViewModel>.Instance);
        _e2eeSetupVm = new E2EESetupStepViewModel(
            _deviceMgmt, _deviceKeys, _sync, _output,
            NullLogger<E2EESetupStepViewModel>.Instance);
    }

    private FirstRunWizardViewModel CreateSut() => new(
        _settings, _memory, _voice, _loc, _auth, _providers, _sync,
        _deviceMgmt, _onboardingVm, _e2eeSetupVm,
        NullLogger<FirstRunWizardViewModel>.Instance);

    [Fact]
    public void NotLoggedIn_ShouldNotShowE2EEStep()
    {
        var sut = CreateSut();

        Assert.False(sut.IsLoggedIn);
        Assert.False(sut.IsE2EESetupVisible);
        Assert.Equal(6, sut.VisibleStepCount); // welcome + account + provider + modes + profile + ready
    }

    [Fact]
    public async Task LoggedInCloudUser_AccountE2EEOff_ShouldShowE2EEStep()
    {
        _auth.LoginAsync("microsoft").Returns((true, (string?)null));
        _auth.UserDisplayName.Returns("Alice");
        _auth.UserEmail.Returns("a@example.com");
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = false });

        var sut = CreateSut();
        await sut.LoginWithMicrosoftCommand.ExecuteAsync(null);

        Assert.True(sut.IsLoggedIn);
        Assert.True(sut.IsE2EESetupVisible);
        Assert.Equal(6, sut.VisibleStepCount);
        // First sync NOT started yet — deferred to E2EE step
        await _sync.DidNotReceive().PerformFirstSyncMigrationAsync();
    }

    [Fact]
    public async Task LoggedInCloudUser_AccountE2EEAlreadyOn_ShouldNotShowE2EEStep()
    {
        _auth.LoginAsync("microsoft").Returns((true, (string?)null));
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = true });
        _deviceMgmt.IsInitialized().Returns(true);

        var sut = CreateSut();
        await sut.LoginWithMicrosoftCommand.ExecuteAsync(null);

        Assert.True(sut.IsLoggedIn);
        Assert.False(sut.IsE2EESetupVisible);
        Assert.Equal(5, sut.VisibleStepCount);
        // Sync starts immediately because UMK is available
        await _sync.Received(1).PerformFirstSyncMigrationAsync();
    }
}
```

- [ ] **Step 2: Build and run; iterate until green**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~FirstRunWizardViewModelTests"`
Expected: 3 passed. If sync setup leaks across tests (e.g., constructor of `FirstRunWizardViewModel` triggers something), inspect and adjust the substitutes (the existing constructor doesn't auto-trigger sync, so no leakage expected).

- [ ] **Step 3: Add navigation tests**

```csharp
[Fact]
public async Task Next_FromAccountStep_ShouldGoToE2EEStep_WhenCloudUserNoE2EE()
{
    _auth.LoginAsync("microsoft").Returns((true, (string?)null));
    _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = false });

    var sut = CreateSut();
    await sut.LoginWithMicrosoftCommand.ExecuteAsync(null);
    sut.CurrentStep = 1;

    await sut.NextOrFinishCommand.ExecuteAsync(null);

    Assert.Equal(2, sut.CurrentStep);
}

[Fact]
public async Task Next_FromAccountStep_ShouldSkipToModesStep_WhenE2EEAlreadyOn()
{
    _auth.LoginAsync("microsoft").Returns((true, (string?)null));
    _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = true });
    _deviceMgmt.IsInitialized().Returns(true);

    var sut = CreateSut();
    await sut.LoginWithMicrosoftCommand.ExecuteAsync(null);
    sut.CurrentStep = 1;

    await sut.NextOrFinishCommand.ExecuteAsync(null);

    Assert.Equal(4, sut.CurrentStep); // skip both E2EE (2) and Provider (3)
}

[Fact]
public void Back_FromE2EEStep_PreBootstrap_ShouldReturnToAccountStep()
{
    var sut = CreateSut();
    sut.IsLoggedIn = true; // synthetic; bypasses login flow
    sut.CurrentStep = 2;

    Assert.True(sut.BackCommand.CanExecute(null));
    sut.BackCommand.Execute(null);

    Assert.Equal(1, sut.CurrentStep);
}

[Fact]
public void Back_FromE2EEStep_PostBootstrap_ShouldBeDisabled()
{
    var sut = CreateSut();
    sut.IsLoggedIn = true;
    sut.CurrentStep = 2;
    _e2eeSetupVm.State = E2EESetupState.SavingRecoveryCode; // post-bootstrap

    Assert.False(sut.BackCommand.CanExecute(null));
}
```

- [ ] **Step 4: Run integration tests**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~FirstRunWizardViewModelTests"`
Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add tests/Pia.Wpf.Tests/ViewModels/FirstRunWizardViewModelTests.cs
git commit -m "Add wizard integration tests for E2EE step visibility and navigation"
```

---

## Chunk 3: View, XAML, localization, manual verification

### Task 14: Add localization keys (English)

**Files:**
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`

- [ ] **Step 1: Add the keys**

Add inside `<root>` (alphabetical order is fine but not required):

```xml
<data name="Wizard_E2EE_Title" xml:space="preserve">
  <value>Encrypt your data end-to-end</value>
</data>
<data name="Wizard_E2EE_Subtitle" xml:space="preserve">
  <value>Your notes, todos, and memories stay readable only on your devices.</value>
</data>
<data name="Wizard_E2EE_Pros_Title" xml:space="preserve">
  <value>Why turn this on (recommended)</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet1" xml:space="preserve">
  <value>Pia Cloud staff can't read your data.</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet2" xml:space="preserve">
  <value>If our servers are ever breached, your data is unreadable to attackers.</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet3" xml:space="preserve">
  <value>Only devices you approve can decrypt your data.</value>
</data>
<data name="Wizard_E2EE_Cons_Title" xml:space="preserve">
  <value>What you should know</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet1" xml:space="preserve">
  <value>You'll get a recovery code — save it somewhere safe (a password manager or a printed copy).</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet2" xml:space="preserve">
  <value>If you lose all your devices and the recovery code, your encrypted data is unrecoverable.</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet3" xml:space="preserve">
  <value>You can turn it off later in Settings.</value>
</data>
<data name="Wizard_E2EE_Toggle_Label" xml:space="preserve">
  <value>Encrypt my data with end-to-end encryption</value>
</data>
<data name="Wizard_E2EE_OptOut_Warning_Title" xml:space="preserve">
  <value>Continue without encryption?</value>
</data>
<data name="Wizard_E2EE_OptOut_Warning_Message" xml:space="preserve">
  <value>Your synced data will be readable by Pia Cloud staff in case of a server breach. You can turn encryption on later in Settings.</value>
</data>
<data name="Wizard_E2EE_OptOut_Continue" xml:space="preserve">
  <value>Continue without encryption</value>
</data>
<data name="Wizard_E2EE_OptOut_GoBack" xml:space="preserve">
  <value>Go back</value>
</data>
<data name="Wizard_E2EE_Recovery_Title" xml:space="preserve">
  <value>Save your recovery code</value>
</data>
<data name="Wizard_E2EE_Recovery_Description" xml:space="preserve">
  <value>This is the only way to recover your encrypted data if you lose access to all your devices.</value>
</data>
<data name="Wizard_E2EE_Recovery_Warning" xml:space="preserve">
  <value>Pia cannot recover this for you. Store it in a password manager or print it.</value>
</data>
<data name="Wizard_E2EE_Recovery_Confirm" xml:space="preserve">
  <value>I've saved my recovery code somewhere safe</value>
</data>
<data name="Wizard_E2EE_Error_Bootstrap" xml:space="preserve">
  <value>Could not enable encryption: {0}</value>
</data>
<data name="Wizard_E2EE_Bootstrapping" xml:space="preserve">
  <value>Setting up encryption…</value>
</data>
```

- [ ] **Step 2: Build and confirm `ViewStrings.Designer.cs` regenerates**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds; `ViewStrings.Designer.cs` updated by the build (the project uses ResX → Designer code-gen).

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Resources/Strings/ViewStrings.resx src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs
git commit -m "Add English Wizard_E2EE_* localization keys"
```

---

### Task 15: Add German + French translations

**Files:**
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx`

- [ ] **Step 1: Add German keys**

```xml
<data name="Wizard_E2EE_Title" xml:space="preserve">
  <value>Ende-zu-Ende-Verschlüsselung aktivieren</value>
</data>
<data name="Wizard_E2EE_Subtitle" xml:space="preserve">
  <value>Notizen, Aufgaben und Erinnerungen bleiben nur auf deinen Geräten lesbar.</value>
</data>
<data name="Wizard_E2EE_Pros_Title" xml:space="preserve">
  <value>Warum aktivieren (empfohlen)</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet1" xml:space="preserve">
  <value>Pia-Cloud-Mitarbeitende können deine Daten nicht lesen.</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet2" xml:space="preserve">
  <value>Bei einem Server-Einbruch bleiben deine Daten für Angreifer unlesbar.</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet3" xml:space="preserve">
  <value>Nur Geräte, die du freigibst, können deine Daten entschlüsseln.</value>
</data>
<data name="Wizard_E2EE_Cons_Title" xml:space="preserve">
  <value>Das solltest du wissen</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet1" xml:space="preserve">
  <value>Du erhältst einen Wiederherstellungscode — bewahre ihn sicher auf (Passwort-Manager oder ausgedruckt).</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet2" xml:space="preserve">
  <value>Verlierst du alle Geräte und den Wiederherstellungscode, sind deine verschlüsselten Daten unwiederbringlich verloren.</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet3" xml:space="preserve">
  <value>Du kannst die Verschlüsselung später in den Einstellungen abschalten.</value>
</data>
<data name="Wizard_E2EE_Toggle_Label" xml:space="preserve">
  <value>Meine Daten Ende-zu-Ende verschlüsseln</value>
</data>
<data name="Wizard_E2EE_OptOut_Warning_Title" xml:space="preserve">
  <value>Ohne Verschlüsselung fortfahren?</value>
</data>
<data name="Wizard_E2EE_OptOut_Warning_Message" xml:space="preserve">
  <value>Deine synchronisierten Daten könnten bei einem Server-Einbruch von Pia-Cloud-Mitarbeitenden gelesen werden. Du kannst die Verschlüsselung später in den Einstellungen aktivieren.</value>
</data>
<data name="Wizard_E2EE_OptOut_Continue" xml:space="preserve">
  <value>Ohne Verschlüsselung fortfahren</value>
</data>
<data name="Wizard_E2EE_OptOut_GoBack" xml:space="preserve">
  <value>Zurück</value>
</data>
<data name="Wizard_E2EE_Recovery_Title" xml:space="preserve">
  <value>Wiederherstellungscode sichern</value>
</data>
<data name="Wizard_E2EE_Recovery_Description" xml:space="preserve">
  <value>Dies ist die einzige Möglichkeit, deine verschlüsselten Daten wiederherzustellen, falls du den Zugriff auf alle Geräte verlierst.</value>
</data>
<data name="Wizard_E2EE_Recovery_Warning" xml:space="preserve">
  <value>Pia kann den Code nicht für dich wiederherstellen. Bewahre ihn in einem Passwort-Manager auf oder drucke ihn aus.</value>
</data>
<data name="Wizard_E2EE_Recovery_Confirm" xml:space="preserve">
  <value>Ich habe meinen Wiederherstellungscode sicher gespeichert</value>
</data>
<data name="Wizard_E2EE_Error_Bootstrap" xml:space="preserve">
  <value>Verschlüsselung konnte nicht aktiviert werden: {0}</value>
</data>
<data name="Wizard_E2EE_Bootstrapping" xml:space="preserve">
  <value>Verschlüsselung wird eingerichtet…</value>
</data>
```

- [ ] **Step 2: Add French keys**

```xml
<data name="Wizard_E2EE_Title" xml:space="preserve">
  <value>Chiffrer vos données de bout en bout</value>
</data>
<data name="Wizard_E2EE_Subtitle" xml:space="preserve">
  <value>Vos notes, tâches et souvenirs restent lisibles uniquement sur vos appareils.</value>
</data>
<data name="Wizard_E2EE_Pros_Title" xml:space="preserve">
  <value>Pourquoi activer (recommandé)</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet1" xml:space="preserve">
  <value>Le personnel de Pia Cloud ne peut pas lire vos données.</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet2" xml:space="preserve">
  <value>En cas de violation de nos serveurs, vos données restent illisibles pour les attaquants.</value>
</data>
<data name="Wizard_E2EE_Pros_Bullet3" xml:space="preserve">
  <value>Seuls les appareils que vous approuvez peuvent déchiffrer vos données.</value>
</data>
<data name="Wizard_E2EE_Cons_Title" xml:space="preserve">
  <value>Ce que vous devez savoir</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet1" xml:space="preserve">
  <value>Vous obtiendrez un code de récupération — conservez-le en lieu sûr (gestionnaire de mots de passe ou copie imprimée).</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet2" xml:space="preserve">
  <value>Si vous perdez tous vos appareils et le code de récupération, vos données chiffrées sont irrécupérables.</value>
</data>
<data name="Wizard_E2EE_Cons_Bullet3" xml:space="preserve">
  <value>Vous pourrez désactiver le chiffrement plus tard dans les Paramètres.</value>
</data>
<data name="Wizard_E2EE_Toggle_Label" xml:space="preserve">
  <value>Chiffrer mes données de bout en bout</value>
</data>
<data name="Wizard_E2EE_OptOut_Warning_Title" xml:space="preserve">
  <value>Continuer sans chiffrement ?</value>
</data>
<data name="Wizard_E2EE_OptOut_Warning_Message" xml:space="preserve">
  <value>Vos données synchronisées pourront être lues par le personnel de Pia Cloud en cas de violation des serveurs. Vous pourrez activer le chiffrement plus tard dans les Paramètres.</value>
</data>
<data name="Wizard_E2EE_OptOut_Continue" xml:space="preserve">
  <value>Continuer sans chiffrement</value>
</data>
<data name="Wizard_E2EE_OptOut_GoBack" xml:space="preserve">
  <value>Retour</value>
</data>
<data name="Wizard_E2EE_Recovery_Title" xml:space="preserve">
  <value>Sauvegardez votre code de récupération</value>
</data>
<data name="Wizard_E2EE_Recovery_Description" xml:space="preserve">
  <value>C'est le seul moyen de récupérer vos données chiffrées si vous perdez l'accès à tous vos appareils.</value>
</data>
<data name="Wizard_E2EE_Recovery_Warning" xml:space="preserve">
  <value>Pia ne peut pas le récupérer pour vous. Conservez-le dans un gestionnaire de mots de passe ou imprimez-le.</value>
</data>
<data name="Wizard_E2EE_Recovery_Confirm" xml:space="preserve">
  <value>J'ai sauvegardé mon code de récupération en lieu sûr</value>
</data>
<data name="Wizard_E2EE_Error_Bootstrap" xml:space="preserve">
  <value>Impossible d'activer le chiffrement : {0}</value>
</data>
<data name="Wizard_E2EE_Bootstrapping" xml:space="preserve">
  <value>Configuration du chiffrement…</value>
</data>
```

- [ ] **Step 3: Build and commit**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds.

```bash
git add src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx
git commit -m "Add German and French translations for Wizard_E2EE_* keys"
```

---

### Task 16: Create `E2EESetupStep.xaml`

**Files:**
- Create: `src/Pia.Wpf/Views/WizardSteps/E2EESetupStep.xaml`
- Create: `src/Pia.Wpf/Views/WizardSteps/E2EESetupStep.xaml.cs`

- [ ] **Step 1: Create the code-behind**

`E2EESetupStep.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace Pia.Views.WizardSteps;

public partial class E2EESetupStep : UserControl
{
    public E2EESetupStep()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2: Create the XAML**

`E2EESetupStep.xaml`:

```xml
<UserControl x:Class="Pia.Views.WizardSteps.E2EESetupStep"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:loc="clr-namespace:Pia.Localization"
             xmlns:models="clr-namespace:Pia.Models"
             mc:Ignorable="d"
             d:DesignHeight="600" d:DesignWidth="720">
  <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
    <StackPanel HorizontalAlignment="Center" MaxWidth="560" Margin="0,20,0,20"
                DataContext="{Binding E2EESetupViewModel}">

      <!-- Mode A — Choice -->
      <StackPanel>
        <StackPanel.Style>
          <Style TargetType="StackPanel">
            <Setter Property="Visibility" Value="Collapsed"/>
            <Style.Triggers>
              <DataTrigger Binding="{Binding State}" Value="{x:Static models:E2EESetupState.Choice}">
                <Setter Property="Visibility" Value="Visible"/>
              </DataTrigger>
              <DataTrigger Binding="{Binding State}" Value="{x:Static models:E2EESetupState.Bootstrapping}">
                <Setter Property="Visibility" Value="Visible"/>
              </DataTrigger>
            </Style.Triggers>
          </Style>
        </StackPanel.Style>

        <ui:SymbolIcon Symbol="Shield24" FontSize="48"
                       HorizontalAlignment="Center" Margin="0,0,0,16"
                       Foreground="{DynamicResource SystemAccentColorPrimaryBrush}"/>
        <TextBlock Text="{loc:Str Wizard_E2EE_Title}"
                   Style="{StaticResource H2TextStyle}"
                   HorizontalAlignment="Center" Margin="0,0,0,8"/>
        <TextBlock Text="{loc:Str Wizard_E2EE_Subtitle}"
                   Style="{StaticResource BodyTextStyle}"
                   HorizontalAlignment="Center" TextWrapping="Wrap" TextAlignment="Center"
                   Foreground="{DynamicResource TextPlaceholderColorBrush}"
                   Margin="0,0,0,16"/>

        <!-- Pros card -->
        <Border Background="{DynamicResource ControlFillColorDefaultBrush}"
                CornerRadius="8" Padding="16" Margin="0,0,0,12">
          <StackPanel>
            <TextBlock Text="{loc:Str Wizard_E2EE_Pros_Title}"
                       FontWeight="SemiBold" Margin="0,0,0,8"/>
            <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
              <ui:SymbolIcon Symbol="Checkmark24" FontSize="16" Margin="0,0,8,0"
                             Foreground="{DynamicResource SystemAccentColorPrimaryBrush}"/>
              <TextBlock Text="{loc:Str Wizard_E2EE_Pros_Bullet1}" TextWrapping="Wrap" FontSize="13"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
              <ui:SymbolIcon Symbol="Checkmark24" FontSize="16" Margin="0,0,8,0"
                             Foreground="{DynamicResource SystemAccentColorPrimaryBrush}"/>
              <TextBlock Text="{loc:Str Wizard_E2EE_Pros_Bullet2}" TextWrapping="Wrap" FontSize="13"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal">
              <ui:SymbolIcon Symbol="Checkmark24" FontSize="16" Margin="0,0,8,0"
                             Foreground="{DynamicResource SystemAccentColorPrimaryBrush}"/>
              <TextBlock Text="{loc:Str Wizard_E2EE_Pros_Bullet3}" TextWrapping="Wrap" FontSize="13"/>
            </StackPanel>
          </StackPanel>
        </Border>

        <!-- Cons card -->
        <Border Background="{DynamicResource ControlFillColorDefaultBrush}"
                CornerRadius="8" Padding="16" Margin="0,0,0,16">
          <StackPanel>
            <TextBlock Text="{loc:Str Wizard_E2EE_Cons_Title}"
                       FontWeight="SemiBold" Margin="0,0,0,8"/>
            <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
              <ui:SymbolIcon Symbol="Warning24" FontSize="16" Margin="0,0,8,0"/>
              <TextBlock Text="{loc:Str Wizard_E2EE_Cons_Bullet1}" TextWrapping="Wrap" FontSize="13"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
              <ui:SymbolIcon Symbol="Warning24" FontSize="16" Margin="0,0,8,0"/>
              <TextBlock Text="{loc:Str Wizard_E2EE_Cons_Bullet2}" TextWrapping="Wrap" FontSize="13"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal">
              <ui:SymbolIcon Symbol="Info24" FontSize="16" Margin="0,0,8,0"/>
              <TextBlock Text="{loc:Str Wizard_E2EE_Cons_Bullet3}" TextWrapping="Wrap" FontSize="13"/>
            </StackPanel>
          </StackPanel>
        </Border>

        <!-- Toggle -->
        <ui:ToggleSwitch Content="{loc:Str Wizard_E2EE_Toggle_Label}"
                         IsChecked="{Binding ShouldEnableE2EE, Mode=TwoWay}"
                         IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBooleanConverter}}"
                         Margin="0,0,0,12"/>

        <!-- Bootstrapping spinner -->
        <StackPanel Orientation="Horizontal" Margin="0,0,0,12"
                    Visibility="{Binding IsBusy, Converter={StaticResource BooleanToVisibilityConverter}}">
          <ui:ProgressRing IsIndeterminate="True" Width="16" Height="16" Margin="0,0,8,0"/>
          <TextBlock Text="{loc:Str Wizard_E2EE_Bootstrapping}" VerticalAlignment="Center" FontSize="13"/>
        </StackPanel>

        <!-- Error -->
        <ui:InfoBar Severity="Error"
                    IsClosable="False"
                    Title="{loc:Str Wizard_E2EE_Error_Bootstrap}"
                    Message="{Binding ErrorMessage}"
                    IsOpen="{Binding ErrorMessage, Converter={StaticResource NullToBooleanConverter}}"
                    Margin="0,0,0,12"/>
      </StackPanel>

      <!-- Mode A — Confirming opt-out -->
      <StackPanel Visibility="Collapsed">
        <StackPanel.Style>
          <Style TargetType="StackPanel">
            <Setter Property="Visibility" Value="Collapsed"/>
            <Style.Triggers>
              <DataTrigger Binding="{Binding State}" Value="{x:Static models:E2EESetupState.ConfirmingOptOut}">
                <Setter Property="Visibility" Value="Visible"/>
              </DataTrigger>
            </Style.Triggers>
          </Style>
        </StackPanel.Style>

        <ui:SymbolIcon Symbol="Warning24" FontSize="48"
                       HorizontalAlignment="Center" Margin="0,0,0,16"
                       Foreground="#f59e0b"/>
        <TextBlock Text="{loc:Str Wizard_E2EE_OptOut_Warning_Title}"
                   Style="{StaticResource H2TextStyle}"
                   HorizontalAlignment="Center" Margin="0,0,0,8"/>
        <TextBlock Text="{loc:Str Wizard_E2EE_OptOut_Warning_Message}"
                   TextWrapping="Wrap" TextAlignment="Center"
                   Foreground="{DynamicResource TextPlaceholderColorBrush}"
                   Margin="0,0,0,16"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
          <ui:Button Content="{loc:Str Wizard_E2EE_OptOut_GoBack}"
                     Command="{Binding OptOutGoBackCommand}"
                     Margin="0,0,8,0"/>
          <ui:Button Content="{loc:Str Wizard_E2EE_OptOut_Continue}"
                     Appearance="Caution"
                     Command="{Binding ProceedCommand}"/>
        </StackPanel>
      </StackPanel>

      <!-- Mode B — Saving recovery code -->
      <StackPanel>
        <StackPanel.Style>
          <Style TargetType="StackPanel">
            <Setter Property="Visibility" Value="Collapsed"/>
            <Style.Triggers>
              <DataTrigger Binding="{Binding State}" Value="{x:Static models:E2EESetupState.SavingRecoveryCode}">
                <Setter Property="Visibility" Value="Visible"/>
              </DataTrigger>
              <DataTrigger Binding="{Binding State}" Value="{x:Static models:E2EESetupState.Completed}">
                <Setter Property="Visibility" Value="Visible"/>
              </DataTrigger>
            </Style.Triggers>
          </Style>
        </StackPanel.Style>

        <ui:SymbolIcon Symbol="ShieldKeyhole24" FontSize="48"
                       HorizontalAlignment="Center" Margin="0,0,0,16"
                       Foreground="{DynamicResource SystemAccentColorPrimaryBrush}"/>
        <TextBlock Text="{loc:Str Wizard_E2EE_Recovery_Title}"
                   Style="{StaticResource H2TextStyle}"
                   HorizontalAlignment="Center" Margin="0,0,0,8"/>
        <TextBlock Text="{loc:Str Wizard_E2EE_Recovery_Description}"
                   TextWrapping="Wrap" TextAlignment="Center"
                   Foreground="{DynamicResource TextPlaceholderColorBrush}"
                   Margin="0,0,0,16"/>

        <Border Background="{DynamicResource ControlFillColorDefaultBrush}"
                CornerRadius="8" Padding="20" Margin="0,0,0,12">
          <TextBlock Text="{Binding RecoveryCode}"
                     FontFamily="Consolas" FontSize="20" FontWeight="Bold"
                     TextAlignment="Center" TextWrapping="Wrap"/>
        </Border>

        <ui:Button Content="{loc:Str Enum_CopyToClipboard}"
                   Icon="{ui:SymbolIcon Copy24}"
                   Command="{Binding CopyRecoveryCodeCommand}"
                   HorizontalAlignment="Center"
                   Margin="0,0,0,16"/>

        <ui:InfoBar Severity="Warning"
                    IsOpen="True"
                    IsClosable="False"
                    Title="{loc:Str Wizard_E2EE_Recovery_Warning}"
                    Margin="0,0,0,16"/>

        <CheckBox Content="{loc:Str Wizard_E2EE_Recovery_Confirm}"
                  IsChecked="{Binding HasConfirmedRecoveryCode, Mode=TwoWay}"
                  HorizontalAlignment="Center"/>
      </StackPanel>

    </StackPanel>
  </ScrollViewer>
</UserControl>
```

- [ ] **Step 3: Verify `NullToBooleanConverter` exists**

Run: `Grep "NullToBooleanConverter" -path src/Pia.Wpf`
If it doesn't exist, replace `IsOpen="{Binding ErrorMessage, Converter={StaticResource NullToBooleanConverter}}"` with a binding to a small computed `bool HasErrorMessage` property added to the view model:

```csharp
public bool HasErrorMessage => !string.IsNullOrEmpty(ErrorMessage);

partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasErrorMessage));
```

And in the XAML:

```xml
IsOpen="{Binding HasErrorMessage}"
```

- [ ] **Step 4: Build to confirm XAML compiles**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Views/WizardSteps/E2EESetupStep.xaml src/Pia.Wpf/Views/WizardSteps/E2EESetupStep.xaml.cs src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs
git commit -m "Add E2EESetupStep view with Choice/OptOut/Recovery modes"
```

---

### Task 17: Insert step in `FirstRunWizardWindow.xaml`

**Files:**
- Modify: `src/Pia.Wpf/Views/FirstRunWizardWindow.xaml`

- [ ] **Step 1: Add a conditional progress dot for E2EE step**

In the progress dots `StackPanel` (around line 49), insert a new conditional dot **before** the existing Provider dot block:

```xml
<!-- Dot 2: E2EE Setup (hidden when not visible) -->
<StackPanel Orientation="Horizontal"
            Visibility="{Binding IsE2EESetupVisible, Converter={StaticResource BooleanToVisibilityConverter}}">
  <Rectangle Width="32" Height="2" VerticalAlignment="Center"
             Fill="{DynamicResource ControlStrongStrokeColorDefaultBrush}"/>
  <Ellipse>
    <Ellipse.Style>
      <Style TargetType="Ellipse" BasedOn="{StaticResource DotStyle}">
        <Style.Triggers>
          <DataTrigger Binding="{Binding CurrentStep}" Value="2">
            <Setter Property="Width" Value="12"/>
            <Setter Property="Height" Value="12"/>
            <Setter Property="Fill" Value="{DynamicResource SystemAccentColorPrimaryBrush}"/>
          </DataTrigger>
        </Style.Triggers>
      </Style>
    </Ellipse.Style>
  </Ellipse>
</StackPanel>
```

Update the existing Provider step block (currently `Value="2"`) to `Value="3"`, and adjust its `Visibility` binding so it hides when logged in (existing behavior, just renumbered):

```xml
<!-- Dot 3: Provider Setup (hidden when logged in) -->
<StackPanel Orientation="Horizontal"
            Visibility="{Binding IsLoggedIn, Converter={StaticResource InverseBooleanToVisibilityConverter}}">
  <Rectangle Width="32" Height="2" .../>
  <Ellipse>
    ...
    <DataTrigger Binding="{Binding CurrentStep}" Value="3">
    ...
  </Ellipse>
</StackPanel>
```

Renumber the rest:
- Dot 4 (Modes): `Value="4"`
- Dot 5 (Profile): `Value="5"`
- Dot 6 (Ready): `Value="6"`

- [ ] **Step 2: Add the new step view inside the step content grid**

In the `Step Content` grid (around line 169), add the new step **before** the Provider step:

```xml
<!-- Step 2: E2EE Setup -->
<steps:E2EESetupStep>
  <steps:E2EESetupStep.Style>
    <Style TargetType="UserControl">
      <Setter Property="Visibility" Value="Collapsed"/>
      <Style.Triggers>
        <DataTrigger Binding="{Binding CurrentStep}" Value="2">
          <Setter Property="Visibility" Value="Visible"/>
        </DataTrigger>
      </Style.Triggers>
    </Style>
  </steps:E2EESetupStep.Style>
</steps:E2EESetupStep>
```

Renumber the existing Provider/Modes/Profile/Ready DataTriggers to 3/4/5/6.

- [ ] **Step 3: Hide Skip on the E2EE step + disable Back post-bootstrap**

Update the Skip button:

```xml
<ui:Button Grid.Column="0"
           Content="{loc:Str Wizard_Skip}"
           Appearance="Transparent"
           Command="{Binding SkipCommand}">
  <ui:Button.Style>
    <Style TargetType="ui:Button" BasedOn="{StaticResource {x:Type ui:Button}}">
      <Setter Property="Visibility" Value="Visible"/>
      <Style.Triggers>
        <MultiDataTrigger>
          <MultiDataTrigger.Conditions>
            <Condition Binding="{Binding CurrentStep}" Value="2"/>
            <Condition Binding="{Binding IsE2EESetupVisible}" Value="True"/>
          </MultiDataTrigger.Conditions>
          <Setter Property="Visibility" Value="Collapsed"/>
        </MultiDataTrigger>
      </Style.Triggers>
    </Style>
  </ui:Button.Style>
</ui:Button>
```

Back button visibility/enablement is already covered by `BackCommand.CanExecute` returning false when `!E2EESetupViewModel.CanGoBack`.

- [ ] **Step 4: Wire the wizard's Next button to route through the step view model**

The wizard's bottom Next/Finish button binds to `NextOrFinishCommand`. We need step 2 to call `E2EESetupViewModel.ProceedCommand` instead of just advancing.

In `FirstRunWizardViewModel.HandleNextOrFinishAsync`:

```csharp
private async Task HandleNextOrFinishAsync()
{
    if (CurrentStep == 2 && IsE2EESetupVisible)
    {
        await E2EESetupViewModel.ProceedCommand.ExecuteAsync(null);
        return;
    }

    if (IsLastStep)
        await ExecuteFinishAsync();
    else
        ExecuteNext();
}
```

(`AdvanceFromE2EEStep` will be invoked via `E2EESetupViewModel.AdvanceRequested` and will call `ExecuteNext`/set `CurrentStep` directly.)

- [ ] **Step 5: Build and run the app**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: succeeds.

```bash
git add src/Pia.Wpf/Views/FirstRunWizardWindow.xaml src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs
git commit -m "Insert E2EE setup step into FirstRunWizardWindow XAML and route Next"
```

---

### Task 18: Manual smoke test in DEBUG

**Files:** none (manual test)

- [ ] **Step 1: Reset wizard state and run**

Delete or unset `HasCompletedFirstRunWizard` in `%LOCALAPPDATA%\Pia\settings.json` (or use a dev test account whose wizard hasn't run). Then:

Run: `dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj`

- [ ] **Step 2: Walk the three paths**

Confirm visually:

1. **Cloud login, account E2EE off** — login succeeds; clicking Next on the account step shows the new E2EE page; toggle is on; clicking Next runs bootstrap; recovery code appears; copy works; checkbox gates Next; Skip is hidden; Back is disabled after bootstrap.
2. **Cloud login, account E2EE on** — existing inline onboarding shows on the account step (unchanged); clicking Next skips the new E2EE page entirely.
3. **Skip-login (provider only)** — wizard shows the provider step (now at index 3); E2EE page is skipped.

- [ ] **Step 3: Confirm German localization**

Switch UI language in the wizard's first step (or pre-set settings) to `de` and re-walk. Confirm German strings render.

- [ ] **Step 4: Confirm logs are clean of recovery code**

Run: `Grep "RecoveryCode|XXXX-XXXX" -path %LOCALAPPDATA%\Pia\Logs --glob "pia-*.log"`
Expected: no matches (recovery code only goes through `_logger.LogInformation` for state transitions, not the value).

- [ ] **Step 5: Note any UI fit/finish issues and fix in a follow-up commit**

If any issues found, fix them on the spot, then:

```bash
git add -A
git commit -m "Polish E2EE wizard step based on smoke test"
```

If no issues, no commit needed.

---

## Final verification

- [ ] **Run full test suite**

Run: `dotnet test`
Expected: all tests pass, including the 9 `E2EESetupStepViewModelTests` and 7 `FirstRunWizardViewModelTests`.

- [ ] **Confirm `dotnet build -c Release` succeeds**

Run: `dotnet build -c Release`
Expected: succeeds.

- [ ] **Update CHANGELOG.md if the project tracks user-visible changes**

Run: `Read CHANGELOG.md`
If it lists user-visible features, add a line under the appropriate section:
```
- First-run wizard now offers end-to-end encryption setup (default on) with a plain-language explanation and inline recovery code.
```

```bash
git add CHANGELOG.md
git commit -m "Note E2EE wizard step in changelog"
```

---

## Out of scope (do not implement)

- Telemetry of opt-out reasons.
- Re-prompting users who finished the wizard before this feature.
- Changes to Settings → Account E2EE toggle behavior.
- Changes to the multi-device approval flow.
