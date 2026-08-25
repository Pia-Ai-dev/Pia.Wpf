# Failure legibility — the redacted log export, and the named failure layer

**Status:** shipped and exercised in the running app. `G1` (export) shipped 2026-08-24 and was driven
through the real UI the same day; `G2`, `G3` and `G4` (the failure layer) landed 2026-08-24; gate `G-Q1`
was **answered 2026-08-25** and `G5` (Retry) is **withdrawn as specified** — §15. Retention and the shared
name parser landed 2026-08-25. **Owner:** Marco Altmann.
**Written:** 2026-08-24. Rewritten 2026-08-25 as the track's single doc, from the code rather than from the
four reports it replaces.
**Origin:** recommendations **#2** and **#3** of
[`../hermes_checkup/2026-08-22-hermes-update-review.md`](../hermes_checkup/2026-08-22-hermes-update-review.md).
#3 was scoped as *Export* rather than *Send* by the owner on 2026-08-24 and shipped as `G1`; #2 slice 1 (the
failure reason on the run card) shipped as `3c90aa74`, and slice 2 is `G2`–`G5`. Rows and ticks live in
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md);
this track has no tracking file of its own.

The two halves are one document because they are one dependency chain: `G4`'s recovery action for an `App` or
`Workspace` failure **is** the `G1` export.

Executable cold. Everything needed is here.

---

## Contents

- **Part I — the export** (§1–§9): the redaction design centre, the rule set, the manifest, the caps, the
  retention sweep, and the traps each of them cost.
- **Part II — the failure layer** (§10–§16): the descriptor, where it is built, what the card shows, and why
  a Retry gated on it cannot be built today.
- **Part III — how it was verified, and what that verification could not see** (§17): the fixture trap first,
  then the mutation sweep, the four defects the app found, and the checks that are structurally blind.
- **Part IV — open, and deliberately not done** (§18–§19).
- **Appendix A** — the throwaway-profile recipe, verbatim.

---

# Part I — the export

## 1. The gap this closes

`CLAUDE.md`'s support story already assumes users hand-attach `%LOCALAPPDATA%\Pia\Logs\pia-*.log`. The app
offered **no route to those files at all** — no button, no menu item, no reveal. A user willing to help
diagnose a failure had to be told a path over the phone.

Two facts found while measuring, both worth knowing independently of this feature:

- **`MaxRollingFiles = 7` prunes nothing.** `Bootstrapper.cs` sets it next to the comment *"Keep 7 days"*, but
  `FormatLogFileName` mints a **new base name per day**, so NReco's rolling window never applies. The
  developer profile held **39 files, 41,530,655 bytes**, 2026-06-28 through 2026-08-24. Fixed here:
  `LogFileRetention` sweeps every name outside a 30-day window at startup (§8) — and it is also why the export
  needs a cap rather than "zip the folder".
- **`SafeLog.SensitiveInformation` and `SensitiveWarning` forwarded to `LogInformation`/`LogWarning`.** Both
  are `[Conditional("DEBUG")]`, so their content is debug-build-only — but it landed at **INFO and WARN**,
  where a level-based gate cannot see it. `AdaptiveSpeakerIdentificationService.cs:372` even carries the
  comment *"Labels can carry user-typed names after a rename → DEBUG-only"* directly above a call that
  emitted at Information. Fixed here (§7 decision 6); both now call `LogDebug`
  (`src/Pia.Wpf/Logging/SafeLog.cs:36`, `:40`).

## 2. The design centre: redact on the way out, not at the log site

The obvious plan is to audit the log sites and stop the leaks. **Measured 2026-08-24: 523 call sites pass an
exception object to `LogError`/`LogWarning`/`LogCritical`** (`grep -rEo "Log(Error|Warning|Critical)\(ex"`,
217 `.LogError(` in total), so an exception's `Message` **and stack trace** reach the release log in hundreds
of places. One of them is the exact string slice 1 put on the UI: `BackgroundAssistantTurnRunner` logs
`LogError(ex, …)` and then persists the same `ex.Message` as the run's failure reason.

523 invasive edits is not a feature, and the log is a debugging asset. **So the log stays exactly as written
and the export applies a documented redaction pass.** That decision is the whole shape of this work.

The consequence to accept out loud: the redaction is only as good as its rules, and §3's best-effort tier
will lose to input built to defeat it. That is survivable **because this is Export and not Send** — the zip
is written to the user's own disk, Pia never uploads it, and the user is the last gate before anyone else
sees it. If this ever becomes *Send*, the best-effort tier stops being good enough and this section has to be
reopened.

## 3. The rule set

`src/Pia.Wpf/Logging/LogRedactor.cs`. Rules run **in the order below, on the message field only** — the
tab-separated `timestamp \t LEVEL \t [Category] \t [EventId]` prefix is preserved byte for byte, which is
what keeps the export a debugging asset rather than a redacted blob.

**Twelve ordered rules, split 6 Deterministic / 6 Best-effort.** The two tiers are **code, not prose**: every
rule declares a `RedactionTier`, and
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
  every `Sensitive*` helper now emits at Debug or Trace (§7), so one level test kills the entire family and no
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
  logged an address or a token. §17c says what that costs the residual scan.
- **R12 last.** It is the catch-all sweep, and it runs on what the keyed rules left behind — including the
  `<profile-*>` chains they produced, so `<profile-user>\Documents\Some Client\x.md` becomes
  `<profile-user>\<path>\x.md`.
- **A rule keyed on user text must not run inside a token an earlier rule emitted.** The order reasoning
  above is about keys containing each other; this is the same hazard one level up, and only the live run
  found it — §17f is the full account. R05, R06, R08 and R09 now run **outside** the emitted tokens
  (`OutsideEmittedTokens` in `LogRedactor.cs`); R05's DNS-suffix pass and R12's tokenised-path pass
  deliberately do **not**, because both read the previous rule's output on purpose, and wrapping either would
  re-break what the guard exists to protect. The code says so on the line: *"The suffix pass stays unguarded:
  it anchors on the `<machine>` token just emitted."*

### Measured residual

Redacting all 39 real files and scanning **every output line** for the account name, the machine name,
`C:\Users`, or an email-shaped string: **0 hits.** 247,884 lines read → 245,755 written (130,790 debug bodies
replaced, 2,129 continuation lines omitted), 41,530,655 bytes in → 33,342,033 bytes out before compression.

**Be precise about what that zero proves, because it is partly circular.** The scan looked for the account
name and the machine name — the same two values R05 and R06 key on — and those rules measured 0 hits because
R04 had already consumed every occurrence. So on this corpus the zero is evidence about **R04, R07 and R12**,
the rules that actually fired, and it is *not* corpus evidence about R05, R06, R10 or R11. Those four are
covered by unit tests only. A profile with a corporate machine name in a UNC path, or one that had logged an
address or a token, would exercise them and has not been measured.

Eyeballing the changed lines found no false positives: framework stack frames, GUIDs, tool names and
approval decisions are untouched, which is what §4 says they should be.

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

Exactly four kinds of entry, asserted as an **exact set** in
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
contain a **backslash** — `Path.DirectorySeparatorChar`, which is what that assertion actually reads. Forward
slashes are not forbidden and do occur in prose (`logs/`, `DBUG/TRCE`), so a `\\|/` check over a real archive
reports two false alarms; the UI run confirmed **zero** backslashes. The corollary, and it is a rule for
anyone extending this: **a value that cannot be produced safely is absent, not explained.**
`DiagnosticsExclusionReason` is a closed enum precisely so an exception message can never become a manifest
field — and it is serialized **by name**, because the UI run shipped an archive whose manifest read
`"ExclusionReason": 0` and no reader could tell that from a default (§17e, defect 1).

## 5. Caps

`DiagnosticsExportCaps.Default` = **newest 7 files, 10 MB total source bytes**, both record parameters rather
than constants so the cap test constructs a 900-byte cap over four tiny files instead of writing 10 MB.

Both numbers mirror the sink's own budget: 7 is `MaxRollingFiles` and its *"Keep 7 days"* intent, 10 MB is
`FileSizeLimitBytes`. On the measured profile the byte cap binds first and 5–7 files land, ~1 MB compressed.

