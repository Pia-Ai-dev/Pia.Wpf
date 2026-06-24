# Implementation Plan — `search_files` (Pia.Wpf)

> **Classification: `scratch` (from-scratch build).** Pia.Wpf has **no content/regex search engine of any
> kind** — `rg`/ripgrep/`grep` appear nowhere under `src` (verified by a repo-wide Grep). The only adjacent
> code is `FilesToolHandler.HandleListFiles` (a `Directory.EnumerateFiles` wildcard lister gated on a
> *privacy sandbox folder*), which overlaps only the secondary `target="files"` mode and even there lacks
> mtime sorting, substring-glob semantics, gitignore-awareness, pagination, and loop detection. The
> defining capability — ripgrep-backed regex **content** search with output modes, context lines, and
> per-file counts — must be built from nothing.
>
> **Scope of this document:** planning only. No `.cs`/`.csproj`/`.xaml`/DI source is created or modified.
> Source reading was used to ground the placement decisions below.

---

## 1. Tool Contract (restated from `search_files.md`)

### Name & registration
- **Name:** `search_files`
- **`max_result_size_chars = 100_000`** (feeds the dispatcher truncation / budgeting layer 2 per
  `tool_registration.md` §4).
- **Legacy aliases on `target`:** `grep` → `content`, `find` → `files`. Normalize before dispatch.

### JSON-Schema parameters (exact)

| Param | Type | Default | Required | Semantics |
|-------|------|---------|----------|-----------|
| `pattern` | string | — | **yes** | **Regex** when `target="content"`; **glob** (`*.py`, `*config*`) when `target="files"`. |
| `target` | enum `content`\|`files` | `content` | no | `content` = grep inside files; `files` = find/ls by name. Accept aliases `grep`/`find`. |
| `path` | string | `.` | no | Directory or file to search in. Resolved relative to the **workspace root** (see §2). |
| `file_glob` | string | — | no | In `content` mode, restrict which files are searched (e.g. `*.cs`). |
| `limit` | integer | `50` | no | Max results returned. |
| `offset` | integer | `0` | no | Skip first N results (pagination). |
| `output_mode` | enum `content`\|`files_only`\|`count` | `content` | no | `content` mode only: matching lines w/ line numbers, file paths only, or per-file counts. |
| `context` | integer | `0` | no | Lines of context before & after each match (`content` mode only). |

### Description string (part of the contract — copy verbatim)
> "Search file contents or find files by name. Use this instead of grep/rg/find/ls in terminal.
> Ripgrep-backed, faster than shell equivalents. …" (full text in `search_files.md` §JSON Schema).

### Return shape
- **`content` mode:**
  - `output_mode=content`: matching lines with line numbers. **≥5 matches → compact path-grouped text
    block** (group by file, lines as `line: text`) to save tokens. **<5 matches → small structured array.**
  - `output_mode=files_only`: list of file paths that matched.
  - `output_mode=count`: per-file match counts.
- **`files` mode:** file paths **sorted by modification time, newest first** (doubles as a smart `ls`).
- All modes carry a separate **diagnostics** field (see invariant 5) and an optional **truncation hint**
  and **`limit_reason`** (see invariants 1–2).

### Required invariants (from the spec)
1. **Pagination** — `offset`+`limit`; on truncation append `"showing 50 of N; pass offset=50"`.
2. **Timeout** — ~60 s cap. On timeout (rg exit 124 / our CTS) return **partial** results +
   `limit_reason="search_timeout"`. Never hang the agent.
3. **Consecutive-search loop guard (per `task_id`)** — key on the full arg tuple
   `(pattern, target, path, file_glob, limit, offset)`. **Warn at 3** identical consecutive searches,
   **block at 4**.
4. **Multiline-regex warning** — if `pattern` contains `\n`, warn the backend is line-oriented (no
   cross-line matching) so the agent doesn't get silent empty results.
5. **Diagnostics vs results separation** — rg/grep can exit non-zero (e.g. exit 2) while still producing
   valid matches. **Keep the matches; report diagnostics separately.** Never discard good output over a
   stderr warning.
