# Recorded UI scripts

WinWright recordings of real UI flows, plus a harness that replays them. These are **not** part of
the `dotnet test` gate — they launch the actual desktop client and drive the actual Windows UI, so
they run on demand on a Windows desktop session.

Background and the traps behind every rule here: `docs/ui_automation/2026-08-18-winwright-recording-eval.md`.
Selector reference: `docs/ui_automation/ui-automation-playbook.md`.

```
Invoke-UiScripts.ps1                    the harness
scripts/settings-general.json           recorded flows (ww_record exports)
fixtures/settings.ui-test-seed.json     the profile every script starts from
artifacts/                              junit reports, healed scripts, profile backups (gitignored)
agent-run-e2e/                          the unrecorded agent-run walkthrough (see below)
```

## Run them

```powershell
dotnet build                                     # the harness replays against the build output
tests/ui-scripts/Invoke-UiScripts.ps1            # every script, Debug build, text output
tests/ui-scripts/Invoke-UiScripts.ps1 -Name settings-general -Format junit
tests/ui-scripts/Invoke-UiScripts.ps1 -Configuration Release -Screenshots
tests/ui-scripts/Invoke-UiScripts.ps1 -ListScripts
```

You do **not** have to close Pia first, and the harness does not touch your own profile — see below.

Exit codes: `0` all passed, `1` a script failed or the real profile was written, `2` a setup problem
(app not built, WinWright missing, contradictory arguments). With `-Format junit` the verdict is the
exit code — the per-step summary goes only into the report file.

Requires WinWright at `%LOCALAPPDATA%\WinWright\Civyk.WinWright.Mcp.exe` (override with
`-WinWrightPath`). Replay is a CLI verb on that binary, not an MCP tool:
`Civyk.WinWright.Mcp.exe run <script.json>`. Anything written with `--output` (junit reports,
screenshots, healed scripts) needs `Permissions.AllowFileWrite` in WinWright's `winwright.json`,
which is **off** in a fresh install — without it those flags fail with *"disabled by server
configuration"*.

## What the harness does to your machine

By default it runs **hermetically**: it creates a throwaway data directory under `%TEMP%`, copies the
fixture into it as `settings.json`, and points the app there with `PIA_DATA_DIR` /
`PIA_LOCAL_DATA_DIR` (see `src/Pia.Wpf/Infrastructure/PiaPaths.cs`). `%APPDATA%\Pia` and
`%LOCALAPPDATA%\Pia` are never seeded, written or restored, so:

- **You do not have to close Pia.** A replay and your own instance share no settings file, no
  `history.db` and no log directory. The harness notes that yours is running and carries on.
- A crashed run cannot leave a fixture in your profile.
- At the end the harness re-hashes your real `settings.json` and `history.db` and prints
  `real profile untouched`. If either changed and no other Pia was running, that is a `LEAK` and the
  run fails — this is the assertion that keeps the routing honest.

```powershell
tests/ui-scripts/Invoke-UiScripts.ps1 -DataDir C:\temp\pia-ui   # a named profile instead of %TEMP%
tests/ui-scripts/Invoke-UiScripts.ps1 -KeepDataDir              # keep it after a pass, to read the logs
```

The temp profile is deleted after a passing run and kept after a failing one, so a red run leaves you
its `local\Logs\pia-*.log` and `local\history.db` to read.

### What a hermetic run still shares

Downloaded artifacts stay on the **real** `%LOCALAPPDATA%\Pia` even under an override, because
re-fetching them per run would cost gigabytes: `Models\` (Whisper, Parakeet, Silero, the speaker
embedding, the embedding model), `Piper\`, `Browsers\` (Playwright's Chromium). Plugins (`plugins\`)
and the consent trails (`ConsentAudit\`, `ConsentEvidence\`) are shared too, deliberately — a replay
should see the same tool surface your real install has, and a consent decision belongs in one ledger.

So a run is hermetic for **data** and shared for **downloaded artifacts and consent**. That is the
qualifier on "CI-ready": a clean agent gets a clean profile but still downloads the models on first
use, and nobody has yet run these on such an agent.

### Driving your real profile on purpose

```powershell
tests/ui-scripts/Invoke-UiScripts.ps1 -KeepProfile
```

This is the pre-override behaviour and the one mode that **does** require Pia to be closed: it
replaces `%APPDATA%\Pia\settings.json` with the fixture, replays, waits for the app to exit, then
restores your file from a timestamped copy under `artifacts/profile-backup/` (printed at start and
verified by hash at the end).

Those backups are copies of your **real** profile — DPAPI-encrypted tokens and account email
included. They are gitignored; delete them when you are done.

The fixture sets `syncEnabled: false` on purpose, so a replay never talks to your server or touches
your account. It also pins `uiLanguage: 0` (English), because any selector that falls back to a
control's name matches a localized string — see below. `hasCompletedFirstRunWizard: true` matters more
now than it used to: a throwaway profile is a first-run profile, and without that key every script
would meet the wizard instead of the main window.

## Why the fixture exists

A recording captures **actions, no preconditions**. `settings-general.json` enables *Start minimized
to system tray*; on the next launch the app starts hidden in the tray and step 1 fails with
*"No main window found"*. Any script that changes persisted state passes exactly once unless it
starts from a known profile. That is the fixture's whole job.

So: if a script needs a setting to be in a particular state, seed it in the fixture rather than
adding steps to arrange it.

## Recording a new script

Record it **against the seeded fixture**, not your own profile — otherwise the start state you
recorded is not the one the harness reproduces. Build the throwaway profile first and hand it to
`ww_launch` through its `env` parameter, which is the recording-time equivalent of what the harness
does:

```powershell
$p = "$env:TEMP\pia-record"
New-Item -ItemType Directory -Force "$p\roaming", "$p\local" | Out-Null
Copy-Item tests/ui-scripts/fixtures/settings.ui-test-seed.json "$p\roaming\settings.json"
# then: ww_launch with env = { PIA_DATA_DIR = "$p\roaming"; PIA_LOCAL_DATA_DIR = "$p\local" }
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
- **`ww_select` on a `TabControl` must use `optionText`, not `optionSelector`.** Both work live, and
  the recorder writes both into the same `extra` field — but on replay the `optionSelector` form
  resolves 0 elements and errors every time. This is the one place the playbook's "prefer an
  AutomationId" advice does not survive a recording, and the fixture pins `uiLanguage: 0` so the
  localized header is safe to match.
