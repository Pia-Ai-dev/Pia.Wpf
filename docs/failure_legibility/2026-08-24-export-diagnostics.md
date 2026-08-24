# Export Diagnostics — a consented, redacted log bundle

**Status:** shipped 2026-08-24. Human smoke test pending (nothing here has been exercised through the
running app). **Owner:** Marco Altmann. **Written:** 2026-08-24.
**Origin:** item #3 of the *not yet planned* table in
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md),
scoped as *Export* rather than *Send* by the owner on 2026-08-24, and built after slice 1 of #2 (the failure
reason on the run card, `3c90aa74`). This doc replaces the handoff prompt that commissioned it; the row it
came from is now a ticked `G1` in that checklist.

---

## 1. The gap this closes

`CLAUDE.md`'s support story already assumes users hand-attach `%LOCALAPPDATA%\Pia\Logs\pia-*.log`. The app
offered **no route to those files at all** — no button, no menu item, no reveal. A user willing to help
diagnose a failure had to be told a path over the phone.

Two facts found while measuring, both worth knowing independently of this feature:

- **`MaxRollingFiles = 7` prunes nothing.** `Bootstrapper.cs` sets it next to the comment *"Keep 7 days"*, but
  `FormatLogFileName` mints a **new base name per day**, so NReco's rolling window never applies. The
  developer profile holds **39 files, 41,530,655 bytes**, 2026-06-28 through 2026-08-24. Retention is out of
  scope here and is **not fixed** — but it is why the export needs a cap rather than "zip the folder".
- **`SafeLog.SensitiveInformation` and `SensitiveWarning` forwarded to `LogInformation`/`LogWarning`.** Both
  are `[Conditional("DEBUG")]`, so their content is debug-build-only — but it landed at **INFO and WARN**,
  where a level-based gate cannot see it. `AdaptiveSpeakerIdentificationService.cs:372` even carries the
  comment *"Labels can carry user-typed names after a rename → DEBUG-only"* directly above a call that
  emitted at Information. Fixed here (§6).

## 2. The design centre: redact on the way out, not at the log site

The obvious plan is to audit the log sites and stop the leaks. **Measured 2026-08-24: 523 call sites pass an
exception object to `LogError`/`LogWarning`/`LogCritical`** (`grep -rEo "Log(Error|Warning|Critical)\(ex"`,
217 `.LogError(` in total), so an exception's `Message` **and stack trace** reach the release log in hundreds
of places. One of them is the exact string slice 1 put on the UI:
`BackgroundAssistantTurnRunner.cs:273` logs `LogError(ex, …)` and then persists the same `ex.Message` as the
run's failure reason.

523 invasive edits is not a feature, and the log is a debugging asset. **So the log stays exactly as written
and the export applies a documented redaction pass.** That decision is the whole shape of this work.

The consequence to accept out loud: the redaction is only as good as its rules, and §3's best-effort tier
will lose to input built to defeat it. That is survivable **because this is Export and not Send** — the zip
is written to the user's own disk, Pia never uploads it, and the user is the last gate before anyone else
sees it. If this ever becomes *Send*, the best-effort tier stops being good enough and this doc has to be
reopened.

## 3. The rule set

`src/Pia.Wpf/Logging/LogRedactor.cs`. Rules run **in the order below, on the message field only** — the
tab-separated `timestamp \t LEVEL \t [Category] \t [EventId]` prefix is preserved byte for byte, which is
what keeps the export a debugging asset rather than a redacted blob.

Two tiers, and they are **code, not prose**: every rule declares a `RedactionTier`, and
`LogRedactorTests.EveryRuleDeclaresATierAndTheIdListMatchesTheDescriptors` holds it.

- **Deterministic** — substitutes a value read from this machine at export time. Exact.
- **Best-effort** — matches a shape. Will lose to a determined adversary; see the paragraph above.

Hit counts are **measured over the real 39-file corpus** (247,884 lines) with the developer's real keys.

