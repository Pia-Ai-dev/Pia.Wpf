# Prompt — Export Diagnostics, in one workflow

**Status:** ready to paste into a fresh session. **Written:** 2026-08-24.
**Origin:** item #3 of the *not yet planned* table in
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md),
scoped as *Export* rather than *Send* by the owner on 2026-08-24, to be built after slice 1 of #2 (which
landed at `3c90aa74`). Everything below the rule is the prompt. **Delete this file once consumed.**

---

## The prompt

> **Use exactly ONE workflow for this session.** Multi-agent orchestration is authorised, but one workflow —
> not a sequence of them. Fan out where the work is genuinely independent and adversarially verify the
> redaction before shipping it.
>
> Repo `C:\projects\Pia.Wpf`, branch `feature/agent-run-spine`, at or after `3c90aa74`. **Another session may
> commit here** — re-read the checklist and `git log` before trusting any line number below.
>
> **Read first, in this order:** `CLAUDE.md` (privacy-first logging, doc layout, the test gate), then the
> *not yet planned* table and the "open points with no row yet" section of
> `docs/hermes_checkup/2026-08-22-hermes-followup-checklist.md`. Read
> `docs/hermes_checkup/2026-08-24-p9-refused-write-reading.md` §5 only if you get to the follow-up in §6 below.
>
> ### What to deliver
>
> **Export Diagnostics — a consented, redacted zip written locally, plus reveal-in-Explorer.** Logs only,
> never transcripts. Plus a `docs/failure_legibility/` topic folder that owns this work, and a ticked row in
> the checklist's *not yet planned* table (promote #3 out of it, the way `C4` was promoted, rather than
> leaving the table claiming it is unplanned).
>
> The app currently offers **no route to its own logs at all**, while `CLAUDE.md`'s support story already
> assumes users hand-attach `%LOCALAPPDATA%\Pia\Logs\pia-*.log`. That gap is the whole feature.
>
> ### The reframe that sets the scope — do not skip this
>
> The obvious plan is "audit the Information-level log sites and stop the leaks first." **Measured
> 2026-08-24: 523 call sites pass an exception object to `LogError`/`LogWarning`** (217 `LogError` calls in
> total), so an exception's `Message` **and stack trace** reach the release log in hundreds of places. One of
> them is the exact string slice 1 just labelled sensitive on the UI: `BackgroundAssistantTurnRunner.cs:273`
> logs `LogError(ex, …)` and then persists the same `ex.Message` as the run's failure reason.
>
> **So do not try to stop the logging.** The log is a debugging asset and 523 invasive edits is not a feature.
> **Redact on the way out instead:** the log stays as it is, and the export applies a documented redaction
> pass. That decision is the design centre of this work — write it down, with its rule set, and make the rule
> set testable.
>
> ### What already exists — build on it, do not reinvent
>
> - **`System.IO.Compression` is already used** in `AssistantChatSyncService`, `E2EEService` and
>   `SyncClientService`. `ZipFile`/`ZipArchive` needs no new dependency. (The owner favours minimal
>   dependencies — do not add a zip library.)
> - **`ShellLauncher`** (`src/Pia.Wpf/Helpers/`) already has a **reveal-in-Explorer** path, written for
>   exactly this "do not execute it, show it" reason. Reuse it; do not shell out yourself.
> - **`AssignmentConsentContentDialog`** (`src/Pia.Wpf/Views/Dialogs/`) is the consent-dialog precedent, and
>   `Pia.Services.Consent` already exists — note that `NamingConventionTests` **exempts** that namespace from
>   the service-suffix rule, so a type there is free of it.
> - **`PiaPaths`** (`src/Pia.Wpf/Paths/`) owns every profile path. See the invariants below.
>
> ### Decisions to make in writing, with the recommendation on record
>
> 1. **Redact on export, not at the log site.** Recommended, for the reason above. If you overrule it, say
>    what you are doing about the other 522 sites.
> 2. **What goes in the zip besides the logs.** Recommendation: the log files plus **one generated
>    environment summary** — app version, OS build, .NET version, provider *types* in use, feature flags,
>    counts. **Never** `providers.json` (it holds DPAPI-encrypted keys), **never** `history.db`, **never** the
>    vault, **never** `settings.json` verbatim. State the manifest in the doc and assert it in a test.
> 3. **A size and count cap**, so the zip cannot be a 900 MB WAL. Recommendation: the newest N log files under
>    a total byte cap, with the cap named in the consent dialog.
> 4. **Where the entry point lives.** Recommendation: Settings, near whatever already talks about support or
>    the app version. Not the Flow, not a toast.
> 5. **Whether the consent dialog shows a preview** of what is being exported. Recommendation: yes for the
>    manifest and the cap; a full content preview is a bigger feature — say so rather than half-building it.
>
> ### The one workflow — suggested shape
>
> Four phases, one `Workflow` call. **The implementation phase must fan out only over file-disjoint pieces** —
> parallel agents editing the same file is the failure mode here, and `isolation: 'worktree'` then leaves you
> merging. Prefer: the workflow surveys, designs and adversarially verifies; **the session itself writes the
> code and runs the gate.**
>
> 1. **Survey** (parallel, read-only) — (a) sample a real `pia-*.log` and categorise what a zip would ship
>    today, naming the categories that need redaction and quoting real examples; (b) the consent-dialog,
>    settings-entry-point and `ILocalizationService` conventions, with exact snippets; (c) what must never
>    leave the machine, derived from `CLAUDE.md`'s sensitive list plus what the code already treats as
>    sensitive.
> 2. **Design** — one agent reconciles the survey into the redaction rule set, the file manifest, the caps and
>    the UI shape. Output is the spec the session then implements.
> 3. **Adversarially verify the redaction** — several independent agents, each with a distinct lens, trying to
>    find content that survives the rules: a URL with credentials in the query, a Windows path containing the
>    user's name, an `ex.Message` quoting a goal, a tool argument, an email address, a bearer token, a machine
>    name. Each should try to **defeat** the rules, not confirm them. **A rule set nothing tried to break is
>    not a rule set.**
> 4. **Completeness critic** — what is missing: an unhandled category, an untested path, a claim in the doc
>    the code does not support.
>
> ### Invariants and traps — each of these was paid for once
>
> - **Log level is not a privacy gate** (it is runtime-configurable). Use `SensitiveDebug` /
>   `SafeUrl.Format`. `SensitiveDebug` is `[Conditional("DEBUG")]`, so the call *and its argument evaluation*
>   are erased from release IL — that is the mechanism, not a convention.
> - **Never call `Environment.GetFolderPath(ApplicationData | LocalApplicationData)`.** Go through
>   `PiaPaths`, and expose any routed path as a **property**, never `static readonly` or `{ get; } = …` —
>   those freeze at type load. `DataDirectoryRoutingTests` and `PiaPathsTests` both police this, and a third
>   test now proves `SensitivePathGuard` rebuilds when the roots move.
> - **The gate must not touch the developer's real profile.** As of `99b6c951` it does not: snapshot → run →
>   compare is **0 of 9 changed**. A test that needs a real profile path uses `RedirectedProfileFixture`
>   (`tests/Pia.Wpf.Tests/TestInfrastructure/`) **and** joins the `PiaPathsStatic` collection — that
>   collection is `DisableParallelization = true` and is the only thing making a process-wide override safe.
>   Do not regress this: a diagnostics export test that writes to `%LOCALAPPDATA%\Pia` is exactly the shape
>   that would.
> - **Localization: three files, one line each.** `ViewStrings.resx`, `.de.resx`, `.fr.resx` under
>   `src/Pia.Wpf/Resources/Strings/`. Form is
>   `  <data name="X" xml:space="preserve"><value>Y</value></data>` — two-space indent, UTF-8 **no BOM**,
>   **pure CRLF**. `LocalizationTests.AllTranslations_MustBeComplete` asserts parity in **both** directions,
>   so all three land in one commit. **Do not hand-edit `ViewStrings.Designer.cs`** — it is VS-generated,
>   already drifted (420 properties for 1195 keys, zero `Run_*`), and nothing reads it.
> - **Scripted resx edits keep breaking line endings.** A regex anchor of the form `/…[^\n]*$/m` **eats the
>   `\r`**, leaving a lone LF and a doubled CR. It happened again on 2026-08-24. Verify byte-wise with node
>   (count `10` and `13`+`10`) — **`grep -c $'\r'` is not reliable here**, and `grep -c $'\x00'` always
>   returns the line count because bash cannot hold a NUL. Never use `perl -0pi` on a source file: `-0` sets
>   the output record separator to NUL and injects one per record.
> - **Every new interactive control needs an `AutomationProperties.AutomationId`** plus the matching
>   `[InlineData]` count bump in `ViewAutomationIdTests`, in the same change. `ButtonBase`, `ComboBox`,
>   `TextBoxBase`, `PasswordBox`, `Slider`, `Expander`, `TabItem` — a `TextBlock` is **not** one, and the
>   existing note lines on the run panel deliberately have no id.
> - **Records may not live in the `Pia.Services` root namespace** (`NamingConventionTests`), and a non-record
>   class there must end with an approved suffix. `Pia.Services.Consent` is exempt from the suffix rule.
> - **The gate is `dotnet test` with no filter and the bar is `failed: 0`.** 4841 total at `3c90aa74`; 1
>   skipped and 58 `Not Run` are expected. A feature is not done until `dotnet build -t:Rebuild` reports
>   **0 Warning(s) in both Debug and Release** — `TreatWarningsAsErrors` is on.
> - **`dotnet test` intermittently hangs** (three times on 2026-08-24, always after a `-c Release` rebuild;
>   ~25 min at ~59 s CPU, i.e. wedged, not busy). Running the built exe directly
>   (`tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe`) completes in ~28 s. Use
>   the exe to confirm, re-run `dotnet test` once on its own for the official number, and **do not spend the
>   session chasing it** — but do open a row for it if it recurs, because a gate that wedges is worth one.
> - **Prove every test non-vacuous** by reverting the half it covers and watching that assertion fail.
>   Slice 1 did this and it is what showed 8 of 12 tests were the load-bearing ones.
> - **Tick the checklist row in the commit that lands it**, carrying what it actually shipped **and what it
>   deliberately left out**.
>
> ### Out of scope, deliberately — name these in the report, do not build them
>
> - **#2 slice 2** (a named failure layer, `PiaFailure(Layer, Code, Retryable)`, recovery actions, Retry).
>   One warning if you do pick it up: slice 1 renders a reason read from `AgentRuns.ExtraJson`, and **both
>   existing resume claims `SET ExtraJson = NULL`**. They only fire from `WaitingForInput`/`Paused`, never
>   `Failed`, so nothing wipes it today — but a Retry adds a *new* `Failed → Running` transition, and written
>   in the shape of its siblings it would null the column. Either the retry claim leaves `ExtraJson` alone or
>   the reason gets its own column first.
> - **P9 §5's `WriteResult` seam** — the interactive chat currently shows a **green success snackbar** for a
>   `write_file` that returned `WriteResult.Failed`. Verified at `ChatSession.cs:1160-1167` (logs "Executed
>   {ToolName} action successfully" and raises `ToolSucceeded` unconditionally after `Execute()` returns) and
>   `AssistantViewModel.cs:554` (`ControlAppearance.Success`). It is the only place the app tells the user
>   something false. Next after this, not part of it.
> - **Anything compaction.** The B-track closed 2026-08-24 promoting nothing; see
>   `docs/hermes_checkup/2026-08-24-compaction-arms-cde-reading.md`. Do not fund a second sweep.
> - **The D-track.** Parked; see `docs/guided_tour/2026-08-24-d-track-parked.md`. `D7` is a tag-along, not a
>   row to pick up.
>
> Report at the end what you deliberately did not do and why, and state the gate numbers you actually
> observed rather than the ones above.
