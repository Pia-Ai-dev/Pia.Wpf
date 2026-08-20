# Deploying Pia.Wpf with presets — what `policy.json` can manage

Current as of 2026-08-19. The policy engine was rewritten on the same day to key off **which keys the
admin wrote** instead of comparing values against the built-in defaults; see **Upgrade note** for what
that changes for an existing deployment.

## The two files

| File | Read from | Survives update? | Scope |
|---|---|---|---|
| `policy.json` | `<exeDir>` → **install root (parent)** → `%ProgramData%\Pia.Wpf\` | Install root: yes. `<exeDir>`: no | Everything in `AppSettings` |
| `appsettings.json` | `<exeDir>` only | **No** — Velopack replaces `current\` | Update feed + plugin signing |

`policy.json`: first existing path wins, no merging across paths. Put it at the **install root**
(`%ProgramFiles%\Pia.Wpf\`, next to the launcher stub) — `<exeDir>` is `…\current\`, which Velopack
overwrites on every update.

Shape:

```json
{
  "defaults": { "…": "…" },
  "enforce":  { "…": "…" }
}
```

Both blocks are a full `AppSettings` object: **camelCase** keys, enums as **strings**. Unknown keys are
ignored silently.

- **`defaults`** — applied only when the user's current value still equals the built-in default. The user
  can change it afterwards and their choice sticks.
- **`enforce`** — overwritten on every settings load *and* re-applied on every save, so hand-editing
  `%AppData%\Pia\settings.json` does not defeat it.

Policy is loaded once per process and cached — changes need an app restart. A malformed file (one
trailing comma) is caught, logged as a warning, and the **entire policy is discarded**. Confirm which
path was used in `%LOCALAPPDATA%\Pia\Logs\pia-*.log`: `Loaded enterprise policy from …` or
`No enterprise policy file found. Searched: …`.

## How a key is detected

A setting is "set by policy" when its **key is present** in the `defaults` or `enforce` object. Nothing
is inferred from the value, so:

- Enforcing a value that equals the built-in default works (`"autoUpdateEnabled": true` is a real lock).
- A block that omits a key never touches it — an `enforce` section listing only `theme` leaves the
  user's tool grants, PII keywords, per-mode provider/persona defaults and agent roster alone.
- Collections work in `defaults` as well as `enforce`.

Keys are matched **camelCase and case-sensitively**, the same way the deserializer reads them.
`"Theme"` is not `"theme"` and is ignored. Every unmatched key is logged as a warning naming the
section and the key, so a typo is visible in `%LOCALAPPDATA%\Pia\Logs\pia-*.log` rather than silent.

Object-typed settings are still replaced **wholesale**: writing `privacy` overwrites the entire
`PrivacySettings` object, including clearing `piiKeywords`. There is no per-sub-field merge — include
every sub-field you want to keep.

## Upgrade note

If you already deploy a `policy.json`, re-read it before rolling out a build from 2026-08-19 or later.
Keys that were previously ignored because their value happened to equal the built-in default now take
effect. `enforce.trustSelfSignedCertificates: false` and `enforce.autoUpdateEnabled: true` are the
common cases — both used to be silent no-ops and now do what they always read as doing. Anything you
wrote expecting it to be inert is now live. Deleting a key is how you make it inert.

## The one remaining gap: not every enforced setting greys out

Enforcement applies to every property, and the settings screens now render the read-only state for the
Providers, Personas, Assistant, Agent-runs, Chat, Meeting, Privacy, General, Optimize and Account
controls. Two known exceptions remain:

- `theme` has no control in the settings UI at all, so there is nothing to grey out. Enforcing it still
  applies.
- Window geometry, migration markers and sync state have no control by design.

Where a control has a second gate — git tools need git installed, the diarization sub-controls need
diarization on — the policy lock ANDs with it.

## Locks do not travel between devices

`SyncMapper` maps settings to the wire property by property, and none of the five lock settings is in
that list. A locked machine therefore cannot push its lock to an unmanaged device, and an unmanaged
device cannot pull one it has no `policy.json` to explain or clear. Enforced values *are* written into
the local `settings.json` (that is the anti-circumvention path), they just never leave the machine.

---

## Full inventory

`✓` deployable · `🔒` greys out in the UI when enforced · `⚠` read the note · `✗` don't

### Cloud, sync, identity

| Key | Type / default | |
|---|---|---|
| `serverUrl` | string, `null` | 🔒 Enforcing it also suppresses the hardcoded production URL written at startup, and beats the `PIA_CLOUD_SERVER_URL` dev override |
| `syncEnabled` | bool, `false` | ✓ |
| `trustSelfSignedCertificates` | bool, `false` | ✓ |
| `allowedSyncProviders` | string[], `null` | ✓ `"local"`, `"google"`, `"microsoft"`, `"entraid"` (case-insensitive). `null` or `[]` = all allowed. Disallowed providers are hidden in the first-run wizard *and* account settings. Read from `enforce` first, then `defaults`. Works in both blocks |
| `isE2EEEnabled` · `e2eeUmkVersion` · `e2eeRecoveryConfigured` | bool / int | ✗ device state |
| `encryptedAccessToken` · `encryptedRefreshToken` · `syncUserId` · `syncUserEmail` · `syncUserDisplayName` · `syncProvider` · `syncDeviceId` · `lastSyncTimestamp` · `lastPullETag` · `lastChatPullETag` · `lastPushedSettingsHash` · `lastCatalogVersion` · `e2eeEncryptedUmk` · `e2eeDeviceId` · `assistantChatsBackfilledAt` · `managedPersonaStoreInitialized` | — | ✗ runtime state; presetting these corrupts the sync cursor |

### Appearance, startup, language

| Key | Type / default | |
|---|---|---|
| `theme` | `System` \| `Dark` \| `Light`, `System` | 🔒 |
| `uiLanguage` | `EN` \| `DE` \| `FR`, `EN` | 🔒 |
| `targetLanguage` | `EN` \| `DE` \| `FR`, `null` | ✓ default output language |
| `targetSpeechLanguage` | `Auto` \| `EN` \| `DE` \| `FR`, `Auto` | 🔒 |
| `launchAtStartup` | bool, `true` | 🔒 ✓ |
| `startMinimized` | bool, `false` | 🔒 ✓ |
| `autoUpdateEnabled` | bool, `true` | ✓ |
| `defaultWindowMode` | `Optimize` \| `Assistant`, `Optimize` | ✓ |
| `userOperatingMode` | `Personal` \| `Business`, `null` | ✓ |
| `hasCompletedFirstRunWizard` | bool, `false` | ✓ mechanically settable to skip the wizard — untested as a deployment lever, and it skips provider setup too |
| `flowPinned` · `lastActiveView` · `draftText` · `windowWidth` · `windowHeight` · `windowLeft` · `windowTop` · `todoColumnWidths` | — | ✗ per-user window state |

### Hotkeys, capture, output

| Key | Type / default | |
|---|---|---|
| `optimizeHotkey` | record `{modifiers, key, virtualKeyCode}`, Ctrl+Alt+O | ✓ |
| `assistantHotkey` | same, Ctrl+Alt+P | ✓ |
| `fastPathHotkey` | same, `null` | ✓ |
| `autoCaptureSelectedText` | bool, `true` | ✓ |
| `defaultOutputAction` | `CopyToClipboard` \| `AutoType` \| `PasteToPreviousWindow` | 🔒 |
| `autoTypeDelayMs` | int, `10` | 🔒 |

`modifiers` is a `[Flags]` enum — `None=0, Alt=1, Control=2, Shift=4, Windows=8`; Ctrl+Alt is `3`.

### Speech & voice

| Key | Type / default | |
|---|---|---|
| `sttBackend` | `Whisper` \| `Parakeet`, `Parakeet` | 🔒 |
| `whisperModel` | `Tiny` \| `Base` \| `Small` \| `Medium` \| `Large`, `Base` | 🔒 used only when `sttBackend` is `Whisper` |
| `ttsEnabled` | bool, `false` | ✓ |
| `ttsVoiceModelKey` | string, `en_US-lessac-medium` | ✓ |

### AI providers, personas, templates

| Key | Type / default | |
|---|---|---|
| `allowProviderManagement` | bool, `true` | 🔒 **Put this in `enforce`.** `false` hides Add provider and refuses add/edit/delete; the configured providers stay usable. The tab shows "Managed by your organization" instead of the Add button. Also removes the first-run wizard's provider step, which is the only other place a provider gets created — so a managed machine is never asked to configure one |
| `allowPersonaManagement` | bool, `true` | 🔒 **`enforce` only.** `false` hides Add persona and refuses add/edit/duplicate/delete. Built-in and managed personas stay available |
| `blockedBuiltInPersonas` | string[], `null` | ✓ Hides built-ins from the picker. Keys: `PiaPersonal`, `PiaBusiness`, `ExperiencedCoder`, `MarketingWriter`, `FinancialExpert`, `WorldwideCompanyCeo`, `ExplainItSimply` (case-insensitive; a Guid also works). Unknown entries are ignored. Hidden ids stay reserved, so a hidden built-in cannot be re-created as a user persona, and one already in an agent-persona roster is kept rather than pruned — unblocking restores it. If you block the persona a mode falls back to, resolution degrades to another built-in rather than failing |
| `useSameProviderForAllModes` | bool, `true` | 🔒 ✓ |
| `defaultProviderId` · `defaultTemplateId` | `Guid?`, `null` | ⚠ GUIDs are minted per install — unusable in a fleet deployment unless the values come from sync |
| `modeProviderDefaults` · `modePersonaDefaults` | `Dictionary<WindowMode, Guid>` | ⚠ same Guid problem |
| `agentPersonaRoster` | `Dictionary<UserOperatingMode, Guid[]>`, max 6 per mode | ⚠ same Guid problem |

**Provider credentials, endpoints, models, personas and templates are not deployable this way.** They
live in `providers.json` / `templates.json` under `%AppData%\Pia\`, and `JsonPersistenceService` reads
only from that directory — there is no seed-from-install-dir path. Fleet provisioning has to go through
sync/managed personas.

### Assistant & agent runs

| Key | Type / default | |
|---|---|---|
| `assistantFileToolsEnabled` | bool, `true` | ✓ kill switch for assistant file read/write/delete |
| `assistantGitToolsEnabled` | bool, `true` | ✓ enforcing `false` works |
| `assistantFilesFolder` | string, `null` | ✓ sandbox root |
| `assistantDefaultWorkingDirectory` | string, `Playground` | ✓ |
| `assistantAgentModeDefault` | bool, `false` | ✓ |
| `assistantSuggestionsEnabled` | bool, `false` | ✓ |
| `agentRunAutoApproveBuiltInWrites` | bool, `false` | ✓ — you cannot policy-lock auto-approve *off* |
| `agentPlanReasoningTurnEnabled` | bool, `false` | ✓ |
| `alwaysAllowedTools` | `ToolGrant[]` `{pluginId, toolName, grantedAt}` | ✓ works in both blocks |
| `agentMaxSteps` / `agentMaxReplans` / `agentWallClockMinutes` | `24` / `2` / `20` | ✓ interactive agent run budget |
| `maxToolRoundsPerStep` | int, `24` | ✓ |
| `scheduledMaxSteps` / `scheduledMaxReplans` / `scheduledWallClockMinutes` | `24` / `2` / `45` | ✓ routines / scheduled run budget |
| `maxParallelBackgroundRuns` | int, `2` | ✓ clamped to 1–8 at read time |
| `maxParallelRequestsPerProvider` | int, `4` | ✓ clamped to 1–24 at read time |
| `assistantFolderLayoutVersion` | int, `0` | ✗ migration marker |

### Chat history

| Key | Type / default | |
|---|---|---|
| `chatHistoryEnabled` | bool, `true` | ✓ |
| `chatHistoryRetentionDays` | int, `30` | ✓ |
| `chatAutoTitleEnabled` | bool, `false` | ✓ |

### Meeting attendee & transcription

| Key | Type / default | |
|---|---|---|
| `meetingAttendeeEnabled` | bool, `true` | 🔒 **`enforce` only.** `false` hides the Teams meeting-attendee toggle in the assistant toolbar and refuses to open it. Independent of the flag below |
| `directTranscriptionEnabled` | bool, `true` | 🔒 **`enforce` only.** `false` hides the in-room microphone transcription toggle. Set both to `false` to remove meeting capture entirely |
| `meetingAttendeeDisplayName` | string, `null` | ✓ name Pia joins meetings under |
| `meetingTranscriptFolder` | string, `null` | ✓ |
| `meetingBrowserSelection` | `BundledChromium` \| `SystemChrome` \| `SystemEdge` \| `SystemDefault` | ✓ |
| `meetingAttendeeShowBrowserWindow` | bool, `false` | ✓ |
| `enableMeetingDiarization` | bool, `true` | ✓ |
| `meetingSmartSpeakerDetection` | bool, `true` | ✓ |
| `speakerEmbeddingThreshold` | float, `0.50` | ✓ |
| `meetingMaxSpeakers` | int, `0` (auto) | ✓ |
| `meetingMinSpeechSeconds` | float, `1.5` | ✓ |
| `meetingAttendeeRosterSnapshotMinutes` | int, `2` | ✓ |
| `lastCounterpartName` | string | ✗ state |

### Vault, ingest, privacy

| Key | Type / default | |
|---|---|---|
| `autoIngestSources` | bool, `true` | ✓ enforcing `false` works — each ingest makes LLM calls and writes synced memory |
| `privacy` | `{ tokenizationEnabled: true, piiKeywords: [{keyword, category}] }` | ⚠ replaced **wholesale**, no per-field merge — include every sub-field you want to keep. Categories: Person, Nickname, Email, Phone, Address, Date, Custom |
| `ingestSchemaVersion` · `vaultVersion` | int, `0` | ✗ migration markers — presetting these skips migrations and loses data |

---

## `appsettings.json` (second file, next to the exe)

```json
{
  "Update": {
    "GitHubRepoUrl": "https://github.com/Pia-Ai-dev/Pia.Wpf",
    "AccessToken": null,
    "Prerelease": false
  },
  "Plugins": { "SigningRequired": true }
}
```

| Key | Default | |
|---|---|---|
| `Update:GitHubRepoUrl` | the public repo | Point at a private/internal release feed |
| `Update:AccessToken` | `null` | PAT for a private feed — plain text on disk |
| `Update:Prerelease` | `false` | Opt a ring into pre-releases |
| `Plugins:SigningRequired` | `true` | `false` disables plugin CAB signature verification |

PascalCase here (standard `IConfiguration` binding), unlike `policy.json`. `appsettings.Development.json`
is also read if present. Both live in `…\current\` and are **replaced on every update** — anything you
change here must be re-applied by the deployment tooling after each upgrade.

---

## Recommended baseline

Recommendations go in `defaults`; anything the user must not undo goes in `enforce`.

```json
{
  "defaults": {
    "theme": "Dark",
    "uiLanguage": "DE",
    "targetLanguage": "DE",
    "syncEnabled": true,
    "chatHistoryRetentionDays": 14
  },
  "enforce": {
    "serverUrl": "https://pia.corp.example.com",
    "allowedSyncProviders": ["entraid"],
    "allowProviderManagement": false,
    "allowPersonaManagement": false,
    "blockedBuiltInPersonas": ["PiaPersonal", "WorldwideCompanyCeo"],
    "meetingAttendeeEnabled": false,
    "directTranscriptionEnabled": false,
    "assistantFileToolsEnabled": false,
    "assistantGitToolsEnabled": false,
    "autoIngestSources": false,
    "autoUpdateEnabled": false
  }
}
```

Note the asymmetry: a lock written under `defaults` is a suggestion the user can switch back on. The
five lock settings only bite in `enforce`.

Deploy by file copy — GPO preferences, Intune/SCCM Win32 app, a post-install script, or:

```powershell
Copy-Item \\fileserver\deploy\pia\policy.json "$env:ProgramFiles\Pia.Wpf\policy.json" -Force
```

## Open issues

- `theme` is enforceable but has no settings control, so enforcement is invisible there.
- Provider credentials, personas and templates still cannot be *deployed* by file — only locked down.
  Fleet provisioning goes through sync/managed personas.
