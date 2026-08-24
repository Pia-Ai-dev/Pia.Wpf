# Export Diagnostics — manual UI test plan (WinWright)

**Status:** ready to execute. Not yet run. **Owner:** Marco Altmann. **Written:** 2026-08-24.
**Origin:** the "human smoke test pending" line on `G1` in
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md),
and §9 of [`2026-08-24-export-diagnostics.md`](2026-08-24-export-diagnostics.md). The feature shipped in
`c37b78c2`..`ed01cecc`; **nothing in it has been exercised through the running app.**

Executable cold. Everything needed is below — you should not have to read the implementation.

---

## 0. Read first

1. `docs/ui_automation/ui-automation-playbook.md` — §Ground rules, §Navigation, §Settings, §Dialogs,
   §Cross-checks. It is the selector reference and it lists the traps.
2. `tests/ui-scripts/README.md` — §"What the harness does to your machine". You are **not** running the
   harness (see §2), but its profile-isolation model is the one this plan reuses.
3. §3 and §7 of [`2026-08-24-export-diagnostics.md`](2026-08-24-export-diagnostics.md) — the rule set and
   the traps, so you can tell a correct redaction from a bug.

## 1. What this test can and cannot prove

The gate already proves the mechanisms: 4907 tests, and **17 of 17** shipped mechanisms fail when reverted
(§9 of the feature doc). What no test has touched is the part that only exists at runtime.

**In scope — the four things only the app can answer:**

| # | Question | Why a test cannot answer it |
|---|---|---|
| 1 | Is the button reachable, and does the section render? | `Activator.CreateInstance` never lays out; `ArrowDownload24` is a `SymbolRegular` name that compiles even when bogus and then renders a garbage letter. |
| 2 | Does the confirmation dialog appear, read correctly, and honour Cancel? | `ShowConfirmationDialogAsync` goes through Wpf.Ui's `ShowSimpleDialogAsync`; nothing in `dotnet test` constructs a `ContentDialog`. |
| 3 | Does the export succeed against a **live, sink-held** log directory and land a readable zip? | The `FileShare.ReadWrite` test simulates the lock with a second `FileStream`; it has never met the real NReco writer. |
| 4 | Does reveal-in-Explorer actually surface the file? | `ShellLauncher` swallows every failure by design, so a broken reveal is silent. |

**The load-bearing verification is the artifact on disk, not the UI feedback.** §5 is the part that must not
be skipped. If §4 passes and §5 is skipped, nothing has been verified.

**Out of scope, and why:**

- **The "Nothing to export" path is not reachable through the UI.** The sink writes `pia-<today>.log` during
  startup, so `Plan()` always finds at least one file by the time the button exists. Covered by
  `AnEmptySourceDirectory_ReportsNoLogFiles` only. Do not spend time trying to force it.
- **The snackbar text may not be readable at all.** `ui:InfoBar` exposes no automation peer in this Wpf.Ui
  version (playbook, Known gaps) and `ui:SnackbarPresenter` (`MainWindow.xaml:194`) has not been probed.
  **Record what you find either way** — it is a playbook entry we do not have yet. Its absence is not a
  feature failure.
- Localization. The de/fr values are asserted for placeholder parity in `dotnet test`; eyeballing them in the
  app is a separate, cheaper pass.

## 2. Which profile — decide this first

**Do not use `Invoke-UiScripts.ps1`.** It replays a recorded script, and this flow cannot be a recorded
script: the artifact name carries a timestamp and the real assertions are inside a zip, neither of which the
replay harness can express. Drive the app directly through the WinWright MCP tools.

### Arm A — throwaway profile, seeded with REAL log files. **Recommended; do this one.**

`PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR` point at `%TEMP%`, so the export writes into the throwaway
`local\Diagnostics\` and **cannot touch `%LOCALAPPDATA%\Pia`**. But a fresh local dir holds exactly one tiny
log file, which would exercise neither the caps nor any real redaction — so **copy real log files into the
throwaway `local\Logs\` before launching.** That buys almost everything Arm B offers at none of the risk:

- real content to redact, at real scale;
- enough files and bytes to trip **both** caps;
- and seeding *today's* file means the sink opens it in append mode, so the export meets the **real writer
  holding the real file** — which is test question 3.

**One expected difference you must not misread as a bug.** Under the override, `keys.LocalRoot` is the
*throwaway* path, so a copied line containing `C:\Users\<you>\AppData\Local\Pia` does **not** match
`LocalRoot`. It matches `UserProfileRoot` instead and comes out as `<profile-user>\<path>\…` rather than
`<profile-local>`. That is correct behaviour — it is the fallback for a log written before an override — and
it is a genuinely useful thing to see exercised. `<profile-roaming>` will likewise not appear.

### Arm B — the real profile. Optional, and it needs a decision.

The only thing Arm B adds is `<profile-local>` / `<profile-roaming>` appearing, and the true end-to-end path.
It costs a **write to the real profile**: a new `%LOCALAPPDATA%\Pia\Diagnostics\` directory containing a zip
of your own redacted logs.

That write is additive, easy to delete, and exactly what a user would do — but it is still a write to the
profile this repo has spent two work items keeping the gate out of (`F1`, `F3`). **Ask the owner before
running Arm B**, and if you run it, delete the directory afterwards and say so in the report.

Run Arm A first regardless. If Arm A passes, Arm B is a confirmation, not a discovery.

## 3. Setup (Arm A)

Build first — the plan drives the Debug output.

```powershell
dotnet build
```

Seed the throwaway profile. **Copy from the real `%APPDATA%\Pia` rather than hand-writing a settings file**:
the Pia Cloud tokens live DPAPI-encrypted *inside* `settings.json`, so a hand-written one meets the first-run
wizard and a "Setup Required" overlay.

```powershell
$p = "$env:TEMP\pia-diag-ui"
Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$p\roaming", "$p\local\Logs" | Out-Null

Copy-Item "$env:APPDATA\Pia\settings.json"  "$p\roaming\"
Copy-Item "$env:APPDATA\Pia\providers.json" "$p\roaming\"
Copy-Item "$env:APPDATA\Pia\templates.json" "$p\roaming\" -ErrorAction SilentlyContinue
# NOT pending-sync-deletes.json - it is the only source of sync deletes.

# syncEnabled off so a throwaway run never talks to the live account;
# defaultWindowMode 1 so the window is Assistant-mode with the full sidebar.
$s = Get-Content "$p\roaming\settings.json" -Raw | ConvertFrom-Json
$s.syncEnabled = $false
$s | Add-Member -NotePropertyName defaultWindowMode -NotePropertyValue 1 -Force
$s | ConvertTo-Json -Depth 20 | Set-Content "$p\roaming\settings.json" -Encoding utf8

# Real logs, so there is something worth redacting and enough of it to trip both caps.
Copy-Item "$env:LOCALAPPDATA\Pia\Logs\pia-2026-08-*.log" "$p\local\Logs\"
Get-ChildItem "$p\local\Logs" | Measure-Object -Property Length -Sum |
  Select-Object Count, @{n='MB';e={[math]::Round($_.Sum/1MB,1)}}
```

Expect roughly **12 files / ~18 MB** — comfortably over the 7-file and 10 MB caps, so both exclusion reasons
should appear in the manifest.

Then launch through WinWright, handing the override in via `env`:

```
ww_launch(
  path: "<repo>\src\Pia.Wpf\bin\Debug\net10.0-windows10.0.17763.0\Pia.Wpf.exe",
  env: { PIA_DATA_DIR: "<%TEMP%>\pia-diag-ui\roaming",
         PIA_LOCAL_DATA_DIR: "<%TEMP%>\pia-diag-ui\local" })
