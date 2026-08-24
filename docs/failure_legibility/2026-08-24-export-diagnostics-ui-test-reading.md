# Export Diagnostics — what the UI run actually found

**Status:** run, 2026-08-24. Arm A and Arm B both executed. **Owner:** Marco Altmann.
**Written:** 2026-08-24.
**Origin:** executing [`2026-08-24-export-diagnostics-ui-test-plan.md`](2026-08-24-export-diagnostics-ui-test-plan.md),
which exists because `G1` in
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md)
said "human smoke test pending" and nothing in `c37b78c2`..`ed01cecc` had ever run in the app.

Executable cold: every number below is quoted from the run, and §8 says how to reproduce it.

---

## 1. Verdict

**All four test questions pass.** The export was driven through the real UI **eight** times — seven
that wrote an archive and one that was correctly refused — and the residual scan came back **zero**
every time, against real logs at real scale.

| # | Question | Verdict |
|---|---|---|
| 1 | Button reachable, section rendered, icon a real glyph? | **Pass.** `ww_count` = 1; the description names the exclusions and the cap; the icon is a download arrow, confirmed on a pixel. |
| 2 | Dialog appears, reads correctly, honours Cancel? | **Pass.** Title, body and file count all correct; **Cancel wrote nothing at all** — the `Diagnostics` directory did not even come into existence. |
| 3 | Export succeeds against a live, sink-held log directory? | **Pass, premise proved first.** `File.OpenRead` on today's log threw `IOException` *before* the export, and today's log is in the archive anyway. |
| 4 | Reveal-in-Explorer surfaces the file? | **Pass.** Explorer opened with the zip **selected**, verified through `Shell.Application`, not by reading a window title. |

And the load-bearing one, §5 of the plan: **the artifact is clean.** Across both arms, the residual
scan over the log entries *and* the three generated entries found **0** hits for the account name,
the machine name, `C:\Users`, `AppData`, the throwaway root, an e-mail shape, or **any of the five
configured provider names**.

**Four defects were found.** None of them is a privacy leak; all four are legibility failures, which
is the thing this folder exists to fix. All four are fixed in the same commit as this document —
see §6. The one that only the app could have found is §6.4.

## 2. What was run

Eight invocations, all through `ww_invoke` on the real button and the real dialog. Runs 8 and 9 came
after the fixes in §6 and are the runtime proof for three of the four.

| # | Arm | Seed | Result |
|---|---|---|---|
| 1 | A | 20 real log files, 23.1 MB | 7 files, `pia-diagnostics-2026-08-24-192248.zip`, 444,227 bytes |
| 2 | A | +12.4 MB on one mid-run file, plus `pia.log` and a rolled `pia-2026-08-24-001.log` | 3 files — **byte cap bound** |
| 3 | A | 91 decoy zips planted over the next 91 seconds | **refused, nothing written, no decoy touched** |
| 4 | A | decoys removed | succeeded again — the button recovers |
| 5–6 | A | same | succeeded twice (two snackbar-timing probes) |
| 7 | **B** | the **real** `%LOCALAPPDATA%\Pia\Logs`, 39 files | 7 files, 444,176 bytes |
| 8 | A, **post-fix** | the run-2 seed again | manifest now reads `"OverFileCountCap"` / `"OverTotalByteCap"` / `"UnrecognisedName"` |
| 9 | A, **post-fix** | 121 decoys | refused, and now says **why** — in the notice and in the log |

Arm B's artifact and its extracted copy were **deleted immediately afterwards**;
`%LOCALAPPDATA%\Pia\Diagnostics` does not exist. The real roaming profile, the real log directory
and `Documents\Pia Assistant` were all verified untouched by timestamp after the run.

## 3. The walkthrough, step by step

Steps 1–4 were exactly as the plan predicted. The Application tab renders a **Diagnostics** heading,
this description —

> Writes a redacted copy of Pia's own log files to a zip you can attach to a support request. Logs
> only — never chats, vault content, your history database or your provider credentials. File paths,
> your account and machine name, host names, e-mail addresses and tokens are replaced before anything
> is written, and every debug message body is dropped whole. The newest 7 log files up to 10 MB are
> included. Nothing is sent anywhere: the zip is written to your disk and you decide who sees it.