- **Prefer `automationId=` selectors.** Name-based selectors are localized strings — they break when
  the UI language changes, and `winwright heal` cannot repair them (it only scores elements that have
  an AutomationId). Every interactive control in the General, Assistant, Providers, Account and
  Optimize settings views has one, inner tab headers included, and so does AssistantView's own
  composer/toolbar; where you still find one that does not (Plugins, the E2EE onboarding screen,
  most nested chat controls, the other top-level views), add it to the XAML rather than recording
  a fragile selector. `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` holds that line for the
  views it covers and is part of the `dotnet test` gate — a new control in one of those views turns
  the test red until it carries an id, and the failure message names the control and the id form
  to use. It does not fail the build; a missing id compiles fine.
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

## Agent-run e2e (unrecorded)

`agent-run-e2e/` drives the foreground and background agent-run flows. There is no recorded script
and no replay: you launch the app through `ww_launch` and work the six prompts in
`docs/agent_run_e2e/2026-08-26-agent-run-e2e-prompts.md` through the UI yourself. Selectors are in
`docs/ui_automation/ui-automation-playbook.md`. Like everything else in this folder it is **not**
part of the `dotnet test` gate.

Three Node scripts, no `package.json` and no dependencies. `node:sqlite` needs **Node >= 22.5**.

```
agent-run-e2e/setup-profile.mjs   seed a throwaway profile + the six fixture folders; verify the real one
agent-run-e2e/watch.mjs           tail run-state transitions out of the throwaway history.db
agent-run-e2e/probe.mjs           read runs / last messages / the files tree afterwards
```

A cold run:

```powershell
dotnet build
node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-e2e
# ww_launch src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.exe with env
#   PIA_DATA_DIR       = $env:TEMP\pia-e2e\roaming
#   PIA_LOCAL_DATA_DIR = $env:TEMP\pia-e2e\local
node tests/ui-scripts/agent-run-e2e/watch.mjs $env:TEMP\pia-e2e         # second terminal, while you drive
node tests/ui-scripts/agent-run-e2e/probe.mjs $env:TEMP\pia-e2e all     # after the runs settle
node tests/ui-scripts/agent-run-e2e/setup-profile.mjs $env:TEMP\pia-e2e verify
```

The throwaway root is the first argument to all three and defaults to `%TEMP%\pia-e2e`. `probe`
takes `runs` (the default), `msgs`, `files`, `exchanges`, `vault`, `park` or `all` as its second.

### The approval-park variant

`setup-profile.mjs <root> park <providerName>` seeds the same profile plus the preconditions the
approval-park e2e needs — plan and results in
[`docs/agent_run_approval_park/`](../../docs/agent_run_approval_park/):

- `agentRunAutoApproveBuiltInWrites:false` and `alwaysAllowedTools:[]`, or the run never parks and
  every assertion downstream of the park is void rather than green. The plain `seed` mode sets
  auto-approve **on**, and a persisted Always grant rides in from the copied real profile.
- Both mode defaults pinned to the named provider. Pinning `Assistant` alone is not enough: with
  `useSameProviderForAllModes` on (the usual real value) the resolver reads the **Optimize** default
  for every mode, so the run silently goes to Pia Cloud and fails `Authentication required` under
  `syncEnabled:false`.
- `assistantDefaultWorkingDirectory:'Absence'`, the new fixture folder. The working-directory flyout
  is `StaysOpen="False"` and one stray query closes it, so the default is the reliable way in.

The three probe modes that go with it: `exchanges` dumps `AgentToolExchanges` (what the model saw as
`Kind` 1/2 beside what the gate saw as 3/4, with the `ReplayedAt` / `SupersededAt` flags), `vault`
walks the redirected `files\Vault`, and `park` reads the log for each park's
**round count** between the park line and `WaitingForInput` — zero is the pass, and that, not the
wall-clock delta, is the discriminator.

### How this profile differs from the fixture one

`Invoke-UiScripts.ps1` writes a **fixture** `settings.json` from scratch. This one **copies** your
real `settings.json`, `providers.json` and `templates.json` — an agent run needs a working
provider, and the API key is DPAPI-encrypted and the sign-in is bound to the machine, so only the
bytes survive. The copy is then patched: `syncEnabled:false`, `autoIngestSources:false`,
`defaultWindowMode:1`, and `assistantFilesFolder` pointed at the throwaway `files\`.

So it **reads** your profile and must never write it. `setup-profile.mjs` records SHA-256 hashes of
`settings.json`, `providers.json`, `templates.json` and `history.db` at seed time into
`real-profile-baseline.json`; `verify` re-hashes the same four and prints `real profile untouched`
or `LEAK <file>`, exiting 1 on a leak — the same contract as the PowerShell harness's guard.

`PIA_DATA_DIR` does **not** redirect the memory vault, so keep `remember`, `create_source` and
`recall` out of any prompt you drive here — a vault write would land in the real one.
