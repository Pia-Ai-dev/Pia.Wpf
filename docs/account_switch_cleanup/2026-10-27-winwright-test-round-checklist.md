# WinWright test round: account-switch cleanup, E2EE hardening — checklist

**Status:** Round run 2026-10-27. Findings F1–F7 open.

Not done or only partly done:
- A9 is partial (F1).
- C4: *Set up again* and the late-Settings case were not run.
- C6: the reset half was not run.
- S1: partial; the token-wipe case was not run.
- S3 fails (F3).
- S5: the before-flip case and the research half are missing.
- B6: the login-body half is blocked.
- A6: Cancel on the sign-in prompt was not pressed.
- B5's plaintext push was stopped by `device_revoked`, not by the gate under test.
- A5's evidence comes from account B.
**Owner:** Marco Altmann
**Written:** 2026-10-27
**Origin:** Branch `feature/account-switch-cleanup` (merge base `68090ded`). Covers the manual half of
step 11 in [2026-10-20-local-data-cleanup-checklist.md](2026-10-20-local-data-cleanup-checklist.md) and
the client half of
[../e2ee_pentest/2026-10-20-e2ee-downgrade-hardening-checklist.md](../e2ee_pentest/2026-10-20-e2ee-downgrade-hardening-checklist.md),
plus the device-approval and revocation fixes that rode along on the branch.

The round drives the **Debug** client with WinWright against a **copy of the owner's live profile** and the
local Podman e2e stack on `https://localhost:18081`. The **piaserver** session owns all server work: the
stack, the accounts, and the account state flips. Tick a box in the commit that records its result. A
failure gets a one-line note under the step and a fix commit. It is never ticked.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Ground rules

- **Never `https://localhost:8081`.** That stack holds the owner's real data. Use only 18081, and do not
  run `pia-stack.ps1 reset`/`e2e` on it without asking piaserver.
- **Debug exe only:** `src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.exe`. Only Debug
  honours `PIA_CLOUD_SERVER_URL`. A Release build rewrites `ServerUrl` to `cloud.pia-ai.de`.
