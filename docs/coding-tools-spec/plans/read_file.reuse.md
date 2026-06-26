# Plan: `read_file` coding tool — MODIFICATION INSTRUCTIONS (bucket = reuse)

> Status: PLANNING ONLY. No C# / DI / csproj / xaml changes in this task. This doc tells the
> implementer exactly what to change and why, grounded in both the spec
> (`docs/coding-tools-spec/read_file.md`) and the live Pia codebase.

## TL;DR — the spine

- **Reuse the plumbing**, not the path model. The existing `FilesToolHandler` already ships the
  exact scaffolding (`AIFunctionFactory` schema pattern, the `(result, pending)` dispatch tuple,
  `GetStringArg`, `SensitiveDebug` privacy logging, the full
  `BuiltInPluginHandler → PluginService → Bootstrapper → BuiltInPluginDefaults` registration chain).
- **Build the coding `read_file` as a NEW handler/pack alongside** the assistant-files pack. Do **not**
  edit the shipping `FilesToolHandler.HandleReadFile` in place, and do **not** relax
  `SafeFolderPath.TryResolveInside`. Those are load-bearing for the shipping `write_file`/`delete_file`
  in the same handler.
- **Introduce a new workspace-root resolver** alongside `SafeFolderPath` (absolute + relative-to-cwd +
  `~/` expansion, confined to a workspace root). Leave `SafeFolderPath` untouched.
- **Resolve the tool-name collision first** (`read_file` is registered by name; two packs cannot both
  own it). This is a forced architectural decision, not a footnote.
- **Thread `task_id` on day one** through dispatch (spec is emphatic; retrofit is painful). This touches
  every built-in adapter — a real, named regression surface.

---

## 1. The exact tool contract (from the spec)

### Schema

```json
{
  "name": "read_file",
  "parameters": {
    "type": "object",
    "properties": {
      "path":   {"type": "string",  "description": "absolute, relative (to session cwd), or ~/path"},
      "offset": {"type": "integer", "default": 1, "minimum": 1},
      "limit":  {"type": "integer", "default": 500, "maximum": 2000}
    },
    "required": ["path"]
  }
}
```
Registered with `max_result_size_chars = 100_000`.

### Output format

One line per source line, **`LINE_NUM|CONTENT`** — pipe separator, **no padding**, 1-indexed. The line
numbers are the coordinate system `patch` anchors and `file:line` citations rely on.

```
1|import os
2|
3|def main():
```

### Return shape (conceptual)

A dict the dispatcher serializes (or a preformatted string) with minimum fields:
`content`, `total_lines`, `offset`, `limit`. On not-found: an error string **plus ranked suggestions**.

### Required behaviors / invariants

| # | Invariant |
|---|-----------|
| 1 | Binary detection **before** read (ext + content sniff). Images → redirect to a vision tool. |
| 2 | Device-path blocklist (`/dev/*`, `/proc/*/fd/*`, `/proc/*/environ`, …) — host-OS dependent. |
| 3 | UTF-8 BOM stripping (model never sees a phantom `U+FEFF`; write/patch restore it on disk). |
| 4 | Structured-doc extraction: `.ipynb` / `.docx` / `.xlsx` → readable text rather than failing as binary. |
| 5 | File-not-found → scan target dir, return ranked similar names (exact basename > prefix > substring). |
| 6 | Read-dedup guard per `task_id`: cache `(resolved_path, offset, limit) -> mtime`; same key + unchanged mtime → short stub; hard block after ~2 stubs on a key. |
| 7 | Consecutive-identical-read loop guard: warn at 3, block at 4. |
| 8 | Staleness bookkeeping: store mtime-at-read per `task_id`+path; **`write_file`/`patch` consume it**. |
| 9 | `reset_read_dedup(task_id)` hook — host calls after context compression so re-reads are legitimate. |
| 10 | Large-file hint: file > ~512KB and no narrow offset/limit → include a pagination hint (NOT a hard reject). |

---