— and a button reading **Export diagnostics**.

**Step 6, the SymbolIcon check, passes on a real pixel.** The glyph resolves to `U+F151`, a private-use
codepoint the icon font actually carries, and an element-scoped screenshot shows a download arrow,
not a Latin letter. No hardware-rendering stall occurred; §6 of the plan was not needed.

**Step 8, the dialog.** Title `Export diagnostics?`, and verbatim:

> 7 log file(s) will be redacted and written to
> C:\Users\maltm\AppData\Local\Temp\pia-diag-ui\local\Diagnostics. Nothing is sent anywhere — open the
> zip and check it before you share it.

The count is live: after the run-2 reseed the same dialog said **3**, matching what `Plan()` would
compute from the directory as it then stood. While the dialog is open the Export button greys out.

**Step 10, Cancel.** The dialog closed and `Test-Path` on the Diagnostics directory returned `$false`
— not "an empty directory", but no directory. Nothing was written.

**Step 11, Yes.** Dialog closed, the zip appeared, Explorer opened. Note that **reveal opens a new
Explorer window per export** rather than reusing one; every successful export left one behind.

## 4. The two answers the plan wanted for the playbook

### 4a. The dialog selectors: `PrimaryButton` / `CloseButton` both work

This was recorded as unverified because the confirmation is a Wpf.Ui `SimpleContentDialog` from
`ShowSimpleDialogAsync`, not one of Pia's own dialogs. It **does** carry the shared ids:

```
ww_count("automationId=PrimaryButton") -> 1     # named "Yes"
ww_count("automationId=CloseButton")   -> 1     # named "No"
```

Better still, the whole dialog is a nested `Window` peer named by its title, so one call reads all
of it — and `type=Window` resolves **0** when no dialog is up, which makes it a reliable presence
check:

```
ww_dump_tree(selector="type=Window")
[Window]  "Export diagnostics?"
  [Text]  "Export diagnostics?"
  [Text]  "7 log file(s) will be redacted and written to …"
  [Button] #PrimaryButton "Yes"
  [Button] #CloseButton "No"
```

### 4b. The snackbar: the plan was looking in the wrong place, and half of it is unreadable by design

`RootSnackbarPresenter` never renders anything. `ISnackbarService` is bound to
`Services.Flow.FlowSnackbarService`, which funnels **every** `Show(...)` into the Flow rail instead;
`SetSnackbarPresenter` stores the presenter and never drives it. So the notice is a **Flow item**,
and its readability depends on severity:

- **Failure (`ControlAppearance.Danger`) → `FlowLifetime.Persistent`.** Fully UIA-readable, at
  leisure. Read from run 3's refusal:

  ```
  [DataItem]  "Pia.ViewModels.Flow.FlowItemViewModel"
    [Text] #TitleText "Error"
    [Text] #BodyText  "The diagnostics export could not be written."
    [Button] #Flow_Dismiss_9a2a2340-… "Dismiss"
  ```

- **Success (`ControlAppearance.Success`) → `FlowLifetime.Transient(5s)`.** A whisper-peek that then
  expires. **Four attempts failed to catch it**: one MCP round trip after the invoke is already too
  late, `PeekItems` reads empty, and a window screenshot taken seconds after the export shows no
  notice. It is not absent — the export succeeded each time — it is simply gone. Treat the success
  notice as **unassertable from an MCP-driven script**; assert the artifact instead.

Two more rail facts worth having: while collapsed the rail shows an unread **count badge** (a `Text`
reading `"1"` under the bell) which *is* readable and makes a good "something was published" probe;
and expanding it needs a **real mouse click** — the collapsed `Handle` is a `Border` with a
`MouseBinding`, carrying no `AutomationId` and no `InvokePattern`, so `ww_invoke` cannot open it.

## 5. The artifact — §5 of the plan, in full

### 5a. Entry set