6. **Path-not-found suggestions** — if `path` doesn't exist, list similar entries (same UX as `read_file`).
7. **Shell-escape `file_glob`** and every interpolated arg if a subprocess is invoked. (We pass them as
   **separate `ProcessStartInfo` argument tokens**, never a concatenated shell string — see §2/§5.)

---

## 2. Placement in Pia.Wpf (following existing conventions)

### Decision summary
| Question | Decision | Rationale |
|----------|----------|-----------|
| Extend `FilesToolHandler` or new handler? (**Q5**) | **New handler.** | No name collision (`search_files` is new); scope mismatch — search needs workspace-wide access, the files handler is sandbox-folder gated. A 5th method would entangle the two scopes and risk regressing the existing sandbox UX. |
| Native or MCP? (**Q3**) | **Build native.** | A filesystem MCP server *could* deliver search, but you'd forfeit token-compact path-grouped output, pagination, loop-guard, partial-on-timeout, and `max_result_size_chars` budgeting — all spec invariants. MCP hides stdio, so diagnostics/results separation (invariant 5) and the 60 s partial-result harness aren't expressible. |
| Approval guard? (**Q1**) | **No pending-action / ActionCard.** | `search_files` is **read-only**, like `read_file`/`list_files`. It executes immediately and returns `(result, null)`. The only security surface is **argument injection** — handled by passing args as separate tokens + workspace-root validation, not the dangerous-command guard. |
| FS scope? (**Q2**) | **Introduce a "workspace root"** distinct from the sandbox folder. | Coding tools need repo-wide access; reuse `SafeFolderPath.TryResolveInside` against the new root. |

### Proposed types & files (new, to be created in implementation phase — not in this plan)
- `Pia.Services.Interfaces.ISearchFilesToolHandler` — mirrors `IFilesToolHandler` shape **minus** the
  pending-action members (no `ExecutePendingActionAsync`): `bool IsAvailable`, `IList<AITool> GetTools()`,
  `Task<object?> HandleToolCallAsync(FunctionCallContent toolCall, string taskId, CancellationToken ct)`.
- `Pia.Services.SearchFilesToolHandler` — implementation (`src/Pia.Wpf/Services/`).
- `Pia.Services.Search.ISearchBackend` + two implementations:
  - `RipgrepSearchBackend` — shells out to `rg` (primary).
  - `DotNetSearchBackend` — pure-.NET fallback (`Directory.EnumerateFiles` + `System.Text.RegularExpressions`
    for content; `EnumerateFiles` + `LastWriteTimeUtc` sort for files).
  Abstracting the backend behind an interface keeps the handler testable without `rg` installed (see §5).
- A **workspace-root resolver** (e.g. `IWorkspaceRootProvider`) reading a new
  `AppSettings.WorkspaceRoot` (distinct from `AssistantFilesFolder`).

### Reusable patterns to follow (and the one NOT to follow)

| Pattern | Source | Apply here? |
|---------|--------|-------------|
| `GetTools()` via `AIFunctionFactory.Create` with private `…Schema` methods + `[Description]` | `FilesToolHandler.GetTools` (`FilesToolHandler.cs:72`) | **Yes** — single `search_files` tool. |
| Dispatch via `HandleToolCallAsync` switch on `toolCall.Name` | `FilesToolHandler.cs:110` | **Yes**, but returns `(result, null)` only — no pending action. |
| **Pending-action / ActionCard approval guard** | `FilesToolHandler` write/delete path | **NO** — read-only tool. Cargo-culting the write shape here is the easiest mistake. Execute immediately. |
| Path-safety via `SafeFolderPath.TryResolveInside(root, userPath, out resolved)` | `SafeFolderPath.cs:18` | **Yes**, against the **workspace root** rather than the sandbox folder. |
| Availability gating (`IsAvailable` suppresses `GetTools()` + system prompt) | `FilesToolHandler.cs:56`, `BuiltInPluginHandler.FromFilesHandler` (`BuiltInPluginHandler.cs:185`) | **Yes** — `IsAvailable` ⇔ workspace root configured & exists (`SafeFolderPath.IsConfiguredAndExists`). |
| `rg`-on-PATH detection via `where.exe` ProcessStartInfo | `PluginService.CheckCommandOnPathAsync` (`PluginService.cs:551`) | **Yes** — reuse the shape to detect `rg`; result chooses backend (see §3). |
| Subprocess stdout/stderr capture + timeout | *(none exists)* | **New** — extend the `CheckCommandOnPathAsync` ProcessStartInfo shape to redirect **both** streams with a 60 s `CancellationTokenSource` → kill → partial (§5). |
| Privacy logging | `CLAUDE.md`, `SafeLog`/`SafeUrl` | **Yes** — patterns, paths, match lines are user-content → `SensitiveDebug`; `LogInformation` for counts/durations only (§ below). |
| Size limits as named constants | `FilesToolHandler` (`MaxReadBytes` etc.) | **Yes** — `DefaultLimit=50`, `SearchTimeout=60s`, `MaxResultSizeChars=100_000`. |