Only the newest 7 files are ever reachable, so **the byte cap binds only when those seven are big** — on a
seed of 20 ordinary files it never fires. Once the byte cap has stopped the walk, **every file after it also
carries `OverTotalByteCap`** — naming the file count there would tell a support engineer that raising it
changes something, when the run had slots to spare. (That labelling is a fix; the shipped-then-corrected
behaviour rides along with defect 1 in §17e.)

Selection is a **contiguous newest-first run**: walk newest to oldest and stop at the first file that would
breach, rather than skipping it and hunting for a smaller older one. *"You have 08-19 through 08-24"* is
something a support engineer can reason about; a set with a hole in it is not. Every file passed over is still
**named in `manifest.json` with its reason**, so an exclusion is visible from inside the archive.

**The cap is a size control, not a privacy control.** Nothing about it reduces exposure, and it must not be
cited as if it did.

`DiagnosticsExclusionReason` has **four** members — `OverFileCountCap`, `OverTotalByteCap`,
`UnrecognisedName`, `OpenFailed`. Three of them were observed at runtime (§17e); `OpenFailed` is set during
the copy, at `DiagnosticsExportService.cs:250`, when a file that planned as included cannot be read, and no
run has produced one.

### Name parsing, and the two things that were wrong about it

The enumeration pattern is `pia*.log`, deliberately wider than `pia-????-??-??.log`. The sink appends the roll
index with **no separator** — measured against NReco.Logging.File 1.3.1 with this app's `FormatLogFileName`,
`pia-2026-08-24.log` rolls to `pia-2026-08-241.log` — so a fixed-width pattern would have dropped a real log
file **and** left it out of the manifest, and nothing would have said it existed.

The first cut widened the *pattern* and not the *parser*: `DateOf` required a `-` at index 10 of the stamp, so
the sink's real bare-digit roll was enumerated by `pia*.log` and then excluded as `UnrecognisedName`. Both
forms are accepted now, and the parsing is **shared with the retention sweep** —
`LogFileRetention.SliceOf(nameWithoutExtension)` returns `(DateOnly Date, int Roll)?` and is called from
`DiagnosticsExportService.cs:51` and from `Sweep` through `DateOf`, so the export and the sweep cannot
disagree about which files are ours. A name with no parseable date (including the sink's own `pia.log` base
name) is listed as excluded.

Within one day the newest-first order is by **write time**, not by name and not by roll index. The default
`RollingFilesConvention` is `Ascending`, which **wraps** (`0-1-2-3-0-1-2-3`), so the un-indexed base file can
hold the newest content and the highest index can be the oldest slice. **Write time is the only key that is
right under every convention**; the roll index survives only as a tiebreak, and the shipped sort says so —
`OrderByDescending(Slice.Date)`, then `ThenByDescending(File.LastWriteTimeUtc)`, then
`ThenByDescending(Slice.Roll)`, then `ThenBy(File.Name, Ordinal)`.
`TheNewestSliceOfADayWins_ByWriteTimeNotByName` is the assertion.

## 6. What the user is shown before consenting

Two surfaces, and both are permanent copy rather than a preview.

**The Settings description**, always visible, above the button:

> Writes a redacted copy of Pia's own log files to a zip you can attach to a support request. Logs
> only — never chats, vault content, your history database or your provider credentials. File paths,
> your account and machine name, host names, e-mail addresses and tokens are replaced before anything
> is written, and every debug message body is dropped whole. The newest 7 log files up to 10 MB are
> included. Nothing is sent anywhere: the zip is written to your disk and you decide who sees it.

**The confirmation dialog**, built in `GeneralSettingsViewModel.ExportDiagnosticsAsync` from a plan computed
**before** the dialog opens, so every number is a count and not a promise. It is up to three sentences:

1. `Settings_ExportDiagnostics_Confirm_Message` — the included count and the destination.
2. `Settings_ExportDiagnostics_Confirm_ExcludedByCap`, only when the caps actually excluded something:
   *"This is not your whole log history: an export stops at {1} log files or {2} MB, whichever comes first, so
   {0} file(s) are left out. Mention it in your support request if you need those too."* The two cap values
   are read off `DiagnosticsExportCaps.Default`, so the sentence cannot drift from the cap it describes.
3. `Settings_ExportDiagnostics_Confirm_Excluded`, only when something was excluded for a **non-cap** reason:
   *"A further {0} file(s) in that folder are not recognised as Pia logs; the archive lists them in its
   manifest."*

The split is deliberate and is the reason the ViewModel counts by kind rather than reading `plan.ExcludedCount`
whole: *"a name with no date is left out at any cap, so folding it into the cap sentence blames the cap for an
exclusion it did not cause."* Note that `DiagnosticsExportPlan.CapApplied` exists and is asserted in the
service tests, but the **dialog does not read it** — it derives the same fact from the per-file reasons,
because it needs the count as well as the boolean.

## 7. Decisions, with the recommendation on record

1. **Redact on export, not at the log site.** Taken, for §2's reason.
2. **What goes in the zip.** As §4. Taken.
3. **Cap.** Newest 7 under 10 MB, named in the Settings description. Taken.
4. **Entry point.** Settings → General → Application, between the app-behaviour block and the reset danger
   zone. Not the Flow, not a toast. Taken.
5. **Consent preview: the cap is shown, the manifest is not, and a content preview is not.** The Settings
   description states, always visible, what is collected and what is replaced; the confirmation dialog states
   the real included count, the destination, both cap values, and how many files each kind of exclusion cost
   (§6). What it does **not** show is the manifest itself — that is `manifest.json`, inside the archive, read
   after the fact. A content preview would need its own scrolling, redacted-text viewer — that is a bigger
   feature, and half-building it would be worse than not having it. `README.txt` inside the archive carries
   the full explanation for whoever receives it.
6. **`SensitiveInformation`/`SensitiveWarning` now emit at `LogDebug`.** *This was not in the brief.* It is
   two lines, and it is what makes R01's guarantee true instead of nearly true: without it, 13 call sites
   (9 + 4) put speaker names, consent names and run-workspace paths at INFO/WARN in a debug build, where the
   level gate cannot reach them and no regex can either. **Checked before changing it:** nothing keys on the
   level. The only consumer of any of those lines is `scripts/Measure-SpeakerAttribution.ps1:152`, whose regex
   matches on message text, is level-agnostic, and already parses a `SensitiveDebug` sibling line the same
   way. Release behaviour is unchanged: both were already compile-erased. `SafeLogLevelTests` locks it, by
   reflection so the assertion runs in Release too.

## 8. Retention, and the reason it exists

This was written down as *deliberately left out* when the export shipped. It is **shipped** now, and the
reason is §1's first bullet: `MaxRollingFiles = 7` bounds **one day's rolls** and nothing else, because
`FormatLogFileName` mints a new base name every day. The dev profile had grown to 39 files / 40 MB.

- `LogFileRetention.Sweep(logDirectory, retainedDays)`, **30-day default** (`DefaultRetainedDays`), taking the
  directory as a parameter so no test can point it at the real profile.
- **Age comes from the date in the NAME, not from `mtime`** — the export *copies* files, so mtime lies. The
  doc comment on the type says exactly that.
- The window is measured on the **local** clock, because the sink stamps names from the local clock.
- A name dated **after** today is left alone rather than deleted: *"left alone it never ages out, and it
  outranks the live slice in an export."* A name that does not parse is `Kept`, not deleted — `Skipped` in
  `LogFileRetentionOutcome` means only "could not delete".
- **Hoisted out of the `AddLogging` lambda.** It runs at the top of `Bootstrapper.InitializeAsync`
  (`Bootstrapper.cs:84`) and the comment there is load-bearing: *"that delegate runs eagerly, and three
  architecture tests reflect-invoke `ConfigureServices` against the real, un-redirected profile."* Those three
  are `AssignmentConsentNotRememberedTests`, `BootstrapperGraphValidationTests` and `DiRegistrationTests`. Put
  the sweep back inside the lambda and the gate starts deleting the developer's own logs.
- `DiagnosticsExportCaps`' doc comment was rewritten with it, from "one file per day / 40 MB read" to *"The
  sink writes up to 7 rolls a day and retention keeps a month of them, so without a cap the export would be a
  multi-gigabyte read."*