Exactly `README.txt`, `manifest.json`, `environment.json` and `logs/pia-*.log`. **No** `providers.json`,
`history.db`, `settings.json` or `.md` entry, in any of the six archives. Worth noting that the
throwaway `local\` directory held live `history.db`, `history.db-shm` and `history.db-wal` throughout
— they are siblings of `Logs\`, and none of them came near the archive.

Run 2 confirms the wider `pia*.log` enumeration end to end: a **rolled name**,
`pia-2026-08-24-001.log`, was parsed as that day's file, included, and carried into the archive —
which is `ed01cecc` exercised at runtime.

**Correction, 2026-08-24.** `pia-2026-08-24-001.log` was **hand-seeded**, and it is a name the sink cannot
produce: NReco appends the roll index with no separator, so the real form is `pia-2026-08-241.log`. Run 2
therefore exercised the separator branch and not the one that ships, and the parser was in fact rejecting
every file the sink can roll. Both forms are accepted now.

### 5b. The residual scan — the check that could have found something

**Zero hits, both arms.** The pattern set was wider than the plan asked for: account name, machine
name, `C:\Users`, `AppData`, the throwaway root `pia-diag-ui`, an e-mail shape, and all five
configured provider names — run over the seven log entries **and** over `README.txt`,
`manifest.json` and `environment.json`, which bypass the redactor by construction.

Nothing to capture. This is the strongest result in the run: 45,944 real lines in Arm A run 1 alone,
written by a real app across 20 real days, and not one leak.

### 5c. Redaction fired, and the prefix survived

Arm A run 1, counted over the archive's own log entries:

| marker | count |
|---|---|
| `<debug-payload-dropped>` | 10,021 |
| `<profile-*>` | 923 |
| `<url:https://host-NNN>` | 22,430 |
| `<provider-N>` | 2,310 |
| `host-NNN` | 23,004 |
| `<path>` | 9,095 |

and the record prefix is intact, tab for tab:

```
2026-08-24T12:24:51.9219150+02:00 <TAB> INFO <TAB> [Bootstrapper] <TAB> [0] <TAB> …
```

### 5d. The generated entries

`environment.json` names **no machine and no provider**: `ProviderTypeCounts` is types-and-counts
(`PiaCloud: 1, Mistral: 1, OpenRouter: 1, Ollama: 1, OpenAI: 1`), `ProviderCount: 5`, and there is no
`MachineName` field. All **12 rules** are listed with a `Tier` and a `Hits` number. `R05_MACHINE_NAME`
and `R06_USER_NAME` both read 0, exactly as §3 of the feature doc predicts — R04 consumed every
occurrence first.

**The plan's "no directory separator" check over-fires, and the feature doc overstates it.** The
shipped assertion is `Path.DirectorySeparatorChar`, i.e. backslash only. There are **zero
backslashes** in the three generated entries. There are two forward slashes, both prose:

```
README.txt:4      … environment.json, and logs/ - the app's own log
environment.json  "every DBUG/TRCE message body, which is where the whole Conditional(DEBUG) …"
```

Neither is a path. Not a defect — a sentence in §4 of the feature doc, corrected in this commit.

### 5e. Caps, and the collision guard

**The plan's cap prediction was wrong, and it is arithmetic, not a defect.** Selection is a
contiguous newest-first run, so **only the newest 7 files can ever be reached** no matter how many
older ones exist. On the natural seed those seven total 8.59 MB — under the 10 MiB byte cap — so the
**file cap binds and the byte cap cannot**. Twenty seeded files produced 7 included, 13 excluded, one
exclusion reason.

Run 2 forced the other half by growing one mid-run file to 12.4 MB, and the walk stopped exactly
where the arithmetic said it would:

```
pia-2026-08-24.log        137,102  included
pia-2026-08-24-001.log     21,280  included     (the rolled name)
pia-2026-08-23.log        126,932  included
pia-2026-08-22.log     12,361,729  EXCLUDED  OverTotalByteCap   <- the breach
pia-2026-08-21.log      1,040,189  EXCLUDED  OverFileCountCap
… 17 more …                        EXCLUDED  OverFileCountCap
pia.log                     4,461  EXCLUDED  UnrecognisedName
```

So **all three exclusion reasons were observed at runtime**, and one behaviour is worth writing down
because it is not obvious from the code: once the byte cap stops the walk, **exactly one file ever
carries `OverTotalByteCap`** — every file after the stop is labelled `OverFileCountCap`, whichever
cap actually ended the run.

**Correction, 2026-08-24.** That labelling is now fixed: every file after a byte-cap stop carries
`OverTotalByteCap`, so a manifest can no longer blame a file count that had slots to spare.