```

**You do not need to close your own Pia.** The two share no settings file, no `history.db` and no log
directory. Confirm the app you are driving is the right one before asserting anything:

```
ww_get_value(selector="type=Window")        # title carries the mode and version
```

and cross-check that the throwaway `local\Logs\pia-<today>.log` has just been appended to.

## 4. The walkthrough

Selectors are from the playbook. **Prefer `ww_invoke` over `ww_click`** — invoke works regardless of window
foreground state, and `ww_click` returns `{"success": true}` for no-ops.

| Step | Call | Expect |
|---|---|---|
| 1 | `ww_invoke(selector="automationId=NavItem_Settings")` | Settings page. Sidebar buttons report a **zero bounding rect while collapsed** — that is normal, and one more reason not to click. |
| 2 | `ww_select(selector="automationId=Settings_CategoryList", optionText="General")` then `ww_get_value` on the same list | reports `General`. Do not click the item's `Text` child — it succeeds and does nothing. |
| 3 | `ww_select(selector="type=TabControl", optionSelector="automationId=Settings_General_Tab_Application")` | language-independent; the tab's content appears as **children of the selected TabItem**. |
| 4 | `ww_count(selector="automationId=Settings_General_ExportDiagnostics")` | **exactly 1**. Zero means the section did not render. |
| 5 | `ww_snapshot` scoped to the tab | The "Diagnostics" label, the description paragraph, and the button reading **"Export diagnostics"**. Read the description: it must name what is excluded and the cap. |
| 6 | `ww_screenshot` of the window | **The icon must be a download glyph, not a letter.** A bogus `SymbolRegular` name renders a garbage character with zero build warnings — this step is the only thing that catches it. If the surface is blank cream, see §6. |
| 7 | `ww_invoke(selector="automationId=Settings_General_ExportDiagnostics")` | the confirmation dialog. |

### The dialog (steps 8–11)

This is a Wpf.Ui `SimpleContentDialog` from `ShowSimpleDialogAsync`, **not** one of Pia's own dialogs, so
whether it exposes the `PrimaryButton` / `CloseButton` template-part ids is **unverified**. Resolve it
empirically and record the answer.

| Step | Call | Expect |
|---|---|---|
| 8 | `ww_snapshot` / `ww_dump_tree` on the dialog | Title **"Export diagnostics?"**. Body naming a **file count** and the destination path. The count must be **7** (the file cap binds before the byte cap only if the newest 7 fit in 10 MB — otherwise fewer; either way it must match what §5 finds in the zip). |
| 9 | `ww_count("automationId=PrimaryButton")` and `ww_count("automationId=CloseButton")` | If 1 each, use them. If 0, fall back to `type=Button[name='Yes']` / `[name='No']` (EN values of `Common_Yes`/`Common_No`). **Record which worked** — it belongs in the playbook. |
| 10 | Invoke **No / CloseButton** | Dialog closes. Then assert **nothing was written**: `Test-Path "$p\local\Diagnostics"` is `$false`. This is the Cancel case and it is worth the extra round trip. |
| 11 | Re-invoke the button, then invoke **Yes / PrimaryButton** | Dialog closes; an Explorer window opens with the zip selected. |

Note: a native `MessageBox` would be invisible to `ww_list_windows` and to window-scoped screenshots. If step
11 appears to do nothing, call `ww_dialog(action=handle)` before concluding anything.

### After the export (steps 12–13)

| Step | Call | Expect |
|---|---|---|
| 12 | Try to read the snackbar: `ww_snapshot`, then `type=Text[name*='Diagnostics']`, then the `RootSnackbarPresenter` subtree | **Unknown.** Record whether the title "Diagnostics exported" and the body "N log file(s), redacted." are reachable at all. Not a failure either way. |
| 13 | Independent window check | An Explorer window with the zip selected. `ww_list_windows` misreports modality and misses native dialogs, so cross-check with a PowerShell `EnumWindows` filtered to `explorer` if it looks wrong. |

## 5. Artifact inspection — the part that must not be skipped

```powershell
$p   = "$env:TEMP\pia-diag-ui"
$zip = Get-ChildItem "$p\local\Diagnostics\pia-diagnostics-*.zip" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
"{0}  {1:N0} bytes" -f $zip.Name, $zip.Length

Add-Type -AssemblyName System.IO.Compression.FileSystem
$a = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
$a.Entries | Select-Object FullName, Length, CompressedLength | Format-Table -AutoSize
$a.Dispose()

$out = "$env:TEMP\pia-diag-check"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
[IO.Compression.ZipFile]::ExtractToDirectory($zip.FullName, $out)
```

### 5a. The entry set

Expect **exactly**: `README.txt`, `manifest.json`, `environment.json`, and `logs/pia-YYYY-MM-DD.log` × N.
**Nothing else.** Any `providers.json`, `history.db`, `settings.json` or `.md` entry is a stop-the-line
failure.

### 5b. The residual scan — the single most important check

```powershell
$hits = Select-String -Path "$out\logs\*.log" -List -Pattern `
  ([regex]::Escape($env:USERNAME)), ([regex]::Escape($env:COMPUTERNAME)), 'C:\\Users', '\b[\w.+-]+@[\w.-]+\.\w{2,}\b'
if ($hits) { $hits | Format-Table Filename, LineNumber, Line -AutoSize } else { "clean" }
```

**Expect zero hits.** The corpus measurement found 0 across 39 files, so a hit here is either a real gap or a
line shape the corpus did not contain — **either way capture the offending line** (masked if it is a secret)
and record it. This is the one check that can find something the 4907 tests cannot.

### 5c. Redaction actually happened

```powershell
Select-String -Path "$out\logs\*.log" -Pattern '<debug-payload-dropped>' | Measure-Object | % Count
Select-String -Path "$out\logs\*.log" -Pattern '<profile-user>|<profile-local>' | Measure-Object | % Count
Select-String -Path "$out\logs\*.log" -Pattern '<url:https://host-\d{3}>'       | Measure-Object | % Count
Get-Content "$out\logs\$((Get-ChildItem "$out\logs")[0].Name)" -TotalCount 3
```

All three counts must be **> 0** (a zero means the pass silently no-opped), and the first lines must still
show the intact tab-separated `timestamp / LEVEL / [Category] / [EventId]` prefix — the export is only a
debugging asset if that survived.

### 5d. The generated entries