### Registration / dispatch wiring (proposed steps)
Pia registers built-in tool packs as `SyncPlugin` entries dispatched through `PluginService`. To plug in:

1. **GUID** — add `SearchFilesPluginId = new("10000000-0000-0000-0000-000000000007")` to
   `BuiltInPluginDefaults` and include it in `PreloadedPluginIds`.
2. **Default entry** — add a `SyncPlugin` to `BuiltInPluginDefaults.Defaults` with
   `ConfigJson = {"handlerId":"search-files","defaultEnabled":true,"systemPromptAddition":"…"}`. The
   prompt should steer the model to use `search_files` instead of `grep`/`rg`/`find`/`ls` and explain
   `target`/`output_mode`.
3. **Adapter factory** — add `BuiltInPluginHandler.FromSearchFilesHandler(handler, config)` mirroring
   `FromFilesHandler` (`BuiltInPluginHandler.cs:185`), with `isAvailable: () => handler.IsAvailable`. Since
   there's no pending action, the adapter's pending branch is never taken (always returns `(result, null)`).
4. **InitializeBuiltInPlugins** — add a `"search-files" => BuiltInPluginHandler.FromSearchFilesHandler(...)`
   case (`PluginService.cs:79`).
5. **DI** — register `ISearchFilesToolHandler` / `IWorkspaceRootProvider` as singletons in `Bootstrapper`,
   alongside `IFilesToolHandler`.
6. **Settings reactivity** — workspace-root changes already trigger `RebuildToolNameRoutes` via the
   `SettingsChanged` subscription (`PluginService.cs:70`); ensure the handler refreshes its cached root on
   that event (same `OnSettingsChanged` pattern as `FilesToolHandler.cs:58`).

### Recommended grouping (cross-tool)
These 7 coding tools (`read_file`, `write_file`, `patch`, `search_files`, `terminal`, `process`,
`execute_code`) are a **toolset**. Rather than 7 isolated plugins each re-deriving workspace-root +
`task_id` plumbing, **stand up a shared "coding" scaffold**: one workspace-root provider, one `task_id`
threading change, one DI cluster. `search_files` is a good first consumer because it's read-only and
exercises the workspace-root + `task_id` + subprocess-harness foundations the rest depend on.

---

## 3. Backend strategy (the first hard decision)

Separate **tool availability** from **backend selection**:

- **Availability** (gates `GetTools()` + system prompt): workspace root is configured and exists.
  *Independent of whether `rg` is installed.*
- **Backend selection** (per-invocation or cached): is `rg` on PATH?

```
rg present?  ──yes──▶  RipgrepSearchBackend   (primary)
     │
     └──no───────────▶  DotNetSearchBackend    (fallback — pure .NET)
```

**Why a real .NET fallback (not a hollow "grep/find" no-op):** on Windows `grep`/`find` are **absent**
(only `findstr`, which lacks regex parity, gitignore, and mtime sort). The honest fallback is:
- **content:** `Directory.EnumerateFiles` (recursive) filtered by `file_glob`, each line tested with
  `System.Text.RegularExpressions.Regex`; replicate hidden-dir skipping + a best-effort `.gitignore`
  read. Per-line iteration gives line numbers and `context` for free.
- **files:** `Directory.EnumerateFiles` matched against the glob, **sorted by `LastWriteTimeUtc` desc** —
  this is exactly the mtime sort `FilesToolHandler.HandleListFiles` lacks.