**The collision guard was exercised deterministically rather than by racing the clock.** Two dialog
round trips will never land in the same second, so instead 91 decoy zips were planted covering
`pia-diagnostics-2026-08-24-HHmmss.zip` for the next 91 seconds and the export was invoked into that
minefield. Result:

- **no archive written** — 93 files before, 93 after;
- **all 91 decoys byte-identical**, so the generic cleanup arm did not delete a file this export did
  not write;
- the two real archives from runs 1 and 2 untouched;
- a persistent Flow error item raised (§4b);
- and export #4, run straight afterwards with the decoys removed, **succeeded** — the button recovers.

That is `FileMode.CreateNew` and its `when (File.Exists(...))` filter, proven in the app.

## 6. The four defects

### 6.1 `manifest.json` reports the exclusion reason as a bare integer

Observed, Arm A run 2:

```json
"FileName": "pia-2026-08-22.log", "Included": false, "ExclusionReason": 1
"FileName": "pia-2026-08-21.log", "Included": false, "ExclusionReason": 0
"FileName": "pia.log",            "Included": false, "ExclusionReason": 2
```

`0`/`1`/`2` are `OverFileCountCap`/`OverTotalByteCap`/`UnrecognisedName`, so the value is correct and
useless: a support engineer opening the archive cannot tell which. Worse, `0` is also what a reader
expects "none/default" to look like, and `"ExclusionReason": null` on the *included* rows sits right
next to it. `environment.json`'s `Tier` in the same archive reads `"Deterministic"` because the
collector calls `.ToString()`; the manifest goes through the default enum serializer instead.

This contradicts the archive's own README — *"so a file left out is visible from in here rather than
simply absent"* — and §4 of the feature doc, which says the manifest lists every file **with its
reason**.

**Fixed:** `JsonStringEnumConverter` on the shared serializer options. `ProviderTypeCounts` is keyed
`string`, so `environment.json` is unaffected.

### 6.2 A refused export leaves nothing in the log

The `OutputAlreadyExists` arm is the **only** failure arm in `DiagnosticsExportService` that does not
log. Run 3 refused an export and the log window `19:29`–`19:31` contains **zero** `WARN`/`FAIL` lines
— not one word about it. The user sees a generic failure notice and the log a support engineer would
then ask for says nothing happened.

**Fixed, and confirmed in the app (run 9):** `WARN … Diagnostics export refused: an archive from the
same second already exists`, where before there was nothing.

### 6.3 The six failure causes collapse into one message

`DiagnosticsExportFailure`'s own doc comment calls it *"a cause the caller can branch on"*, and the
caller does not: `GeneralSettingsViewModel` branches on `!result.Succeeded` and shows one string,
`Msg_Settings_DiagnosticsFailed`, for all six. So "the name is taken, try again in a second" and "the
disk refused the write" are indistinguishable to the person the message is for.

**Fixed:** the two causes a user can actually hit and act on — `OutputAlreadyExists` and
`OutputDirectoryMissing` — get their own message; `SourceDirectoryMissing`/`NoLogFiles` route to the
existing "Nothing to export" pair; `OutputInsideSourceDirectory` and `WriteFailed` keep the generic
one. `OutputInsideSourceDirectory` is an invariant `PiaPaths` guarantees and no user can reach it. Run 9
read the new text straight off the Flow item: *"An archive from this second already exists. Try again
in a moment."* — which also proves a resx-only key resolves, since `LocalizationSource` goes through
`ResourceManager.GetString` and would otherwise have rendered `[Msg_Settings_DiagnosticsFailed_NameTaken]`.

### 6.4 A provider name rewrote the inside of another rule's token

**This is the one that only a live run against a real profile could find**, and it is why Arm B
earned its cost. The developer's profile has a provider named `local`. R09 (`PROVIDER_NAMES`) runs
*after* R04 (`PROFILE_ROOTS`), and its boundary — `(?<![A-Za-z0-9])name(?![A-Za-z0-9])`, case
insensitive — happily matches inside the token R04 just emitted, because `-` and `>` are not
alphanumeric. Arm B, verbatim from `logs/pia-2026-08-24.log`:

```
… INFO [Bootstrapper] [0] Data directories: Roaming=<profile-roaming>, <provider-3>=<profile-<provider-3>>, Overridden=False
```

`<profile-local>` came out as `<profile-<provider-3>>`. Counted over the whole Arm B archive:
`<profile-roaming>` 295, `<profile-user>` 326, **`<profile-local>` 0** — the token is not rare in
that archive, it is *extinct*, and R12's `TokenisedDirectoryPattern`, which anchors on
`<profile-(roaming|local|user)>`, silently stops firing on every local-root path as a result.

The same collision hits ordinary prose: in Arm A the literal word `Local=` in
`Data directories: Roaming=…, Local=…` became `<provider-3>=`.

This generalises past provider names. Every rule keyed on **arbitrary user-chosen text** — provider
names, host literals, the machine name, the account name — can rewrite the inside of an earlier
rule's replacement token. A provider or host named `path`, `host`, `user`, `token`, `machine` or
`profile` would corrupt a different one.

**Fixed, and only half of it:** the raw-key replacements now run **outside** the placeholder spans
earlier rules emitted, so `<profile-local>` survives. The `Local=` → `<provider-3>=` case is **not**
fixed and is not a bug in the guard — that occurrence is outside any placeholder, and a
five-character provider name clears the existing four-character floor. Naming a provider after a
common English word costs you that word in your logs; the mitigation would be a stop-word list or a
longer floor, and neither is obviously right.

Note the guard must be applied to the raw-key replacement **only**, never to a pass that deliberately
anchors on an emitted token: R05's `MachineSuffixPattern` (`<machine>(\.[…])+`) and R12's
`TokenisedDirectoryPattern` both read the previous rule's output on purpose, and wrapping either
would have re-broken what the guard exists to protect.

## 7. What the plan got wrong, for the next one

- **The cap prediction.** "~12 files trips both caps" cannot be true for any seed: only the newest 7
  are reachable, so the byte cap binds only if those seven are big. Compute the newest-7 sum before
  launching and write the prediction down.
- **`Test-Path` on the Diagnostics directory** is the wrong Cancel assertion in general — it happens
  to work only because the directory is created lazily. Count `pia-diagnostics-*.zip` instead.
- **§5e is reachable**, contrary to the note that two exports cannot collide: plant decoys instead of
  racing the clock.
- **The snackbar section looked at `RootSnackbarPresenter`**, which this app never drives.
- **The `\\|/` separator check** is wider than the shipped assertion and reports two false alarms.

## 8. Reproducing this

```powershell
$p = "$env:TEMP\pia-diag-ui"
New-Item -ItemType Directory -Force "$p\roaming", "$p\local\Logs", "$p\files" | Out-Null
Copy-Item "$env:APPDATA\Pia\settings.json","$env:APPDATA\Pia\providers.json" "$p\roaming\"
# syncEnabled=false, autoUpdateEnabled=false, defaultWindowMode=1,
# assistantFilesFolder="$p\files"  <- PIA_DATA_DIR does NOT isolate that one.
# Leave launchAtStartup TRUE: App.xaml.cs only writes the HKCU Run key when the setting and the
# key disagree, so flipping it to false DELETES the real one.
Copy-Item "$env:LOCALAPPDATA\Pia\Logs\pia-2026-08-*.log" "$p\local\Logs\"
```

then `ww_launch` with `PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR` pointed at `$p\roaming` / `$p\local`, and
assert the premise for question 3 before anything else:

```powershell
try { [IO.File]::OpenRead("$p\local\Logs\pia-<today>.log"); "PREMISE FAILED" }
catch { "PREMISE OK - the sink is holding it" }
```

You do not need to close your own Pia; the two share no settings file, no `history.db` and no log
directory.

## 9. Left open

- **The success notice is unassertable** from a script (§4b). If that ever needs a regression test,
  the lever is `FlowLifetime`, not the test.
- **The `Local=` half of §6.4** — a common-word provider name still eats that word in the logs.
- **The deterministic half of this flow is recordable** as a `tests/ui-scripts/` script — button
  present, dialog opens, Cancel writes nothing — exactly as §8 of the plan says. Nothing found in
  this run argues against it, and the fixture needs `defaultWindowMode: 1`.