```powershell
Get-Content "$out\README.txt"
Get-Content "$out\manifest.json" -Raw | ConvertFrom-Json |
  % { $_.Files | Select-Object FileName, Bytes, Included, ExclusionReason } | Format-Table -AutoSize
$env = Get-Content "$out\environment.json" -Raw | ConvertFrom-Json
$env.Environment
$env.RedactionRulesApplied | Format-Table Id, Tier, Hits -AutoSize
```

Check:

- **`manifest.json` lists every file, included or not**, and the excluded ones carry
  `OverFileCountCap` / `OverTotalByteCap`. With ~12 seeded files **both** reasons should appear.
- **`environment.json` names no provider and no machine.** `ProviderTypeCounts` is types-and-counts only;
  there must be **no provider name anywhere** and no `MachineName` field.
- **All 12 rules are listed**, each with a `Tier` of `Deterministic` or `BestEffort` and a `Hits` number.
  A rule missing from the list is a bug; a rule with 0 hits is not.
- **No directory separator in any of the three generated files.** This is the invariant that keeps a path out
  of the un-redacted entries:
  ```powershell
  Select-String -Path "$out\README.txt","$out\manifest.json","$out\environment.json" -Pattern '\\|/' -List
  ```
  Expect **no match**. (A match in `README.txt` prose would still be a finding — the assertion in
  `dotnet test` forbids the character outright.)

### 5e. Second export in the same second

Invoke the button twice in quick succession. Both should succeed with **distinct file names**, or the second
should fail cleanly with the failure snackbar — **never** overwrite the first. `BuildFileName` is unique only
to the second, and `FileMode.CreateNew` is what makes the collision safe.

## 6. Traps, each already paid for once

- **Blank cream screenshots with a perfectly correct UIA tree = the documented WPF hardware-rendering
  stall,** not a broken app. `PrintWindow`, a screen grab and a resize all return the same dead frame.
  `ww_window action=resize` does **not** clear it. The fix is
  `HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration = 1` then relaunch — that is a registry
  write, so **ask the owner to run it** rather than doing it. Fall back to `ww_dump_tree` bounds: font size
  shows up as text height (12→16px, 14→19, 18→24, 22→29) and margins as gaps between sibling bounds.
  **But note step 6 needs a real pixel** — if the surface is stalled, the SymbolIcon check is deferred, not
  passed. Say so.
- **`ww_click` returns success for no-ops.** Confirm every state change independently.
- **A Wpf.Ui `ContentDialog` removes its primary button from the tree** rather than disabling it, so
  `count == 0` is the "not available" state; `enabled=false` fails with `no_match` and looks like a broken
  selector.
- **`ww_snapshot`'s `label` is not a selectable `name`** — it is inferred from neighbouring text, so
  `[name='<that label>']` can resolve 0 elements.
- **`ww_inspect label_map` pairs settings checkboxes with the *previous* row's description** (off-by-one).
  Plausible-looking and wrong — do not use it to confirm the description paragraph belongs to the Diagnostics
  section.
- **`ww_get_tree_path` emits ordinal paths the selector parser rejects**, despite claiming otherwise.
- **Window-scoped screenshots never show native dialogs**, and `ww_list_windows` does not enumerate popups.

## 7. Report

Write findings into a new `docs/failure_legibility/2026-08-<dd>-export-diagnostics-ui-test-reading.md` and
**link it from `G1`** in the checklist, replacing "Human smoke test pending" with what was actually observed.
Carry:

- The verdict per test question 1–4 in §1.
- The two answers this run is expected to produce for the playbook: **which dialog selector worked** (§4
  step 9) and **whether the snackbar is UIA-readable at all** (§4 step 12). Both belong in
  `docs/ui_automation/ui-automation-playbook.md` in the same commit.
- Any residual hit from §5b, verbatim (masked if secret).
- Whether Arm B was run, and if so that `%LOCALAPPDATA%\Pia\Diagnostics` was deleted afterwards.
- The observed file count and cap behaviour, against the ~12-file / ~18 MB seed.

Delete `$env:TEMP\pia-diag-ui` and `$env:TEMP\pia-diag-check` when done, unless the run failed — a red run's
throwaway `local\Logs\` is the evidence.

## 8. Optional follow-up, not part of this run

The deterministic half of this flow — button present, dialog opens, **Cancel** writes nothing — *is*
recordable as a `tests/ui-scripts/` script, because none of it depends on a timestamped artifact. Worth ~an
hour if the UI test finds nothing, and it would keep the button from silently losing its wiring. Record it
against the seeded fixture per that folder's README, and note that the fixture needs
`defaultWindowMode: 1` added. The export half is not recordable — the replay harness cannot look inside a zip.