## 2. What already exists (verified against the codebase)

All paths below were read and confirmed; line references are current as of this writing.

| Asset | File | What it does today | Reuse verdict |
|-------|------|--------------------|---------------|
| `FilesToolHandler` | `src/Pia.Wpf/Services/FilesToolHandler.cs` | Hosts `list_files` / `read_file` / `write_file` / `delete_file` over a configured sandbox folder. `read_file` (`HandleReadFile`, lines 177–205) does `SafeFolderPath.TryResolveInside` → `File.Exists` → 256KB byte cap → `File.ReadAllText` → returns **bare string**. | Reuse as a **pattern template**; do not edit `HandleReadFile`. |
| Dispatch tuple | `FilesToolHandler.HandleToolCallAsync` (lines 92–119) | `switch` on `toolCall.Name`; `read_file`/`list_files` execute immediately (`(result, null)`); `write_file`/`delete_file` return `(null, pending)`. | Reuse shape verbatim — read is immediate, no approval. |
| Schema pattern | `ReadFileSchema` (lines 312–314) + `AIFunctionFactory.Create` (lines 81–82) | Private static method with `[Description]` params; `AIFunctionFactory` reflects the signature into tool metadata. | Reuse pattern; new schema adds `offset`/`limit`. |
| Arg parsing | `GetStringArg` (325–336), `GetOptionalStringArg` (338–353) | Unwraps `JsonElement` / string args. | Reuse; needs an int variant for `offset`/`limit`. |
| Privacy logging | `_logger.SensitiveDebug("read_file path: {Path}", requested)` (line 197) + `LogInformation`/`LogWarning` | Paths are `SensitiveDebug` (stripped in RELEASE); counts/sizes are normal. | Reuse pattern — mandatory for the new handler. |
| Availability gate | `IsAvailable => _currentFolder is not null` (line 56) | Suppresses `GetTools()` and system prompt when no sandbox folder. | Reuse the **mechanism** (gated availability), gate the coding pack on a configured **workspace root** instead. |
| Pending-action record | `FilesToolCall` (`IFilesToolHandler.cs` 5–10) + `ExecutePendingActionAsync` (121–135) | Deferred `Execute` lambda for write/delete with re-validation + try/catch. | Not needed for `read_file` (immediate). Relevant later for `write_file`/`patch`. |
| Path safety | `SafeFolderPath.TryResolveInside` (`Infrastructure/SafeFolderPath.cs` 18–49) | **Rejects** rooted/absolute/UNC paths (line 27), `..` escapes, invalid chars; confines to a sandbox root. | **Do NOT modify.** Build a new resolver alongside (see §4.1). |
| Adapter factory | `BuiltInPluginHandler.FromFilesHandler` (`Services/Plugins/BuiltInPluginHandler.cs` 185–202) | Wraps `IFilesToolHandler` as `IPluginToolHandler`, gates on `handler.IsAvailable`. | Reuse pattern → add `FromCodingFilesHandler` (or equivalent). |
| Registry/dispatch | `PluginService.RouteToolCallAsync` (`Services/Plugins/PluginService.cs` 265–284) | Looks up handler by **tool name** in `_toolNameRoutes`; calls `HandleToolCallAsync(toolCall, ct)`. **No task_id threaded.** | Reuse; must extend signature for task_id (§4.3) and resolve name collision (§3). |
| Built-in registration | `PluginService.InitializeBuiltInPlugins` (`Services/Plugins/PluginService.cs` 73–94) | Maps `handlerId` → factory; `"files" => FromFilesHandler(...)`. | Reuse; add a new `handlerId` branch. |
| DI registration | `Bootstrapper.cs` line 250 | `services.AddSingleton<IFilesToolHandler, FilesToolHandler>();` | Reuse pattern; register the new handler the same way. |
| Defaults / system prompt | `Services/Plugins/BuiltInPluginDefaults.cs` (`FilesPluginId` 16, files entry 84–95) | Well-known GUID + `ConfigJson` with `systemPromptAddition`. | Reuse pattern; new GUID + new prompt for the coding pack. |

