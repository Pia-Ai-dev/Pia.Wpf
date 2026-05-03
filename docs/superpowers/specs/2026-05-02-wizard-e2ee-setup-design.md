# First-Run Wizard: E2EE Setup Step

**Date:** 2026-05-02
**Branch:** feature/scheduled-research (will be moved to feature/wizard-e2ee-setup)
**Status:** Approved

## Problem

When a user signs in to Pia Cloud during the first-run wizard, today the app behaves in two paths:

1. **Cloud account already has E2EE enabled** → the wizard shows the existing `E2EEOnboardingView` inline (device approval / recovery-code activation). This works.
2. **Cloud account has E2EE disabled** → the wizard silently calls `PerformFirstSyncMigrationAsync()` and starts uploading data unencrypted. The user is never asked, never informed, never given the choice to turn encryption on.

Path 2 is the gap. Users who would have wanted E2EE never see the option until they later discover it under Settings → Account, by which time their data is already on the server unencrypted.

## Goals

- Add a wizard page that explains E2EE in consumer-friendly language (pros + cons).
- Default to **on**: privacy-preserving by default.
- Let the user opt out with one soft confirmation, never a hard block.
- Reuse the existing bootstrap flow (`BootstrapFirstDeviceAsync` + recovery-code confirmation) so we don't fork the security path.
- Defer the first sync until the user has made the choice — avoid uploading unencrypted data that would then need to be re-uploaded.

## Non-goals

- Re-prompting existing users who already finished the wizard. (They use Settings → Account today.)
- Telemetry on opt-out reasons.
- Changes to the existing `AccountSettingsViewModel.EnableE2EEAsync` flow.
- Changes to the multi-device approval path (existing `E2EEOnboardingView` behavior is unchanged).

## Wizard placement

A new step is inserted as **step 2**, between AccountSetup and ProviderSetup.

| Step | After change |
|------|-------------|
| 0 | Welcome |
| 1 | AccountSetup |
| **2** | **E2EESetup** *(new — only when logged-in cloud user, account E2EE off)* |
| 3 | ProviderSetup |
| 4 | ModesOverview |
| 5 | UserProfile |
| 6 | Ready |

`TotalSteps` becomes 7. Visibility logic in `FirstRunWizardViewModel`:

- **Logged-in cloud user, account E2EE off** → E2EESetup shown, ProviderSetup hidden. Visible step count: 6.
- **Logged-in cloud user, account E2EE already on** → existing inline E2EE-onboarding flow handles device approval inside AccountSetup (unchanged); E2EESetup hidden. Visible count: 5.
- **Not logged in to cloud** → E2EESetup hidden, ProviderSetup shown. Visible count: 6.

The progress dot bar gets a new conditional dot for E2EESetup that mirrors the existing pattern that hides the ProviderSetup dot when logged in. `ExecuteNext` and `ExecuteBack` get extra branches to skip-over the hidden step in both directions (same shape as the existing ProviderSetup skip).

## The page itself

`Views/WizardSteps/E2EESetupStep.xaml` has two visual modes driven by `E2EESetupState`.

### Mode A — Choice

- Shield icon, heading, subtitle.
- "Why turn this on (recommended)" section with three plain-language bullets:
  - Pia Cloud staff can't read your data.
  - If our servers are ever breached, your data is unreadable to attackers.
  - Only devices you approve can decrypt.
- "What you should know" section with three bullets:
  - You'll get a recovery code — save it somewhere safe (password manager, printed copy).
  - If you lose all your devices AND the recovery code, your encrypted data is unrecoverable.
  - You can turn it off later in Settings.
- A `ToggleSwitch` bound to `ShouldEnableE2EE` (default `true`).

### Mode B — Recovery code reveal (after bootstrap)

- Key icon, heading "Save your recovery code".
- Recovery code rendered in monospace inside a bordered card.
- Copy-to-clipboard button.
- `ui:InfoBar` with `Severity="Warning"`: "Pia cannot recover this for you."
- Confirmation `CheckBox` bound to `HasConfirmedRecoveryCode`.

This mirrors `RecoveryCodeContentDialog` so users see consistent wording.

