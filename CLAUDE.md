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

## Comment Discipline

Default to no comment. A surviving comment or XML-doc `<summary>` gets **one short line** — never a multi-paragraph essay, never a `<para>` block. Only write one when the WHY is genuinely non-obvious from the code (a hidden constraint, an invariant, a workaround, a surprising side effect); never to restate WHAT the code does. Then cut it as short as it will go: drop every clause the adjacent code already shows, and treat two wrapped lines as the ceiling.

Never cite the originating task in code — no batch/decision/spec IDs (`18 D1`, `G3`, `§4.1`, `owner Q4`, `Batch 08 F19`, `(I1)`, ticket numbers). That belongs in the commit message, not the source, and rots the moment the plan doc is renumbered. If you catch yourself writing "per spec §…" or "18 Gx", delete the comment and state only the underlying fact in plain language.

This applies to XML-doc as much as to `//` — a `<summary>` is not exempt from the brevity rules above just because it's Intellisense-facing.

## Git Workflow

Main: `main`. Features: `feature/<name>`.

Before treating a feature branch as done, clear the **Zero-Warning Policy** above.

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

## UI Automation

Driving the app with WinWright/UIA (walkthroughs, UI regression tests): read `docs/ui-automation-playbook.md` first. It lists the stable AutomationIds and the techniques that work; do not fall back to pixel-offset clicking.

Recorded UI flows live in `tests/ui-scripts/` and replay through `Invoke-UiScripts.ps1` (WinWright's
CLI `run` verb, not an MCP tool). They are **not** part of the `dotnet test` gate — they launch the
real app and drive the real desktop, so close Pia first and expect the harness to swap
`%APPDATA%\Pia\settings.json` for its fixture and restore it afterwards. Read that folder's README
before recording a new one; the recorder has sharp edges (`stop` is a one-way door, only
`ww_assert_value` is recorded).

## Rules

- Do not read entire large files in a first run. Use grep or read file signatures first.
- Do not output conversational filler.