**There is still no floor under retention.** A plain 30-day window means a user who relaunches after a month
has only the new session left to export. Flooring it at the export's own file cap — never delete the newest 7,
whatever their age — would close that, and is not done.

## 9. Traps this cost, so the next change does not re-pay them

- **The live log file is held open by the sink.** `File.OpenRead`, `File.ReadLines` and
  `ZipFileExtensions.CreateEntryFromFile` all request `FileShare.Read`, which is refused while the writer
  holds the file — so **today's log**, the one a support request needs most, would throw `IOException`. Every
  source is opened `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)`. **A temp-file
  test passes either way**, so `ALogFileHeldOpenForWriting_IsStillExported` proves the premise first: it
  asserts `File.OpenRead` throws, then asserts the export succeeds.
- **`FileMode.CreateNew` is the *only* collision guard, on purpose.** The first cut also had a
  `File.Exists` pre-check, and that pre-check made `CreateNew` unreachable — so the atomic guarantee, and the
  same-second race it closes (`BuildFileName` is unique to the second, not finer), had **no test at all**.
  §17d is what found it. The pre-check is gone; `CreateNew`'s `IOException` maps to `OutputAlreadyExists`
  under a `when (File.Exists(...))` filter, which also keeps the generic cleanup arm from deleting a file this
  export did not write.
- **The archive must not be written inside `Logs\`,** or the second export ships the first.
  `PiaPaths.DiagnosticsDirectory` is a **sibling**, `ExportAsync` refuses a target inside the source, and
  `PiaPathsTests` asserts the sibling relationship. The guard compares a trimmed full path plus a separator,
  so a sibling named `Logs-out` is not mistaken for a child.
- **The exporter resolves no path of its own.** Source directory and output path are **parameters**; a thin
  caller in the ViewModel does the `PiaPaths` join. That is what lets every test in both new test classes run
  against a plain temp directory with **no `RedirectedProfileFixture` and no `PiaPathsStatic` collection** —
  and it is why this feature cannot regress the work that took the gate's footprint on the real profile to
  zero.
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
- **`System.IO.Compression` needs no package**, but the three files cited as precedent
  (`AssistantChatSyncService`, `E2EEService`, `SyncClientService`) use `GZipStream`/`ZLibStream`, not
  `ZipArchive`. **This is the repo's first zip writer**, so there was no in-repo entry-name or
  `ZipArchiveMode` convention to inherit.
- **Scripted resx edits keep breaking line endings.** All six files (`ViewStrings` × 3, `MessageStrings` × 3)
  were written through a helper that appends before `</root>` and then **verifies byte-wise** that every LF is
  preceded by CR, that no bare CR or NUL exists, and that there is no BOM — it throws rather than write a
  damaged file. `grep -c $'\r'` is not reliable for this; count bytes with node.

---

# Part II — the failure layer

## 10. Where this started from

Three pieces already existed, and `G2` connected them rather than replacing any of them.

**`AgentRunService.FailAsync` records a reason on every failure.** It serialises `{"error": …}` into
`AgentRuns.ExtraJson`. Slice 1 taught `RunProgressViewModel` to read it, so a failed run's card says *why*.

**Slice 1's vocabulary is deliberately OPEN**, and that is the single most important thing not to break.
`DescribeFailureReason` localizes the app-owned constants and lets **everything else through unchanged**,
because an `ex.Message` or the model's own summary is the *informative* case, not a fallback. A closed enum
is the opposite shape.

**`ScheduledJobService.IsPreModelFailure` was already the retry verdict**, for exactly one value
(`ScheduledJobService.cs:486`), and its doc comment stated the gap plus the rule for closing it:

> **KNOWN GAP, accepted:** `IHeadlessRunLauncher.LaunchAsync` can also fail genuinely pre-model (its own
> provider resolve, the stub-chat save, workspace setup), and that arrives here as a bare message, so such a
> one-off still dies on the first strike. **Widening needs a reason value the CALLER can vouch for — never a
> substring match on provider error text.**

That sentence is the acceptance criterion for the whole mapper: **key on exception type, never on message
text.**

## 11. The two decisions that shape everything

### 11.1 The descriptor is ADDITIVE. It does not replace the string.

`PiaFailure` travels **alongside** the existing free-text reason, never instead of it, and its own doc comment
says so: *"Travels ALONGSIDE the free-text failure reason, never instead of it — an unmapped message must
still reach the card unchanged."* `FailAsync` keeps writing `{"error": …}` exactly as before. A test pins that
an unmapped `ex.Message` still reaches the card unchanged; that assertion is the guard on this whole decision.

**`Unclassified` is a first-class outcome, not a hole.** A run whose exception maps to nothing still shows its
`ex.Message` through slice 1's open arm — the descriptor adds a layer name when it can and stays quiet when it
cannot. One design call made while building: an unrecognised exception still **persists** a descriptor
(`Unclassified`) rather than leaving the column null. It renders identically, and it buys the difference
between "this build classified it and had no arm" and "written before the column existed".

### 11.2 `SafeToReRun` means Pia's question, not hermes's

hermes's `error_surface.py` bool asks *"could the API call succeed if repeated?"*. `IsPreModelFailure` asks
*"can we prove this run spent nothing and wrote nothing?"*. **They are not the same question**, and treating
them as one ships a duplicate-write bug: a provider 503 on step 7 is transient by hermes's meaning and
emphatically unsafe to re-dispatch by Pia's, because a step may already have written to the vault.

**Pia's meaning won, and the member is named so the confusion cannot recur** — `SafeToReRun`, not `Retryable`.
The string `Retryable` appears nowhere in `src/`. The rename was deliberate; the origin review still calls it
`Retryable` and is wrong on that one word.

## 12. The shipped shape

`src/Pia.Wpf/Models/PiaFailure.cs`:

```csharp
namespace Pia.Models;                       // NOT Pia.Services - see §16

public enum FailureLayer { Unclassified, App, Workspace, Provider, Endpoint, Tool, Cancelled }