| # | Rule | Tier | What it does | Hits |
|---|---|---|---|---|
| R01 | `DEBUG_BODY` | deterministic | Replaces every `DBUG`/`TRCE` **message body** with `<debug-payload-dropped>`. Stateful: continuation lines under a dropped record are **omitted**, and no other rule runs on them. | 130,790 |
| R02 | `RESPONSE_BODY` | best-effort | A provider response body quoted into an exception message (`failed (502): {…}`). | 4 |
| R03 | `PROCESS_AND_WINDOW` | best-effort | `process='…', class='…'`, `(process: …)`, and the window title interpolated into a restore failure. | 97 |
| R04 | `PROFILE_ROOTS` | deterministic | The roaming, local and user profile roots → `<profile-roaming>` / `<profile-local>` / `<profile-user>`. | 619 |
| R05 | `MACHINE_NAME` | deterministic | Machine name **and any DNS suffix following it**. | 0 |
| R06 | `USER_NAME` | deterministic | The account name where it appears outside a profile path. | 0 |
| R07 | `URL` | best-effort | Any `http(s)` URL, **whole** → `<url:{scheme}://host-NNN>`. | 27,914 |
| R08 | `HOST_LITERALS` | deterministic | A configured server or provider host outside a URL → `host-NNN`; **the port survives**. | 2,737 |
| R09 | `PROVIDER_NAMES` | deterministic | User-chosen provider names → `<provider-{index}>`. | 1,254 |
| R10 | `EMAIL` | best-effort | Email addresses. | 0 |
| R11 | `CREDENTIALS` | best-effort | Bearer/basic tokens, `api_key:`-style assignments, JWTs, known key prefixes, credential query parameters. | 0 |
| R12 | `ABSOLUTE_PATH` | best-effort | The **directory part** of any remaining absolute or UNC path; the **leaf survives**. | 12,097 |

### Why the order is what it is

- **R01 first.** It is the only rule that makes "logs only, never transcripts" *true* rather than aspirational:
  every `Sensitive*` helper now emits at Debug or Trace (§6), so one level test kills the entire family and no
  regex has to. It is also 53% of the corpus, and a dropped body skips the other eleven rules.
- **R02 and R03 before the keyed rules.** Both collapse a whole span — a server-controlled JSON blob, a
  two-hole template. Running them after the narrower rules would leave a half-redacted fragment that *reads*
  as if it had been vetted, which is worse than a clean miss.
- **R04 before R05/R06, longest key first.** The profile roots **contain** the user name. Redacting the name
  first turns `C:\Users\ada\AppData\Local\Pia` into `C:\Users\<user>\AppData\Local\Pia` — no profile key
  matches any more, the whole directory structure ships with one segment swapped, and the roaming/local
  distinction is lost. `TheLongestProfileKeyWins_SoTheRootsAreNotReducedToTheUserNameOnly` is that assertion.
- **R07 before R08.** Whole-URL collapse first, so a URL becomes one token; R08 then only has to handle bare
  hosts. **R05/R06 read 0 hits because R04 consumed every occurrence** — that zero is an artefact of this
  profile, not evidence the rules are unnecessary, and the same is true of R10/R11 on a profile that never
  logged an address or a token.
- **R12 last.** It is the catch-all sweep, and it runs on what the keyed rules left behind — including the
  `<profile-*>` chains they produced, so `<profile-user>\Documents\Some Client\x.md` becomes
  `<profile-user>\<path>\x.md`.

### Measured residual

Redacting all 39 real files and scanning **every output line** for the account name, the machine name,
`C:\Users`, or an email-shaped string: **0 hits.** 247,884 lines read → 245,755 written (130,790 debug bodies
replaced, 2,129 continuation lines omitted), 41,530,655 bytes in → 33,342,033 bytes out before compression.

Eyeballing the changed lines found no false positives: framework stack frames, GUIDs, tool names and
approval decisions are untouched, which is what §5 says they should be.

### Not covered, by decision

Named here rather than left implicit, and repeated inside the archive in `README.txt`:

- **Run / chat / step / persona GUIDs.** They are a correlation key against server-side records, and they are
  also the entire point of the run/step scope in the log. Kept.
- **Tool names and approval decisions.** Behavioural metadata from a fixed registry, not content.
- **Log category names** in the preserved prefix. A category is a type name.
- **Arbitrary `ex.Message` prose that names nothing keyed and matches no shape.** Residual by construction —
  this is the 523-site surface, and it is the reason the best-effort tier exists at all.
