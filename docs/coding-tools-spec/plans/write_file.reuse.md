# Plan: `write_file` coding tool — REUSE (extend `FilesToolHandler` in place)

**Classification:** `bucket = reuse`. Pia already ships a working `write_file` tool with the
exact spec name and the exact required schema (`path`, `content`). The work is **additive**:
layer the spec's richer behaviors onto the existing pending-action path. Do **not** build a
parallel tool.

> Scope note: this is a planning doc. No C# / `.csproj` / XAML / DI changes are made here.
> Everything below is a description precise enough to implement.

---

## 1. Tool contract (from the spec)

Source: `docs/coding-tools-spec/write_file.md`.

**Purpose:** write content to a file, completely replacing existing content. Creates parent
dirs. For *targeted* edits the model should use `patch` instead.

**Schema (exact):**

```json
{
  "name": "write_file",
  "parameters": {
    "type": "object",
    "properties": {
      "path":    {"type": "string", "description": "Path to the file (created if missing, overwritten if present)"},
      "content": {"type": "string", "description": "Complete content to write to the file"},
      "cross_profile": {"type": "boolean", "default": false}
    },
    "required": ["path", "content"]
  }
}
```

- Registered with `max_result_size_chars = 100_000` (a **result** cap, distinct from the
  existing 512 K **input-content** cap).
- `cross_profile` is Hermes-specific multi-profile config-safety. **Pia has no multi-profile
  concept → DROP this parameter.** Final Pia schema is exactly `{path, content}` (unchanged
  from today).
- **No `offset` / `limit` / `line_numbers` params.** Those are `read_file` / `patch`
  concerns. `write_file` is a whole-file overwrite; its richness lives in **behavior and
  return shape**, not in new parameters.

**Return shape (conceptual):** structured object, not a bare string.

| Field             | Meaning                                                         |
|-------------------|----------------------------------------------------------------|
| `success`         | bool                                                           |
| `resolved_path`   | path written (Pia: sandbox-relative, per existing convention) |
| `bytes_written`   | byte count actually written                                   |
| `lines`           | line count of written content                                 |
| `lint`            | NEW-errors-only post-write syntax check, or `null`            |
| `lsp_diagnostics` | optional semantic diagnostics (separate field) — out of scope |
| `_warning`        | staleness / workspace-divergence (non-blocking)               |
| `error`           | present on failure                                            |

**Invariants (spec §"Required behaviors"):** 1 atomic write; 2 mkdir -p; 3 preserve line
endings; 4 preserve BOM; 5 stream large content (N/A — native FS APIs); 6 delta-filtered
post-write syntax check (headline feature); 7 sensitive-path blocklist; 8 staleness warning;
9 workspace-divergence warning; 10 internal-content guard; 11 missing/non-string `content`
self-healing.

---

## 2. What already exists (verified against the codebase)