public sealed record PiaFailure(FailureLayer Layer, string Code, bool SafeToReRun);
```

Three things about that enum are worth stating, because the plan-time sketch got all three wrong:

- **`Unclassified` is a member, and it is first.** It is not a null. `FromJson` normalises an *undefined*
  numeric layer to it rather than letting an out-of-range enum value leak to every caller that switches.
- **There is no `Policy` layer.** It was in the plan and was never built; nothing has a policy failure to
  report.
- **`App` is declared and rendered but never produced.** No arm of `FailureMapper` returns it, yet
  `DescribeFailureLayer` and `DescribeFailureAction` both handle it and it maps to the diagnostics action. It
  is a reserved slot, not dead code — say so before "cleaning it up".

`Code` is a `string`, not an enum: it is the stable machine token, and the app-owned constants slice 1 already
localizes **are** those tokens. **14 distinct codes ship**: `Undetailed`, `EmptyResponse`, `WorkspaceSetup`,
`Interrupted`, `Superseded`, `NoProvider`, `Timeout`, `Truncated`, `BrowserLaunch`, `Transport`, `Cancelled`,
`AccessDenied`, `Io`, `Unclassified`.

**The codec lives on the type.** `PiaFailure.ToJson`/`FromJson` own the `JsonSerializerOptions` (camelCase
plus `JsonStringEnumConverter`) so a writer and a reader cannot disagree about casing. They already did once —
§17g.

### Where descriptors are built

`FailureMapper` (`src/Pia.Wpf/Services/FailureMapper.cs`) has two entry points because there are two kinds of
caller: one that already knows which failure this is, and one holding an exception. It constructs **15**
descriptors — 6 `ForReason` arms, 8 `Classify` arms, and the `Unclassified` fallback. **Two carry `true`.**

`ForReason`, matched by value on an app-owned constant — **6 arms**, not the "seven named constants" the plan
predicted:

| Constant | Layer / Code | `SafeToReRun` |
|---|---|---|
| `AgentStepTools.UndetailedFailure` | Tool / Undetailed | false |
| `AgentStepTools.EmptyResponseFailure` | Provider / EmptyResponse | false |
| `HeadlessRunLauncher.WorkspaceSetupFailure` | Workspace / WorkspaceSetup | false |
| `HeadlessRunLauncher.ShutdownInterruptedFailure` | Cancelled / Interrupted | false |
| `AgentRunOrchestrator.SupersededFailureReason` | Cancelled / Superseded | false |
| `ScheduledJobService.NoProviderFailureReason` | Provider / NoProvider | **true** |

`ForException`, matched on exception type through the unwrapped inner chain — **8 arms plus the fallback**:

| Type | Layer / Code | `SafeToReRun` |
|---|---|---|
| `PreModelLaunchException` | Provider / NoProvider | **true** |
| `LlmTimeoutException` | Provider / Timeout | false |
| `LlmTruncatedException` | Provider / Truncated | false |
| `BrowserLaunchException` | Tool / BrowserLaunch | false |
| `HttpRequestException` | Endpoint / Transport | false |
| `TaskCanceledException` / `OperationCanceledException` | Cancelled / Cancelled | false |
| `UnauthorizedAccessException` | Workspace / AccessDenied | false |
| `IOException` | Workspace / Io | false |
| *nothing matched* | Unclassified / Unclassified | false |

The pair is pinned by `FailureMapperTests.OnlyTheProviderResolveFailure_IsSafeToReRun`, which asserts all
fifteen — both `true` arms and every one of the thirteen `false` ones, the `Unclassified` fallback included.
The "only" in its name is therefore enforced: flipping any arm to `true` turns it red.

### Where the mapper is called

**The mapper sits at the `catch`, not in `AgentRunService`.** By the time `SafeFail(runId, ex.Message, …)`
runs, the exception is gone and only text remains — and text is exactly what the repo's own comment forbids
keying on. `IAgentRunService.FailAsync` gained an optional `PiaFailure?` parameter and each `catch` passes one.

**`ForException` has 6 call sites**, not the "four catch sites" the plan predicted:

| Site | Table it feeds |
|---|---|
| `AgentRunOrchestrator.cs:585` | `AgentRuns` |
| `BackgroundAssistantTurnRunner.cs:285` | `AgentRuns` |
| `HeadlessRunLauncher.cs:540` | `AgentRuns` |
| `HeadlessRunLauncher.cs:967` | `AgentRuns` |
| `ScheduledJobBackgroundService.cs:531` | scheduled-job runs |
| `ScheduledJobBackgroundService.cs:745` | scheduled-job runs |

`ForReason` is called from seven places — `AgentRunOrchestrator.cs:1512` and `:1899`,
`BackgroundAssistantTurnRunner.cs:197`, `HeadlessRunLauncher.cs:428` and `:524`,
`ScheduledJobBackgroundService.cs:494` and `:679`. **They are two tables with two different writers**, which
is why `G2` persists the descriptor on the agent-run side while `G3` is where the scheduled-job side consumes
one.

`AgentRunOrchestrator.SafeFail` is the fallback seam: `failure ??= FailureMapper.ForReason(error)` at `:1899`,
with the comment *"A caller holding the exception hands the descriptor over; everyone else passes a named
constant, which is the only thing `ForReason` recognises."* Keep that sentence in mind for §15's caveat.

## 13. Storage: its own column

The discriminating question is *does this value have to survive a transition that runs `ExtraJson = NULL`?*
It does — a Retry is exactly that transition, and knowing what failed is the point of offering one.
`AgentRun.ClarificationsJson` faced the same question and the answer is on the field:

> Its own column rather than part of `ExtraJson` because both resume claims `SET ExtraJson=NULL`, which
> would destroy an answer kept there.

So: **`AgentRuns.FailureJson TEXT NULL`**, added the way this repo adds columns — a `PRAGMA table_info`
existence check then an `ALTER TABLE` in `SqliteContext.MigrateSchema`. It is the **only** such column in the
schema. Written by `AgentRunService.FailAsync` (`:324`), read by `RunProgressViewModel.ReadFailureLayer`
(`:1269`), whose own doc comment carries the reason: *"Its own column, not `ExtraJson`: a Retry would be a
`Failed → Running` claim, and every existing claim nulls that column."*

**The `ExtraJson = NULL` trap, for whoever picks up a Retry.** `TryBeginResumeAsync`
(`AgentRunService.cs:443`) and `TryResumeFromPauseAsync` (`:544`) both `SET ExtraJson = NULL`, and are safe
only because they fire from `WaitingForInput`/`Paused` where there is no failure reason to lose. **A
`Failed → Running` transition written in their shape would wipe the reason slice 1 reads.** `FailureJson`
survives it; `{"error": …}` does not. Any future retry inherits this, and no existing test would catch it,
because no such transition exists yet to test.

## 14. What shipped on the card, and in the scheduled-job retry

**`G3` — `IsPreModelFailure` widened.** `ScheduledJobService.MarkRunFailedAsync:359` now reads
`var preModel = failure is { SafeToReRun: true } || IsPreModelFailure(reason);` — the string comparison stays
as a floor, and the descriptor is the widening. A `HeadlessRunLauncher` failure that provably happened before
the model was called stops dying on the first strike, decided by a value the caller vouched for rather than by
one string comparison. **`G3` is covered by tests only and has never been exercised live** — nobody has
watched a one-off scheduled job survive a pre-model launch failure in the app.

**`G4` — layer name and recovery action on the failure card.** `RunProgressViewModel` exposes
`FailureLayerName` and `FailureActionLabel`; `RunProgressPanel.xaml:612` and `:616` carry
`AutomationProperties.AutomationId="Run_FailureLayer"` and `"Run_FailureAction"`, both bound through
`NullToVisibilityConverter` so an unclassified failure renders byte-identically to before.

- `DescribeFailureLayer` names `App`, `Workspace`, `Provider`, `Endpoint` and `Tool`. `Cancelled` and
  `Unclassified` deliberately fall to `null` — and `ReadFailureLayer` already returns null for `Unclassified`.
- `DescribeFailureAction` offers an action for **two pairs only**, with the comment *"Only the two layers a
  person can actually act on get an action; the rest stay quiet"*: `Provider`/`Endpoint` → open providers,
  `App`/`Workspace` → *Export diagnostics*.
- **`RunFailureAction` navigates rather than acting.** Its doc comment is the design call: *"the diagnostics
  export owns its own consent dialog and snackbar, and re-raising them from here would be a second copy of a
  flow that already exists."* `Provider`/`Endpoint` goes to
  `NavigateTo<SettingsViewModel, int>((int)SettingsTab.Providers)`; `App`/`Workspace` goes to the tuple
  overload `((int)SettingsTab.General, (int)GeneralSettingsInnerTab.Application)`, which lands on the very
  button Part I built.
- **`RunProgressViewModel` gates the reason on the Failed FAMILY**, which `MapState` folds `Cancelled` into.
  A run cancelled because a *child* failed carries the child's reason today; the layer line inherits that
  gating.

## 15. Decision gate G-Q1 — ANSWERED 2026-08-25, and `G5` is withdrawn

**Closes:** `G5`. **Question:** does Retry re-dispatch the whole run from its goal, or resume from the failed
step?

**Answer: resume from the failed step — and it is not buildable today.** Re-dispatch is dead on arrival: a
Retry gated on `SafeToReRun` can never enable, because both descriptors carrying `true` are produced where no
failure card exists. Resume-from-step is the only shape that does not duplicate writes, and it needs a step
ledger that a failed run does not leave behind. **`G5` as specified is withdrawn**, and the prerequisite list
below replaces it.

### Neither `true` can reach a card

The card has one data path: `AgentRuns.FailureJson`, written only by `AgentRunService.FailAsync` (`:324`) and
read only by `RunProgressViewModel.ReadFailureLayer` (`:1269`).

- **The string arm never goes near it.** Both raisers (`ScheduledJobBackgroundService.cs:494`, `:679`) hand
  the descriptor to `MarkRunFailedAsync`, which uses it once — at `ScheduledJobService.cs:359`, which *is*
  `G3` — and persists it nowhere. A scheduled job has no descriptor column and renders no failure card.
- **The exception arm fires before the row exists.** `PreModelLaunchException` is thrown at exactly one
  place, `HeadlessRunLauncher.cs:323`, on the `?? throw` of the provider ladder — ahead of the stub chat
  (`:328`) and of `_agentRunService.CreateAsync` (`:368`). The comment two lines above it is the vouching:
  *"nothing is written until the stub chat below, so this is the launcher vouching for 'nothing spent, nothing
  written'."* It escapes to `ScheduledJobBackgroundService.cs:531` (into `MarkRunFailedAsync`, above) and to
  `ChatSessionManager.cs:1415`, which propagates to its awaiting caller. Neither has an `AgentRuns` row.
- **No `FailAsync` site sees it second-hand.** `HeadlessRunLauncher.cs:540` and `:967` are gated on
  `started`, hence past `:323` in the same dispatch. `AgentRunOrchestrator.cs:585` sees planner and step
  faults only — the one in-run launch (`LaunchChildAsync`) has its own catch that settles the step with a
  fixed string. `BackgroundAssistantTurnRunner.cs:285` launches nothing.

So a Retry gated on `SafeToReRun` is enabled **never** — with one latent qualifier. **`ForReason` matches by
string *value*, not "by reference to its declaration" as its own doc comment claims** (`FailureMapper.cs:19`),
and `SafeFail`'s fallback (`AgentRunOrchestrator.cs:1899`) feeds it arbitrary reason text from sites that *do*
have a run row. A reason byte-identical to the token `"NoProvider"` would therefore mint `SafeToReRun: true`
into `AgentRuns.FailureJson` on a real card. No raiser produces that string today: the constant is only ever
passed by name, and `PreModelLaunchException`'s message is the sentence at `HeadlessRunLauncher.cs:323`. It is
a hazard for whoever adds the next reason string, not a live bug.

### Why resume-from-step is not buildable yet

A failed run's ledger cannot be drained. The in-flight step goes `Running` at `AgentRunOrchestrator.cs:405`;
it is settled by `SafeRecordStep` on the success path (`:494`) and restored to `Pending` on the pause path
(`:565-566`), and the fail path (`:585`) does **neither** — so the step is left `Running`.
`NextPendingStepAsync` (`AgentRunService.cs:1197`) selects `Status=Pending` only, and the sole repair in the
codebase — statement 1b of `FailInterruptedRunsAsync` (`AgentRunService.cs:705-728`) — is scoped to
`State=WaitingForChildren` and never touches a Failed run. Its own comment states the cost of draining an
unrepaired ledger:

> a step left Running is INVISIBLE to it: without this statement a re-parked parent would skip its whole
> delegated group, execute the steps AFTER it out of order against inputs that were never produced, and
> settle Completed while the panel still rendered those steps as active — permanently and silently.

### What a Retry would require

1. A `Failed → Running` claim that first resets that run's `Running` steps to `Pending` — statement 1b's
   rule, handed to the fail path.
2. That claim must **not** `SET ExtraJson = NULL`. See §13.
3. A card reader that keeps more than the layer. `ReadFailureLayer` (`RunProgressViewModel.cs:1267-1270`)
   discards `Code` and `SafeToReRun`, so nothing in the ViewModel can gate on the verdict today. The button
   itself would live in `RunProgressPanel.xaml`.

Together these put the work **above** `G5`'s `M` estimate.

## 16. Traps for the failure-layer half

- **`Classifier` is not an approved suffix.** `NamingConventionTests.ServiceClasses_MustFollowNamingConvention`
  holds a closed list (`Service`, `Handler`, **`Mapper`**, `Parser`, `Detector`, …), and `Classifier` is not
  on it. Hence `FailureMapper`. `Pia.Consent` and `Pia.Services.Exceptions` are excluded from that rule;
  `Pia.Services.*` is not — the same prefix behaviour that made `Pia.Services.Diagnostics` inherit it in
  Part I.
- **A record may not live in the `Pia.Services` root namespace.**
  `RecordTypes_MustNotLiveInTheServicesRootNamespace` fails it outright. `PiaFailure` lives in `Pia.Models`.
- **`IAgentRunService` has one production implementation and four test doubles** — `SpyRunService` in both
  `AgentRunClarificationResumeTests` and `AgentRunResumeNoRePlanPremiseTests`, `FaultyRunService` in
  `AgentRunOrchestratorTests`, `ThrowingAgentRunService` in `BackgroundAssistantTurnRunnerRunSpineTests`.
  Adding a parameter to `FailAsync` touches all five. An optional parameter *looks* like it keeps them
  compiling silently — this repo has already shipped a green gate over an unstubbed mock — but the prediction
  was wrong in the safe direction: **an interface member must match exactly, defaults included**, so all seven
  broke loudly.
- **Every optional dependency of `RunProgressViewModel` goes LAST and DEFAULTED.** It is hand-constructed with
  a **positional** argument list in production and in its tests, and the file says so four times over — each
  added service is trailing, defaulted, and documented with what a null means ("the panel is byte-identical to
  before"). `G4`'s navigation dependency follows that discipline or it breaks every positional construction at
  once; `RunFailureAction` opens with `if (_navigation is null) return;`.
- **The letter `G` is overloaded in this code.** Comments inside `RunProgressViewModel.cs` say "G4" and "G7"
  meaning *agent-roadmap batches*, which is not what row `G4` means. Say "row `G4`" in prose, and per
  `CLAUDE.md` put no task id in the source at all.

---

# Part III

## 17. How it was verified, and what that verification could not see

Four instruments were used: the gate, a mutation sweep, a real-profile footprint snapshot, and a driven UI
run. Each found something. Each is also blind to something, and the blind spots are the part worth keeping.

### 17a. The fixture trap, and it outranks every number in this document

Run 2 of the UI walkthrough seeded a file called `pia-2026-08-24-001.log`, exported, and confirmed that the
"rolled name" was parsed as that day's file and carried into the archive. The reading said so:

> Run 2 confirms the wider `pia*.log` enumeration end to end: a **rolled name**, `pia-2026-08-24-001.log`,
> was parsed as that day's file, included, and carried into the archive.

**It confirmed nothing.** The fixture was **hand-seeded**, and it is a name the sink **cannot produce**:
NReco appends the roll index with **no separator**, so the real form is `pia-2026-08-241.log`. Run 2 exercised
the separator branch — the one written for a name typed by hand or by an older sink — and not the one that
ships. The parser was in fact **rejecting every file the sink can roll**, and a green live run against a real
profile said it was fine.

**Fixture shapes must come from the producer, never from the parser's author.** The author of a parser knows
what they intended to accept; only the producer knows what it emits. This trap has no checklist row, and it is
the reason §5 now shares one parser between the export and the retention sweep — two readers of the same names
that could otherwise disagree about which files are ours.

The same failure mode is a family, not a one-off:

- The plan's cap prediction, *"~12 files trips both caps"*, was **arithmetically impossible for any seed** —
  selection is a contiguous newest-first run, so only the newest 7 are ever reachable and the byte cap binds
  only if those seven are big. A prediction nobody checked against the algorithm.
- `Test-Path` on the Diagnostics directory is the wrong Cancel assertion in general; it happens to work only
  because the directory is created lazily. Count `pia-diagnostics-*.zip` instead.
- The plan's `\\|/` separator check is **wider than the shipped assertion** and reports two false alarms
  (`logs/` in `README.txt` prose, `DBUG/TRCE` in `environment.json`). The shipped assertion is
  `Path.DirectorySeparatorChar` — backslash only.
- The plan's snackbar section looked at `RootSnackbarPresenter`, **which this app never drives** — see 17h.
- And from the sibling artifact-probe work, the identical shape in a different instrument: **never quote a
  probe line's `declared` count as a run total.** `AgentRunOrchestrator`'s drain loop reaches
  `if (cancelled || failed) break;` immediately before the verify pass, so a run that fails never verifies
  again; every declaration made after the last verify pass goes unprobed and the counters under-report. Read
  `AgentSteps.ExpectedArtifact` from the database, which is untruncated and is what the verifier itself reads.

### 17b. The gate

`dotnet test` with no filter, on Windows, is the gate; the bar is `failed: 0`. At the time this document was
rewritten: **total 4987, failed 0, skipped 59** (1 real skip, the rest `Not Run` live-provider tests). Debug
and Release both rebuild to `0 Warning(s)` / `0 Error(s)` under `-t:Rebuild`, with `TreatWarningsAsErrors` on.

**What the gate cannot see:** it never lays out a control (`Activator.CreateInstance` does not render, and a
bogus `SymbolRegular` name compiles clean and then draws a garbage letter), never constructs a
`ContentDialog`, never meets the real NReco writer holding the real file, and cannot tell that
`ShellLauncher` swallowed a failed reveal. Those four gaps are exactly what the UI run was for.

### 17c. What the residual scan cannot see

**Zero hits, both arms**, over the log entries *and* the three generated entries, for the account name, the
machine name, `C:\Users`, `AppData`, the throwaway root, an e-mail shape, and **all five configured provider
names**. 45,944 real lines in Arm A run 1 alone, written by a real app across 20 real days, and not one leak.

**And it is partly circular** — see §3's *Measured residual*. The scan keys on the account name and the
machine name, which are the two values R05 and R06 key on, and those rules fired 0 times because R04 consumed
every occurrence first. The zero is corpus evidence about **R04, R07 and R12**. **R05, R06, R10 and R11 are
covered by unit tests only, on both instruments.** A profile with a corporate machine name in a UNC path, or
one that had ever logged an address or a token, would exercise them, and none has been measured.

### 17d. The mutation sweep — non-vacuity measured rather than asserted

Each shipped mechanism was reverted one at a time, rebuilt, and the covering test class re-run. A mutation
that leaves the class green means the test never covered the mechanism.

**17 of 17 caught** — and the first pass found a real hole, which is the point of running it:
`FileMode.CreateNew → Create` was **NOT** caught, because a redundant `File.Exists` pre-check shadowed it.
Fixed (§9) rather than documented around, and then caught.

| Mutation | Caught by |
|---|---|
| `FileShare.ReadWrite` → `FileShare.Read` | `ALogFileHeldOpenForWriting_IsStillExported` |
| R01's drop state starts open instead of closed | `AStreamThatStartsMidRecord_EmitsNothingBeforeItsFirstRecord` |
| debug bodies no longer dropped | the drop and continuation tests |
| record predicate loses its level check | `AFiveColumnTabularLineInsideAPayload_IsNotMistakenForARecord` |
| profile keys sorted shortest-first | `TheLongestProfileKeyWins_…` |
| machine-name DNS suffix left standing | `TheMachineName_IsReplacedWithItsDnsSuffix` |
| credential delimiter loses trailing whitespace | `ACredential_IsReplaced` |
| UNC anchor drops its colon guard | `AUncHeadIsReplaced_ButAJsonEscapedDrivePathIsNotMistakenForOne` |
| output-inside-source guard disabled | `AnOutputPathInsideTheSourceDirectory_IsRefused` |
| `FileMode.CreateNew` → `Create` | `AnExistingArchive_IsNeverOverwritten` |
| enumeration narrowed to `pia-????-??-??.log` | 3 failures, one of them the end-to-end rolled-file export |
| cap skips-and-continues instead of stopping | `TheByteCapTakesAContiguousNewestRun_…` |
| `SensitiveWarning` back to `LogWarning` | `SafeLogLevelTests` |
| `DiagnosticsDirectory` moved inside `Logs` | `TheDiagnosticsDirectoryIsASiblingOfTheLogDirectory_NotAChildOfIt` |
| `LogsDirectory` frozen as `static readonly` | `RoutedMember_ObservesAnOverrideAppliedAfterItsTypeIsLoaded` (2 failures) |
| the new button loses its `AutomationId` | `ViewAutomationIdTests` |
| the de confirm message loses a placeholder | `ADiagnosticsKeyCarriesTheSamePlaceholdersInEveryLocale` |

**What the sweep cannot see:** it can only revert a mechanism that exists, and it validates the *tests*, never
the *fixtures*. Row 11 above — narrowing the enumeration — passed, while the test seeding it used the same
wrong rolled-name shape as 17a.

**Real-profile footprint**, snapshot → work → compare over 11 mtimes and hashes: `history.db`,
`history.db-wal`, `settings.json` and `providers.json` **byte-identical**; `%LOCALAPPDATA%\Pia`, `\runs`,
`\workdir`, `\Logs` and `%APPDATA%\Pia` mtimes **unmoved**; no `Diagnostics` directory created. One exception,
reported rather than smoothed over: **`history.db-shm` changed once** during the session and then stayed
byte-identical across a repeat full gate run. `-shm` is SQLite's scratch index and holds no durable data,
nothing in this change constructs a database connection, and the collector is never constructed by any test —
so this reads as a one-off convergence of a pre-existing behaviour rather than something the feature
introduced. Worth a row if it recurs.

### 17e. The UI run, and the four defects it found

Driven 2026-08-24 through `ww_invoke` on the real button and the real dialog, over two arms. Arm A was a
throwaway profile (Appendix A) seeded with **real** log files; Arm B was the real `%LOCALAPPDATA%\Pia\Logs`,
39 files, run once with the owner's agreement and its artifact deleted immediately afterwards —
`%LOCALAPPDATA%\Pia\Diagnostics` does not exist. The real roaming profile, the real log directory and
`Documents\Pia Assistant` were all verified untouched by timestamp after the run.

**Nine invocations**: seven wrote an archive (runs 1, 2, 4, 5, 6, 7, 8) and **two were correctly refused**
(runs 3 and 9). Earlier drafts said six and then eight; eight came from counting the post-fix verification run
as an addendum to a table it had already been appended to. Nine is what the rows add up to.

| # | Arm | Seed | Result |
|---|---|---|---|
| 1 | A | 20 real log files, 23.1 MB | 7 files, `pia-diagnostics-2026-08-24-192248.zip`, 444,227 bytes |
| 2 | A | +12.4 MB on one mid-run file, plus `pia.log` and a rolled name | 3 files — **byte cap bound** |
| 3 | A | 91 decoy zips planted over the next 91 seconds | **refused, nothing written, no decoy touched** |
| 4 | A | decoys removed | succeeded again — the button recovers |
| 5–6 | A | same | succeeded twice (two snackbar-timing probes) |
| 7 | **B** | the **real** log directory, 39 files | 7 files, 444,176 bytes |
| 8 | A, **post-fix** | the run-2 seed again | manifest now reads `"OverFileCountCap"` / `"OverTotalByteCap"` / `"UnrecognisedName"` |
| 9 | A, **post-fix** | 121 decoys | refused, and now says **why** — in the notice and in the log |

All four test questions passed. The button is reachable and its `SymbolIcon` resolves to `U+F151` and renders
a download arrow **on a real pixel**, not a Latin letter. The dialog reads correctly and its count is live
(7 on the natural seed, **3** after the run-2 reseed). **Cancel wrote nothing at all** — the `Diagnostics`
directory did not even come into existence. Question 3's premise was proved first: `File.OpenRead` on today's
log threw `IOException` *before* the export, and today's log is in the archive anyway. Reveal opened Explorer
with the zip **selected**, verified through `Shell.Application` rather than by reading a window title — and
note it opens a **new** Explorer window per export rather than reusing one.

Entry set, every archive: exactly `README.txt`, `manifest.json`, `environment.json` and `logs/pia-*.log`. **No**
`providers.json`, `history.db`, `settings.json` or `.md` entry. The throwaway `local\` directory held live
`history.db`, `history.db-shm` and `history.db-wal` throughout — they are siblings of `Logs\`, and none of them
came near the archive.

All three cap-reachable exclusion reasons were observed at runtime. Run 2's walk stopped exactly where the
arithmetic said it would:

```
pia-2026-08-24.log        137,102  included
<rolled name>              21,280  included
pia-2026-08-23.log        126,932  included
pia-2026-08-22.log     12,361,729  EXCLUDED  OverTotalByteCap   <- the breach
pia-2026-08-21.log      1,040,189  EXCLUDED  OverFileCountCap
… 17 more …                        EXCLUDED  OverFileCountCap
pia.log                     4,461  EXCLUDED  UnrecognisedName
```

The collision guard was exercised **deterministically rather than by racing the clock**: two dialog round
trips will never land in the same second, so 91 decoy zips were planted covering the next 91 seconds and the
export was invoked into that minefield. No archive written (93 files before, 93 after), **all 91 decoys
byte-identical** so the generic cleanup arm did not delete a file this export did not write, the two real
archives untouched, a persistent Flow error item raised — and the next export, with the decoys removed,
succeeded. That is `FileMode.CreateNew` and its `when (File.Exists(...))` filter, proven in the app.

Redaction fired at scale (Arm A run 1, counted over the archive's own log entries): `<debug-payload-dropped>`
10,021 · `<profile-*>` 923 · `<url:https://host-NNN>` 22,430 · `<provider-N>` 2,310 · `host-NNN` 23,004 ·
`<path>` 9,095 — and the record prefix survived tab for tab:
`2026-08-24T12:24:51.9219150+02:00 <TAB> INFO <TAB> [Bootstrapper] <TAB> [0] <TAB> …`.