**Key fact verified:** `RouteToolCallAsync` (PluginService.cs:271) routes purely on `toolCall.Name`, and
`HandleToolCallAsync` receives only `(FunctionCallContent, CancellationToken)`. `ChatSession.Id` exists
but dies at the session boundary — it is **not** threaded into handlers today. This is the task_id gap.

---

## 3. Tool-name collision — DECIDE THIS FIRST (forced by architecture)

`_toolNameRoutes` is a `Dictionary<string, IPluginToolHandler>` keyed by tool name
(`PluginService.cs:271`). If both the shipping files pack and a new coding pack register `read_file`,
**one silently shadows the other** (last `RegisterHandler` wins). The implementer cannot ignore this.

Pick one (recommend **Option B**):

- **Option A — Supersede.** The coding pack *replaces* `read_file` for everyone; deprecate the files
  pack's `read_file`. Simplest routing, but regresses the existing assistant-files UX (relative-only,
  sandbox-folder semantics, user-facing system prompt) and the contract differs (line numbers, dict
  return). High blast radius on a shipping feature.
- **Option B — Mutual exclusion (recommended).** When a **workspace root** is configured/active, the
  coding files pack is available and the assistant-files `read_file` is suppressed (and vice-versa),
  via the existing `isAvailable` lambda mechanism (`BuiltInPluginHandler` line 42). Only one pack ever
  contributes `read_file` to `GetTools()` for a given turn, so no route collision. Cleanest reuse of
  the existing availability gate; keeps both UXes intact in their own contexts.
- **Option C — Rename.** Coding tool registers as e.g. `read_code_file`. Avoids collision mechanically
  but **violates the spec** (the model is steered to `read_file` explicitly as the cat/head/tail
  replacement) and splits the namespace confusingly. Not recommended.

> Whichever is chosen, document it in the pack's system prompt so the model knows which `read_file`
> contract is live. With Option B, the two prompts are never active simultaneously.

---

## 4. Gap analysis

| # | Spec requirement | Current behavior | Needed change |
|---|------------------|------------------|---------------|
| G1 | `path` accepts absolute / relative-to-cwd / `~/` | `SafeFolderPath` **rejects** rooted/UNC; relative-to-sandbox only | New workspace-root resolver (§4.1). Do not touch `SafeFolderPath`. |
| G2 | Workspace/repo-wide scope | Gated on a single configured sandbox folder | Introduce a **workspace root** concept + availability gate (§4.1). |
| G3 | `LINE_NUM\|CONTENT`, no padding, 1-indexed | Returns raw `File.ReadAllText` | Format output line-by-line in the read path (§4.2). |
| G4 | `offset` (default 1) / `limit` (default 500, max 2000) | No params; whole file | Add `offset`/`limit` to schema + read window logic (§4.2). |
| G5 | 100K-char output cap (reject w/ guidance) + 2000-line cap | 256KB **byte** cap, hard error | Replace caps with line/char caps + narrowing guidance (§4.2). |
| G6 | Return dict `{content, total_lines, offset, limit}` | Bare string | Return structured object (or preformatted string with the same fields) (§4.2). |
| G7 | Binary detection before read; image → vision redirect | None | Pre-read ext + content sniff (§4.4). |
| G8 | Device-path blocklist | None (Windows-first; lower priority) | Cheap guard; low Windows relevance — see §4.4 / open Q. |
| G9 | UTF-8 BOM stripping | None | Strip leading BOM after decode (§4.2). |
| G10 | Structured-doc extraction (`.ipynb`/`.docx`/`.xlsx`) | **`.docx`/`.xlsx` already extracted** by `Helpers/DroppedFileReader.cs` (`ReadDocxAsync`/`ReadXlsxAsync`, via the already-shipping `DocumentFormat.OpenXml`); `.ipynb` none | Reuse `DroppedFileReader` for `.docx`/`.xlsx`; add only `.ipynb` (JSON) (§4.5). |
| G11 | Not-found → ranked suggestions | `"Error: File '...' not found."` | Scan dir, rank by basename match (§4.4). |
| G12 | Per-`task_id` dedup cache + stub/block escalation | None (and no task_id threaded) | Thread task_id (§4.3) + per-task cache (§4.6). |
| G13 | Consecutive-identical-read loop guard | None | Per-task counter (§4.6). |
| G14 | mtime-at-read staleness store (shared w/ write/patch) | None | **Shared cross-tool state** service (§4.6). |
| G15 | `reset_read_dedup(task_id)` hook | None | Host hook called post-compression (§4.6). |
| G16 | Large-file >512KB pagination hint (not reject) | Hard 256KB reject | Soft hint when no narrow window (§4.2). |

