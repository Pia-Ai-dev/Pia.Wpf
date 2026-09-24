# WinWright setup for driving Pia from Claude Code

How to give a Claude Code session on any Pia developer machine the same UI-testing
capabilities this repo relies on: driving the running app live through the WinWright MCP
server, replaying recorded UI scripts, and the agent-run walkthroughs. Written for Claude as
the reader; a human can follow it step by step too.

This file is the **entry point and machine setup**. It does not repeat the selector reference
or the record/replay rules — those live in:

| Doc | What it is |
|---|---|
| [ui-automation-playbook.md](ui-automation-playbook.md) | Stable AutomationIds, navigation, dialogs, known gaps, WinWright traps. Read before any run. |
| [`tests/ui-scripts/README.md`](../../tests/ui-scripts/README.md) | Recorded scripts, the replay harness, recording rules, the agent-run e2e scripts. |
| [2026-08-18-winwright-recording-eval.md](2026-08-18-winwright-recording-eval.md) | Why the recording/replay rules are what they are. |

Half of what makes this work lives outside the repo — the WinWright binary, its config file and
the MCP registration are per machine. Sections 1–3 set those up; section 4 is the knowledge a
session needs before it touches the app.

## 1. Machine prerequisites

- **Windows desktop session**, interactive and unlocked. UI Automation does not work on a locked
  screen or in a non-interactive service session.
- **.NET 10 SDK** (the app and tests build with it), **PowerShell 7** (`Invoke-UiScripts.ps1`),
  **Node ≥ 22.5** (`tests/ui-scripts/agent-run-e2e/*.mjs` use `node:sqlite`).
- **WinWright ≥ 3.1.0** from <https://github.com/civyk-official/civyk-winwright> (freeware,
  by Civyk). Take the release binary and place it at:

  ```
  %LOCALAPPDATA%\WinWright\Civyk.WinWright.Mcp.exe
  ```

  That path is what the MCP entry below and `Invoke-UiScripts.ps1` assume (the harness takes
  `-WinWrightPath` to override). Versions before 3.1.0 have screenshots disabled, which makes
  them close to useless here.
- Check the environment:

  ```powershell
  & "$env:LOCALAPPDATA\WinWright\Civyk.WinWright.Mcp.exe" doctor
  ```

  The pass is `All checks passed.` with `UIA3: accessible`. `doctor` also reports the .NET
  runtime it found; it is the authority on whether that runtime is sufficient.

## 2. WinWright's own config: `winwright.json`

WinWright reads `winwright.json` from **next to the exe** (not the working directory). Create
`%LOCALAPPDATA%\WinWright\winwright.json`:

```json
{
  "WinWright": {
    "Permissions": {
      "AllowFileWrite": true,
      "AllowFileRead": true,
      "AllowNetworkProbe": true,
      "AllowBrowserEval": true
    },
    "Audit": {
      "Enabled": true
    }
  }
}
```

- **The `WinWright` root section is mandatory.** A flat `{"permissions": {...}}` file parses
  without complaint and is silently ignored — every grant falls back to its default (mostly off)
  and nothing is logged at startup. The symptom is tool-specific:
  `Tool '<name>' is disabled by server configuration`, while ungated tools keep working.
- `AllowFileWrite` is what `ww_screenshot` with a `filePath`, `run --output`, `--format junit`
  reports and `heal --output` need. A fresh install has it off.
- Shell, registry, process-kill, service and task-scheduler permissions stay **off** on purpose.
- Audit logs land in the install directory as `audit-<yyyy-MM-dd>.jsonl`; `Audit.LogPath` has no
  effect.

## 3. Wire WinWright into Claude Code

### Register the MCP server

The repo's `.mcp.json` declares the `winwright` server, pointing at
`${LOCALAPPDATA}\WinWright\Civyk.WinWright.Mcp.exe mcp` (Claude Code expands `${VAR}`,
so the entry is machine-independent). Nothing to add — once the binary is in place, Claude Code asks on
first start whether to enable the project's MCP servers; approve `winwright`. That approval is
stored in the gitignored `.claude/settings.local.json` as `"enabledMcpjsonServers": ["winwright"]`.

Do not also register it at local or user scope (`claude mcp add winwright …`): a second entry with
the same name shadows the repo one, and the machines drift apart again.

Confirm with `claude mcp list` (or `/mcp` in a session) that `winwright` shows `Connected` and the
`mcp__winwright__ww_*` tools are listed. `Failed to connect` almost always means the exe is not at
that path.

### Permissions

Without an allow list every `ww_*` call prompts. The read-only tools are safe to allow in
`.claude/settings.local.json`:

```json
{
  "permissions": {
    "allow": [
      "mcp__winwright__ww_query",
      "mcp__winwright__ww_count",
      "mcp__winwright__ww_get_value",
      "mcp__winwright__ww_dump_tree",
      "mcp__winwright__ww_snapshot",
      "mcp__winwright__ww_inspect",
      "mcp__winwright__ww_list_windows",
      "mcp__winwright__ww_is_alive",
      "mcp__winwright__ww_get_session_info"
    ]
  }
}
```

Leave `ww_launch`, `ww_click`, `ww_invoke`, `ww_set_value`, `ww_set_checked`, `ww_type`,
`ww_registry` and `ww_file` prompted: they act on the desktop. In auto mode the permission
classifier also blocks `ww_screenshot` and registry writes as often as not — when it does, ask the
user rather than working around it.

### Blank screenshots: the GPU stall

If `ww_screenshot` returns a blank or cream surface while the UIA tree is correct, WPF's hardware
rendering has stalled — the app is fine. Retry once after `ww_window action=resize`; if it stays
blank, ask the user to run this and relaunch Pia:

```powershell
New-Item -Force 'HKCU:\Software\Microsoft\Avalon.Graphics' | Out-Null
Set-ItemProperty 'HKCU:\Software\Microsoft\Avalon.Graphics' DisableHWAcceleration 1 -Type DWord
```

Remove the value afterwards. Screenshots are for showing a human a verified state, not evidence:
read state off the tree (`ww_query` / `ww_count` / `ww_get_value`). See the playbook's
*Cross-checks* for the stale-frame trap.

Tooltips still render during the stall — they are separate HWNDs — but neither `ww_screenshot`
(window-scoped) nor `ww_list_windows` sees them, and WPF suppresses tooltips while Pia is not
foreground. To capture one: `ww_window activate`, hover a *different* element and then back so a
fresh MouseEnter fires, and grab the screen region at the window's bounds with
`[Drawing.Graphics]::CopyFromScreen`.

### Setup is done when

1. `doctor` passes.
2. `claude mcp list` shows `winwright` connected.
3. In a session, `ww_list_windows` returns the desktop's windows without an error.
4. `dotnet build` succeeded and `src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.exe`
   exists. That is the app binary — a stale `Pia.exe` beside it is an old artifact that dies with a
   PiperSharp error; never launch it.

## 4. Before touching the app

### Never drive the real profile with writes

A developer's own `%APPDATA%\Pia` holds their real chats, signed-in cloud session and vault. Drive
it **read-only** if at all: navigate and read, but do not toggle settings, send messages, delete
anything or import. Anything that writes goes against a throwaway profile.

### Throwaway profiles: the rules that each cost a lost session

