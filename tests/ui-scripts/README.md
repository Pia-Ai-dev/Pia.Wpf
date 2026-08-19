# Recorded UI scripts

WinWright recordings of real UI flows, plus a harness that replays them. These are **not** part of
the `dotnet test` gate — they launch the actual desktop client and drive the actual Windows UI, so
they run on demand on a Windows desktop session.

Background and the traps behind every rule here: `docs/2026-08-18-winwright-recording-eval.md`.
Selector reference: `docs/ui-automation-playbook.md`.

```
Invoke-UiScripts.ps1                    the harness
scripts/settings-general.json           recorded flows (ww_record exports)
fixtures/settings.ui-test-seed.json     the profile every script starts from
artifacts/                              junit reports, healed scripts, profile backups (gitignored)
```

## Run them

```powershell
dotnet build                                     # the harness replays against the build output
tests/ui-scripts/Invoke-UiScripts.ps1            # every script, Debug build, text output
tests/ui-scripts/Invoke-UiScripts.ps1 -Name settings-general -Format junit
tests/ui-scripts/Invoke-UiScripts.ps1 -Configuration Release -Screenshots
tests/ui-scripts/Invoke-UiScripts.ps1 -ListScripts
```

Exit codes: `0` all passed, `1` a script failed, `2` a setup problem (app not built, WinWright
missing, Pia already running). With `-Format junit` the verdict is the exit code — the per-step
summary goes only into the report file.

Requires WinWright at `%LOCALAPPDATA%\WinWright\Civyk.WinWright.Mcp.exe` (override with
`-WinWrightPath`). Replay is a CLI verb on that binary, not an MCP tool:
`Civyk.WinWright.Mcp.exe run <script.json>`. Anything written with `--output` (junit reports,
screenshots, healed scripts) needs `Permissions.AllowFileWrite` in WinWright's `winwright.json`,
which is **off** in a fresh install — without it those flags fail with *"disabled by server
configuration"*.

**Close Pia first.** The harness refuses to run while `Pia.Wpf` is alive, because the app rewrites
`settings.json` on every property change and would eat the seeded profile.

## What the harness does to your machine

It replaces `%APPDATA%\Pia\settings.json` with the fixture, replays, waits for the app to exit, then
restores your file from a timestamped copy under `artifacts/profile-backup/` (printed at start and
verified by hash at the end). `-KeepProfile` skips all of that and runs against your live settings.

The backups under `artifacts/profile-backup/` are copies of your **real** profile — DPAPI-encrypted
tokens and account email included. They are gitignored; delete them when you are done.

The fixture seeds `settings.json` only. `providers.json`, `templates.json` and
`%LOCALAPPDATA%\Pia\history.db` stay whatever the machine already has, and the log directory is
shared with your real install — so treat a run as "the app was opened once". Making this hermetic
needs a data-directory override in the app; the ~23 call sites that read
`SpecialFolder.ApplicationData` / `LocalApplicationData` directly would have to go through one
resolver. That is also why "CI-ready" here means *verified on a developer machine* — nobody has yet
run these on a clean agent with no providers configured.

The fixture sets `syncEnabled: false` on purpose, so a replay never talks to your server or touches
your account. It also pins `uiLanguage: 0` (English), because any selector that falls back to a
control's name matches a localized string — see below.

## Why the fixture exists

A recording captures **actions, no preconditions**. `settings-general.json` enables *Start minimized
to system tray*; on the next launch the app starts hidden in the tray and step 1 fails with
*"No main window found"*. Any script that changes persisted state passes exactly once unless it
starts from a known profile. That is the fixture's whole job.

So: if a script needs a setting to be in a particular state, seed it in the fixture rather than
adding steps to arrange it.

## Recording a new script

Record it **against the seeded fixture**, not your own profile — otherwise the start state you
recorded is not the one the harness reproduces:

```powershell
Copy-Item $env:APPDATA\Pia\settings.json $env:TEMP\settings.mine.json
Copy-Item tests/ui-scripts/fixtures/settings.ui-test-seed.json $env:APPDATA\Pia\settings.json
# ... record ...
Copy-Item $env:TEMP\settings.mine.json $env:APPDATA\Pia\settings.json
```

Then, driving the app through WinWright's MCP tools:

1. `ww_record start`, then `ww_record test_start` with an id (`TC-003`) and a title.
2. Do the flow. Pass `record: false` on discovery calls so only real steps land in the script.
3. Assert with **`ww_assert_value`** — it embeds into the last recorded step. `ww_assert` is *not*
   recorded. On a `CheckBox`, use `property=value` with `On` / `Off`.
4. `ww_record test_end`, then `test_start` for the next case, and finally
   `ww_record export` with `launchPath`.
5. Save the exported JSON into `scripts/`, replace the `launchPath` value with the string
   `"{{APP_EXE}}"`, and drop the `appId` and `timestamp` fields — they are dead provenance.

Rules that cost real time to learn:

- **Never call `stop` to peek.** It ends the session, `pop` then refuses, and `start` clears the
  buffer. Mid-run the only safe read is `pop`'s `remaining` count.
- **`pop` any step whose effect you did not verify.** The buffer records calls that *returned
  success*, not calls that *did something*: a `ww_click` on a static `Text` element records happily
  and passes on replay while doing nothing.
- **Prefer `automationId=` selectors.** Name-based selectors are localized strings — they break when
  the UI language changes, and `winwright heal` cannot repair them (it only scores elements that have
  an AutomationId). Every interactive control in the General, Assistant, Providers, Account and
  Optimize settings views has one, inner tab headers included; where you still find one that does
  not (Plugins, the E2EE onboarding screen), add it to the XAML rather than recording a fragile
  selector. `tests/Pia.Wpf.Tests/Views/SettingsViewAutomationIdTests.cs` holds that line and is
  part of the `dotnet test` gate — a new settings control turns that test red until it carries an
  id, and the failure message names the control and the id form to use. It does not fail the
  build; a missing id compiles fine.
- Keep one scenario per script file. The runner stops at the first failure (`maxFailures: 0`) and a
  junit report then omits the remaining test cases entirely.

## Checking selectors after a UI change

```powershell
tests/ui-scripts/Invoke-UiScripts.ps1 -Heal
```

This probes every selector against the app and writes a healed copy into `artifacts/`. Read it with
care: `heal` only sees the screen the app is on *right now*, so steps whose target appears later are
reported `unresolvable` even when they are fine. It is useful for confirming that a renamed
AutomationId broke something, not as a pass/fail gate.