**rg flag mapping (primary backend):**
| Need | rg flags |
|------|----------|
| content + line numbers | `--line-number` |
| files_only | `-l` |
| count | `-c` |
| context | `-C <n>` |
| restrict files | `--glob <file_glob>` |
| files mode | `--files` (+ `--sortr=modified` on rg ≥13; fall back to unsorted on older) |

**Hidden-dir + gitignore:** rg already excludes hidden dirs and respects `.gitignore` by default. **Hidden-root
exception:** if the search root is *itself* hidden (e.g. `.git`), don't filter the root out. The .NET
fallback must replicate this behavior as best it can.

**`rg` packaging:** rg is **not bundled** and **not guaranteed on PATH**. Open question (see §7): bundle
vs. document-system-install vs. fallback-only. The plan does **not** require bundling — the .NET fallback
makes the tool functional without `rg`, just slower and without gitignore fidelity.

---

## 4. Cross-cutting invariants from the overview (mapped to this tool)

| Overview principle | Relevance to `search_files` | Plan |
|--------------------|------------------------------|------|
| 1. Line-numbered reads as coordinate system | **High** — content mode emits line numbers that anchor downstream `read_file`/`patch`. | Emit `line: text`; rg `--line-number` / .NET per-line index. |
| 2. Fuzzy matching on edits | N/A (edit concern) | — |
| 3. Delta-filter diagnostics | N/A (no syntax check) — but **invariant 5** (diagnostics vs results) is the analog. | Keep stderr/exit-2 diagnostics in a separate field. |
| 4. Loop / dedup guards | **High** — invariant 3. | Per-`task_id` consecutive-search counter keyed on the arg tuple; warn@3, block@4. |
| 5. Staleness tracking | Low (read-only) | Not required; reads don't mutate. |
| 6. Return a diff / verify write | N/A | — |
| 7. Head+tail truncation | **Medium** — applies to oversized result sets. | When capping to `max_result_size_chars`, prefer last-newline truncation + truncation marker; pagination (`offset`/`limit`) is the primary control. |
| 8. Pagination everywhere | **High** — invariant 1. | `offset`/`limit` + `"showing 50 of N"` hint. |
| 9. Atomic writes / line endings / BOM | N/A (read-only) | — |
| 10. Self-healing arg validation | **High** | Normalize aliases (`grep`/`find`); coerce missing/typed args; detect `\n` in pattern → warning (invariant 4); empty/absent `pattern` → corrective error. |
| **`task_id`-keyed state** | **High** | See §4.1. |

### 4.1 `task_id` threading (Q4 — central, day-one)
**Confirmed gap:** `IPluginService.RouteToolCallAsync(FunctionCallContent, CancellationToken)`
(`IPluginService.cs:12`) and `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent, CancellationToken)`
(`IPluginToolHandler.cs:19`) thread **no** session/task id. `ChatSession.HandleToolCall`
(method header `ChatSession.cs:404`; the `RouteToolCallAsync` call site to modify is `ChatSession.cs:412`)
has the session's `Id` available but does not pass it down. Note `ChatSession.Id` is
`Guid? Id` (`ChatSession.cs:46`), not a `string`, so threading it as a `string taskId` requires
`.ToString()` and a null fallback (the `"default"` sentinel below covers the null case).

**Proposal (shared infra, affects every coding tool — belongs in tool_registration):**
- Add a `string taskId = "default"` parameter to `RouteToolCallAsync`, `IPluginToolHandler.HandleToolCallAsync`,
  and the built-in handler signatures.
- Thread `ChatSession.Id` (background-chats / multi-assistant sessions already give each session a stable
  id) as `taskId` at the call site in `ChatSession.HandleToolCall` (`ChatSession.cs:412`).
- `SearchFilesToolHandler` keys its **consecutive-search loop-guard** state on `taskId` → last
  `(pattern, target, path, file_glob, limit, offset)` tuple + count.

**The spec stresses implementing `task_id` from day one — retrofitting is painful.** Without it, the
loop-guard invariant (warn@3/block@4) cannot be honored, and parallel background sessions would share a
single global guard (false positives across concurrent chats). This change is a prerequisite for `patch`,
`terminal`, `process`, and `execute_code` too (their per-task state — cwd, mtime tracking, process
registry — all key off it).

