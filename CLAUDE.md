# CLAUDE.md

Pia.Wpf is the desktop client for Pia (Personal Intelligent Assistant), a WPF application with a shared DTO library.

## Build & Run

```bash
dotnet build                                           # Build all projects
dotnet build -c Release                                # Release build
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj       # Run WPF client
dotnet test                                            # Run all tests
```

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

Default to no comment. A surviving comment or XML-doc `<summary>` gets **one short line** — never a multi-paragraph essay, never a `<para>` block. Only write one when the WHY is genuinely non-obvious from the code (a hidden constraint, an invariant, a workaround, a surprising side effect); never to restate WHAT the code does.

Never cite the originating task in code — no batch/decision/spec IDs (`18 D1`, `G3`, `§4.1`, `owner Q4`, `Batch 08 F19`, `(I1)`, ticket numbers). That belongs in the commit message, not the source, and rots the moment the plan doc is renumbered. If you catch yourself writing "per spec §…" or "18 Gx", delete the comment and state only the underlying fact in plain language.

This applies to XML-doc as much as to `//` — a `<summary>` is not exempt from the one-line rule just because it's Intellisense-facing.

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

## Rules

- Do not read entire large files in a first run. Use grep or read file signatures first.
- Do not output conversational filler.