- **Every launch sets all three variables:** `PIA_DATA_DIR=$T\roaming`, `PIA_LOCAL_DATA_DIR=$T\local`,
  `PIA_CLOUD_SERVER_URL=https://localhost:18081`, with `$T = G:\Git\Pia.Wpf\artifacts\ww-round`. Use
  `artifacts\`, not `%TEMP%`, because the TEMP cleaner deletes files there.
- **Choice-dialog buttons are not consistent. Read the button's Name before every press.**
  - Sign-in prompt: `PrimaryButton` = *Keep and sign in*, `SecondaryButton` = *Clear first*.
  - Sign-out prompt: `PrimaryButton` = *Sign out and clear*, `SecondaryButton` = *Sign out and keep*.
  - Deleted-account prompt: `PrimaryButton` = *Clear this device*.
  - Every prompt: `CloseButton` = Cancel.

  The copied profile may run in German, so match on ids and verify the Name. Never match on English
  text.
- **Every *clear* restarts Pia** through `Environment.ProcessPath` with `UseShellExecute`. The WinWright
  attachment is gone afterwards. Re-attach to the new `Pia.Wpf.exe`. Before continuing, confirm that its
  log lands in `$T\local\Logs`, which proves the env vars were inherited.
- **Pre-seed every fresh throwaway profile's `assistantFilesFolder`.** `PIA_DATA_DIR` does not redirect it.
  A fresh profile defaults to the real `Documents\Pia Assistant` and indexes the real vault. client2–4
  did this on 2026-10-27. Nothing was written there, but an assistant turn could have written.
- **A copied live profile starts the live profile's MCP plugins.** Plugin
  `17f72006…` (49 tools) came up in every copy, with whatever credentials its config holds. Remove or
  disable plugins in the copy before launch if that matters.
- **One scripted `/auth/login/local` per account per run.** An extra login rotates the refresh token, and
  the running client signs out.
- `AccountDeletion` → `PrimaryButton` deletes the **server** account. Assert `Settings_Account_ServerUrl`
  reads 18081 first.

## Steps

### P — Preparation

- [x] **P1. Stack up (piaserver).** Bring up the 18081 e2e stack from Pia `develop` `4f3d9eca`, which holds
  #7851, #7864 and #7901.
  2026-10-27: up, `/health` 200, volumes kept.
  *Deps:* none · *Effort:* XS · *Value:* Enabler
- [x] **P2. Accounts (piaserver).** Create throwaway account **A** with E2EE on (plays "work") and **B**
  without E2EE (plays "private"), and hand over their passwords and ids.
  2026-10-27: both created with E2EE off. The server turns E2EE on only when the first device posts its
  recovery copy, so A gets it through client onboarding.
  *Deps:* P1 · *Effort:* XS · *Value:* Enabler
- [x] **P3. Real-profile fingerprint.** Hash every file under the real `%APPDATA%\Pia`, `%LOCALAPPDATA%\Pia`
  (without `Models`) and `Documents\Pia Assistant` into `$T\real-before.txt`.
  2026-10-27: 15 files.
  *Deps:* none · *Effort:* XS · *Value:* High
- [x] **P4. Profile copy.** With no Pia running, copy the live profile into `$T` and point the copied
  `assistantFilesFolder` at `$T\files`.
  2026-10-27: an untouched copy also sits in `$T\pristine`, and A8–A10 each ran on a copy of it of their
  own.
  *Deps:* P3 · *Effort:* XS · *Value:* Enabler
- [x] **P5. Copy baseline.** Record what the copy holds before any test touches it: providers, templates,
  chats, todos, reminders, kanban columns, vault notes, and the 13 synced preferences.
  2026-10-27:
  - Providers: Pia Cloud, Ollama, Mistral Agent.
  - No custom templates and no assistant chats.
  - In `history.db`, per the wipe log: 17 Sessions, 1 Todo, 2 KanbanColumns.
  - Vault: `AGENTS.md`, `templates.md`.
  - Preferences: UI language DE, `targetLanguage` 1, default template `…0005`.
  *Deps:* P4 · *Effort:* XS · *Value:* Enabler

### A — Local-data cleanup on account switch

- [x] **A1. Sign-in prompt: Keep.** Sign in to B from Settings → Account with the copied data present;
  *Keep and sign in* signs in, and the first sync uploads the local data to B.
  2026-10-27: pass. `PrimaryButton` "Keep and sign in". The first-sync push sent settings, 2 providers,
  17 sessions, 2 kanban columns and 1 todo. piaserver's count 2 matches exactly; count 1 had no content
  rows.
  *Deps:* P2, P5 · *Effort:* XS · *Value:* High
- [x] **A2. Sign-out prompt: Clear, with a final upload.** Edit a chat, then sign out of B with *Sign out
  and clear*; the edit reaches the server, Pia restarts, and the copy matches the wipe contract.
  2026-10-27: pass.
  - Log order: "Pushed chat" (11:11:27) → `POST /auth/logout` → "Restarting Pia" → "Local synced data
    cleared" (17 Sessions, 4 messages, 1 chat, 1 todo, 2 columns, 2 providers).
  - Kept: `syncDeviceId`, `serverUrl`, the wizard flag, the vault, and `history.db.key`.
  - Reset: `targetLanguage`, `defaultTemplateId`, `managedPersonaStoreInitialized`, and
    `lastCatalogVersion`.
  - The chat turns themselves ended in Error, because the vLLM model was not set yet; the chat was still
    saved and synced.
  *Deps:* A1 · *Effort:* XS · *Value:* High
- [x] **A3. Nothing deleted on the server.** After A2, B's server rows equal the count before the clear,
  and signing in to B again restores everything by download.
  2026-10-27: pass.
  - Count 3 equals count 2, with no `DeletedAt` set. The chat's UpdatedAt is the second message. Only
    `refresh_tokens` went 2 → 1.
  - Re-sign-in with no prompt: the pull inserted 20 rows and updated 2. The chat came back, plus 2
    providers, `targetLanguage` 1 and the default template.
  - Watch items: the startup backfill re-pushed the chat it had just pulled, and the first push re-sent
    the 2 reseeded kanban columns. piaserver is checking for duplicates.
  *Deps:* A2 · *Effort:* XS · *Value:* High
- [x] **A4. Sign-in prompt: Clear first.** With local data present, choose *Clear first* before signing in
  to A; Pia restarts with the store empty and asks for the sign-in again.
  2026-10-27: pass. `SecondaryButton` "Clear first" restarted before any `/auth/login`. Only Pia Cloud
  was left, preferences were reset, and nothing was signed in.
  *Deps:* A3 · *Effort:* XS · *Value:* High
- [x] **A5. First sync after a clear keeps the account's preferences.** After A4, sign in to A and check
  that A's server settings row and its theme and languages win over the reset defaults.
  2026-10-27: pass.
  - Every first push after a clear logged `settingsIncluded: False`: B's re-sign-in, and A's.
  - B's preferences (`targetLanguage` 1 and the default template) came back from the server instead of
    being overwritten.
  - A is a fresh account with no settings row of its own, so B is the evidence.
  *Deps:* A4 · *Effort:* XS · *Value:* High
- [x] **A6. Sign-out prompt: Keep, and Cancel.** *Sign out and keep* leaves every store intact, and
  `CloseButton` on either prompt changes nothing.
  2026-10-27: pass for the sign-out prompt. Cancel on the sign-in prompt was not pressed.
  - Cancel left the session signed in, with no marker.
  - "Sign out and keep" (`SecondaryButton`) logged out without a restart. The providers and
    `targetLanguage` stayed.
  - The prompt wording is right for both account types. On B (no E2EE) it says API keys are not stored
    in Pia Cloud. On A (E2EE, no own providers) it has no key line.
  *Deps:* A1 · *Effort:* XS · *Value:* Med
- [x] **A7. Upload failure on sign-out.** With the API paused, *Sign out and clear* shows *Couldn't upload
  your latest changes*. Cancel keeps everything. Confirm clears anyway.
  2026-10-27: pass, run on client2 (A) with `podman pause`.
  - The prompt appeared after the pull's 60 s HttpClient timeout. "No" left the session, the data and
    the absent marker untouched.
  - The repeat with "Yes": the marker was written, then `/auth/logout` hung until cancelled after 100 s
    ("Failed to revoke refresh token on server"), then the restart and the wipe.
  - Finding F6: about 3.5 min with no progress shown, and the server-side refresh token survives.
  *Deps:* A1 · *Effort:* XS · *Value:* High
- [x] **A8. Crash guarantee via the marker.** A hand-written `pending-local-data-reset` is finished at
  startup before any sign-in UI. A failed wipe keeps the marker, and sign-in then shows *Local data not
  removed yet*.
  2026-10-27: pass.
  - With `providers.json` held open exclusively, the log reads "Pending local-data clear failed; it will
    be retried", the marker stays, and sign-in shows the pending prompt.
  - Released the lock and pressed *Yes*: the restart kept the env vars, the clear finished at startup
    (2 providers, 17 Sessions, 1 Todo, 2 kanban columns), and the marker was gone.
  - Synced preferences were reset. `serverUrl`, the wizard flag, the vault and `history.db.key` were kept.
  *Deps:* P5 · *Effort:* S · *Value:* High
- [ ] **A9. Malformed settings.json is never overwritten.** A marker plus an unparsable `settings.json`
  leaves both files byte-identical after start.
  2026-10-27: the wipe half passes. It refused the file (`EnsureSettingsReadableAsync` threw a
  `JsonException`), kept the marker, and left `providers.json` byte-identical. The app half fails, but not
  because of this branch: `JsonPersistenceService` loads defaults for an invalid file, and the startup
  `ServerUrl` save writes them back. So `settings.json` was replaced with defaults and the first-run
  wizard opened. Release saves in the same place. Finding F1.
  *Deps:* P5 · *Effort:* XS · *Value:* High
- [x] **A10. First-run wizard path.** With `hasCompletedFirstRunWizard=false` and local data, the wizard's
  sign-in asks first. *Clear first* restarts into the wizard again, not marked done, and does not run the
  rest of startup.
  2026-10-27: pass, in German.
  - "Vor der Anmeldung": `PrimaryButton` "Behalten und anmelden", `SecondaryButton` "Zuerst entfernen",
    with the API-key notice.
  - The clear restarted into the wizard with one process. The wizard flag stayed false, nothing was signed
    in, and the wipe was logged.
  - Signing in to B afterwards went straight through with no prompt.
  *Deps:* P5 · *Effort:* XS · *Value:* High
- [x] **A11. Account deletion offer and wording.** Deleting throwaway account B states that keys are lost
  and the deletion is final, then offers *Clear this device* and *Keep on this device*.
  2026-10-27: pass, in French.
  - The deletion dialog says the data can be neither exported nor restored after the grace period. B
    has no E2EE, so there is no key line.
  - `POST /auth/account/delete` 200.
  - "Compte supprimé" offers *Vider cet appareil* / *Conserver sur cet appareil*, with the restart line,
    "irréversible", the vault line and the API-key line.
  - *Vider* → restart and wipe. The vault memory note survived.
  - Server side: B is disabled with DeletedAt set and purged after 14 days. Data rows are intact until
    then, and its sessions are gone.
  - Cosmetic F7: a third "Fermer" button does the same as Keep.
  *Deps:* A3 · *Effort:* XS · *Value:* High
- [x] **A12. Vault and API-key notices.** The prompts mention the vault memories (which stay) and the
  provider-key loss only when they apply, in en, de and fr.
  2026-10-27: pass.
  - With the vault holding only scaffolding (`AGENTS.md`, `templates.md`), there is no vault line in
    en/de. That is right, because `ListMemoriesAsync` skips scaffolding.
  - With one real note, the fr sign-in prompt adds "La mémoire de Pia sur cet appareil n'est pas
    supprimée…".
  - The key line appears only with own providers on a non-E2EE account.
  - *Keep* stays first and neutral in every language.
  *Deps:* A1 · *Effort:* XS · *Value:* Med

### B — E2EE downgrade hardening

- [x] **B1. Marker set and kept.** After onboarding on A, `e2EERequiredAccountIds` holds A's id, and it
  survives a later sign-out and clear.
  2026-10-27: pass.
  - "Turn on encryption" bootstrapped device `d48c4e19`, showed the recovery code, and put A's id
    `8b12aa70…` into the marker.
  - After "Sign out and clear", the id is still there, while the key, the UMK and the session are gone.
  - Note: *Copy to Clipboard* left the WinWright clipboard read empty. Not checked further.
  *Deps:* A5 · *Effort:* XS · *Value:* Enabler
- [x] **B2. Downgrade detected.** With A's E2EE flipped off by piaserver and no key on the device, sync
  pushes nothing in plaintext, and the main-window bar and the Account notice appear.
  2026-10-27: pass.
  - The copy signed in to A after the flip.
  - The log reads "Server reports E2EE off for an account that required it; refusing plaintext sync",
    with no push request.
  - The red bar with `E2EE_OpenDowngradeReview` and `Settings_Account_E2EEDowngradeNotice` both showed.
  - The server would have accepted plaintext at that point, so the block came from the client alone.
    piaserver found no new rows for A.
  *Deps:* B1 · *Effort:* XS · *Value:* High
- [x] **B3. Chats held while the key is missing.** A chat saved during B2 is not uploaded in plaintext,
  and it goes up encrypted after onboarding.
  2026-10-27: pass.
  - The log reads "Holding chat 7afbcd4d…: the account requires E2EE and this device has no key", with
    no PUT.
  - After recovery-code onboarding, the PUT returned 201.
  - piaserver: EncryptedPayload and WrappedDek are set, the title is empty, and the message list is
    empty.
  *Deps:* B2 · *Effort:* XS · *Value:* High
- [x] **B4. Warning clears when E2EE is back.** With A flipped on again, the bar and the notice go away
  without an override.
  2026-10-27: pass. After the on-flip, the red bar and the notice were gone. In their place was the
  normal "End-to-end encryption setup required" bar with the approval and recovery options.
  *Deps:* B2 · *Effort:* XS · *Value:* High
- [x] **B5. Override.** *Sync without encryption* asks for confirmation. Cancel keeps sync blocked.
  Confirm removes A from the marker and sync resumes, and it refuses if E2EE came back meanwhile.
  2026-10-27: the No and race halves pass.
  - "No" kept sync blocked and kept the marker.
  - With the confirm open, piaserver flipped E2EE on, then *Yes* → "Unencrypted sync not allowed: the
    server did not confirm E2EE is off". The marker was kept.
  - Success half, after a second off-flip: the copy (no key) signed in, *Yes* → "User allowed
    unencrypted sync…", and `e2EERequiredAccountIds` was emptied.
  - Its plaintext push got `403 device_revoked`, because the copy still held revoked `d48c4e19`.
    piaserver confirms no plaintext row landed. Pass for the client behaviour.
  *Deps:* B2 · *Effort:* XS · *Value:* High
- [x] **B6. Evidence from refresh and login.** A fresh device signing in to A gets marked from the token
  claim or the refresh body, before the first push.
  2026-10-27: pass for the claim, refresh and status path. A fresh client2 profile held A's id in
  `e2EERequiredAccountIds` right after `POST /auth/login/local`, and no push ran before it. The login-body
  half stays blocked (see the P1 gate).
  *Deps:* P1 · *Effort:* XS · *Value:* Med

### C — Device approval, revocation, key loss

- [x] **C1. Approval dialog.** A second throwaway client asks to join A, and the approving client shows
  the labelled fields `DeviceApproval_DeviceName`, `_DeviceFingerprint` and `_OwnFingerprint`.
  2026-10-27: pass.
  - client2 (`0e8db4cd`) started approval from the wizard.
  - On the copy, "Check for device requests" showed "New device requesting access" with:
    - Device name "HYD-DEV1";
    - Fingerprint 274A-CEA8-EFE9-04EB;
    - This device's fingerprint 2795-50D2-2457-7103.
  - *Approve* (`PrimaryButton`) → `POST devices/approve` 200.
  *Deps:* B1 · *Effort:* XS · *Value:* High
- [x] **C2. Hostile device name.** For an API-registered device with a long or odd name, the name is
  sanitized (`DeviceApproval_NameSanitized`), and neither the reject prompt nor the approved notice repeats
  it.
  2026-10-27: pass.
  - piaserver registered device `96211e24` with a 63-char name: "Ünïcode-PC ", an "e" with 3 stacked
    combining marks, and 46 × W.
  - The server refused U+E000 as private use, so it is not in the name.
  - In the dialog:
    - the third mark was dropped;
    - `DeviceApproval_NameSanitized` showed;
    - the name wrapped inside the field and ended in an ellipsis.
  - "Don't approve" (`CloseButton`) → "Reject device?". The prompt has no name and stays narrow.
  - *Yes* → revoke 200. The name is not in the Debug log.
  - The approved notice (`Settings_E2EE_DeviceApproved_Message`) takes no name argument either.
  *Deps:* C1 · *Effort:* XS · *Value:* High
- [x] **C3. Joining device names the approver.** After approval, the joining client names the approving
  device.
  2026-10-27: pass.
  - client2 showed "Bestätigt von: HYD-DEV1" and 2795-50D2-2457-7103, which matches the copy.
  - `E2EEOnboarding_ConfirmApprover` → "E2EE onboarding completed successfully".
  *Deps:* C1 · *Effort:* XS · *Value:* Med
- [ ] **C4. Revocation notice.** With the copy's device revoked by piaserver, the notice shows even when
  Settings is opened late. *Sign out* takes the sign-out prompt without a final upload, and *Set up again*
  resets onboarding.
  2026-10-27: pass for *Sign out*.
  - First attempt: the revoke deleted the session bound to it (#7851), so the next refresh got 401 and
    signed the copy out before any dialog.
  - During B5 the copy signed in again, and the new session is unbound because the device stayed
    revoked. The push then got `403 device_revoked`.
  - "Device removed" showed *Set up this device again* / *Sign out* / *Close*.
  - *Sign out* → sign-out prompt → "Sign out and keep" → `/auth/logout`, with no chat or sync upload
    before it.
  - Not run: *Set up again*, and the late-Settings case (the dialog came up while Settings was open).
    Unit coverage: `AccountSettingsDeviceGoneTests`.
  *Deps:* C1 · *Effort:* XS · *Value:* High
- [x] **C5. Key loss detected.** With the copy's CNG device keys deleted, relaunching reports the lost key
  and offers the recovery-code path, with no duplicate sign-out.
  2026-10-27: pass, run on client2, because the copy had been revoked by then.
  - Deleted the `Pia-Device-ECDH/ECDSA-0e8db4cd…` keys and relaunched.
  - Sync went on with the DPAPI-held account key. Settings showed an empty "Device fingerprint:"
    (cosmetic).
  - Sign-out (keep) then sign-in: "This device's E2EE keys were lost; routing onboarding to recovery".
    The view opened on `E2EEOnboarding_RecoveryCodeInput`.
  - The recovery code then gave register 200, `recovery/activate` 200, and onboarding completed, with
    one sign-out only.
  *Deps:* B1 · *Effort:* XS · *Value:* High
- [ ] **C6. Onboarding poll survives cancel and reset.** Cancelling a pending approval, or scheduling a
  clear while one waits, leaves the onboarding screen in the state the user chose.
  2026-10-27: the cancel half passes. client3 (`ff0d594e`), in the wizard, ran `StartApproval` →
  `WaitingCancel`. 15 s later, past the poll interval, it was still on the Initial state. The reset half
  needs a signed-in Settings view with a wait in progress; still open.
  *Deps:* C1 · *Effort:* XS · *Value:* Med

### S — Server changes seen from the client (#7851, #7864, #7901)

- [ ] **S1. Ended session.** After piaserver deletes A's refresh tokens, a device setup or key fetch that
  gets `409 session_ended` leaves the client signed out, with no error loop.
  2026-10-27: partial. Seen during S3 instead of a token wipe.
  - The revoked copy's re-register got `409 session_ended`.
  - The client logged "Failed to handle pending device" and stayed signed in, with no loop.
  - The sign-out came only from the next `/auth/refresh` 401, about 1.5 min later.
  - The 409 itself is not treated as a sign-out. Finding F5.
  *Deps:* P2 · *Effort:* XS · *Value:* High
- [x] **S2. Revoked device, sync side.** After a revoke, `403 device_revoked` on sync and chats skips push
  and pull without plaintext retries or error spam. The next refresh's `401` signs out silently.
  2026-10-27: pass.
  - No sync or chat call went out between the revoke and the sign-out.
  - *Sync Now* → `/auth/refresh` 401 → `/auth/logout` → signed out, with the inline "Your session ended or
    was revoked on the server. Please sign in again."
  - The marker kept A.
  - No `403 device_revoked` was seen: the access token had already expired, so the first call was the
    refresh.
  *Deps:* C4 · *Effort:* XS · *Value:* High
- [ ] **S3. Revoked device, E2EE writes.** From the revoked client, approving a device and the recovery
  actions fail with a clear message, not a generic error.
  2026-10-27: fail on the message.
  - An approval prompt opened by itself on the Assistant view. Approve → the client saw its own status
    Revoked, re-registered, and got `409 session_ended`.
  - The error goes to a Settings-hosted snackbar, so nothing was visible. Its text is hardcoded English:
    "Error" / "Failed to approve device: …" in `AccountSettingsViewModel` around line 936. That text
    predates the branch. Finding F3.
  - The copy then went on prompting for the other 4 pending devices from one stale list, all of them
    already revoked on the server. Finding F4.
  *Deps:* S2 · *Effort:* XS · *Value:* Med
- [x] **S4. Too many pending devices.** With five Pending rows inserted by piaserver, a new device's
  registration shows a message that names the cause (`409 too_many_pending_devices`).
  2026-10-27: pass.
  - Pending on A: client3 plus 4 fillers, all registered through the API.
  - client4: `register` → 409, with the body `too_many_pending_devices`.
  - The wizard showed "Es warten zu viele Geräte auf Genehmigung. Genehmige oder entferne zuerst auf einem
    anderen Gerät eines davon und versuche es erneut." plus *Erneut versuchen*.
  *Deps:* P2 · *Effort:* XS · *Value:* Med
- [ ] **S5. Encrypted scheduled jobs and research.** On A, a scheduled job and a research session sync
  encrypted, without `400 validation_failed`, before and after an E2EE off→on flip.
  2026-10-27: partial. The scheduled job passes after one flip. The before-flip case was not run, and the
  research half can't be reached from the client.
  - Routine `e1b70c31` was created after the off→on flip and pushed encrypted with no 400.
  - piaserver: EncryptedPayload and WrappedDek are set, and every plaintext column is empty.
  - Research sessions can't be reached from the client: it always pushes an empty `ResearchSessions`
    collection. The server tests cover that half.
  *Deps:* B4 · *Effort:* XS · *Value:* High
- [x] **S6. No `e2ee_required` from an honest client.** Across the whole B group, the log shows no
  `403 e2ee_required` on `/api/sync/push` or the chat PUT, which proves the client gate fires first.
  2026-10-27: pass. The server log has no 403 at all for A since 10:21Z. The only push in the window was
  the encrypted one after onboarding.
  *Deps:* B5 · *Effort:* XS · *Value:* High
- [x] **S7. Key-material regression.** First-device bootstrap, approval, recovery set-up and recovery
  activation still succeed against the stricter shape, `CreatedByDeviceId` and session-binding checks.
  2026-10-27: pass. Every call returned 200, and the server log shows no `device_mismatch` or
  `invalid_field`:
  - first-device bootstrap with recovery set-up (B1);
  - recovery replace on an Active device (B3);
  - approve plus the joiner's wrapped-umk (C1/C3);
  - re-register plus `recovery/activate` after key loss (C5).
  *Deps:* C1 · *Effort:* XS · *Value:* High

### Z — Wrap-up

- [x] **Z1. Log privacy (Release).** A short sign-in, sign-out-and-clear and device-registration pass on
  a **Release** build leaves no email, device name, account data, or full URL in its log.
  2026-10-27: pass.
  - Run: a Release build with `policy.json` enforcing 18081 and trust. It did the wizard sign-in to A,
    a device register, a chat titled "ZEDPRIVACYMARKER…" (held), and sign-out + clear through the
    upload-failed prompt.
  - The log has 0 hits for the email, `local.test`, the marker, "HYD-DEV1", `localhost:18081`, "WinWright
    Work", the password prefix and the user id.
  - `policy.json` was removed afterwards.
  *Deps:* A2, C1 · *Effort:* XS · *Value:* High
- [x] **Z2. Teardown and leak check.** Delete the CNG keys of every throwaway profile and the throwaway
  server accounts, then re-hash the real profile against `$T\real-before.txt`.
  *Deps:* all above · *Effort:* XS · *Value:* High

  2026-10-27: pass.
  - All round instances stopped. CNG keys deleted for `d48c4e19`, `0e8db4cd`, `ff0d594e` and two
    release/a-copy ids.
  - `real-after.txt` is identical to `real-before.txt`: 15 files. The `Documents\Pia Assistant` folder
    timestamps are unchanged.
  - Server leftovers are piaserver's: account A, and B in its 14-day purge window.

## Findings

| Id | Where | Finding | From this branch? |
|---|---|---|---|
| F1 | A9 | An unreadable `settings.json` is replaced with defaults at the next start: `JsonPersistenceService` default plus the startup `ServerUrl` save. The wipe itself refuses it correctly. | No (pre-existing) |
| F2 | A8 | While a clear is pending, Settings → Account shows an empty Server URL and an unticked trust box, although `settings.json` holds both. Cosmetic. | Likely (the pending-reset hold) |
| F3 | S3 | A failed approval (revoked approver) goes to a Settings-hosted snackbar, so it is invisible when the prompt opens elsewhere. Its text is hardcoded English ("Error" / "Failed to approve device: …", `AccountSettingsViewModel` around line 936). | No: the hardcoded strings predate the branch, and 2e618cbe only swapped `ex.Message` for `message` |
| F4 | S3 | The pending-device prompts run through one fetched list. After the approver learns it is revoked, it still prompts for every other device, including ones revoked meanwhile. | Probably pre-existing |
| F5 | S1 | `409 session_ended` from register is logged as a failure and does not sign the client out; only the next refresh 401 does. | No (server-side change) |
| F6 | A7 | *Sign out and clear* with an unreachable server takes about 3.5 min with no progress (60 s upload timeout + 100 s logout timeout). The server-side refresh token survives the failed logout. | Partly (the upload step is new) |
| F7 | A11 | The deleted-account prompt shows a third "Close" button that acts the same as Keep. | Yes (cosmetic) |

## Decision gates

| Gate | Question | Blocks |
|---|---|---|
| P1 | Does 18081 run Pia `develop` `4f3d9eca` or later? | B6 and all of S. The sign-in body still carries no `isE2EEEnabled`: the `LocalLoginResponse` property exists only on this branch, the server's `lib/Pia.Wpf` pin lacks it, and `LoginCodeExchangeService`/`LocalAuthService` don't fill it. So B6 passes through the token claim, `/auth/refresh` and `/auth/me` only. The login-body half stays blocked; do not drop it. |
| C2 | Can a name that sanitizes to nothing pass the server's `DeviceName` validation? | If not, the "approver named even when the name sanitizes to nothing" path is unit-only (`DeviceNameDisplayTests`). |

## Step details

**P3 / Z2 fingerprint.** Reuse `Get-HashOrNull` from `tests/ui-scripts/Invoke-UiScripts.ps1`, but over
the whole trees, not only `settings.json` and `history.db`. This round runs outside that harness, so its
leak check does not apply.

**P4 copy.** At the time of writing, the live profile is signed out: no tokens, no `syncDeviceId`,
`e2EERequiredAccountIds` empty. Re-check this. If it is signed in, blank `encryptedAccessToken` and
`encryptedRefreshToken` in the copy, so it can never rotate the live session.
- `%APPDATA%\Pia` → `$T\roaming`.
- `%LOCALAPPDATA%\Pia` → `$T\local`, without `Models` (5.5 GB) and `Logs`. Copy `history.db` together
  with `-wal`, `-shm` and `.key`.
- `Documents\Pia Assistant` → `$T\files`, which holds `Vault` and `Playground`.

Edit `assistantFilesFolder` with the Edit tool, not `sed`, so CRLF survives. Afterwards, grep the copy
for any remaining `C:\Users\man\` path.

**P5 baseline.** Before the first launch, take hashes and counts. Afterwards, read them through the UI.
history.db is encrypted, so do not use `probe.mjs`. Note the copy's theme, UI and target language, and
default template, because A5 checks that the reset defaults do not overwrite them.

**A2 wipe contract.**
- **Gone:** custom templates, every provider except Pia Cloud, chats, todos, reminders, the kanban
  contents (default columns reseeded), `pending-sync-deletes.json`, and managed personas. The 13 synced
  preferences are back at their defaults.
- **Kept:** `hasCompletedFirstRunWizard`, `serverUrl`, `syncDeviceId`, `e2EERequiredAccountIds`, the vault
  notes and playground files, and `history.db.key`.
- **Logs:** the sign-out (`/auth/logout`) is logged before the first wipe line.
- **Restart:** chat sync, chat retention and the assignment drain do not start while the marker exists.

**A7 pause.** Ask piaserver to `podman pause` the API container. Run the step, then unpause.

**A8 marker.** Create an empty `$T\roaming\pending-local-data-reset` and launch. To force a failed wipe,
hold `$T\roaming\templates.json` open with an exclusive `FileShare.None` handle from pwsh. Release the
handle, relaunch, and confirm the clear then finishes and the marker is gone.

**A9.** Snapshot `settings.json` first. Truncate it to `{"theme":` and add the marker. After the check,
restore the snapshot. That is safe here, because the copy is signed out at that point.

**A10.** Run it after a sign-out, with the copy signed out. The wizard sign-in is reached through Next,
then sign in. Use the recipe in the memory note for the E2EE step.

**A11.** Run it last against B. It needs B's password in `AccountDeletion_Password` and a tick on
`AccountDeletion_Understood`. Recreating B afterwards is piaserver's job, if the round continues.

**A12.** Switch the UI language between runs of A1 and A6. The *Keep* wording must not push toward
clearing.

**B2 flip.** piaserver sets A's E2EE off so that `GET api/e2ee/status` answers `isEnabled: false`. The
device must hold no key for A, which is the state right after A4 or a sign-out and sign-in. Evidence:
- the `E2EE_OpenDowngradeReview` bar;
- `Settings_Account_E2EEDowngradeNotice`;
- no `push` lines in the log;
- piaserver confirms no new plaintext rows for A.

**B6.** Use a second fresh throwaway profile and look at `e2EERequiredAccountIds` straight after the
login.

**C1–C3** follow the two-client recipe in the `e2e-device-approval-run` memory. The approving client is
the copy. The joining client is a second fresh profile under `$T\client2`. Create the hostile name
through `POST api/e2ee/devices/register`. The server allows at most 63 characters and no `:`.

**C4.** piaserver revokes the copy's device. Navigate to Settings only after the notice has been raised
in the background.

**C5.** Close Pia. Delete `Pia-Device-ECDH-<id>` and `Pia-Device-ECDSA-<id>` with
`CngKey.Open(name).Delete()`, where `<id>` is the **copy's** `syncDeviceId`. Then relaunch.

**S1.** piaserver deletes A's refresh tokens ("revoke all sessions"). The client then triggers a device
setup or a key fetch (`GET devices/{own id}/wrapped-umk`) with its old access token.

**S2.** Sign the device in fresh on this build before the revoke. Sessions from older builds are not bound
to a device and survive a revoke. Expect push and pull to be retried about every 5 minutes until the
refresh's `401`. Use the log as evidence.

**S4.** piaserver inserts five Pending rows for A. Then start onboarding on a fresh client under
`$T\client3`. Older clients show a generic "failed to register". Record whether this branch does better;
if not, note it as a finding rather than a failure.

**S5.** Create the scheduled job and the research session after A5, while A holds its key. Repeat after
the B4 flip. A `400` carries the detail "<Type> <id>: E2EE entities must have EncryptedPayload and
WrappedDek."

**S7.** Watch the log during C1 and during a recovery set-up and activation on the copy. A single
`403 device_mismatch` on `wrapped-umk` right after a token rotation is a known server race. The client
should retry and finish; if bootstrap stops after register, that is a finding.

Not covered, because an honest client cannot reach them from the UI:
- `device_mismatch` on register;
- registration `invalid_field` (Windows names already pass);
- the sync-cursor cap;
- the NUL and U+2028 short-field refusals;
- the key-shape ranges (`invalid_field` on approve and wrapped-UMK).

C2 already checks that a forged name renders as data.

**Z1.** A Debug log is no evidence here: `SensitiveDebug` and `SafeUrl` print everything in Debug by
design. Set the run up like this:
- Build with `-c Release`.
- Next to the Release `Pia.Wpf.exe`, put a `policy.json` with
  `"enforce": { "serverUrl": "https://localhost:18081", "trustSelfSignedCertificates": true }`. Release
  ignores `PIA_CLOUD_SERVER_URL` and drops the trust setting unless a policy enforces them.
- Use a fresh profile under `$T\release`.

Delete the `policy.json` afterwards. In the log, grep for the A and B emails, the C2 device name,
`localhost:18081`, and a chat title.

## Coverage by commit

Each code commit on the branch maps to a step here or names the unit tests that cover it instead.

| Commit | Change | Covered by |
|---|---|---|
| 67e6b972 | Revocation and recovery state | C4, C5 |
| 3d95ca3a, a1e7e98d | Marker; finish a pending clear at startup | A8 |
| 49673ada, d1936b10 | Settings resets; wiper | A2 |
| f007c031 | Keep device settings when settings.json is unreadable | A9 |
| 67051130 | No reset prefs pushed over the account's | A5 |
| 0ee18861 | Keep the latch when the settings row cannot be decrypted | Unit only: `AppSettingsLocalResetTests`, `SyncClientServiceTransferOptimizationTests`. A row that fails to decrypt cannot be produced from the UI. |
| 08a8c340, f8c78640 | Chat flush; report earlier failures | A2, A7 |
| 58a94530, 1a757ccb, e19bb3ef | Reset service, prompts, sign-in and sign-out wiring | A1, A4, A6, A12 |
| 2f3ff345, bfe15b36 | Wizard prompt; stop startup after a wizard restart | A10 |
| 5c23b15e | Naming and latch classification | Unit only: `BootstrapperGraphValidationTests`. No behaviour change. |
| 0de78ce7 | Sign out before the wipe | A2 (log order) |
| 98d331b7 | Stale failed chat delete never replays | Unit only: `AssistantChatSyncServiceTests`. Needs a failed delete racing a newer save. |
| 5fee8822 | Delete-account wording | A11 |
| 2e618cbe | Reset sync state, policy, registration logs | A5, Z1 |
| 6d37129e, 50570a84 | Downgrade gate; clear the warning when E2EE is back | B2–B5 |
| 0a549d9a, dbe590e9, ada41599 | Approval fields, sanitized name, approver named, name out of the follow-ups | C1–C3 |
| c2e48781 | Cancelled poll keeps the onboarding screen | C6 |
| 58dcd943 | Hold services while a reset is pending; clear managed personas; refuse malformed settings | A2, A9 |
| d1f20f21 | Hold pushes when a refresh marks the account E2EE | B1, B6 |

Server changes on Pia `develop` `4f3d9eca`, as listed by piaserver:

| Pia item | Change | Covered by |
|---|---|---|
| #7851 | Session bound to a device; `409 session_ended` | S1 |
| #7851 | Revoke ends bound sessions; `403 device_revoked` on sync, chats and E2EE writes | C4, S2, S3 |
| #7851 | Registration validation; `409 too_many_pending_devices`; 7-day Pending expiry | C2, S4 |
| #7851 | `e2ee` claim, `isE2EEEnabled` on refresh and `/auth/me`; `403 e2ee_required`; ciphertext on scheduled jobs and research | B6, S5, S6 |
| #7864 | Key-material shape; session binding on wrapped-UMK writes | S7 |
| #7901 | `CreatedByDeviceId` must match on wrapped-UMK POST | S7 |

## Not yet planned

- A brand-new device's first sync can still overwrite the account's preferences. This is an open item
  on the cleanup checklist, so it is not a failure here.
- `isE2EEEnabled` in the sign-in body. This needs this branch merged, a `lib/Pia.Wpf` bump in Pia, and the
  server filling it in `LoginCodeExchangeService`/`LocalAuthService`.
- A client-side range check on the recovery KDF values from `GET recovery/wrapped-umk`. The client does
  not do it, and the server does not re-check stored values.
- The client revoking stale Pending devices (#7851 Unit 6). This is planned but not built on either side.
- Device-signed requests for account-changing calls (#7938). #7901 stays open until they exist.
- Pia.Docs coverage of the sign-in and sign-out prompts. The `help-corpus` skill checks it on the release
  branch.

## Suggested order

P1–P5. Then A8 and A9: cheapest, and they decide the crash guarantee. Then the A1 → A2 → A3 → A4 → A5
chain, then A6, A7, A10 and A12. Then B1 → B2 → B3 → B4 → B5 and B6, then S5 and S6, which reuse the B state. Then C1 → C2 → C3 (with S7
alongside C1), C6, S4, then C4 → S2 → S3, then S1, then C5, last because it destroys the copy's keys. A11 last of all, because it deletes B. Finish with Z1
and Z2.