### 4.2 Privacy logging under workspace scope (Q2 interaction)
Per `CLAUDE.md` privacy rules, with workspace-wide reach the data is **more** sensitive, not less:
- `pattern`, `file_glob`, `path`, resolved paths, and **match lines / file names** are user-content →
  log via **`SensitiveDebug`** only.
- `LogInformation` is limited to **non-sensitive metadata**: result count, match count, backend used
  (`rg`/.NET), duration, `limit_reason`. Never log full match lines or absolute paths at Information.
- Any URL that surfaces (unlikely here) → `SafeUrl.Format`.

---

## 5. Subprocess harness (second hard decision)

No existing pattern captures both streams **and** a timeout (`CheckCommandOnPathAsync` redirects stdout
only, no timeout). Plan:

- Extend the `ProcessStartInfo` shape from `PluginService.cs:551`:
  `CreateNoWindow=true`, `UseShellExecute=false`, **`RedirectStandardOutput=true`**,
  **`RedirectStandardError=true`**.
- Pass `pattern` / `path` / `file_glob` / flags as **separate `ArgumentList` tokens** — never a
  concatenated shell string. This satisfies invariant 7 (shell-escape) structurally: there is no shell.
- Read stdout and stderr **concurrently** (avoid pipe-buffer deadlock) into bounded buffers.
- Wrap in a `CancellationTokenSource(TimeSpan.FromSeconds(60))`. On cancellation **or** rg exit 124:
  `process.Kill(entireProcessTree: true)`, return **partial** stdout already captured, set
  `limit_reason="search_timeout"`.
- **Diagnostics/results separation (invariant 5):** treat stdout as results regardless of exit code;
  capture stderr + exit code into a separate `diagnostics` field. rg exit 2 with valid matches → keep the
  matches.
- **Path-not-found (invariant 6):** before launching, if resolved `path` doesn't exist, enumerate sibling
  entries and suggest similar names (same UX as `read_file`); don't launch the subprocess.

---

## 6. Build / implementation checklist

- [ ] Add `AppSettings.WorkspaceRoot` (nullable) + `IWorkspaceRootProvider`; reactive on `SettingsChanged`.
- [ ] Thread `taskId` (default `"default"`) through `RouteToolCallAsync` + `IPluginToolHandler.HandleToolCallAsync`
      + built-in handler signatures; pass `ChatSession.Id` at `ChatSession.cs:412`. *(Shared coding-tools infra.)*
- [ ] `ISearchFilesToolHandler` + `SearchFilesToolHandler` (read-only; returns `(result, null)`, no ActionCard).
- [ ] `GetTools()` exposes single `search_files` via `AIFunctionFactory.Create` with the exact contract description.
- [ ] Arg normalization / self-healing: alias `grep`→`content`, `find`→`files`; default `target=content`,
      `output_mode=content`, `path=.`, `limit=50`, `offset=0`, `context=0`; missing/empty `pattern` → corrective error.
- [ ] Multiline-regex warning when `pattern` contains `\n`.
- [ ] `ISearchBackend` abstraction; `RipgrepSearchBackend` (rg flags per §3) + `DotNetSearchBackend` (pure .NET).
- [ ] `rg`-on-PATH detection (reuse `where.exe` shape); backend selection independent of availability.
- [ ] Subprocess harness: dual-stream capture, 60 s CTS → kill → partial + `limit_reason="search_timeout"`.
- [ ] Hidden-dir + gitignore handling with hidden-root exception (rg native; .NET best-effort).
- [ ] `target="files"` mtime sort (`--sortr=modified` / `LastWriteTimeUtc` desc).
- [ ] Output rendering: `content` (compact path-grouped block for ≥5 matches; small array for <5),
      `files_only`, `count`.
- [ ] Pagination: `offset`/`limit` + truncation hint `"showing N of M; pass offset=N"`.
- [ ] Per-`task_id` consecutive-search loop guard (arg-tuple key; warn@3, block@4).
- [ ] Diagnostics/results separation (keep matches on non-zero exit; diagnostics field).
- [ ] Path-not-found similar-entry suggestions.
- [ ] `max_result_size_chars = 100_000`; head+tail/last-newline truncation marker when capped.
- [ ] Privacy logging: `SensitiveDebug` for pattern/paths/matches; `LogInformation` for counts/duration/backend only.
- [ ] Registration wiring: GUID, `BuiltInPluginDefaults` entry, `FromSearchFilesHandler`, `InitializeBuiltInPlugins`
      case, `Bootstrapper` DI singletons, `systemPromptAddition`.