| File | Class / member | Current behavior |
|------|----------------|------------------|
| `src/Pia.Wpf/Services/FilesToolHandler.cs` | `PrepareWriteFile(root, args)` (lines 207–257) | Builds a `FilesToolCall` whose `Execute` lambda does `File.WriteAllText(finalPath, content)` (line 244). Validates path via `SafeFolderPath.TryResolveInside` at prepare **and** execute time. Auto-creates parent dir (lines 240–242). Enforces `MaxWriteChars = 512K` (line 216). Returns a **bare string** `"File 'x' created/updated."` |
| `src/Pia.Wpf/Services/FilesToolHandler.cs` | `GetTools()` (lines 72–90) | Registers `write_file` via `AIFunctionFactory.Create(WriteFileSchema, …)`; gated on `IsAvailable`. |
| `src/Pia.Wpf/Services/FilesToolHandler.cs` | `WriteFileSchema` (lines 316–319) | `[Description]`-annotated signature `(string path, string content)`. AIFunctionFactory reflects this. |
| `src/Pia.Wpf/Services/FilesToolHandler.cs` | `GetStringArg` (lines 325–336) | Returns `string.Empty` for a **missing** key — silently turns a dropped `content` arg into an empty-file write. Coerces non-string JSON via `GetRawText()` / `ToString()`. |
| `src/Pia.Wpf/Services/FilesToolHandler.cs` | `HandleToolCallAsync` (lines 92–119) | Switch dispatch; `write_file` → `(null, PrepareWriteFile(...))`. Receives `FunctionCallContent` + `CancellationToken` — **no task/session id**. |
| `src/Pia.Wpf/Services/FilesToolHandler.cs` | `ExecutePendingActionAsync` (lines 121–135) | Runs the deferred `Execute` lambda after user confirmation; wraps exceptions into an error string. |
| `src/Pia.Wpf/Services/Interfaces/IFilesToolHandler.cs` | `FilesToolCall` record (lines 5–10) | `(ToolName, Description, Details, TargetPath, Execute: Func<Task<object?>>)`. `Execute` already returns `object?`, so a structured return is **type-compatible** today. |
| `src/Pia.Wpf/Infrastructure/SafeFolderPath.cs` | `TryResolveInside` | Rejects rooted/UNC/`..`-escape/invalid-char/null paths; confirms result stays under root. This is the sandbox boundary. |
| `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs` | `FromFilesHandler` (lines 185–202) | Adapts `IFilesToolHandler` → `IPluginToolHandler`; `isAvailable: () => handler.IsAvailable` suppresses tools + system prompt when no sandbox folder. Maps `FilesToolCall` → `PluginToolCall(ToolName, "files", Description, Details, Execute)`. |
| `src/Pia.Wpf/Models/ActionCardInfo.cs` | `ActionCardInfo`, `ActionCardCategory.Files` | Approval card; `WaitForUserDecisionAsync()` (TCS) blocks the turn. Has `IsDestructive`, `WarningText`, `Details`, `OldValueDetails`. |
| `src/Pia.Wpf/Services/ActionCardBuilder.cs` | `Build` (lines 24–64), `FormatToolTitle` (line 111 maps `write_file` → `ActionCard_Action_Write`) | Renders the card. **Reads `pendingAction.Details` as a string** (lines 46–50) via `JsonHelper.ParseKeyValueText`. This is a regression surface if the Details format changes. |

**Plumbing that already exists and must be reused, not rebuilt:** registration
(`BuiltInPluginHandler.FromFilesHandler`, wired via `BuiltInPluginDefaults.FilesPluginId`
in `BuiltInPluginDefaults.cs` line 16, surfaced through `PluginService`), dispatch
(`HandleToolCallAsync` switch), approval card (two-phase pending action + `ActionCardInfo`),
sandbox guard (`SafeFolderPath`), privacy logging (`SensitiveDebug` for paths).

---

## 3. Gap analysis

Gaps are tiered by **dependency**, because they do not all land in one PR.

### Tier 1 — local to the handler (land independently, no cross-tool infra)

