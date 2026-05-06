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

1. **Next to the executable** — in production this is `%ProgramFiles%\Pia.Wpf\policy.json`. This is the preferred location and is the one used when the file is shipped alongside the installer.
2. **Machine-wide fallback** — `%ProgramData%\Pia.Wpf\policy.json` (typically `C:\ProgramData\Pia.Wpf\policy.json`). Kept for backward compatibility with existing deployments and for cases where the install directory is not writable by the deployment tool.

Both directories are readable by all users; the install directory requires administrator rights to write to, and `%ProgramData%\Pia.Wpf` is writable by administrators — either is suitable for machine-wide policy deployment.

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

- **Policy not applied**: Verify the file exists at `%ProgramFiles%\Pia.Wpf\policy.json` (preferred) or `%ProgramData%\Pia.Wpf\policy.json` (fallback) and is valid JSON
- **Invalid JSON**: If the policy file contains invalid JSON, it is silently ignored and a warning is logged to `%LocalAppData%\Pia\Logs\pia.log`
- **No effect on a setting**: Ensure the property name matches the camelCase format. Check the sample file in `samples/policy.json`

## Sample File

A sample policy file is available at [`samples/policy.json`](../samples/policy.json) in the repository.