---

## 7. Test strategy (matches repo: xunit.v3, no FluentAssertions)

> Repo memory: tests run **xunit.v3 + plain `Xunit.Assert`** (MTP via `global.json`); **FluentAssertions
> was removed**. New `.cs` test files must be **CRLF**. Place tests in `tests/Pia.Wpf.Tests/`.

**Backend abstraction enables rg-free unit tests.** Inject a fake `ISearchBackend` so the handler's pure
logic is tested without `rg` installed.

| Area | Test (pure / deterministic) |
|------|------------------------------|
| Alias normalization | `target="grep"` → content; `target="find"` → files; default `content`. |
| Self-healing args | missing `pattern` → corrective error; defaults applied (`limit=50`, `offset=0`, `context=0`). |
| Multiline warning | `pattern` containing `\n` → warning emitted; results not silently dropped. |
| Output formatting | ≥5 matches → compact path-grouped block; <5 → small array; `files_only`; `count`. |
| Pagination | `offset`/`limit` slicing; truncation hint text exact (`"showing 50 of N; pass offset=50"`). |
| Loop guard | same arg tuple repeated under one `taskId` → warn@3, block@4; **different `taskId` does not interfere** (concurrent-session isolation). |
| Path-not-found | nonexistent `path` → similar-entry suggestions, no subprocess launched. |
| Diagnostics separation | backend returns matches + non-zero exit/stderr → matches retained, diagnostics in separate field. |
| Files mtime sort | `target="files"` returns newest-first (fake backend with known timestamps). |
| Path safety | `path` escaping workspace root via `..` → rejected by `TryResolveInside`. |

| Area | Test (integration — gated) |
|------|----------------------------|
| Real rg | `[Fact(Skip=…)]` unless `rg` on PATH; verify flag mapping + gitignore/hidden-dir behavior over a temp fixture tree. |
| .NET fallback | force fallback backend over a temp fixture tree; verify content matches, line numbers, context, mtime sort. |
| Timeout | simulate a long-running backend → 60 s CTS → partial + `limit_reason="search_timeout"`. |

---

## 8. Open questions

1. **rg packaging** — bundle a pinned `rg.exe` with the app (best fidelity, ships a binary — weighed
   against the user's *minimal-dependency* preference), depend on a system-installed `rg`, or ship
   fallback-only and treat `rg` as an optional speedup? Recommend: **fallback-first, rg-if-present**; revisit
   bundling if gitignore fidelity proves necessary.
2. **Workspace root model** — single `AppSettings.WorkspaceRoot`, or per-conversation root, or "current
   git repo" auto-detection? Does it coexist with `AssistantFilesFolder` or supersede it for coding mode?
3. **`task_id` rollout** — land the signature change as standalone shared infra (touches `IPluginService`,
   `IPluginToolHandler`, all 6 existing built-in handlers + MCP handler) **before** any coding tool, or
   introduce it alongside `search_files` as the first consumer? (Retrofit cost grows per tool added.)
4. **Toolset enablement UX** — should coding tools be a single user-toggleable "coding" pack (one
   `SyncPlugin`/system prompt) rather than 7 independent plugins? Affects GUID allocation and `ConfigJson`.
5. **`.gitignore` in .NET fallback** — full gitignore parsing is non-trivial; how faithful must the
   fallback be (root `.gitignore` only vs. nested vs. global)? Acceptable to under-match here?
6. **Binary-file handling** — rg skips binaries by default; should the .NET fallback detect/skip binary
   files (NUL-byte heuristic) to avoid garbage matches?
7. **Result persistence (budgeting layer 2)** — does Pia's dispatcher persist oversized results to a temp
   file + `<persisted-output>` preview (per `tool_registration.md` §4), or only inline-truncate? Affects
   how `max_result_size_chars` interacts with pagination.