`environment.json` names **no machine and no provider**: `ProviderTypeCounts` is types-and-counts
(`PiaCloud: 1, Mistral: 1, OpenRouter: 1, Ollama: 1, OpenAI: 1`), `ProviderCount: 5`, and there is no
`MachineName` field. All **12 rules** are listed with a `Tier` and a `Hits` number; `R05_MACHINE_NAME` and
`R06_USER_NAME` both read 0, exactly as §3 predicts.

**Four defects, none of them a privacy leak; all four are legibility failures, which is the thing this folder
exists to fix.**

**Defect 1 — `manifest.json` reported the exclusion reason as a bare integer.** Observed, Arm A run 2:

```json
"FileName": "pia-2026-08-22.log", "Included": false, "ExclusionReason": 1
"FileName": "pia-2026-08-21.log", "Included": false, "ExclusionReason": 0
"FileName": "pia.log",            "Included": false, "ExclusionReason": 2
```

`0`/`1`/`2` are `OverFileCountCap`/`OverTotalByteCap`/`UnrecognisedName`, so the value is correct and useless:
a support engineer opening the archive cannot tell which. Worse, `0` is also what a reader expects
"none/default" to look like, and `"ExclusionReason": null` on the *included* rows sits right next to it.
`environment.json`'s `Tier` in the same archive read `"Deterministic"` because the collector calls
`.ToString()`; the manifest went through the default enum serializer instead. This contradicted the archive's
own README — *"so a file left out is visible from in here rather than simply absent"* — and §4's promise that
the manifest lists every file **with its reason**. **Fixed:** `JsonStringEnumConverter` on the shared
serializer options. `ProviderTypeCounts` is keyed `string`, so `environment.json` was unaffected. A sibling
labelling bug rode along: once the byte cap stopped the walk, **exactly one** file carried `OverTotalByteCap`
and every file after it was labelled `OverFileCountCap`, blaming a cap that had slots to spare. Also fixed —
§5.