### Soft opt-out confirmation

When `ShouldEnableE2EE` is `false` and the user clicks Next, the page transitions to `ConfirmingOptOut`: the toggle area is replaced by an inline `ui:InfoBar` (`Severity="Warning"`) explaining the trade-off, with two buttons — "Continue without encryption" and "Go back". No second click of Next required.

### Error state

If `BootstrapFirstDeviceAsync` throws, the page stays in `Choice` mode, shows an `ui:InfoBar` (`Severity="Error"`), and keeps the toggle and Next button available so the user can retry.

## View model: `E2EESetupStepViewModel`

Lives next to `E2EEOnboardingViewModel` in `ViewModels/`. A separate view model rather than inlining into `FirstRunWizardViewModel` because that file is already 645 lines doing five jobs (profile, account, provider, navigation, completion); a focused view model also matches the existing pattern.

### Dependencies (DI)

- `IDeviceManagementService` — `CheckE2EEStatusAsync`, `BootstrapFirstDeviceAsync`
- `IDeviceKeyService` — `GetFingerprint()` (informational, optional)
- `ISyncClientService` — start sync after bootstrap
- `IOutputService` — copy-to-clipboard
- `ILogger<E2EESetupStepViewModel>`

### State enum

```csharp
public enum E2EESetupState
{
    Choice,             // Mode A — toggle visible
    ConfirmingOptOut,   // Mode A — soft warning bar visible
    Bootstrapping,      // brief spinner during BootstrapFirstDeviceAsync
    SavingRecoveryCode, // Mode B — recovery code visible
    Completed           // bootstrap done + checkbox confirmed; Next can advance
}
```

### Observable properties

- `State` — current `E2EESetupState`
- `ShouldEnableE2EE` — `true` by default; bound to the toggle
- `RecoveryCode` — populated after bootstrap, never logged in non-`SensitiveDebug` paths
- `HasConfirmedRecoveryCode` — bound to the inline checkbox
- `ErrorMessage` — bound to the error InfoBar
- `IsBusy` — disables toggle/buttons during bootstrap

### Commands

- `ProceedCommand` — invoked when wizard's Next is pressed on this step. Routes by state:
  - `Choice` + toggle on → bootstrap → `SavingRecoveryCode` (does NOT advance the wizard yet)
  - `Choice` + toggle off → `ConfirmingOptOut` (does NOT advance)
  - `ConfirmingOptOut` (continue) → advance signal, no E2EE
  - `SavingRecoveryCode` + checkbox checked → advance signal, E2EE enabled
- `OptOutGoBackCommand` — back to `Choice`
- `CopyRecoveryCodeCommand` — calls `IOutputService`

An `event Action<bool>? AdvanceRequested` (the `bool` indicates whether E2EE was enabled) is fired when the wizard should advance. `FirstRunWizardViewModel` subscribes to it and calls `ExecuteNext` plus the appropriate post-step actions.

## Wiring into `FirstRunWizardViewModel`

- New constructor dependency: `E2EESetupStepViewModel`.
- New computed property: `IsE2EESetupVisible = IsLoggedIn && !IsE2EEOnboardingRequired && !_cloudAccountHasE2EE`.
- `_cloudAccountHasE2EE` is captured in `HandlePostLoginSyncAsync` from the same `e2eeStatus.IsEnabled` check that already runs.
- `HandlePostLoginSyncAsync` is changed: when E2EE is **off** on the account, do **not** start sync immediately. Sync start is deferred until the wizard's E2EE step finishes. This avoids syncing unencrypted data that would then need to be re-uploaded after bootstrap.
- `CanExecuteNextOrFinish` gains a clause for `CurrentStep == 2`: blocked unless the step view model says advance is allowed.
- `ExecuteNext`/`ExecuteBack` add a skip-over branch for step 2 when `!IsE2EESetupVisible`, mirroring the existing ProviderSetup skip pattern.
- `ExecuteFinishAsync` is unchanged for E2EE concerns: settings persistence already happens inside `BootstrapFirstDeviceAsync`.
- The Skip button is hidden when `CurrentStep == 2 && IsE2EESetupVisible`.
- The Back button is disabled when `E2EESetupState >= Bootstrapping`.

