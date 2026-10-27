# WinWright round findings: fixes — checklist

**Status:** Open
**Owner:** Marco Altmann
**Written:** 2026-10-27
**Origin:** Findings F1–F7 of [2026-10-27-winwright-test-round-checklist.md](2026-10-27-winwright-test-round-checklist.md)
(section *Findings*). Each step names the code, the fix, and the test that locks it.

Tick a box in the commit that lands its step.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

**Where each step lands.** Steps marked *branch* fix behaviour that `feature/account-switch-cleanup`
introduced, so they go on that branch before its PR. Steps marked *own branch* are older than this
branch: they get a `feature/<name>` from `develop` of their own, so the cleanup PR stays reviewable.

## Decision gates

| Gate | Question | Blocks | Decision |
|---|---|---|---|
| G1 | When `settings.json` cannot be parsed at startup, should Pia keep the broken file aside and start with defaults, or refuse to start until the user acts? | 1 | Keep it aside and start with defaults (owner, 2026-10-27). |
| G2 | When the server answers `409 session_ended`, should the client sign out at once (same path as a refresh 401), or first try one refresh? | 5 | Sign out at once, no refresh attempt (owner, 2026-10-27). |

## Steps

- [x] **1. Keep an unreadable settings.json instead of overwriting it (F1, own branch).** Before any
  save can replace it, a settings file that fails to parse is moved aside as
  `settings.json.unreadable-<UTC timestamp>`, and the log says so.
  *Deps:* G1 (decided) · *Effort:* S · *Value:* High
  *Landed:* `feature/settings-keep-unreadable` `09b7007f`. The wipe-order test needs
  `LocalDataWipeService`, so it follows on this branch once both are merged.
- [x] **2. Show the server URL and trust setting while a clear is pending (F2, branch).** Find out why
  Settings → Account shows an empty Server URL and an unticked trust box when a pending clear failed at
  startup, and make the view load them.
  *Deps:* none · *Effort:* XS · *Value:* Med
- [x] **3. Make a failed device approval visible and localized (F3, own branch).** The pending-device
  errors use resource strings and show where the approval prompt was raised, not only in a Settings
  snackbar.
  *Deps:* none · *Effort:* S · *Value:* High
  *Landed:* `feature/device-session-errors` `f9a3960e`.
  *Follow-up:* develop has no `UserMessage`, so the body uses `.Message` in two places; switch it once
  this branch and the cleanup branch meet.
- [x] **4. Stop prompting once this device cannot approve (F4, own branch).** The pending-device loop
  ends when this device turns out to be revoked or its session has ended. It also re-checks each
  device's status right before showing that device's prompt.
  *Deps:* none · *Effort:* XS · *Value:* Med
  *Landed:* `feature/device-session-errors` `fced5a3a`.