**Defect 2 — a refused export left nothing in the log.** The `OutputAlreadyExists` arm was the **only**
failure arm in `DiagnosticsExportService` that did not log. Run 3 refused an export and the log window
`19:29`–`19:31` contains **zero** `WARN`/`FAIL` lines — not one word about it. The user sees a generic failure
notice and the log a support engineer would then ask for says nothing happened. **Fixed, and confirmed in the
app (run 9):** `WARN … Diagnostics export refused: an archive from the same second already exists`, where
before there was nothing.

**Defect 3 — six failure causes collapsed into one message.** `DiagnosticsExportFailure`'s own doc comment
calls it *"a cause the caller can branch on"*, and the caller did not: `GeneralSettingsViewModel` branched on
`!result.Succeeded` and showed one string, `Msg_Settings_DiagnosticsFailed`, for all six. So "the name is
taken, try again in a second" and "the disk refused the write" were indistinguishable to the person the
message is for. **Fixed:** the two causes a user can actually hit and act on — `OutputAlreadyExists` and
`OutputDirectoryMissing` — get their own message; `SourceDirectoryMissing`/`NoLogFiles` route to the existing
"Nothing to export" pair; `OutputInsideSourceDirectory` and `WriteFailed` keep the generic one.
`OutputInsideSourceDirectory` is an invariant `PiaPaths` guarantees and no user can reach it. Run 9 read the
new text straight off the Flow item: *"An archive from this second already exists. Try again in a moment."* —
which also proves a **resx-only key resolves**, since `LocalizationSource` goes through
`ResourceManager.GetString` and would otherwise have rendered `[Msg_Settings_DiagnosticsFailed_NameTaken]`.