---

## 4.1 New workspace-root resolver (G1, G2) — do NOT modify `SafeFolderPath`

Create a **new** static resolver alongside `SafeFolderPath` (e.g. `WorkspacePath` in `Infrastructure/`).
Rationale: `SafeFolderPath.TryResolveInside` rejecting rooted paths (line 27) is consumed by the
shipping `write_file`/`delete_file` in `FilesToolHandler`. Relaxing it to satisfy the spec's
absolute/`~`/cwd requirement would silently gut their sandbox. Keep them isolated.

The new resolver must, given a configured **workspace root** + a session **cwd**:

- [ ] Accept **absolute** paths (and confine them to the workspace root — reject if outside).
- [ ] Resolve **relative** paths against the session **cwd** (not the workspace root directly).
- [ ] Expand a leading `~/` (and `~`) to the user profile dir, then re-confine to the workspace root.
- [ ] Normalize (`Path.GetFullPath`) and reject `..` escapes the same way `SafeFolderPath` does
      (reuse the trailing-separator `StartsWith` containment trick from `SafeFolderPath` lines 35–44).
- [ ] Reject null chars / invalid path chars (same as `SafeFolderPath` 28–29).

**Privacy-logging compliance:** the resolved/requested path is sensitive. Log rejections with a
non-sensitive `LogWarning` plus a `SensitiveDebug` for the path — exactly as `HandleReadFile` does at
lines 182–183. Never log the full path at `Information`+.

**Workspace root source (open decision — see §7):** likely a new `AppSettings` field (e.g.
`AssistantWorkspaceFolder`) parallel to `AssistantFilesFolder`, wired through `ISettingsService`
`SettingsChanged` exactly like `_currentFolder` (FilesToolHandler 58–70). The availability gate then
mirrors `IsAvailable` (line 56): coding pack is live only when the workspace root is configured + exists.

---

## 4.2 Read window, format, caps, BOM, hint (G3–G6, G9, G16)

In the new handler's read method (model after `HandleReadFile`, but new code):

- [ ] Add `offset` (1-indexed, default 1, min 1) and `limit` (default 500, max 2000, clamp) to the schema
      method and to arg parsing. Add an **int arg parser** alongside `GetStringArg` (unwrap `JsonElement`
      number / string-number; fall back to defaults).
- [ ] Read the file as **UTF-8**, **strip a leading BOM** (`U+FEFF`) if present before any line work.
- [ ] Compute `total_lines` over the whole file (cheap: split once or count newlines).
- [ ] Slice the window `[offset, offset+limit)` (1-indexed). Out-of-range offset → empty content +
      correct `total_lines` (let the model page back).
- [ ] Emit each window line as `"{n}|{text}"` (pipe, **no padding**, `n` = absolute 1-indexed line number).
- [ ] Enforce the **100K-char output cap** on the *formatted* window. If exceeded, **reject** with guidance
      to narrow via `offset`/`limit` (do not silently truncate — the model needs to know).
- [ ] **Large-file hint (G16):** if file > ~512KB and caller passed the defaults (no narrow window),
      include a hint string suggesting pagination — but still return the windowed content. This replaces
      the existing hard 256KB byte reject (G5); the coding tool must not hard-fail large files.