### Sync resume timing

- Toggle on, bootstrap succeeds, recovery confirmed → `_syncClientService.PerformFirstSyncMigrationAsync()` then `StartBackgroundSync()`.
- Opted out → same calls, but data goes up unencrypted (matches today's silent behavior, just now an explicit choice).

## Privacy-first logging discipline

Per `CLAUDE.md`:

- `RecoveryCode` never appears in non-`SensitiveDebug` log calls.
- State transitions log at Information level with state name only.
- Errors log via `_logger.LogError(ex, ...)` without echoing the toggle state or any user content.

## Files

### New

- `src/Pia.Wpf/ViewModels/E2EESetupStepViewModel.cs`
- `src/Pia.Wpf/Views/WizardSteps/E2EESetupStep.xaml` (+ `.xaml.cs`)
- `src/Pia.Wpf/Models/E2EESetupState.cs` (enum)
- `tests/Pia.Wpf.Tests/ViewModels/E2EESetupStepViewModelTests.cs`

### Modified

- `src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs`
- `src/Pia.Wpf/Views/FirstRunWizardWindow.xaml`
- `src/Pia.Wpf/Bootstrapper.cs`
- `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` (+ `.de.resx`, `.fr.resx`)
- Wizard-level integration tests in the existing `FirstRunWizardViewModelTests` (or equivalent).

## Localization keys (`Wizard_E2EE_*`)

- `Wizard_E2EE_Title`
- `Wizard_E2EE_Subtitle`
- `Wizard_E2EE_Pros_Title`, `Wizard_E2EE_Pros_Bullet1/2/3`
- `Wizard_E2EE_Cons_Title`, `Wizard_E2EE_Cons_Bullet1/2/3`
- `Wizard_E2EE_Toggle_Label`
- `Wizard_E2EE_OptOut_Warning_Title`, `_Message`, `_Continue`, `_GoBack`
- `Wizard_E2EE_Recovery_Title`, `_Description`, `_Warning`, `_Confirm`
- `Wizard_E2EE_Error_Bootstrap`
- `Wizard_E2EE_Bootstrapping`

All three resx files (`.resx`, `.de.resx`, `.fr.resx`) get the same set; the German and French translations come along in the same change.

## Tests

### Unit (`E2EESetupStepViewModelTests`) — xunit.v3 + `Xunit.Assert` + NSubstitute

- `Initial_State_ShouldBeChoice_WithToggleOn`
- `Proceed_FromChoice_WithToggleOn_ShouldBootstrapAndEnterRecoveryState`
- `Proceed_FromChoice_WithToggleOff_ShouldEnterConfirmingOptOut`
- `Proceed_FromConfirmingOptOut_ShouldSignalAdvanceWithoutEnabling`
- `OptOutGoBack_FromConfirmingOptOut_ShouldReturnToChoice`
- `Proceed_FromSavingRecoveryCode_WithoutCheckbox_ShouldNotAdvance`
- `Proceed_FromSavingRecoveryCode_WithCheckbox_ShouldSignalAdvanceAndStartSync`
- `Bootstrap_Failure_ShouldStayInChoice_WithErrorMessage`
- `RecoveryCode_NeverAppearsInLogs` — capture log scope, assert recovery code substring is absent

### Wizard-level integration

- `LoggedInCloudUser_AccountE2EEOff_ShouldShowE2EEStep`
- `LoggedInCloudUser_AccountE2EEAlreadyOn_ShouldNotShowE2EEStep`
- `NotLoggedIn_ShouldNotShowE2EEStep_AndShouldShowProviderStep`
- `Back_FromE2EEStep_PreBootstrap_ShouldReturnToAccountStep`
- `Back_FromE2EEStep_PostBootstrap_ShouldBeDisabled`

## Out of scope (YAGNI)

- Telemetry of opt-out reasons.
- Re-prompting users who finished the wizard before this feature ships.
- Changes to Settings → Account E2EE toggle.
- Changes to multi-device approval flow.