**Defect 4 is 17f**, and it is the one only a live run could find.

### 17f. A provider name rewrote the inside of another rule's token

**This is the defect that only a live run against a real profile could find**, and it is why Arm B earned its
cost. The developer's profile has a provider named `local`. R09 (`PROVIDER_NAMES`) runs *after* R04
(`PROFILE_ROOTS`), and its boundary — `(?<![A-Za-z0-9])name(?![A-Za-z0-9])`, case insensitive — happily
matches **inside the token R04 had just emitted**, because `-` and `>` are not alphanumeric. Arm B, verbatim
from `logs/pia-2026-08-24.log`:

```
… INFO [Bootstrapper] [0] Data directories: Roaming=<profile-roaming>, <provider-3>=<profile-<provider-3>>, Overridden=False
```

`<profile-local>` came out as `<profile-<provider-3>>`. Counted over the whole Arm B archive:
`<profile-roaming>` **295**, `<profile-user>` **326**, **`<profile-local>` 0** — the token is not rare in that
archive, it is *extinct*. And R12's `TokenisedDirectoryPattern`, which anchors on
`<profile-(roaming|local|user)>`, therefore **silently stopped firing on every local-root path**.

The same collision hits ordinary prose: in Arm A the literal word `Local=` in
`Data directories: Roaming=…, Local=…` became `<provider-3>=`.

**This generalises past provider names.** Every rule keyed on **arbitrary user-chosen text** — provider names,
host literals, the machine name, the account name — can rewrite the inside of an earlier rule's replacement
token. A provider or host named `path`, `host`, `user`, `token`, `machine` or `profile` would corrupt a
different one.

**Fixed, and only half of it:** the raw-key replacements now run **outside** the placeholder spans earlier
rules emitted, so `<profile-local>` survives. The `Local=` → `<provider-3>=` case is **not** fixed and is not a
bug in the guard — that occurrence is outside any placeholder, and a five-character provider name clears the
existing four-character floor. **Naming a provider after a common English word costs you that word in your
logs**; the mitigation would be a stop-word list or a longer floor, and neither is obviously right.

**The constraint on the fix.** The guard must be applied to the raw-key replacement **only**, never to a pass
that deliberately anchors on an emitted token: R05's `MachineSuffixPattern` (`<machine>(\.[…])+`) and R12's
`TokenisedDirectoryPattern` both read the previous rule's output on purpose, and wrapping either would have
re-broken what the guard exists to protect.

### 17g. Two things the failure-layer build found that reading could not have produced

- **The mapper had to walk the inner exception chain.** A refused connection reaches the orchestrator as
  `AggregateException` → `ClientResultException` → `HttpRequestException` → `SocketException`. Matching only
  the outermost type classified **every real transport failure** as `Unclassified`, and the card named no
  layer — found by pointing a provider at a dead port and watching the card, not by any test. `Unwrap` is
  depth-first, outermost first (so the most specific wrapper still wins over the socket error at the bottom)
  and bounded at depth 8, because an exception graph is caller-supplied.
- **A codec split across two files drifts silently.** `AgentRunService` serialised camelCase; the panel
  deserialised with default (Pascal) options, so every descriptor read back as `Unclassified` — **which the
  reader reports as "no layer", not as an error.** `PiaFailure` now owns `ToJson`/`FromJson` and both sides go
  through it.

### 17h. What no script can assert

- **The success notice is unassertable from an MCP-driven script.** `RootSnackbarPresenter` never renders
  anything: `ISnackbarService` is bound to `Services.Flow.FlowSnackbarService`, which funnels **every**
  `Show(...)` into the Flow rail; `SetSnackbarPresenter` stores the presenter and never drives it. Readability
  then depends on severity — **failure** (`ControlAppearance.Danger`) is `FlowLifetime.Persistent` and fully
  UIA-readable at leisure, **success** (`ControlAppearance.Success`) is `FlowLifetime.Transient(5s)` and
  **four attempts failed to catch it**: one MCP round trip after the invoke is already too late, `PeekItems`
  reads empty, and a window screenshot seconds later shows nothing. It is not absent — the export succeeded
  each time — it is simply gone. **Assert the artifact instead.** If it ever needs a regression test, the
  lever is `FlowLifetime`, not the test.
