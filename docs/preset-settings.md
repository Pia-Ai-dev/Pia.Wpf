# Enterprise Preset Settings

Pia supports enterprise policy-based settings management through a layered policy file system. IT administrators can pre-configure default values and enforce specific settings across managed devices.

## How It Works

Settings are resolved in the following priority order (highest wins):

1. **Policy enforced values** - Always applied, users cannot override
2. **`PIA_CLOUD_SERVER_URL` env var** - Developer-only override for `serverUrl` (see below)
3. **User settings** - Individual user preferences (`%AppData%/Pia/settings.json`)
4. **Policy default values** - Applied only when the user hasn't changed a setting
5. **Built-in defaults** - Hardcoded application defaults

### Developer override (`PIA_CLOUD_SERVER_URL`)

For local development, setting the `PIA_CLOUD_SERVER_URL` environment variable overrides the saved `serverUrl` on startup. It sits **between** policy enforcement and user settings — `enforce.serverUrl` always wins. When the env var is suppressed by an enforced policy, an info-level line is written to `%LocalAppData%\Pia\Logs\pia.log`.

## Policy File Location

The application looks for `policy.json` in this order (first match wins):

1. **Next to the running executable** — `AppContext.BaseDirectory`. For Velopack-managed installs this is the versioned subfolder (e.g. `%ProgramFiles%\Pia.Wpf\current\`), which is overwritten on update — generally don't put `policy.json` here.
2. **Install root** — the parent of (1). For Velopack this is `%ProgramFiles%\Pia.Wpf\` (next to the visible `Pia.Wpf.exe` launcher stub). **This is the recommended location for machine-wide deployment**: it persists across updates because Velopack only replaces the `current\` subfolder.
3. **Machine-wide fallback** — `%ProgramData%\Pia.Wpf\policy.json` (typically `C:\ProgramData\Pia.Wpf\policy.json`). Kept for backward compatibility with existing deployments.

If no policy file is found, an Information-level entry is written to `%LocalAppData%\Pia\Logs\pia-*.log` listing all paths that were searched — useful for confirming where to drop the file on a given machine.

## Policy File Format

```json
{
  "defaults": {
    "theme": "Dark",
    "uiLanguage": "DE",
    "syncEnabled": true,
    "autoUpdateEnabled": true,
    "launchAtStartup": true
  },
  "enforce": {
    "serverUrl": "https://pia.corp.example.com",
    "trustSelfSignedCertificates": false,
    "privacy": {
      "tokenizationEnabled": true
    },
    "autoUpdateEnabled": true
  }
}
```

### `defaults` Section

Values in `defaults` are applied when the user has not explicitly changed the setting from its built-in default. If the user changes the value, their preference is preserved.

Use this for recommended settings that users may override.

### `enforce` Section

Values in `enforce` always override user settings and are read-only in the UI (controls are disabled). Even if a user manually edits their `settings.json`, enforced values are re-applied on every load and save.

Use this for mandatory compliance settings.

## Configurable Settings

All properties from `AppSettings` can be used in both `defaults` and `enforce`. Property names use camelCase (JSON). Common enterprise-relevant settings:

| Setting | Type | Description |
|---------|------|-------------|
| `serverUrl` | string | Pia Cloud server URL |
| `trustSelfSignedCertificates` | bool | Allow self-signed TLS certificates |
| `syncEnabled` | bool | Enable cloud sync |
| `autoUpdateEnabled` | bool | Enable automatic updates |
| `theme` | "System" / "Dark" / "Light" | Application theme |
| `uiLanguage` | "EN" / "DE" / "FR" | UI language |
| `targetLanguage` | "EN" / "DE" / "FR" | Default output language |
| `launchAtStartup` | bool | Start Pia with Windows |
| `startMinimized` | bool | Start minimized to tray |
| `privacy.tokenizationEnabled` | bool | Enable PII tokenization |
| `defaultOutputAction` | "CopyToClipboard" / "AutoType" / "PasteToPreviousWindow" | Default output action |
| `useSameProviderForAllModes` | bool | Use same AI provider for all modes |
| `ttsEnabled` | bool | Enable text-to-speech |
| `whisperModel` | "Tiny" / "Base" / "Small" / "Medium" / "Large" | Speech-to-text model size |

## Deployment Methods

The policy file can be deployed using any standard enterprise tool:

- **Group Policy (GPO)** - File copy via Group Policy Preferences
- **Microsoft Intune / SCCM** - Deploy as a Win32 app or script
- **PowerShell script** - `Copy-Item policy.json "$env:ProgramData\Pia.Wpf\policy.json"`
- **Velopack installer** - Include in post-install script

### Example PowerShell Deployment

```powershell
$policyDir = "$env:ProgramData\Pia.Wpf"
if (-not (Test-Path $policyDir)) {
    New-Item -ItemType Directory -Path $policyDir -Force
}
Copy-Item "\\fileserver\deploy\pia\policy.json" "$policyDir\policy.json" -Force
```

## UI Behavior

When a setting is enforced by policy:
- The corresponding UI control is disabled (grayed out)
- The enforced value is always displayed
- Users cannot modify the value through the settings UI

When a setting has a policy default:
- The default value is shown on first launch or after a settings reset
- Users can freely change the value
- Their preference persists across app restarts

## Troubleshooting

- **Policy not applied**: Open `%LocalAppData%\Pia\Logs\pia-*.log` and look for a line starting with `Loaded enterprise policy from` (success) or `No enterprise policy file found. Searched:` (which lists every path that was checked). Verify the file is at one of the listed paths and contains valid JSON.
- **Invalid JSON**: If the policy file contains invalid JSON, it is silently ignored and a warning is logged to `%LocalAppData%\Pia\Logs\pia.log`
- **No effect on a setting**: Ensure the property name matches the camelCase format. Check the sample file in `samples/policy.json`

## Sample File

A sample policy file is available at [`samples/policy.json`](../samples/policy.json) in the repository.