- [ ] **Return shape:** prefer the structured dict `{content, total_lines, offset, limit}` so the model
      can page intelligently. If returning a preformatted string instead, it must still surface
      `total_lines`/`offset`/`limit` (the spec allows either). See regression note §6.4 — the read result
      goes straight to the model; confirm nothing downstream parses the old bare-string shape.

---

## 4.3 task_id threading (G12, G14, G15) — day-one, names the blast radius

The spec is emphatic that `task_id` lands on day one because retrofitting is painful. Verified gap:
`RouteToolCallAsync` (PluginService.cs:265–284) and `IPluginToolHandler.HandleToolCallAsync` receive only
`(FunctionCallContent, CancellationToken)`. `ChatSession.Id` exists but is not threaded in.

Required signature changes (describe, do not implement here):

- [ ] Thread a `string taskId` (or `Guid`) — sourced from `ChatSession.Id` — through:
  - `ChatSession.HandleToolCall` → `PluginService.RouteToolCallAsync(toolCall, taskId, ct)`
  - `IPluginToolHandler.HandleToolCallAsync(toolCall, taskId, ct)`
  - The new coding handler's `HandleToolCallAsync`.
- [ ] **Blast radius (regression risk — name it):** the `IPluginToolHandler` signature change ripples
      through **every** `BuiltInPluginHandler.FromXxxHandler` factory — memory, todo, reminder,
      scheduled-research, research-history, files (`BuiltInPluginHandler.cs` 77–202) — plus
      `McpPluginToolHandler`. Each currently ignores task_id; they can accept-and-discard, but the
      signature must be updated everywhere it implements/calls the interface. This is exactly the
      "painful retrofit" the spec warns about — doing it now is cheaper.
- [ ] Alternative to a hard interface change (lower blast radius): pass task_id via an `AsyncLocal`
      ambient context (Pia already uses this pattern for `TokenMapAmbient.Current` during `RunTurnAsync`).
      The coding handler reads ambient task_id; other handlers stay untouched. **Trade-off:** less explicit,
      but zero churn on the five unrelated adapters. Flag both; recommend the explicit signature for the
      handler that needs it most (read/write/patch) and note ambient as the pragmatic fallback. **Decide
      in implementation review** — this is a cross-cutting `tool_registration` concern, not local to
      `read_file`.

---

## 4.4 Binary / device / not-found (G7, G8, G11)

- [ ] **Binary detection before read (G7):** check extension blocklist (images, archives, executables,
      media) first; then content-sniff (e.g. NUL byte in the first N KB) for unknown extensions. On a
      detected image, return a message redirecting to the vision tool (`vision_analyze` per spec) instead
      of dumping bytes. Cheap, no dependency.
- [ ] **Device-path blocklist (G8):** Windows-first, so `/dev/*` and `/proc/*` are largely irrelevant
      here. Implement a minimal guard for parity and revisit if a POSIX target appears (see open Q §7).
      Do not over-invest.
- [ ] **Not-found suggestions (G11):** on miss, enumerate the target directory and rank candidates —
      exact basename match > prefix match > substring match. Reuse `Directory.EnumerateFiles` (as
      `HandleListFiles` does, FilesToolHandler 144–148) and the `MaxListEntries`-style truncation. Return
      the error string **plus** the top few ranked names so the model doesn't waste a turn.

---

## 4.5 Structured-doc extraction (G10) — mostly already in the codebase

This is **not** a fresh minimal-deps tension: Pia already extracts the two hard formats.