- Two rail facts worth having: while collapsed the rail shows an unread **count badge** (a `Text` reading
  `"1"` under the bell) which *is* readable and makes a good "something was published" probe; and expanding it
  needs a **real mouse click** — the collapsed handle is a `Border` with a `MouseBinding`, carrying no
  `AutomationId` and no `InvokePattern`, so `ww_invoke` cannot open it.
- **"Nothing to export" is unreachable through the UI.** The sink writes `pia-<today>.log` during startup, so
  `Plan()` always finds a file by the time the button exists. Covered by
  `AnEmptySourceDirectory_ReportsNoLogFiles` only. Do not spend time trying to force it.
- **The dialog selectors, now settled** — this was the run's one open playbook question. The confirmation is a
  Wpf.Ui `SimpleContentDialog` from `ShowSimpleDialogAsync`, and it **does** carry the shared ids:
  `automationId=PrimaryButton` (named "Yes") and `automationId=CloseButton` (named "No"), one each. Better
  still, the whole dialog is a nested `Window` peer named by its title, so
  `ww_dump_tree(selector="type=Window")` reads all of it, and `type=Window` resolves **0** when no dialog is
  up, which makes it a reliable presence check. Already recorded in
  `docs/ui_automation/ui-automation-playbook.md`.

---

# Part IV

## 18. Deliberately not done, and open

- **No upload, no server, no "Send".** Export only. §2 is why that is load-bearing rather than a first
  increment.
- **No content preview** in the consent dialog. §7 decision 5.
- **No policy gate.** An enterprise policy cannot currently forbid a diagnostics export. Nobody asked for one;
  it would be a `PolicyLock` row plus a key.
- **No floor under retention.** §8.
- **No taxonomy for chat turns.** Scope is agent runs and scheduled jobs, the two surfaces with a durable
  failure record. A chat-turn error has no row to hang a descriptor on.
- **No retry budget.** Review #9 (empty-response guard with a cost-aware retry budget) is a separate row and
  stays where it is.
- **`BlueprintKey` stays data-only.**
- **No `WriteResult` snackbar seam fix.** `ChatSession.cs:1160-1167` logs *"Executed {ToolName} action
  successfully"* and raises `ToolSucceeded` unconditionally after `Execute()` returns, so a `write_file` that
  returned `WriteResult.Failed` still shows a **green success snackbar** (`AssistantViewModel.cs:554`,
  `ControlAppearance.Success`). Untouched. §19 is the ranked list for whoever picks it up.
- **One known correctness wart, accepted:** `OutputService.cs:110` interpolates the window title into an
  exception message that is logged at WARN, two lines after wrapping the same value in `SensitiveDebug`. R03
  redacts it on export. **Fixing the source would be better** — it is one line and the author's intent is
  already documented by the adjacent `SensitiveDebug` — but doing it here would contradict §2's design centre,
  so it is written down rather than done.
- **`G3` has never been exercised live.** Tests only; see §14.
- **The `Local=` half of 17f** — a common-word provider name still eats that word in the logs.
- **The deterministic half of the export flow is recordable** as a `tests/ui-scripts/` script — button
  present, dialog opens, Cancel writes nothing. Nothing found in the UI run argues against it, and the fixture
  needs `defaultWindowMode: 1`. The export half is **not** recordable: the artifact name carries a timestamp
  and the real assertions are inside a zip, neither of which the replay harness can express.
- **`history.db-shm` changed once** then stayed byte-identical — reported rather than smoothed over, worth a
  row if it recurs.

## 19. The failed-tool-call gap, and what would be worth building

An executed-but-failed tool call renders in the timeline **exactly like a successful one**. That is the wider
gap behind the `WriteResult` bullet above, and this is the ranked list for whoever closes it. **None of it is
required by anything shipped**; it is recorded here because this is the track that owns failure legibility.

1. **A fourth outcome, `Failed`, populated only where the shape is unambiguous** — i.e. from
   `WriteResult.success == false`, plus any handler later given the same envelope. That is a real signal with
   no guessing, it needs no result text, and `RunProgressViewModel.Project` already has the branch to hang a
   suffix on. It leaves the ~118 string sites unclassified, which is honest: `Ok` keeps meaning "returned",
   and only rows that can prove failure claim it.
2. **A tool-failure count on the step-outcome log line**, same source. Cheapest possible version of the same
   fact, and it makes the next reading of this channel possible at all.
3. **Nothing in the step outcome.** Making `succeeded` depend on tool results would fail steps whose model
   correctly recovered from a failed call, and it is the model — not the executor — that knows whether the
   step's work got done.

**One cheap improvement, adjacent and already costed.** `TryBuildArtifactFactsAsync` is a **pure filesystem
probe with no provider call** — it runs first inside `VerifyAsync`, before any capture. Running it, **and only
it**, on the failure path would complete the artifact tally at **zero token cost**, and would surface *"this
run failed, and here is what it declared but never produced"*, which is the case a user most wants to see.
(Context: 17a's last bullet — the drain loop breaks before the verify pass on a failed run, so the tally is
structurally short on exactly the runs that most need it.)

---

## Appendix A — the throwaway-profile recipe

Verbatim, because both copies of it lived in files this document replaces. `PIA_DATA_DIR` /
`PIA_LOCAL_DATA_DIR` point Pia at a scratch profile, so a UI walkthrough cannot touch the real one — with two
caveats that are the whole reason this is written out rather than summarised.

**Caveat 1: `PIA_DATA_DIR` does not isolate `assistantFilesFolder`.** Set it explicitly.

**Caveat 2, and it can silently damage the real machine:**

> Leave `launchAtStartup` TRUE: `App.xaml.cs` only writes the HKCU Run key when the setting and the
> key disagree, so flipping it to false DELETES the real one.

The short form:

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

The long form, with the reasons:

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

**Copy from the real `%APPDATA%\Pia` rather than hand-writing a settings file**: the Pia Cloud tokens live
DPAPI-encrypted *inside* `settings.json`, so a hand-written one meets the first-run wizard and a "Setup
Required" overlay.

Then launch, handing the override in via `env`:

```
ww_launch(
  path: "<repo>\src\Pia.Wpf\bin\Debug\net10.0-windows10.0.17763.0\Pia.Wpf.exe",
  env: { PIA_DATA_DIR: "<%TEMP%>\pia-diag-ui\roaming",
         PIA_LOCAL_DATA_DIR: "<%TEMP%>\pia-diag-ui\local" })
```

**You do not need to close your own Pia.** The two share no settings file, no `history.db` and no log
directory. Confirm the app you are driving is the right one (`ww_get_value(selector="type=Window")` — the
title carries the mode and version) and cross-check that the throwaway `local\Logs\pia-<today>.log` has just
been appended to.

**Keep the premise assertion.** Before asserting anything about the sink-held log, prove the premise, or the
check passes either way:

```powershell
try { [IO.File]::OpenRead("$p\local\Logs\pia-<today>.log"); "PREMISE FAILED" }
catch { "PREMISE OK - the sink is holding it" }
```

**One expected difference you must not misread as a bug.** Under the override, the local-root redaction key is
the *throwaway* path, so a copied line containing the real `…\AppData\Local\Pia` does **not** match it. It
matches the user-profile root instead and comes out as `<profile-user>\<path>\…` rather than
`<profile-local>`. That is correct behaviour — it is the fallback for a log written before an override — and
`<profile-roaming>` will likewise not appear. It is also why Arm B, against the real profile, was worth its
cost: 17f is only visible there.

**Two traps from the same run, both already paid for once.** Blank cream screenshots with a perfectly correct
UIA tree are the documented WPF hardware-rendering stall, not a broken app — `ww_window action=resize` does
not clear it, the fix is a registry write (`HKCU\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration = 1`)
so **ask the owner** rather than doing it, and fall back to `ww_dump_tree` bounds. But a `SymbolIcon` check
**needs a real pixel** — if the surface is stalled that check is *deferred, not passed*, and you must say so.
And `ww_click` returns success for no-ops: prefer `ww_invoke`, and confirm every state change independently.

Delete `$env:TEMP\pia-diag-ui` when done, unless the run failed — a red run's throwaway `local\Logs\` is the
evidence.