- **Plugin names.** Not on `CLAUDE.md`'s user-named list, zero measured occurrences, and keying on them would
  have cost the collector another dependency. Reconsider if plugin names start appearing at INFO.
- **A URL's path.** R07 collapses it. That loses `/auth/refresh`, which is diagnostically useful, but a URL
  path can carry a vault slug or a query — and `SafeUrl`'s own release arm already collapses the same way, so
  this is consistent with what a release log does anyway.

## 4. The manifest

Exactly five kinds of entry, asserted as an **exact set** in
`DiagnosticsExportServiceTests.TheArchiveHoldsExactlyTheExpectedEntries_WithEveryDecoyLeftBehind`. That test
seeds `providers.json`, `settings.json`, `history.db`, `history.db-wal`, `Logs.zip`, `pia.log` and
`transcript.md` next to the logs and asserts the archive's entry list **equals** the expected one. A deny-list
assertion ("does not contain `providers.json`") would go vacuous the day a new file type lands in the profile;
this one fails.

| Entry | Source | Redacted? |
|---|---|---|
| `logs/pia-YYYY-MM-DD.log` | copied and transformed line by line, never a byte copy | **yes**, all rules |
| `README.txt` | generated — what is and is not in the archive, both tiers named, the counts | n/a, every byte authored by Pia |
| `manifest.json` | generated from the plan — **every `pia*.log` seen**, included or not, with its reason | n/a, file names only |
| `environment.json` | generated — the allow-listed environment plus every rule with its tier and hit count | n/a, allow-list |

**Never in the archive:** `providers.json` (DPAPI-encrypted keys), `history.db` and its `-wal`/`-shm`, the
vault, transcripts, `settings.json` verbatim.

`environment.json` carries an **allow-list**, field by field, never a settings dump: app version, OS
description, OS/process architecture, framework description, UI language, whether sensitive logging was
compiled in, whether the data directory is overridden, provider **type** counts, and the provider count.
Provider **names** are collected only as redaction keys and never reach the summary — a provider name is
user-chosen text on `CLAUDE.md`'s list. Machine name is excluded outright: it is what R05 removes.

The three generated entries are **not** run through the redactor, so the only thing keeping a path out of them
is their shape. `NoGeneratedEntryCarriesAPathTheRulesWouldHaveRemoved` enforces that: none of the three may
contain a directory separator at all. The corollary, and it is a rule for anyone extending this: **a value
that cannot be produced safely is absent, not explained.** `DiagnosticsExclusionReason` is a closed enum
precisely so an exception message can never become a manifest field.

## 5. Caps

`DiagnosticsExportCaps.Default` = **newest 7 files, 10 MB total source bytes**, both record parameters rather
than constants so the cap test constructs a 900-byte cap over four tiny files instead of writing 10 MB.

Both numbers mirror the sink's own budget: 7 is `MaxRollingFiles` and its *"Keep 7 days"* intent, 10 MB is
`FileSizeLimitBytes`. On the measured profile the byte cap binds first and 5–7 files land, ~1 MB compressed.

Selection is a **contiguous newest-first run**: walk newest to oldest and stop at the first file that would
breach, rather than skipping it and hunting for a smaller older one. *"You have 08-19 through 08-24"* is
something a support engineer can reason about; a set with a hole in it is not. Every file passed over is still
**named in `manifest.json` with its reason**, so an exclusion is visible from inside the archive.

**The cap is a size control, not a privacy control.** Nothing about it reduces exposure, and it must not be
cited as if it did.