- **`.docx` / `.xlsx` — reuse `Helpers/DroppedFileReader.cs`.** `DocumentFormat.OpenXml` already ships as a
  `PackageReference` (`src/Pia.Wpf/Pia.Wpf.csproj`), and `DroppedFileReader` already extracts `.docx`
  (`ReadDocxAsync`, paragraph text via `WordprocessingDocument`) and `.xlsx` (`ReadXlsxAsync`, TSV with
  shared-string / inline-string resolution via `SpreadsheetDocument`). It also has BOM-detecting text reads
  (`ReadTextAsync`) and a `FileKind` classifier (`Classify`) that partly serves invariant 1 (binary/image
  sniff). **No new dependency, no hand-rolling** — call `DroppedFileReader` from the new handler.
- **`.ipynb` — the only net-new piece.** Plain JSON; extract markdown + code cells (and optionally outputs)
  with `System.Text.Json`. **No new dependency.**

> One caveat to confirm: `DroppedFileReader` was built for chat-attachment ingestion and returns
> whole-document text. Verify its output composes with `read_file`'s line-numbered/windowed contract
> (extracted text can still be line-numbered + paginated, but check large-sheet truncation behavior).

---

## 4.6 Dedup, loop guard, staleness, reset (G12–G15) — shared cross-tool state

These are **not local to `read_file`**. The mtime-at-read store is consumed by `write_file`/`patch`.
Model them as a small **shared service** (e.g. `IReadStateStore`), keyed by `task_id`:

- [ ] **Dedup cache (G12):** `(taskId, resolvedPath, offset, limit) -> mtime`. On a repeat read with
      unchanged mtime, return a short stub ("unchanged since last read"). After ~2 stubs on the same key,
      hard-block with a pointed message.
- [ ] **Consecutive-read loop guard (G13):** per-task counter of back-to-back identical reads; warn at 3,
      block at 4.
- [ ] **mtime-at-read staleness (G14):** record mtime per `(taskId, path)` at read time. `write_file` /
      `patch` (future tools) read this to warn if the file changed since the agent last saw it. Design the
      store now so those tools can consume it — this is the cross-tool contract the spec calls out.
- [ ] **`reset_read_dedup(task_id)` (G15):** expose a host hook the chat loop calls after context
      compression, so legitimately-evicted content can be re-read in full. Natural call site: wherever
      Pia summarizes/compresses turn history (confirm there is one; if not, note it as a dependency).

> **Why NOT an MCP filesystem server for this (answers Q3 for read_file specifically):** a generic shell
> or filesystem MCP server cannot hold per-`task_id` dedup state nor share mtime-at-read with Pia's own
> `write_file`/`patch`. The cross-tool, per-task state contract (G6/G8/G12–G15) is the reason `read_file`
> must be **native**. Other coding tools (terminal/process) may still be candidates for MCP delegation,
> but `read_file` is not.

---

## 5. Ordered modification instructions (implementation sequence)

1. **Resolve the name collision (§3).** Decide Option B (mutual-exclusion via `isAvailable`). This frames
   everything else. (registry/dispatch concern, do before coding.)
2. **Add workspace-root config + resolver (§4.1).** New `AppSettings` field, `SettingsChanged` wiring,
   new `WorkspacePath` resolver alongside `SafeFolderPath` (untouched).
3. **Thread `task_id` (§4.3).** Cross-cutting; do early so the new handler is built against the final
   signature (the whole point of "day one"). Update the interface + all adapters, or choose ambient.
4. **Create the new coding files handler** (new `*ToolHandler` + interface), modeled on `FilesToolHandler`
   but with the workspace resolver, line-numbered windowed read, structured return, BOM strip, binary
   guard, not-found suggestions. Reuse the `(result, pending)` dispatch tuple and `SensitiveDebug` logging.
5. **Add the shared read-state store (§4.6)** (`IReadStateStore`) — dedup cache, loop guard, mtime store,
   `reset_read_dedup`. Inject into the handler.
6. **Register the pack:** new GUID + `ConfigJson`/system prompt in `BuiltInPluginDefaults`; new
   `FromCodingFilesHandler` adapter in `BuiltInPluginHandler`; new `handlerId` branch in
   `PluginService.InitializeBuiltInPlugins`; DI singleton in `Bootstrapper`. (All mirror the files pack.)