The app routes its data through `PIA_DATA_DIR` (roaming: `settings.json`, `providers.json`, …) and
`PIA_LOCAL_DATA_DIR` (local: `history.db`, `Logs\`). Pass both through `ww_launch`'s `env`
parameter.

- **They name the Pia directory itself**, not a parent. `PIA_DATA_DIR=C:\t\roaming` means
  `C:\t\roaming\settings.json`. Get it wrong and nothing fails — the app boots on defaults.
- **Confirm the override took** before trusting anything: the log in
  `<PIA_LOCAL_DATA_DIR>\Logs\pia-*.log` must contain
  `Data directories: Roaming=… Local=… Overridden=True`.
- **The vault is not isolated.** It follows `assistantFilesFolder` in `settings.json`, which
  defaults to the real `%USERPROFILE%\Documents\Pia Assistant`. Every throwaway seed must point it
  at a scratch folder **and** pin `ingestSchemaVersion: 2`; missing the second makes the
  Bootstrapper's ingest migration delete every `memory/topics/*.md` in the vault on first launch.
  Check the value on disk once the app is up — a seeded value has been seen reverting.
- **Downloaded artifacts are shared, not isolated.** Models, TTS voices, Playwright browsers and
  plugins stay under the real `%LOCALAPPDATA%\Pia`. A Remove/Delete button on one of those deletes
  the real files. Verify such a control with `ww_query` / `ww_count`; never actuate it.
- **Enums in `settings.json` are integers.** `"theme": "Dark"` makes the whole file fail to
  deserialize silently; the app shows the first-run wizard, which reads as "the override was
  ignored". Patch settings with `ConvertFrom-Json` / `ConvertTo-Json` on a file the app wrote, never
  hand-write enum values.
- A profile needs `hasCompletedFirstRunWizard: true` (else the wizard) and `defaultWindowMode: 1`
  (else an Optimize-mode window with no Routines/Memory/Chat-history nav), `syncEnabled: false`
  (never talk to the live account) and `uiLanguage: 0` (name-based selectors are localized strings).
- A throwaway profile is not signed in, so it cannot verify anything sync-related — a working push
  and a broken one look identical.
- **If the vault's `memory/topics/` did get wiped, check before rebuilding.** On the next real
  launch `AutoIngestService` logs `Ingest record names a topic page that is gone; re-ingesting the
  source` and re-synthesises the pages by itself; a different set of pages from the same sources is
  not a loss. The manual rebuild — set the real `settings.json`'s `ingestSchemaVersion` to `0`
  and relaunch — costs a full re-synthesis in LLM calls, so it is the owner's call.
- **Seeding without the enum trap:** boot the app once against an empty `PIA_DATA_DIR`, close it,
  then edit the `settings.json` it wrote — set `hasCompletedFirstRunWizard: true` and point
  `assistantFilesFolder` at a scratch folder in the same edit. That file already carries the
  current `ingestSchemaVersion` and valid enum values. The `providers.json` it mints has no
  working provider; use `setup-profile.mjs` when a run needs one. JSON string paths take forward
  slashes (`C:/Users/...`), which avoids escaping backslashes.

**Use the existing seeders rather than deriving a profile by hand:**

| Need | Seeder |
|---|---|
| Replay recorded scripts | `tests/ui-scripts/Invoke-UiScripts.ps1` writes its fixture profile itself. |
| Live interaction with a working provider (copies the real settings/providers/templates, patches the vault folder, sync, window mode and language, records hashes of the real profile) | `node tests/ui-scripts/agent-run-e2e/setup-profile.mjs <root>` then `… setup-profile.mjs <root> verify` afterwards |
| Ingest measurements | `scripts/Measure-TopicYield.ps1` |

> **Caveat on `Invoke-UiScripts.ps1`:** its fixture pins `ingestSchemaVersion` but not
> `assistantFilesFolder`, so a "hermetic" replay still points at the real vault and may auto-ingest
> it (LLM calls, `index.md` rewrite). Its profile-leak check hashes only `settings.json` and
> `history.db` and cannot see this. On a machine with a real vault, prefer `setup-profile.mjs`
> for anything beyond the Settings scripts until the fixture is fixed.

### A live session, end to end

1. `dotnet build`.
2. Seed: `node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-ww`.
3. `ww_launch` the Debug `Pia.Wpf.exe` with
   `env = { PIA_DATA_DIR = "$env:TEMP\pia-ww\roaming"; PIA_LOCAL_DATA_DIR = "$env:TEMP\pia-ww\local" }`.
4. Check the `Overridden=True` log line.
5. Drive by **AutomationId** with `ww_invoke` (works regardless of foreground); verify every action
   by re-reading the tree — `ww_click` returns success for no-ops. Selectors and patterns are in the
   playbook.
6. Close the app, then `node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-ww verify`
   must print `real profile untouched`.

**Provider for agent-run measurements.** Pia Cloud is usually only the fallback: when the configured
default provider id resolves to nothing, the app auto-creates a Pia Cloud entry, and on a throwaway
profile its refresh token soon fails (`POST /auth/refresh` → 401). The run then dies in the plan
turn with an authentication error and writes no probe line. Pin both mode defaults to a real
provider instead (`setup-profile.mjs <root> park|routines <providerName>` does this). Prefer
Mistral Medium 3.5, falling back to an OpenRouter GPT model when Mistral will not plan: Mistral
declines open-ended "compare two approaches to X" prompts as ungroundable and parks the run for
clarification, so that prompt category yields no plan.

### Just looking at a view? Don't launch the app

To see how a single `UserControl` renders, write a throwaway xunit test in
`[Collection("WpfApplicationStatic")]` that uses `WpfStaHost` (`tests/Pia.Wpf.Tests/Views/`):
construct the control with a small POCO `DataContext`, `Measure` / `Arrange` / `UpdateLayout` it,
render it with `RenderTargetBitmap` to a PNG in the scratchpad, and `Read` the PNG. Theme brushes
resolve, there is no GPU stall, no profile and no vault risk, and it takes about half a second.
For a whole view, collapse every sibling on the path from the target up to the root first —
`Visibility` bindings missing from the POCO fall back to `Visible` and paint overlays over it.
Delete the scratch test before the gate run.

### Traps worth knowing up front

Full list in the playbook; these are the ones that most often read as product bugs:

- `ww_set_checked` on a `ToggleSwitch` that carries a `Command` flips `IsChecked` without running
  the command — nothing persists. Click it and verify the side effect.
- Snackbars and the Flow peek do not render while Pia is not the foreground window, and
  `ww_window activate` returns success without foregrounding it.
- Native `MessageBox` dialogs are invisible to `ww_list_windows` and window screenshots; use
  `ww_dialog action=expect` before the click that may raise one.
- A reply older than the transcript window has no automation peers — invoke
  `Assistant_LoadOlderMessages` until its id resolves; scrolling does not help.

### Every new control needs an AutomationId

Tests and recordings only stay stable because controls carry ids. See CLAUDE.md's *UI Automation*
section: every new interactive control gets `AutomationProperties.AutomationId`, and
`tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` gets the matching `[InlineData]` row.