The enumeration pattern is `pia*.log`, deliberately wider than `pia-????-??-??.log`. The sink rolls at 10 MB
and a rolled name carries a suffix, so a fixed-width pattern would have dropped a real log file **and** left
it out of the manifest — nothing would have said it existed. A rolled file is now included with its day; a
name with no parseable date (including the sink's own `pia.log` base name) is listed as excluded.

## 6. Decisions, with the recommendation on record

1. **Redact on export, not at the log site.** Taken, for §2's reason.
2. **What goes in the zip.** As §4. Taken.
3. **Cap.** Newest 7 under 10 MB, named in the Settings description. Taken.
4. **Entry point.** Settings → General → Application, between the app-behaviour block and the reset danger
   zone. Not the Flow, not a toast. Taken.
5. **Consent preview.** The **manifest and the cap are shown, a full content preview is not.** The Settings
   description states, always visible, what is collected and what is replaced; the confirmation dialog states
   the real file count (planned before the dialog, so it is a count and not a promise) and the destination.
   A content preview would need its own scrolling, redacted-text viewer — that is a bigger feature, and
   half-building it would be worse than not having it. `README.txt` inside the archive carries the full
   explanation for whoever receives it.
6. **`SensitiveInformation`/`SensitiveWarning` now emit at `LogDebug`.** *This was not in the brief.* It is
   two lines, and it is what makes R01's guarantee true instead of nearly true: without it, 13 call sites
   (9 + 4) put speaker names, consent names and run-workspace paths at INFO/WARN in a debug build, where the
   level gate cannot reach them and no regex can either. **Checked before changing it:** nothing keys on the
   level. The only consumer of any of those lines is `scripts/Measure-SpeakerAttribution.ps1:152`, whose regex
   is `'Adaptive pass labels: \[(.*)\]$'` — message text, level-agnostic, and it already parses a
   `SensitiveDebug` sibling line the same way. Release behaviour is unchanged: both were already compile-erased.
   `SafeLogLevelTests` locks it, by reflection so the assertion runs in Release too.

## 7. Traps this cost, so the next change does not re-pay them

- **The live log file is held open by the sink.** `File.OpenRead`, `File.ReadLines` and
  `ZipFileExtensions.CreateEntryFromFile` all request `FileShare.Read`, which is refused while the writer
  holds the file — so **today's log**, the one a support request needs most, would throw `IOException`. Every
  source is opened `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)`. A temp-file
  test passes either way, so `ALogFileHeldOpenForWriting_IsStillExported` proves the premise first: it asserts
  `File.OpenRead` throws, then asserts the export succeeds.
- **The archive must not be written inside `Logs\`,** or the second export ships the first.
  `PiaPaths.DiagnosticsDirectory` is a **sibling**, `ExportAsync` refuses a target inside the source, and
  `PiaPathsTests` asserts the sibling relationship. The guard compares a trimmed full path plus a separator,
  so a sibling named `Logs-out` is not mistaken for a child.
- **The exporter resolves no path of its own.** Source directory and output path are **parameters**; a thin
  caller in the ViewModel does the `PiaPaths` join. That is what lets every test in both new test classes run
  against a plain temp directory with **no `RedirectedProfileFixture` and no `PiaPathsStatic` collection** —
  and it is why this feature cannot regress the F1/F3 work that took the gate's footprint on the real profile
  to zero.
- **`LogsDirectory` and `DiagnosticsDirectory` are properties**, never `static readonly` or `{ get; } = …`,
  which freeze at type load. Both are rows in
  `PiaPathsTests.RoutedMember_ObservesAnOverrideAppliedAfterItsTypeIsLoaded`.
- **The record predicate needs the level field.** Five tab-separated fields is not enough: a five-column
  tabular line inside a tool result satisfies it by accident, and would then clear R01's drop state and let
  the rest of the payload out. A record requires five fields **and** an ISO-8601 field 0 **and** a known level
  token **and** bracketed fields 2 and 3. It must **not** require `[digits]` in the event-id field — 79,204
  real records carry a named framework event id there (`[RequestStart]`, `[CleanupCycleStart]`).
- **R01's drop state starts closed.** A file whose first bytes are the tail of a dropped payload must emit
  nothing until it has parsed a record; initialising the flag to "not dropping" leaks that fragment.
- **`ViewAutomationIdTests`' per-view number is a floor, not a count** — asserted with `>=` and, in the file's
  own words, *"set well under the measured total so ordinary edits to the view never touch this file"*. Adding
  a button to an already-covered view therefore needs **no `[InlineData]` bump**; the load-bearing assertion
  is the missing-id one. (The bump is required when adding a new *view*.)
- **`Exporter` and `Redactor` are not approved suffixes** in `NamingConventionTests`, and `ResideInNamespace`
  is a **prefix** match, so `Pia.Services.Diagnostics` inherits the suffix rule. Hence
  `DiagnosticsExportService` and `DiagnosticsEnvironmentCollector`; the redactor lives in `Pia.Logging`
  next to `SafeUrl`, which is outside the rule entirely and is the right home anyway.
- **`System.IO.Compression` needs no package**, but the three files the handoff cited as precedent
  (`AssistantChatSyncService`, `E2EEService`, `SyncClientService`) use `GZipStream`/`ZLibStream`, not
  `ZipArchive`. **This is the repo's first zip writer**, so there was no in-repo entry-name or
  `ZipArchiveMode` convention to inherit.
- **Scripted resx edits keep breaking line endings.** All six files (`ViewStrings` × 3, `MessageStrings` × 3)
  were written through a helper that appends before `</root>` and then **verifies byte-wise** that every LF is
  preceded by CR, that no bare CR or NUL exists, and that there is no BOM — it throws rather than write a
  damaged file. `grep -c $'\r'` is not reliable for this; count bytes with node.

## 8. What this deliberately does not do

- **No retention fix.** §1's 39-file, 40 MB pile-up is real and untouched. The cap keeps the export bounded;
  it does not keep the log directory bounded.
- **No upload, no server, no "Send".** Export only. See §2 for why that is load-bearing rather than a
  first increment.
- **No content preview** in the consent dialog. Decision 5.
- **No policy gate.** An enterprise policy cannot currently forbid a diagnostics export. Nobody asked for one;
  it would be a `PolicyLock` row plus a key.
- **No `#2` slice 2** — a named failure layer (`PiaFailure(Layer, Code, Retryable)`), recovery actions, Retry.
  Still open, and the warning that came with it still stands: both existing resume claims do
  `SET ExtraJson = NULL`, and they only fire from `WaitingForInput`/`Paused`, so nothing wipes the failure
  reason today — but a Retry adds a **new `Failed → Running`** transition, and written in the shape of its
  siblings it would null the column slice 1 reads. Either the retry claim leaves `ExtraJson` alone, or the
  reason gets its own column first.
- **No `WriteResult` seam fix.** `ChatSession.cs:1160-1167` logs *"Executed {ToolName} action successfully"*
  and raises `ToolSucceeded` unconditionally after `Execute()` returns, so a `write_file` that returned
  `WriteResult.Failed` still shows a **green success snackbar** (`AssistantViewModel.cs:554`,
  `ControlAppearance.Success`). Untouched here. See
  [`../hermes_checkup/2026-08-24-p9-refused-write-reading.md`](../hermes_checkup/2026-08-24-p9-refused-write-reading.md) §5.
- **One known correctness wart, accepted:** `OutputService.cs:110` interpolates the window title into an
  exception message that is logged at WARN, two lines after wrapping the same value in `SensitiveDebug`. R03
  redacts it on export. **Fixing the source would be better** — it is one line and the author's intent is
  already documented by the adjacent `SensitiveDebug` — but doing it here would contradict §2's "do not try to
  stop the logging", so it is written down instead of done.

## 9. Gate

Debug and Release both **rebuild to `0 Warning(s)` / `0 Error(s)`** (`-t:Rebuild`, and
`TreatWarningsAsErrors` is on). `dotnet test` with no filter: **4907 total, failed: 0**, 1 skipped, 58 Not Run
— from a 4841 baseline at `da95cc8b`, so **+66 tests**.

Real-profile footprint, snapshot → work → compare over 11 mtimes and hashes: `history.db`, `history.db-wal`,
`settings.json` and `providers.json` **byte-identical**; `%LOCALAPPDATA%\Pia`, `\runs`, `\workdir`, `\Logs`
and `%APPDATA%\Pia` mtimes **unmoved**; no `Diagnostics` directory created. One exception, reported rather
than smoothed over: **`history.db-shm` changed once** during the session and then stayed byte-identical
across a repeat full gate run. `-shm` is SQLite's scratch index and holds no durable data, nothing in this
change constructs a database connection, and the collector is never constructed by any test — so this reads
as a one-off convergence of a pre-existing behaviour rather than something the feature introduced. Worth a
row if it recurs.