7. **Reuse `DroppedFileReader` for `.docx`/`.xlsx` (§4.5);** add only `.ipynb` JSON extraction.
8. **Privacy pass:** confirm every path/content log is `SensitiveDebug` or wrapped; counts/sizes only at
   `Information`.

> Out of scope for `read_file` (read-only tool): **Q1 code-exec consent** and **Q6 Python runtime** — see
> the `terminal` / `execute_code` planning docs. One line here: `read_file` runs immediately with no
> approval card (matches the shipping `read_file` semantics) and touches no process/runtime.

---

## 6. Regression risks to existing sandbox / UX

1. **DO NOT relax `SafeFolderPath` (highest risk).** If anyone "just adds absolute-path support" to
   `TryResolveInside` to satisfy G1, the shipping `write_file`/`delete_file` in `FilesToolHandler`
   immediately lose their sandbox (they re-validate through the same method at execution time, lines
   235/280). The mitigation is the entire reason for the new resolver in §4.1.
2. **Tool-name collision (§3).** Registering a second `read_file` without the mutual-exclusion gate
   silently breaks the shipping assistant-files `read_file` (route shadowing in `_toolNameRoutes`).
3. **task_id signature change (§4.3).** Touches all six `BuiltInPluginHandler.FromXxxHandler` factories +
   `McpPluginToolHandler` + `RouteToolCallAsync`. A missed call site is a compile break (good) but a
   missed *semantic* (passing the wrong id) is a silent cross-session state leak in dedup/staleness.
   Mitigation: prefer compile-enforced signature over stringly-typed ambient if churn is acceptable.
4. **Return-shape change (bare string → dict).** Low risk because the read result flows straight to the
   model (no UI parse), but **verify** nothing downstream (logging, `ActionCardBuilder`, tests) parses the
   old `read_file` string. Since this is a *new* handler under mutual exclusion, the shipping `read_file`
   string contract is untouched — keep it that way.
5. **Availability gate interplay.** If both `AssistantFilesFolder` and the new workspace root are
   configured, the mutual-exclusion rule (§3) must be deterministic about which pack wins, or both
   `read_file` routes register and one shadows the other. Make the precedence explicit in the gate.

---

## 7. Open questions

1. **Privacy posture on workspace scope.** Should the coding tools be confined to a configured
   **workspace root** (privacy-first, recommended), or operate on the unrestricted real filesystem like
   Claude Code (which the spec's absolute-path contract implies)? This is a product decision the plan
   should not silently resolve. Recommendation leans confined-workspace; needs sign-off.
2. **Workspace root vs. existing sandbox folder.** New `AppSettings.AssistantWorkspaceFolder`, or repurpose
   `AssistantFilesFolder`? Repurposing risks conflating two different UX contracts (assistant files vs.
   coding workspace). Parallel field is cleaner but adds settings surface.
3. **task_id mechanism:** explicit interface signature change vs. `AsyncLocal` ambient (per Pia's existing
   `TokenMapAmbient` pattern). Decide in implementation review based on tolerated churn.
4. **`reset_read_dedup` call site.** Does Pia have a context-compression / summarization step to hook? If
   not, G15 has no caller and the dedup cache needs an alternative eviction (e.g. TTL or turn-count).
5. **Structured-doc reuse.** `.docx`/`.xlsx` are already extracted by `Helpers/DroppedFileReader.cs`
   (`DocumentFormat.OpenXml`, already shipping) — reuse it rather than building a parallel extractor; only
   `.ipynb` (JSON) is net-new. Confirm `DroppedFileReader`'s whole-document output is compatible with the
   line-numbered/windowed read contract (it was written for attachment ingestion, not paginated reads).
6. **Device-path blocklist relevance.** Windows-first today; is a POSIX target planned? If not, G8 stays a
   minimal parity guard.
7. **Vision redirect target.** Spec names `vision_analyze`; confirm Pia's actual image/vision tool name so
   the binary-image redirect message points somewhere real.
