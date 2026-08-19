# Prompts: code changes that make Pia easier to test

Two self-contained prompts, in dependency order. Both come out of
`docs/2026-08-18-winwright-recording-eval.md` (what actually blocked recorded UI tests) and target the
harness in `tests/ui-scripts/`. Hand them over one at a time — prompt 1 is mechanical and unblocks
everything; prompt 2 is a refactor.

---

## Prompt 1 — AutomationIds for the settings surface

```text
Make Pia's settings UI addressable by UI automation.

Context: we can now record and replay UI tests (WinWright `ww_record` → `winwright run`; see
docs/2026-08-18-winwright-recording-eval.md and tests/ui-scripts/README.md), but the settings views
carry almost no AutomationIds, so recorded tests either depend on localized control names or cannot
target a control at all:

- 60-odd interactive controls across src/Pia.Wpf/Views/SettingsViews/*.xaml, and only OptimizeView
  (5) and PersonasView (4) have any AutomationIds. GeneralView has 17 inputs and zero.
- CheckBoxes are reachable only through their Content-derived Name, e.g.
  `type=CheckBox[name='Start minimized to system tray']`. That breaks when the UI language changes,
  and `winwright heal` cannot repair name-only selectors — it scores only elements that have an
  AutomationId.
- The two ComboBoxes on General → Speech (STT engine, STT language) have an empty Name *and* an empty
  AutomationId, sit on the same tab, and the selector grammar has no ordinal attribute. No selector
  can distinguish them, so that flow is unautomatable today.

Scope — add `AutomationProperties.AutomationId` to every interactive control (CheckBox, ComboBox,
TextBox, PasswordBox, Slider, NumberBox, and the buttons that trigger an action) in:
  src/Pia.Wpf/Views/SettingsViews/GeneralView.xaml      (start here — 17 inputs)
  src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml    (32 inputs)
  src/Pia.Wpf/Views/SettingsViews/ProvidersView.xaml    (also give the per-row Edit/Delete buttons
                                                         ids — today `type=Button[name='Edit']`
                                                         matches every row)
  src/Pia.Wpf/Views/SettingsViews/AccountView.xaml
  src/Pia.Wpf/Views/SettingsViews/OptimizeView.xaml     (fill the gaps)

Naming: `Settings_<Category>_<Field>` in PascalCase after the prefix, e.g.
`Settings_General_StartMinimized`, `Settings_General_SttLanguage`, `Settings_Assistant_HistoryEnabled`.
Match the existing conventions in SettingsView.xaml (`Settings_CategoryList`, `SettingsCategory_*`)
and RoutinesView (`Routines_Field_Name`). AutomationIds are identifiers, not UI text — do NOT add
resx entries and do not touch ViewStrings.Designer.cs.

Also fix while you are in there:
- Ambiguous names: the `Settings_Agent_AutoApproveBuiltInWrites` string is bound to two different
  CheckBoxes in AssistantView.xaml (lines 208 and 578), so a name selector matches both and
  `ww_invoke` silently takes the first. Distinct ids resolve it.
- Anything a test needs to assert must not live inside a `ui:InfoBar` — that control exposes no
  automation peer at all in this Wpf.Ui version. If a status message matters, move it to a peer-visible
  element (a `TextBlock` with an id, as `Routines_StatusMessage` does).

Add a guard test so this does not rot: a new xunit fact next to the existing view-parse tests
(tests/Pia.Wpf.Tests/Views/GeneralViewParseTests.cs is the model — it uses `WpfStaHost.Run` plus the
logical-tree walk in BindingPathWalker.cs). Parse each settings view, walk the logical tree, and fail
with the control's type and label when an interactive control has no AutomationId. Keep an explicit,
commented allowlist for genuine exceptions rather than weakening the assertion.

Finally, update the recorded script that this unblocks: rewrite the two selectors in
tests/ui-scripts/scripts/settings-general.json from `type=CheckBox[name='…']` to the new
`automationId=…` form, then verify with `tests/ui-scripts/Invoke-UiScripts.ps1` (close Pia first).

Definition of done:
- `dotnet build -t:Rebuild -v:n` reports 0 Warning(s) 0 Error(s) in BOTH Debug and Release (WPF
  re-reports src warnings under a generated *_wpftmp.csproj — fixing the source clears both).
- `dotnet test` with no filter: failed: 0.
- The new guard fact fails if you delete one AutomationId (check this, don't assume it).
- `Invoke-UiScripts.ps1` passes twice in a row.
- Update the AutomationId table in docs/ui-automation-playbook.md and drop the now-stale "settings
  views carry no AutomationIds" known-gap bullet.
- Follow CLAUDE.md comment discipline: default to no comment, one short line when the WHY is
  non-obvious, and never cite a task/spec id in source.
```

