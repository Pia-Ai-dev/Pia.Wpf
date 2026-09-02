# CLAUDE.md

Pia.Wpf is the desktop client for Pia (Personal Intelligent Assistant), a WPF application with a shared DTO library.

## Build & Run

```bash
dotnet build                                           # Build all projects
dotnet build -c Release                                # Release build
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj       # Run WPF client
dotnet test                                            # Run all tests
```

## Test Gate

`dotnet test` with **no filter** is the gate. The bar is `failed: 0`.

Live-provider tests are marked `[LiveApiFact]` / `[LiveApiTheory]` (xunit v3 `Explicit`), so the
runner excludes them by default and reports them as `Not Run` — no caller-side flag is involved.
Older docs quote `--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`; that namespace no
longer exists, and the flag is now a no-op you should drop rather than carry forward.

```bash
dotnet test                                                        # the gate
dotnet test -- --explicit only --filter-namespace "Pia.Tests.Integration.Providers"   # opt into live APIs
dotnet test -- --coverage --coverage-output-format cobertura       # coverage
```

Run the built exe directly for a faster loop, but note it takes xunit's **native** single-dash
options (`-namespace-`, `-trait-`, `-explicit`, `-class`) and rejects the `--filter-*` forms:
`tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe`

`Microsoft.Testing.Extensions.CodeCoverage` is pinned to **18.0.6** on purpose. 18.1.0+ needs
`Microsoft.Testing.Platform` 2.x while xunit.v3 3.2.2 is on the 1.9.x line, and the resulting
unification makes every `dotnet test` run die with a `TypeLoadException`. Revisit when xunit.v3 4.0
ships.

## Zero-Warning Policy

A feature is not commit-ready until the build reports **`0 Warning(s)` and `0 Error(s)` in both Debug and Release**. Warnings are blocking, not advisory.

- Verify with a **rebuild** — an incremental build does not re-emit warnings from projects it skips: `dotnet build -t:Rebuild -v:n` (and again with `-c Release`).
- Read the count off MSBuild's `N Warning(s)` summary line. At `-v:n` every warning is printed twice (inline + summary), so grepping the log double-counts.
- WPF re-reports `src/` warnings a second time under a generated `Pia.Wpf_<hash>_wpftmp.csproj` (the XAML markup-compile pass). Fixing the source clears both — don't chase them separately.
- If a warning is genuinely wrong for the code, suppress it **narrowly**: a scoped `#pragma warning disable <ID>` / `restore` around the offending lines plus a comment saying why. Do not reach for a project-wide `<NoWarn>`.

## Solution Structure

| Project | Path | Framework |
|---------|------|-----------|
| Pia.Wpf (WPF Client) | `src/Pia.Wpf/` | net10.0-windows |
| Pia.Shared | `src/Pia.Shared/` | net10.0 |
| Pia.Wpf.Tests | `tests/Pia.Wpf.Tests/` | net10.0-windows |

## Code Style

