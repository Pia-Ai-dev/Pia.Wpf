# Gate profile hygiene — the test suite's footprint on the developer's real machine

**Status:** F1, F2 and F3 all closed. One instance of the same defect class is **still live** (§5), and one
seeding trap found in an app run is recorded in §6 so it is not re-diagnosed as a product bug.
**Owner:** Marco Altmann.
**Written:** 2026-08-24.
**Origin:** the `## F — Test hygiene` section of
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md),
found 2026-08-23 while seeding a throwaway profile for the wide artifact read. Not from the hermes review,
and **no plan doc was ever written** — that checklist section was the sole record, and it is being stripped
to one sentence per row, which is why this file exists.

The rule this track establishes: **`dotnet test` must not mutate the machine it runs on.** Everything below
is measured, not argued — every claim names the evidence that produced it.

---

## 1. F1 — the documented gate wrote to the user's REAL profile

Run with no environment overrides, the gate created and mutated `%LOCALAPPDATA%\Pia\history.db`, `Logs\`,
`runs\` and `workdir\` at the **real** path.

**The evidence, not the inference.** `AgentRuns.PersonaId` and `ReasoningEffort` exist only in code committed
at `cb5d9ba7` — hours old and never launched — yet the real `history.db-wal` carried both after a gate run,
and opening the `.db` *without* its `-wal` showed neither. So `MigrateSchema` ran against the real file during
that run. Re-running the whole gate with `PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR` pointed at a scratch directory
then produced a complete profile there — `history.db` plus 832 KB of WAL, both json files and all three
subdirectories — so this was not one stray `ALTER`.

**Narrowed by checking rather than asserting.** Two writes at the real path were confirmed: `history.db` (the
schema migration, plus a WAL restamped by every gate run) and `%LOCALAPPDATA%\Pia\runs`, which held 17 files
and was written again during the last gate run. Two were **not**: `settings.json` and `providers.json` were
untouched across four gate runs — mtimes still predating the session, all five providers, both mode defaults
and `assistantFilesFolder` reading back intact. The scratch-directory copies of those two were an artefact of
pointing *both* env vars at one empty directory, not evidence of a real-path write. So this is a
schema-and-workspace leak, not a settings leak.

**Confirmed negative worth keeping:** the redirected corpus wrote **zero** files into the real
`Documents\Pia Assistant`, so `AssistantFilesFolder` redirection does hold.

**Why it went unnoticed:** every confirmed write is additive and self-healing (a nullable column the app would
add at next launch anyway), and `DataDirectoryRoutingTests` / `PiaPathsTests` police the *production* code's
use of `SpecialFolder`, not the test project's own resolution of an unset override.

**A blanket redirect is not the fix.** Nine tests fail under one, because their premise is that no override is
set: the five `PiaPathsTests.RoutedMember_ObservesAnOverrideAppliedAfterItsTypeIsLoaded` rows,
`DataRoots_WithOverride_UseTheOverrideVerbatim`,
`AssistantWorkspaceTests.LegacyWorkdir_is_workdir_under_local_app_data_Pia`,
`VaultPathProviderTests.Default_root_is_Pia_Vault_under_local_app_data`, and
`FilesToolHandlerWriteTests.Write_IntoWorkdir_IsAllowed_ThroughRealResolver` — that last one writes through
the *real* resolver by name, which is where `workdir\` came from.

**Fixed 2026-08-23, and the offenders were NAMED by instrumentation rather than by reading.** A temporary
stack-trace dump in `SqliteContext` (both constructors and the first `GetConnection`), gated on an env var and
reverted before the commit, caught every real-profile open in one gate run. There were **two, not one** — a
third offender turned up alongside them that opened no database and created a directory instead:

- `ScheduledJobToolIntegrationTests` constructed the **default-path** `SqliteContext` — its `<remarks>` called
  the real `history.db` "a known plan-accepted tradeoff" — so `EnsureSchema` / `MigrateSchema` ran against the
  user's database, into which the test then inserted and deleted its own `TEST_E2E_` job. It now opens a
  throwaway database under the temp directory and deletes it on dispose.
- `WpfStaHost` **boots the whole application.** `Application`'s constructor POSTS its startup callback and the
  host pumps a dispatcher, so `App.OnStartup` ran without anyone calling `Run()` and took
  `Bootstrapper.InitializeAsync()` with it: the DI graph, the real history database, and
  `VaultIndexer.ReconcileAsync()` over the real vault. The host's own comment — *"Run() is never called, so
  OnStartup's SetLanguage() cannot mutate the process-wide culture"* — was false. The seam is
  `Dispatcher.Hooks.OperationPosted`: capture what the constructor posts and `Abort()` it before the first
  pump. Overriding `OnStartup` in a subclass is **not** a seam, measured rather than assumed — `LoadComponent`
  resolves `App.xaml` against the component's own assembly, so a test-assembly subclass fails with *"does not
  have a resource identified by the URI '/Pia.Wpf;component/app.xaml'"* and takes all 143 view tests down with
  it. `WpfStaHostBootTests` is the tripwire: after the host has run, `Bootstrapper.ServiceProvider` must still
  throw.
- **The third, and not a database open:** `FilesToolHandlerWriteTests.Write_IntoWorkdir_IsAllowed_ThroughRealResolver`
  created `%LOCALAPPDATA%\Pia\workdir` and left it behind on a machine that had none. It now removes it
  (non-recursive) only when it was the call that created it.

**The result is measured.** With the probe still in and the three fixes applied: **zero** real-profile opens,
and the only failure in 4664 was the probe itself tripping
`DataDirectoryRoutingTests.OnlyPiaPaths_ReadsTheProfileFolders`. After reverting it: **4665 / failed: 0 / 4611
succeeded / 54 skipped**, with `history.db`, `-wal` and `-shm` **byte-identical** (SHA256) across the run —
where every earlier gate run had grown the WAL by ~64 KB. `settings.json`, `providers.json`, `templates.json`
and `workdir` untouched.

**The nine tests were never touched, and none of them had to be.** The leak was two named tests rather than
ambient `PiaPaths` unsafety, so those nine still assert the real profile and still only read strings from it.

---

## 2. F3 — the remaining footprint was two directory mtimes, and both were by premise

`%LOCALAPPDATA%\Pia\runs` was restamped by 47 tests in five classes (`RunWorkspacePromotionTests`,
`RunWorkspaceRedirectsTests`, `FilesToolHandlerRunsDirGuardTests`, `FilesToolHandlerWorkspaceEscapeTests`,
`LiveTurnExecutorPlannedRunTests`) because `RunWorkspaceRedirects.Record`'s containment gate refuses any root
outside the real `RunsRoot`. `%LOCALAPPDATA%\Pia` itself was restamped by
`FilesToolHandlerListTests.ListRelativeFiles_NegationCannotResurfaceSensitivePathGuardBlockedPath`, which needs
a directory inside a root the **live** guard blocks. Each created a GUID-named child and deleted it, so the
residue was a parent directory's mtime plus an orphan if a test died mid-body.

**Done 2026-08-24, and the containment gate needed nothing — only the guard did.**
`RunWorkspaceRedirects.Record` already re-derives its gate on every call and `AssistantWorkspace.RunsRoot` is
already a property. The whole blocker was `SensitivePathGuard`'s two `static readonly` arrays, frozen at type
load — the exact trap `PiaPaths` exists to avoid, unnoticed because production sets its environment before
anything loads. They now rebuild behind a lock keyed on the two routed roots, so production still builds once.

- **Redirect, not rewrite.** `RedirectedProfileFixture` applies `PiaPaths.OverrideForTests` for a class's
  lifetime; the five run-workspace classes take it as an `IClassFixture` and move into the existing
  **`PiaPathsStatic`** collection, which is already `DisableParallelization = true`. That collection is what
  makes the redirect safe rather than a race — `OverrideForTests` sets process-wide environment variables, and
  nine other tests' premise is that no override is set. F1 refused a *blanket* redirect for exactly that
  reason; a targeted one inside the serialized collection has the same effect without the collision.
- **The second offender needed its own class.**
  `ListRelativeFiles_NegationCannotResurfaceSensitivePathGuardBlockedPath` read
  `SpecialFolder.LocalApplicationData` directly. Moved to `FilesToolHandlerBlockedRootListTests` on the
  redirected profile, and it gained a **non-vacuity assertion first** — `IsBlocked` must say the path is
  blocked — because the test also passes against a root the handler simply cannot read.
- **Two new facts hold the fix**, both in `SensitivePathGuardOverriddenProfileTests`: `IsBlocked` follows an
  override applied *after* the guard has already answered (and reverts when it is dropped), and the runs
  carve-out moves with the profile while a sibling of it stays blocked. The class's ctor reads the guard from
  the real profile first, which is what makes them non-vacuous.
- **Measured.** Snapshot → gate run → compare: **0 of 9 changed.** `%LOCALAPPDATA%\Pia`, `\runs`, `\workdir`
  and `\Logs` mtimes all unmoved; `history.db`, `-wal`, `-shm`, `settings.json`, `providers.json` all
  byte-identical by SHA256. **The first attempt at this comparison was wrong** — `ConvertFrom-Json` parsed the
  ISO timestamps into `DateTime`s, and comparing one to a string is always unequal, which reported `workdir` as
  changed since June. Compare ticks or hashes as strings.
- Cost: none measurable. 4825 / failed: 0 at 29.2s, against 28.7s before the collection change.

---

## 3. F2 — a chat-history row could be DELETED by AutomationId but not opened by one

Found 2026-08-23 when a resume check could not open a parked run's chat: the history row would not activate by
`ww_click`, double-click, `SelectionItemPattern` or Enter, and its only id-addressable action was
`AssistantChat_Delete_{ChatId}`. The one thing a script could reliably do to a named past chat was destroy it.

**Done 2026-08-23, and it landed UNVERIFIED IN THE APP** — that batch had no desktop session, so none of it was
exercised through UIA. Build- and gate-verified only.

- `AssistantChat_Open_{ChatId}` is a real per-row button on `PiaAssistantChatRowContent`, on the same hover
  strip as the trash and wired to a row-parameterised `OpenChatCommand`; `ExecuteResumeChatAsync` now delegates
  to it, so the inspector's Resume button and the row share one body. Chosen over an id on the container alone
  because the sweep in `ViewAutomationIdTests` only inspects `ButtonBase` / `ComboBox` / `TextBoxBase` /
  `PasswordBox` / `Slider` / `Expander` / `TabItem` declared inside a `DataTemplate` — a container id cannot
  bump any count — and because one invoke that OPENS the named chat is what a resume check needs: selecting a
  row only loads the inspector.
- Floor bumped to **(2, 2)**, measured rather than guessed: raising it to 99 made the sweep report exactly 2
  interactive controls in `PiaAssistantChatRowContent`, and (2, 2) passing proves both ids are the per-item
  binding form rather than literals.
- Both `ToString()` UIA names are fixed on the item **container**, which is the node UIA actually offers for a
  row: chat rows carry `AssistantChat_Row_{ChatId}` plus the chat title, Routines rows `Routines_Row_{Id}` plus
  the routine name, both through the list's `ItemContainerStyle`. The id sweep cannot see a container, so
  `RowContainerAutomationTests` locks both — and fails on a literal, which would hand every row the same id.
- The playbook's "Known gaps" claim that a `ListBoxItem` can carry no id was corrected: it can, through
  `ItemContainerStyle`; what it cannot do is appear in the sweep.

Gate at the time: **4667 / failed: 0 / 4613 succeeded / 54 skipped**; both configurations rebuilt to
0 Warning(s).

---

## 4. What is still owed

- **F2's UIA verification.** The open button, the two container ids and the two names have never been driven
  through UIA. Fold it into the next desktop session rather than scheduling it alone.

---

## 5. The same defect class is STILL LIVE — three architecture tests run against the un-redirected profile

Re-confirmed 2026-08-25, independently of F1: `Bootstrapper.ConfigureServices` is reflect-invoked by exactly
three tests, none of which redirects `PiaPaths`:

- `tests/Pia.Wpf.Tests/Architecture/AssignmentConsentNotRememberedTests.cs:95`
- `tests/Pia.Wpf.Tests/Architecture/BootstrapperGraphValidationTests.cs:31`
- `tests/Pia.Wpf.Tests/Architecture/DiRegistrationTests.cs:14`

That is why the log-retention sweep landed at the top of `InitializeAsync` and **not** inside the `AddLogging`
lambda (`8e00f8ee`): that delegate runs eagerly, so a sweep there would have deleted the developer's real logs
on every gate run. The retention feature dodged the trap; the trap itself is unchanged. Anything else placed
in an eagerly-run registration delegate that touches a routed path will hit it.

**The rule that follows:** a registration delegate is not a safe place for filesystem work, because the gate
executes it against the real profile. Either do the work in `InitializeAsync`, or give those three tests a
redirect first.

---

## 6. Seeding trap — console-format log lines produce 0-byte zip entries that look like a product bug

Found in an app run while exercising the diagnostics export. Seeding `%LOCALAPPDATA%\Pia\Logs\pia-*.log` with
console-shaped lines (`info: Category[0]` followed by an indented message) yields zip entries of **zero
bytes** — which reads exactly like a broken export.

It is not. `LogRedactor.Redact` starts with `dropping = true` on purpose (a file whose first bytes are the tail
of a dropped payload must not emit that fragment merely because no record has been parsed yet), and
`LogRedactor.IsRecord` accepts a line only when it splits into **5 tab-separated fields** whose first is an ISO
timestamp, whose second is one of `TRCE` / `DBUG` / `INFO` / `WARN` / `FAIL` / `CRIT`, and whose third and
fourth are bracketed. A console-format line matches none of that, so every line is treated as a continuation
under a dropped record and omitted, and the entry comes out empty.

**Anyone seeding logs for the diagnostics feature must use the sink's tab-delimited shape.** A fixture whose
shape the producer cannot emit proves nothing about the producer — the same trap that made a green live run
confirm a parser which dropped every real rolled log file.

---

## 7. Rules for anyone adding a test

1. **Never construct a default-path `SqliteContext`.** Open a throwaway database under the temp directory and
   delete it on dispose. A "known plan-accepted tradeoff" in a `<remarks>` is not a licence.
2. **Never boot the application to get a Dispatcher.** `WpfStaHost` is the seam, and its boot suppression is
   held by `WpfStaHostBootTests` — `Bootstrapper.ServiceProvider` must still throw after the host has run.
3. **If a test needs a redirected profile, take `RedirectedProfileFixture` and join the `PiaPathsStatic`
   collection.** A redirect outside that serialized collection is a race against the nine tests whose premise
   is that no override is set.
4. **A test that creates a directory under a routed root removes it**, non-recursively, and only when it was
   the call that created it.
5. **Prove the premise before asserting the behaviour.** The blocked-root test passes against a root the
   handler merely cannot read, so it asserts `IsBlocked` first.
6. **Compare mtimes as ticks, or hashes as strings** — never a parsed `DateTime` against a string.