---

## Prompt 2 — a hermetic profile for UI tests

```text
Let Pia run against a throwaway data directory so UI tests stop touching the developer's real profile.

Context: tests/ui-scripts/Invoke-UiScripts.ps1 has to back up %APPDATA%\Pia\settings.json, overwrite
it with a fixture, and restore it afterwards, because the app has no way to be pointed at another
profile — `Environment.GetFolderPath(SpecialFolder.ApplicationData)` ignores a redirected %APPDATA%
(verified: it resolves through SHGetKnownFolderPath). Consequences today: a replay shares
%LOCALAPPDATA%\Pia\history.db and the log directory with the real install, the harness refuses to run
while Pia is open, and a crashed run can leave the fixture in place.

Scope — introduce one resolver and route every data-path read through it:
- New `PiaPaths` (src/Pia.Wpf/Infrastructure/): `RoamingDataDirectory` and `LocalDataDirectory`,
  each defaulting to exactly today's value (`…/ApplicationData/Pia`, `…/LocalApplicationData/Pia`)
  and overridable by the environment variables `PIA_DATA_DIR` and `PIA_LOCAL_DATA_DIR`. Resolve once
  at startup, log the effective paths at Information level (paths are not sensitive; contents are).
- Prefer env vars over a CLI switch: App.xaml.cs parses no command-line arguments today, and the
  test harness can only influence the app through the environment it hands to `winwright run` (which
  has no --env flag, but the launched app inherits the harness process environment).
- Route the 19 files that currently call `SpecialFolder.ApplicationData` /
  `SpecialFolder.LocalApplicationData` directly: App.xaml.cs, Bootstrapper.cs,
  Services/JsonPersistenceService.cs, Infrastructure/SqliteContext.cs (`DefaultDbPath`),
  Infrastructure/Sync/SyncBaseStore.cs, Infrastructure/Vault/VaultPathProvider.cs,
  Infrastructure/AssistantWorkspace.cs, Services/Consent/*, Services/LiveTranscription/*,
  Services/Operators/JsonlAssignmentConsentStore.cs, Services/Plugins/CabManagerService.cs,
  Services/TtsService.cs, Services/EmbeddingService.cs, Helpers/GitLocator.cs,
  Helpers/VsCodeLauncher.cs, ViewModels/GeneralSettingsViewModel.cs.
  Where a path is a `static readonly` field (JsonPersistenceService.SettingsDirectory) make sure the
  override still applies — a static initialized before the resolver runs is the trap here.
- Leave downloaded-model directories (Whisper/Piper/Parakeet, Chromium) on the real LocalAppData by
  default even under an override, unless it is trivial not to: re-downloading gigabytes per test run
  is worse than sharing them. Whatever you decide, say so in the README.

Then simplify the harness: add `-DataDir <path>` (default: a fresh temp directory per run), set the
env vars around the `winwright` invocation, drop the backup/seed/restore dance and the
"Pia is running" pre-flight when a data dir is in use, and copy the fixture into the throwaway
profile instead of over the user's. Keep `-KeepProfile` as the escape hatch for driving the real
profile deliberately. Update tests/ui-scripts/README.md — including the note that this is what makes
a clean-CI-agent run possible, which nobody has tried yet.

Definition of done:
- With no env vars set, every path is byte-identical to today's behaviour (assert this in tests).
- With `PIA_DATA_DIR` set to a temp dir, a fresh launch creates settings.json there and never writes
  to %APPDATA%\Pia (verify by hashing the real file before and after).
- `Invoke-UiScripts.ps1` passes twice in a row without touching the real profile, and passes while
  the developer's own Pia instance is open.
- `dotnet build -t:Rebuild` 0 warnings in Debug and Release; `dotnet test` failed: 0.
- No secrets or user content in the new log lines (see the Privacy-First Logging section of
  CLAUDE.md).
```

---

## Not worth a prompt yet

- **Test-mode network kill switch.** The fixture already sets `syncEnabled: false`, which keeps a
  replay off the server. Revisit only if a scenario needs sync enabled *and* offline.
- **Deterministic window geometry.** Every recorded step is pattern-based, so window size and
  position do not affect replay. Do not seed geometry just because the app persists it.
- **WinWright's own defects** (`ww_get_tree_path` emits unparseable selectors, `ww_snapshot`'s `label`
  is not a selectable name, `heal`'s 0.70 default threshold misses one-character id typos). Report
  upstream; there is nothing to change in Pia.