- [x] **5. Treat `409 session_ended` as a sign-out (F5, own branch).** Device registration and key
  fetches that get `session_ended` sign the client out with the same notice a refresh 401 gives.
  *Deps:* G2 (decided) · *Effort:* S · *Value:* High
  *Landed:* `feature/device-session-errors` `683ee7cb`.
  *Follow-up:* after the merge, check that this sign-out raises no local-data prompt and that the notice
  reads as the checklist quotes (develop's `Auth_SignedOut_Rejected` differs). The merge conflicts in
  `DeviceManagementService`, `AccountSettingsViewModel` and the registration tests.
- [x] **6. Show progress and bound the wait on sign-out (F6, branch).** *Sign out and clear* / *keep*
  shows a busy state during the final upload and the logout call. Both are bounded by short timeouts
  instead of the 60 s / 100 s HttpClient defaults.
  *Deps:* none · *Effort:* S · *Value:* High
- [x] **7. Drop the redundant Close button from the account-deleted prompt (F7, branch).** The prompt
  offers exactly *Clear this device* and *Keep on this device*.
  *Deps:* none · *Effort:* XS · *Value:* Med
- [x] **8. Harden the throwaway-profile rules for UI runs (round lessons, own branch).** The UI
  automation docs and `Invoke-UiScripts.ps1` seed `assistantFilesFolder` for a fresh profile, and warn
  that a copied profile starts the live profile's MCP plugins.
  *Deps:* none · *Effort:* XS · *Value:* Enabler
  *Landed:* `feature/ui-profile-hardening` `b1911d58`.

## Step details

**1 — F1.**
- *Code:* `src/Pia.Wpf/Services/JsonPersistenceService.cs`, the `catch (JsonException)` in the load path
  (around line 70). It sets `_cached = CreateDefault()`, and the next save writes those defaults over the
  file.
- *Seen:* in the A9 run, the startup `ServerUrl` save in `Bootstrapper.InitializeAsync` did that save.
  Release saves in the same place.
- *Fix (G1: keep aside):*
  - On `JsonException`, move the file to `…unreadable-<yyyyMMddTHHmmssZ>` with `File.Move`.
  - Log a warning naming only the file name, never the content.
  - Then return defaults.
  - Do this in the base class, so `providers.json` and `templates.json` get it too.
- *Leave alone:* `LocalDataWipeService.EnsureSettingsReadableAsync`. It already refuses a broken file and
  must still see it. The wipe runs before any store is read, so the order already holds; keep a test
  for it.
- *Tests:* a malformed file is moved aside and defaults are returned. Afterwards a save does not touch
  the aside copy.

**2 — F2.**
- *Code:* `AccountSettingsViewModel.InitializeAsync` (around line 426) sets `ServerUrl` from
  `ISettingsService`.
- *Seen:* in the A8 run, startup logged "Pending local-data clear failed", and `App.xaml.cs` then holds
  account-bound services while `LocalDataResetMarker.IsPending`. Settings → Account showed an empty
  Server URL and an unticked trust box, although `settings.json` held 18081 and `true`.
- *Find first:*
  - Is `InitializeAsync` called on that path?
  - Does it read settings before the `PIA_CLOUD_SERVER_URL` override was saved?
  - Did the view bind to a VM instance that never initialized?
- *Repro:* copy any profile, create `pending-local-data-reset`, hold `providers.json` open with
  `FileShare.None`, launch the Debug exe with the usual three variables, and open Settings → Account.
- *Test:* a VM test that, with the marker pending, `InitializeAsync` still sets `ServerUrl` and
  `TrustSelfSignedCertificates`.

**3 — F3.**
- *Code:* in `AccountSettingsViewModel.CheckForPendingDevicesAsync` / `HandlePendingDevicesAsync`
  (around lines 872–940), the snackbars use literal "Error", "Failed to approve device: …", "No
  Requests", "No pending device requests found." and "Failed to check for pending devices." The
  `EnableE2EEAsync` catch has the same pattern.
- *Seen:* the approval prompt can open while another view is active (S3: on the Assistant view). The
  snackbar presenter belongs to Settings, so the failure was invisible.
- *Fix:*
  - Move the strings to `ViewStrings.resx` / `.de` / `.fr` under `Settings_E2EE_PendingDevices_*`.
  - Show approval failures through `IDialogService` (a one-button message dialog), or a snackbar
    presenter that lives in `MainWindow`.
  - Keep `DeviceRegistrationRejectedException.UserMessage` as the body when present.
- *Tests:* `ViewModels/AccountSettings…` tests assert the dialog service is called with the resource
  strings on approve failure. Add the resource keys to whatever resx-parity test exists.

**4 — F4.**
- *Code:* `HandlePendingDevicesAsync` loops over a list fetched once.
- *Seen:* `DeviceManagementService.ApproveDeviceAsync` found this device Revoked, re-registered, and
  got `409 session_ended`. The loop then went on prompting for four more devices that the server had
  revoked meanwhile.
- *Fix:*
  - On `DeviceRegistrationRejectedException` with `session_ended`, or a revoked-self result, break out
    of the loop.
  - Before each prompt after the first, re-fetch `GET api/e2ee/devices` and skip devices no longer
    Pending.
- *Tests:* with a fake device service, the second device is not prompted after a session_ended on the
  first. A device that turned Revoked between prompts is skipped.

**5 — F5.**
- *Code:* `DeviceManagementService` builds `DeviceRegistrationRejectedException` from the error code
  (around line 479–497).
- *Seen:* register and `GET devices/{id}/wrapped-umk` can answer `409 {"error":"session_ended"}` once the
  refresh-token row is gone (#7851). Today the client logs a failure and stays signed in until the next
  refresh 401.
- *Fix (G2: sign out at once):*
  - No refresh attempt first. Map `session_ended` to the existing forced sign-out path with `AuthSignOutReason.ServerRejectedSession`.
    That path already shows "Your session ended or was revoked on the server. Please sign in again."
  - Do it in one place (the service), not per caller.
  - The sign-out must not trigger the local-data prompt. Same as a refresh 401.
- *Tests:* a register 409 session_ended raises exactly one sign-out with that reason. A 409
  too_many_pending_devices does not sign out.

**6 — F6.**
- *Code:*
  - `AccountSwitchPrompts.SignOutAsync` calls `ILocalDataResetService.UploadPendingChangesAsync`, which
    runs `SyncNowAsync` (60 s HttpClient timeout) and `FlushAsync(30 s)`.
  - `AuthService.LogoutAsync` then posts `/auth/logout` with the default 100 s timeout.
  - A7 measured about 3.5 min with no feedback against a paused server, and the server-side refresh
    token survived.
- *Fix:*
  - The account view shows a busy overlay with a localized "Uploading your latest changes…" /
    "Signing out…" while either runs.
  - Give the final sync and the logout call a linked `CancellationTokenSource` of about 15 s each, and
    treat a timeout as failure (the upload-failed prompt already handles that).
  - The surviving server token is acceptable once the logout is bounded. Note it in the PR as checked:
    the bearer-token sign-out stays as it is.
- *Tests:*
  - `AccountSwitchPromptsTests`: a hanging upload completes as "not uploaded" within the bound.
  - `AuthService` tests: a hanging logout still clears local state.

**7 — F7.**
- *Code:* `AccountSwitchPrompts.OfferClearAfterDeletionAsync` calls `ShowChoiceDialogAsync` without
  `closeText`. `DialogService.ShowChoiceDialogAsync` then falls back to `Common_Close`, and the dialog
  renders a third button that returns `Dismissed`, the same as Keep.
- *Fix:* let `closeText: ""` mean "no close button": `CloseButtonText = closeText ?? …` already passes
  `""`. Check that Wpf.Ui hides an empty close button, then pass `""` from
  `OfferClearAfterDeletionAsync`. If Wpf.Ui still renders it, add an explicit `showClose: false`.
- *Tests:* `AccountSwitchPromptsTests` asserts the deletion prompt passes no close text.

**8 — round lessons.**
- `docs/ui_automation/ui-automation-playbook.md` (throwaway-profile section) and
  `tests/ui-scripts/README.md`: a fresh profile needs `assistantFilesFolder` pre-seeded in
  `roaming\settings.json`, because `PIA_DATA_DIR` does not redirect it.
- Copying a live profile also copies its MCP plugins, which start on launch.
- `Invoke-UiScripts.ps1`: seed the folder when it creates the throwaway profile.

## Not yet planned

These are test gaps from the round, not defects. Pick them up in the next WinWright round:
- **C4:** *Set up again* from the "Device removed" dialog, and opening Settings only after it was raised.
- **C6:** scheduling a clear while an approval wait runs.
- **S1:** a token wipe by piaserver followed by a key fetch.
- **S5:** an encrypted scheduled job before an E2EE off→on flip.
- **A6:** Cancel on the sign-in prompt.
- **B5:** the plaintext push on a non-revoked device, so the gate rather than `device_revoked` decides.
- **B6:** the login-body `isE2EEEnabled` half. Blocked until Pia bumps `lib/Pia.Wpf` and fills it in
  `LoginCodeExchangeService` / `LocalAuthService`.

## Suggested order

1. **7, then 2:** XS, on the branch, unblock the cleanup PR.
2. **6:** branch, the last behaviour this branch made worse.
3. **5 and 4** together on one `feature/device-session-errors` branch, since
   both touch the pending-device path.
4. **3** on the same branch, because it edits the same methods.
5. **1** on its own branch.
6. **8** whenever the next UI round is planned.