| Spec req | Current behavior | Needed change |
|----------|------------------|---------------|
| **11** missing-vs-empty `content` | `GetStringArg` returns `""` for a missing key → silent empty write | Distinguish *key absent* from *empty string*. If `path` present but `content` key absent → corrective error ("dropped-arg under context pressure; re-emit full content"). Do **not** write. |
| **11** non-string `content` | Coerced via `GetRawText()`/`ToString()` | Return a **type error** when the `content` JSON value is not a string. Don't coerce objects/arrays into a file. |
| **1** atomic write | Bare `File.WriteAllText(finalPath, content)` | Temp file **in the same directory** → write → flush-to-disk → atomic replace over target → cleanup temp on any error. (See §4 for the verified Windows API mapping.) |
| **3** line endings | `File.WriteAllText` writes content as-is, no CRLF/LF detection | **Correctness defect, not a nicety.** Detect the existing file's dominant ending (CRLF vs LF) and normalize written content to match. New files: repo/platform default. **Repo fact:** this repo is CRLF and LF-writing has already broken byte-identical raw-string tests (MEMORY) — so default for new files in this repo is CRLF. |
| **4** BOM | Read strips BOM; write does not restore | If the original file began with a BOM, restore it on write. New file: no BOM unless content dictates. |
| **7** sensitive-path blocklist | Relies solely on sandbox boundary | Add an explicit refusal list (system dirs, credential stores, Pia's own config/DB under `%LOCALAPPDATA%\Pia`). Less critical *inside* the sandbox, but spec-required and cheap once a workspace root is introduced (Tier 3 dependency below). |
| **10** internal-content guard | None | Reject content that is obviously `read_file` display text (lines prefixed `N|`) or a dedup-stub echo. Return a corrective error — the model echoed a tool result instead of real content. |
| **return shape** | Bare string | Return the structured object from §1 (`success`, `resolved_path`, `bytes_written`, `lines`, `lint`, `_warning`, `error`). |
| **result cap** | Not enforced | Honor `max_result_size_chars = 100_000` on the **serialized result** (distinct from the 512K input cap, which stays). |
| **approval UX** | Card shows path + char count only; overwrite is silent | Optionally surface create-vs-overwrite distinctly and show byte/line counts. Overwrite of an existing file is destructive-ish — consider a stronger confirmation than create. |

### Tier 2 — shared machinery with `patch` (do not spec solo here)

| Spec req | Current behavior | Needed change |
|----------|------------------|---------------|
| **6** delta-filtered post-write syntax check (headline feature) | Entirely absent | Baseline-lint old content → write → lint new content → surface **only NEW errors**. This machinery is **co-owned with `patch`** (`docs/coding-tools-spec/patch.md`). Spec it once, shared. See §4 for the recommended in-process-only lint scope. |

### Tier 3 — blocked on `task_id` threading (cannot land until tool_registration changes)

| Spec req | Current behavior | Needed change |
|----------|------------------|---------------|
| **8** staleness `_warning` | None | Needs per-`task_id` last-read mtime tracking + a cross-agent file registry. **`HandleToolCallAsync`/`FilesToolCall`/`PluginToolCall` carry NO task/session id today** (verified). Blocked until `task_id` is threaded at the dispatch layer. |
| **9** workspace-divergence `_warning` | None | Needs a "task workspace root" notion to compare the resolved path against. Blocked on the same `task_id`/workspace-root work. |
| registry update | None | "this path was written by this task" — same dependency. |

> Honest dependency statement: **write_file cannot do staleness or workspace-divergence
> alone.** Those three rows are gated by `task_id` threading specced in
> `docs/coding-tools-spec/tool_registration.md`. Implement the warning *hooks* now (a
> nullable `_warning` field that is always `null` until the registry exists) so the return
> shape is stable, but do not block these PRs on the registry.

---

## 4. Ordered modification instructions

**Position: extend `FilesToolHandler` in place.** It owns the exact tool name, schema, and
all the plumbing. A parallel tool would duplicate registration, dispatch, the approval card,
and the sandbox guard for zero benefit. The cost of extend-in-place is regression risk to the
current sandbox UX — see §5, and tie every change back to it.

Do the steps in this order. Each Tier-1 step is independently shippable.

### Step 0 — Schema (no behavior change)
- [ ] Keep the Pia schema as `{path, content}`. Do **not** add `cross_profile`, `offset`, or
  `limit`. `WriteFileSchema` (lines 316–319) is already correct; leave its signature.

### Step 1 — Arg validation (invariant 11)
- [ ] Add a "missing-vs-present" arg accessor (alongside `GetStringArg`, not replacing it —
  `read_file`/`delete_file` still want the lenient version). For `write_file`:
  - `content` key absent → return a `FilesToolCall` whose `Execute` yields a structured
    `error` ("content missing; re-emit full content, or use a larger-file path"). Never write.
  - `content` present but not a JSON string → structured type `error`.
- [ ] Apply the **internal-content guard** here (invariant 10): if the trimmed content is
  predominantly `N|`-prefixed lines or matches a known dedup-stub message, return a corrective
  `error`. Keep the heuristic conservative to avoid false positives on legitimate text.

### Step 2 — Sensitive-path blocklist (invariant 7)
- [ ] Before building the `FilesToolCall`, after `TryResolveInside` succeeds, check the
  resolved absolute path against a blocklist (Pia's own `%LOCALAPPDATA%\Pia` data/config/DB,
  and — once workspace root exists — system/credential dirs). Reject with a clear `error`.
  Within the current single sandbox this is mostly belt-and-suspenders, but it is spec-required
  and forward-compatible with a wider workspace root.

### Step 3 — Atomic write + line-ending + BOM (invariants 1, 3, 4)
Replace the `File.WriteAllText` call inside the `Execute` lambda (line 244) with an atomic
write helper. **Verified Windows-API mapping (no C# written here, just the shape):**
- [ ] Read the existing target (if any) to detect **dominant line ending** (CRLF vs LF) and
  **leading BOM**. New file → repo default = **CRLF** (this repo is CRLF; LF has broken tests).
- [ ] Normalize `content`'s newlines to the detected ending; re-prepend BOM if the original had
  one.
- [ ] Write to a temp file **in the same directory** (same volume → atomic replace possible).
- [ ] Flush to disk (`FileStream.Flush(true)` = fsync equivalent).
- [ ] Atomically replace the target with `File.Replace` (preserves ACLs → satisfies "preserve
  mode"); fall back to `File.Move(overwrite)` when the target does not yet exist.
- [ ] On any exception, delete the temp file and return a structured `error`. Never leave a
  half-written file.
- [ ] Keep `mkdir -p` (lines 240–242) before the temp write.
- [ ] Re-validate `TryResolveInside` inside `Execute` (the existing line-235 re-check) — keep it.

### Step 4 — Structured return shape (return-shape gap)
- [ ] Change `write_file`'s success/error returns from bare strings to a structured object
  (`success`, `resolved_path` = the existing `SafeRelative`, `bytes_written`, `lines`, `lint`,
  `_warning`, `error`). `FilesToolCall.Execute` is `Func<Task<object?>>` so this is
  type-compatible **but** see §5 — two consumers must handle the new shape.
- [ ] Enforce `max_result_size_chars = 100_000` on the serialized result; truncate `lint`
  detail if necessary, never the structural fields.

### Step 5 — Delta-filtered lint (invariant 6, Tier 2, shared with `patch`)
- [ ] **Recommended lint scope: in-process structured parsers ONLY.** Privacy-first + the
  user's minimal-deps preference + the fact that shelling out to `py_compile`/`node --check`/
  `tsc` is *process execution* (drags in the code-exec security model and requires those
  runtimes installed) all argue against shell linters. Recommend:
  - JSON via `System.Text.Json` (already referenced).
  - YAML/TOML **only if** a parser is already present or hand-rolled; otherwise punt and return
    `lint: null` for those extensions.
  - **Punt shell linters entirely** for v1; document as a follow-up.
- [ ] Delta filter: parse old content (baseline error set) → parse new content → surface only
  errors absent from the baseline. For pure-parse formats the baseline is usually "parses or
  not"; capture the parse error so a pre-existing broken JSON file isn't blamed on this write.
- [ ] Factor the lint helper so `patch` reuses it. Spec the helper in the shared/`patch` plan,
  not solely here.

### Step 6 — Warning hooks (invariants 8, 9, Tier 3, blocked)
- [ ] Add the `_warning` field to the return shape now; leave it `null`.
- [ ] Do **not** implement staleness / workspace-divergence until `task_id` is threaded
  (see §"Cross-cutting" and `tool_registration.md`). When it lands: key a per-`task_id`
  last-read mtime registry off the threaded id and populate `_warning` non-blockingly.

### Step 7 — Privacy-logging compliance (always)
- [ ] All path logging stays on `SensitiveDebug` (already done at lines 246). The structured
  result returned to the model is fine; just never `LogInformation` the path or content. Lint
  error text may quote file content → if logged at all, use `SensitiveDebug`.

---

## 5. Regression risks to the existing sandbox / UX

The reuse risk **is** that current sandbox UX regresses. Concrete surfaces:

1. **Return shape change (string → object).** Two consumers must handle it:
   - **Model-facing serialization:** the tool result is wrapped into `FunctionResultContent`.
     A structured object must serialize cleanly (and respect the 100K result cap). Verify the
     tool-loop serializer handles `object?` returns, not just strings.
   - **`ActionCardBuilder.Build` (lines 46–50):** reads `pendingAction.Details` as a string via
     `JsonHelper.ParseKeyValueText`. The card's `Details` (set in `PrepareWriteFile` line 229)
     is **separate** from the `Execute` return — so changing the *return* shape does not by
     itself break the card. **But** if you enrich `Details` (e.g. add a diff/preview, byte/line
     counts) you must keep its format consistent with `ParseKeyValueText` or update the builder.
2. **Atomic-write semantics differ from `WriteAllText`.** `File.Replace` requires source and
   target on the **same volume** — the same-directory temp file guarantees this. Watch for:
   target inside a directory the user can't replace into; antivirus locking the temp/rename;
   first-write (no existing target) must fall back to a plain move.
3. **Line-ending normalization could surprise users** who deliberately wrote LF files in a
   CRLF repo. Detection-from-existing-file mitigates this; new-file default is the only
   opinionated case.
4. **Sensitive-path blocklist false-positives** could block legitimate writes if the list is
   too broad. Keep it tight (Pia's own config/DB + true system dirs).
5. **Internal-content guard false-positives** — a legitimate file that happens to contain
   `N|`-style lines (e.g. a markdown table, a log) could be wrongly rejected. Heuristic must be
   conservative (e.g. majority of lines match the exact `\d+\|` read_file format).
6. **Tool gating unchanged.** Keep `IsAvailable` / `FromFilesHandler` gating so the tool stays
   hidden when no sandbox folder is configured. Don't regress the system-prompt suppression.

---

## 6. Cross-cutting questions (scoped to `write_file`)

**Relevant — answer:**

- **#2 Filesystem scope.** `write_file` is gated on the single configured sandbox folder. Coding
  workflows want repo/workspace-wide access. Recommendation: introduce a **"workspace root"**
  concept (broader than today's single folder) rather than removing the sandbox — the
  sensitive-path blocklist (Step 2) becomes meaningful exactly here. Privacy logging is
  unaffected: paths stay on `SensitiveDebug`; no full paths in release logs. This is a
  cross-tool decision (shared with `read_file`/`search_files`/`patch`) — flag it, don't decide
  it solely in this doc.
- **#4 task_id threading.** Verified: dispatch threads **no** task/session id
  (`HandleToolCallAsync(FunctionCallContent, CancellationToken)`; `FilesToolCall` and
  `PluginToolCall` carry none). The spec stresses doing `task_id` day one because retrofitting
  is painful, and it gates invariants 8/9 here. Recommendation: thread `task_id` at the
  `tool_registration` layer **before or alongside** write_file's Tier-3 work; land Tier 1/2
  without it.
- **#5 extend vs rebuild.** **Extend in place** (this plan). Same name, same schema, all
  plumbing exists. Caveat: write_file does NOT need read_file's pagination/line-number
  richness, so the "build richer toolset alongside" argument applies to `read_file`/`patch`,
  not here.

**Not relevant to `write_file` — dismiss in one line each:**

- **#1 code-execution security model** — N/A; `write_file` is not code execution. (Touches it
  only insofar as Step 5 *avoids* shelling out to linters precisely to stay out of that model.)
- **#3 native vs MCP delegation** — a filesystem MCP server could serve this, but
  `bucket = reuse` already commits to extending the native handler; no fork to resolve here.
- **#6 Python runtime** — N/A; no runtime needed. Reinforces Step 5's in-process-only lint.

---

## 7. Open questions

1. **Lint scope fork:** in-process parsers only (recommended) vs eventually shelling out to
   real linters. The latter pulls in the code-exec security model and runtime dependencies —
   confirm in-process-only for v1.
2. **`task_id` sequencing:** does `task_id` threading land before, with, or after write_file's
   Tier-1/2 work? It gates invariants 8/9 (staleness, workspace-divergence). Recommendation:
   ship Tier 1/2 first with `_warning` stubbed `null`.
3. **Workspace root:** keep the single sandbox folder, or introduce a wider "workspace root"
   for coding tools? Decided cross-tool, not here — but write_file's sensitive-path blocklist
   depends on the answer.
4. **Lint helper ownership:** confirm the delta-filtered syntax-check helper is specced in the
   shared `patch` plan (co-owned), not duplicated in write_file.
5. **Overwrite confirmation UX:** should overwriting an existing file get a stronger/destructive
   confirmation than creating a new one (today both are silent past the single approval card)?
6. **New-file default line ending:** confirm CRLF as the repo default for newly created files
   (repo is CRLF; LF has broken byte-identical tests).
