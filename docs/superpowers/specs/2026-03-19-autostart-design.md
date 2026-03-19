# Autostart Feature Design

## Summary

Add Pia to Windows autostart after installation and updates. Enabled by default, user can toggle it off in settings.

## Components

### 1. AppSettings.LaunchAtStartup

New `bool` property on `AppSettings`, default `true`. Persisted via existing `JsonPersistenceService<AppSettings>`.

### 2. AutostartService

Manages registry key at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with value name `Pia`.

- `Enable()` — writes current exe path to registry
- `Disable()` — removes registry key
- `IsEnabled()` — checks if key exists

Uses `ILogger<AutostartService>` for logging. Registered in DI via `Bootstrapper`.

### 3. Velopack Hooks

On `VelopackApp.Build()`:

- `OnAfterInstall` — calls `Enable()` to set autostart on fresh install
- `OnAfterUpdate` — calls `Enable()` to update exe path after Velopack changes it
- `OnBeforeUninstall` — calls `Disable()` to clean up registry key

### 4. GeneralSettingsViewModel Toggle

Add `LaunchAtStartup` toggle to general settings UI. When toggled, immediately calls `AutostartService.Enable()` or `Disable()`.

### 5. Key Behaviors

- Registry value always points to current exe path
- Velopack updates re-write the key with the new path
- Uninstall removes the key
- User can disable via settings; setting is respected on install/update (if user disabled it, update should not re-enable)

## Architecture Decision: Respecting User Preference on Update

On update, check `LaunchAtStartup` setting before writing registry key. If user has explicitly disabled autostart, the update should not re-enable it. Only write the registry key if the setting is `true`.