- 4-space indent (C#), 2-space indent (XAML). `var` for apparent types.
- Fields: `_camelCase`. Properties/Methods/Classes: `PascalCase`. Interfaces: `IName`.
- MVVM: logic in ViewModels, not Views. Use `[ObservableProperty]` and `[RelayCommand]`.
- Namespaces use `Pia` (not `Pia.Wpf`) — the project was renamed but namespaces were kept.
- Data paths: never call `Environment.GetFolderPath(SpecialFolder.ApplicationData/.LocalApplicationData)`.
  Go through `Pia.Paths.PiaPaths` (`src/Pia.Wpf/Paths/`), which is overridable by `PIA_DATA_DIR` /
  `PIA_LOCAL_DATA_DIR` so UI tests get a throwaway profile. Like `Pia.Logging`, it deliberately sits
  outside `Pia.Infrastructure` so ViewModels can use it without breaking the layer rule. Expose a routed
  path as a **property**, never a `static readonly` field or `{ get; } = …` — those freeze at type load.
  `DataDirectoryRoutingTests` and `PiaPathsTests` hold both lines.

## Comment Discipline

Default to no comment. A surviving comment or XML-doc `<summary>` gets **one short line** — never a multi-paragraph essay, never a `<para>` block. Only write one when the WHY is genuinely non-obvious from the code (a hidden constraint, an invariant, a workaround, a surprising side effect); never to restate WHAT the code does. Then cut it as short as it will go: drop every clause the adjacent code already shows, and treat two wrapped lines as the ceiling.

Never cite the originating task in code — no batch/decision/spec IDs (`18 D1`, `G3`, `§4.1`, `owner Q4`, `Batch 08 F19`, `(I1)`, ticket numbers). That belongs in the commit message, not the source, and rots the moment the plan doc is renumbered. If you catch yourself writing "per spec §…" or "18 Gx", delete the comment and state only the underlying fact in plain language.

This applies to XML-doc as much as to `//` — a `<summary>` is not exempt from the brevity rules above just because it's Intellisense-facing.

## Git Workflow

Main: `main`. Features: `feature/<name>`.

Before treating a feature branch as done, clear the **Zero-Warning Policy** above.

## Release Notes

`docs/release_notes/RELEASE.md` is the curated, cumulative changelog for the **next** release —
rewrite it in place as work lands; it ships as the GitHub release body and, verbatim, as
`storage.pia-ai.de/f/wpf/RELEASE-NOTES.md`. The build stamps the version header itself and falls
back to a raw `git-cliff` commit dump only when the file has no changes since the last release
tag — so an unedited file is safe, but the bar for "curated" is that the body actually changed,
not that anyone remembered a version number. Read `docs/release_notes/README.md` for the format
rules (hard-wrap 80, one bullet level, no tables) before editing it. After a release ships you
**must** archive it by hand — copy to `YYYY-MM-DD-<version>.md`, truncate `RELEASE.md`, commit with
`[skip ci]` — because a push to `main` without it cuts another release. CI cannot do this: the
`Main` ruleset refuses the bot's push, so the step was removed. A build now refuses to start if
`RELEASE.md` still holds the previous release's body.

## Privacy-First Logging

Users may attach `%LOCALAPPDATA%\Pia\Logs\pia-*.log` when contacting support, so anything that ends up there must be safe in release. Log level is **not** a sufficient gate (it is runtime-configurable) — use the helpers below.

Helpers live in `Pia.Logging` (`src/Pia.Wpf/Logging/`):

- `_logger.SensitiveDebug(template, args...)` (also `SensitiveTrace`/`SensitiveInformation`/`SensitiveWarning`) — `[Conditional("DEBUG")]`, so the call **and its argument evaluation** are erased from the release IL. Use for user-content payloads.
- `SafeUrl.Format(uri)` / `SafeUrl.Format(string)` — DEBUG: full URL (truncated 500). RELEASE: `{scheme}://host-NNN` (stable SHA256-mod-1000 host code).

What counts as sensitive (must be `SensitiveDebug` or `SafeUrl`-wrapped):

- **Payloads**: tool-call args, tool results, response/request bodies, prompts, memory contents, plugin command-line args.
- **User-named items**: todo title, reminder description, memory text, template name, kanban column name, provider name, research step title, window title.
- **URLs**: any full URL/endpoint, including server URLs and HTTP request URLs.
- **Env-var values**: never log the *value*, only the name (or wrap the dump in `#if DEBUG`).

Patterns:

```csharp
// Don't:
_logger.LogInformation("Created todo {Id}: {Title}", todo.Id, title);

// Do:
_logger.LogInformation("Created todo {Id}", todo.Id);
_logger.SensitiveDebug("Created todo {Id} title: {Title}", todo.Id, title);

// Don't:
_logger.LogInformation("Fetching from {Url}", requestUrl);

// Do:
_logger.LogInformation("Fetching from {Url}", SafeUrl.Format(requestUrl));
```

`SensitiveDebug` is preferred over `#if DEBUG` blocks for one-off sensitive log lines. Reserve `#if DEBUG` for larger startup blocks (e.g. env-var dumps).

`Pia.Logging` does **not** belong to `Pia.Infrastructure` — ViewModels can import it without violating the layer rule.

## Documentation Layout

Every doc lives in a **topic subfolder** under `docs/` — `docs/<topic_name>/`. This applies to new docs *and* to any md file you update below docs/: moving it into a topic folder is part of that change.

- **Folder name** = the topic, snake_case, no date, and not the doc *type*: `docs/speaker_attribution/`, never `docs/2026-08-22/` or `docs/plans/`.
- The existing type-named folders (`docs/plans/`, `docs/reviews/`, `docs/specs/`, `docs/server/`) are **legacy — do not add to them.** A new plan goes in its own topic folder, next to the review or spec that spawned it.
- **File name** = `YYYY-MM-DD-<slug>.md`, dated when written. The date does not change when the doc is later revised. A *living* reference that gets rewritten in place rather than superseded — a playbook, a folder README — drops the date.
- Links between docs in the same folder are **relative** (`[2026-08-22-foo.md](2026-08-22-foo.md)`), so the folder reads on its own.
- An analysis or plan doc opens with **Status**, **Owner**, **Written**, and **Origin** (the doc or decision it came from), and is **self-contained** — executable cold, without the conversation that produced it.
- One carve-out: the `docs/superpowers/specs/` tree is addressed by path from skill definitions that live outside this repo, so leave it where it is. Everything else moves — and whenever you move a doc, fix every inbound reference in the same commit, including the ones in this file and under `tests/`.

### Checklist for medium-to-large work

Planned work at **`M` or larger**, or work spanning more than one plan doc, also gets a `YYYY-MM-DD-<topic>-checklist.md` in the same topic folder. That file is the tracking surface: tick boxes as steps land, in the commit that lands them.

Required:

- One `- [ ]` per step — bold title, then one sentence saying what it is.
- `*Deps:* · *Effort:* · *Value:*` on every step, with both scales spelled out at the top of the file. Effort: `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface · `L` a week or more, a new subsystem. Value: `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little standalone value, unblocks a High.
- A **suggested order** at the end — cheapest decisive work first, then the vertical slices.

When applicable:

- A table at the top mapping each group letter to its plan doc — only when several plans feed one checklist.
- A **decision gates** table for steps whose answer can cancel the steps below them, naming the question each one answers. Do not tick a dependant of an open gate without revisiting it.
- A **not yet planned** list, so candidates that never got a plan doc are not lost.

## UI Automation

Driving the app with WinWright/UIA (walkthroughs, UI regression tests): read `docs/ui_automation/ui-automation-playbook.md` first. It lists the stable AutomationIds and the techniques that work; do not fall back to pixel-offset clicking.

Every new interactive control added to a `UserControl` (`ButtonBase`, `ComboBox`,
`TextBoxBase`/`RichTextBox`, `PasswordBox`, `Slider`, `Expander`, `TabItem`) needs an
`AutomationProperties.AutomationId="<ViewPrefix>_<Field>"`, or the per-item binding form
`{Binding <Identity>, StringFormat='<ViewPrefix>_<Field>_{0}'}` for anything inside an
`ItemsControl`/`ContentTemplate` — a literal id there makes every row report the same id. Keep
the prefix disjoint from any other control a script might reach via the same `automationId*=`
prefix match (e.g. two different toolbars both rendering a "Copy" button need different
prefixes). `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` locks coverage in per view; add
the `[InlineData]` row in the same change.

Recorded UI flows live in `tests/ui-scripts/` and replay through `Invoke-UiScripts.ps1` (WinWright's
CLI `run` verb, not an MCP tool). They are **not** part of the `dotnet test` gate — they launch the
real app and drive the real desktop. The harness runs the app against a throwaway data directory, so
Pia may stay open and your profile is never touched; it verifies that by hash and fails on a leak.
`-KeepProfile` restores the old seed-and-restore behaviour and does require Pia to be closed. Read
that folder's README before recording a new one; the recorder has sharp edges (`stop` is a one-way
door, only `ww_assert_value` is recorded).

## Rules

- Do not read entire large files in a first run. Use grep or read file signatures first.
- Do not output conversational filler.
