# Pia vs. opencode — file discovery, content search, and the working directory

**Status:** Analysis, complete · **Owner:** man · **Written:** 2026-09-01
**Origin:** Direct request — learn from opencode's agent tooling for Pia's assistant/agent mode.

**Snapshots.** opencode `G:\Git\opencode` branch `dev` @ `ebece6efd7` (2026-09-01). Pia `C:\Git\Pia.Wpf`
branch `feature/agent_issues`. Every Pia claim cited here is **committed** code — `FilesToolHandler.cs`
and `BuiltInPluginDefaults.cs` are clean in the working tree, and the branch's uncommitted edits to
`AgentPlanner.cs` / `AgentVerifier.cs` (6 insertions, at `:332` and `:627`) do not touch the grounding or
artifact-probe paths quoted below.

---

## 1. The one-line difference

opencode's working directory is **a code checkout**, and the model is *told* where it is. Pia's is **a
user's documents folder**, and the model is *never told* where it is — containment is enforced silently
by the host. Every downstream difference falls out of that.

opencode also runs two tool generations side by side, and the migration direction is instructive:
V1 (`packages/opencode/src/tool/*.ts` + sibling `.txt` description assets) is **absolute-path-first**;
V2 (`packages/core/src/tool/*.ts`, Effect + `Location`-scoped + permissioned, see
`packages/core/src/tool/AGENTS.md`) is **relative-path-first** — i.e. moving *toward* Pia's contract while
keeping the env block Pia lacks.

---

## 2. Concern-by-concern

| Concern | opencode | Pia | Gap |
|---|---|---|---|
| **How the model learns the CWD** | `<env>` block in the system prompt: `Working directory`, `Workspace root folder`, `Is directory a git repo`, `Platform`, `Today's date` — `packages/opencode/src/session/system.ts:72-83`. V2 re-emits it as a *live* System Context entry with a `baseline` and an `update` framing ("The environment you are running in is now:") — `packages/core/src/system-context/builtins.ts:16-39`. Extra approved dirs are listed in `<available_references>` (`system.ts:84-101`). | **Nothing.** No env block. `TaskContext.WorkingSubpath` never reaches a prompt (every usage is host-side: `FilesToolHandler.cs:170`, `GitToolHandler.cs:153`, `AgentPlanner.cs:415`, `AgentVerifier.cs:303`). The model gets only "a sandboxed local folder configured by the user under Settings > Assistant". | **Biggest single gap.** |
| **Find a file by name/path pattern** | `glob` tool. V2: `pattern` + optional relative `path` + `limit` (`packages/core/src/tool/glob.ts:18-26`). Full glob syntax, `**/*.ts` works. V1 caps at 100 with *"(Results are truncated: showing first 100 results. Consider using a more specific path or pattern.)"* (`packages/opencode/src/tool/glob.ts:49-60`). There is **no separate `list`/`ls` tool** — `read` absorbed directory listing (*"…or list a directory page"*, `core/src/tool/read.ts:42`), so name-discovery routes through `glob` and listing is a read mode. | **No glob tool.** `list_files` takes a *name-only* glob — "Must not contain a path separator" (`FilesToolHandler.cs:1292`); a path-bearing pattern is rejected with guidance rather than mis-listed (`:226-229`). `docs/**/*.md` is impossible. | **Real capability gap.** |
| **Find a file by content** | `grep` tool over vendored ripgrep. V2 description: *"Search file contents by regular expression within the active Location or an absolute managed tool-output file. Use a path to narrow the search, include to filter files by glob, and limit to bound the match count."* (`packages/core/src/tool/grep.ts:63-66`). V1 caps at 100 matches and appends *"(Results truncated. Consider using a more specific path or pattern.)"* (`packages/opencode/src/tool/grep.ts:67,80`). | `search_files`: regex per line, `path` scope, `mode: content\|files\|count`, offset/limit (default 100, max 500) — `FilesToolHandler.cs:1309-1315`. C#-implemented, not ripgrep. Honours `.gitignore`/`.piaignore` + built-in ignores. | Near-parity. Pia lacks an `include` file-glob filter. |
| **Resolve a relative path; what is "here"** | V2 inputs are typed `RelativePath` — the model *must* send relative; host does `path.resolve(location.directory, input.path ?? ".")` (`glob.ts:75`). V1 is the opposite: read.txt says *"The filePath parameter should be an absolute path."* V2 `read` splits the difference: *"Relative paths resolve from the current location; absolute paths inside it are accepted, while external absolute paths require external_directory approval."* (`read.ts:42`) | Sandbox root = `AssistantFilesFolder`; ambient `TaskContext.WorkspaceRoot` overrides it for unattended runs; per-chat `TaskContext.WorkingSubpath` narrows further (`FilesToolHandler.cs:158-170`, `ResolveEffectiveRoot:191-204`). Prompt says: *"Paths may be RELATIVE to that folder or absolute, but must stay inside the configured folder — the host rejects '..' traversal that escapes the folder and any absolute path that points outside it."* (`BuiltInPluginDefaults.cs:93`) | Comparable. Pia's containment is stronger (see row 8). |
| **Relative in, absolute out** | Results are shown back **absolute**: `toModelOutput` maps every entry through `path.resolve(location.directory, entry.path)` (`glob.ts:52-59`, `grep.ts:68-78`). So the model sends short paths and reads back unambiguous ones. The `explore` subagent is told the same: *"Return file paths as absolute paths in your final response."* (`packages/opencode/src/agent/prompt/explore.txt:14`) | Relative in, **relative out** — `ListRelativeFiles` returns "sandbox-relative paths using forward slashes (so the model can copy them into a tool argument without backslash escape corruption)" (`IFilesToolHandler.cs:43-46`). | Deliberate divergence; Pia's is defensible given no env block (an absolute path the model can't anchor is worse). |
| **Shell as a search escape hatch** | Permitted but actively steered against. `anthropic.txt:85`: *"Use specialized tools instead of bash commands when possible… Read for reading files instead of cat/head/tail, Edit for editing instead of sed/awk… Reserve bash tools exclusively for actual system commands."* `grep.txt`: *"If you need to identify/count the number of matches within files, use the Bash tool with `rg` (ripgrep) directly. Do NOT use `grep`."* `gpt.txt:5`: *"prefer using Glob and Grep tools (they are powered by `rg`)"*. | **No shell tool exists** — enforced by absence. Only allowlisted `git_*` subcommands (`GitToolHandler.cs:106-127`; class doc: *"There is deliberately no generic `git_run(cmd)` and no network tool"*). `hermes-comparison.md:65` calls this out as correctly not-solved rather than under-built. | Pia is stronger here; no prompt line is needed because there is nothing to warn against. |
| **Truncation the model must plan around** | `read`: 2000 lines / 50 KB (`packages/core/src/tool/read-filesystem.ts:11-12`); lines >2000 chars truncated; content returned as `<line>: <content>`. ripgrep wrapper caps: 8 KB stderr, 64 KB/record, 100 submatches (`packages/core/src/ripgrep.ts:18-20`). **Oversized output is spilled to a file** under `<data>/tool-output/` and the model is told what to do with it (see §3). | `read_file`: `LINE\|CONTENT`, 1-indexed, offset/limit default 500 / max 2000, prefixed with `total_lines` (`FilesToolHandler.cs:1294-1298`). `search_files` 100/500. No spill-to-file. | Pia lacks the spill hatch. |
| **Containment / traversal** | `external_directory` permission gate for absolute paths outside the Location (`read.ts:60-68`). `.gitignore` respected by default; `hidden`/`follow` exist in the ripgrep wrapper but are **not exposed to the model**. | `SafeFolderPath.TryResolveInsideAllowingAbsolute` canonicalizes via `GetFinalPathNameByHandleW` so a junction/symlink is not a sandbox hole; the configured root itself is canonicalized (`FilesToolHandler.cs:117`). `SensitivePathGuard` denylists Pia's own data dir and system/credential paths even inside the sandbox. | Pia meaningfully stronger. |
| **Read-before-edit / staleness** | **Not enforced.** Only an edit-time heuristic: *"Refusing replacement because the matched span is much larger than oldString. Re-read the file and provide the full exact oldString…"* (`packages/opencode/src/tool/edit.ts:711`). | **Enforced** via `IFileStalenessStore`; reads are recorded, and the store is cleared when the sandbox is re-pointed so a stale read can't satisfy a check under a new root (`FilesToolHandler.cs:95-98`). `ReadPromptPreviewAsync` deliberately does *not* record a read, "so an edit must still re-read" (`IFilesToolHandler.cs:72-73`). | Pia stronger. |
| **Delegating search to a subagent** | First-class and prompt-enforced. `anthropic.txt:86`: *"VERY IMPORTANT: When exploring the codebase to gather context or to answer a question that is not a needle query for a specific file/class/function, it is CRITICAL that you use the Task tool instead of running search commands directly."* Plus a dedicated `explore` persona: *"You are a file search specialist… Use Glob for broad file pattern matching, Grep for searching file contents with regex, Read when you know the specific file path."* | Sub-agents exist but as **host-side fan-out** (`AgentRunOrchestrator.TryFanOutAsync` → `IHeadlessRunLauncher`), not a model-callable `task` tool. The model cannot choose to delegate a search. | Structural difference, not necessarily a gap for a desktop assistant. |
| **Batching / parallel tool calls** | Explicit — but **per-model, and contradictory across models**. `anthropic.txt:83`: *"make all independent tool calls in parallel. Maximize use of parallel tool calls."* `glob.txt`: *"It is always better to speculatively perform multiple searches as a batch."* `read.txt`: *"Call this tool in parallel when you know there are multiple files you want to read."* Yet `trinity.txt:84` mandates the opposite: *"Use exactly one tool per assistant message. After each tool call, wait for the result before continuing."* plus `:86` *"do not search again in a loop."* | **No batching guidance** in the composer, planner, verifier, or any built-in plugin prompt addition (grepped `src/Pia.Wpf/Services/` + `Models/Persona*.cs` for `in parallel`/`parallel tool`/`multiple tool`/`batch`; only unrelated hits). | Cheapest gap to close. |
| **Symbol-level search** | `lsp` tool: goToDefinition, findReferences, workspaceSymbol, call hierarchy (`packages/opencode/src/tool/lsp.txt`). | None. | Out of scope for a documents-folder assistant. |
| **Orientation before work** | Fuzzy `@file` mentions backed by a full file index warmed at session start — `ripgrep.find` walk, unbounded when git-tracked / 100k cap otherwise, then `fuzzysort` (`packages/core/src/filesystem/search.ts:33-47`). | `@Files` mention with `ListRelativeFiles(filter, max)` autocomplete, same containment + sensitive filtering as `list_files`; plus `ReadPromptPreviewAsync`, which injects a bounded file preview into the prompt *"so a model that won't call read_file on its own still sees the file"* (`IFilesToolHandler.cs:14-18`). | Parity, different mechanism. |
| **Recovering from a wrong path** | `read` scans the parent directory for case-insensitive basename substring matches and returns up to 3 candidates: *"File not found: X\n\nDid you mean one of these?"* (`packages/opencode/src/tool/read.ts:76-99`). | A miss returns a plain error; the model must fall back to `search_files`. | Cheap, high-value steal. |
| **Per-folder instructions** | `AGENTS.md`/`CLAUDE.md` is injected **lazily**: every successful text `read` walks up from that file toward the instance root and, if it finds a *nearer* instructions file not yet attached, appends it to the `read` output inside a `<system-reminder>` (`packages/opencode/src/session/instruction.ts:179-221`, wired at `tool/read.ts:300,355-357`). First project-level match wins — ancestors do not stack (`instruction.ts:110-153`). | No analogue; there is no per-folder instruction file concept. | Interesting, but a documents folder has no `AGENTS.md`. Low priority. |
| **Cross-shell path spellings** | `FSUtil.windowsPath` normalizes Git-Bash `/c/…`, Cygwin `/cygdrive/c/…`, and WSL `/mnt/c/…` into `C:/…` (`packages/core/src/fs-util.ts:257-264`), so a model that emits a POSIX-ish Windows path still resolves. | Not handled; such a path fails containment. | Worth considering — Pia is Windows-only and this class of model output is common. |
| **Plan-time grounding** | None equivalent. | **Pia is ahead.** `AgentPlanner` injects a bounded top-level listing of the working folder into the plan turn, fenced `--- Already in the working folder this run reads and writes (top level; use list_files for more) ---` — 40 entries sent, 5000-entry scan cap, 2 s time box, silent degrade (`AgentPlanner.cs:74-96, 401-447, 464-549`). `AgentVerifier` runs a matching mechanical artifact probe: *"Declared-artifact probe — mechanical filesystem facts gathered by the app, NOT the assistant's claims:"* (`AgentVerifier.cs:240-241`). | Worth keeping; opencode has no analogue. |
| **Per-run workspace isolation** | Location-scoped services; no per-run copy. | `RunWorkspaceService` provisions `%LOCALAPPDATA%\Pia\runs\<runId>` as a **git worktree** when the source is a repo, else a bounded copy (2000 files / 256 MB), with teardown + promotion. Shipped as Batch 06, 2026-07-31 (`docs/superpowers/specs/agent-roadmap/06-run-workspace-isolation.md:5-12`). | Pia ahead. |

---

## 3. The five opencode ideas worth stealing

**1. An `<env>` block.** Verbatim shape (`session/system.ts:72-83`):

```
Here is some useful information about the environment you are running in:
<env>
  Working directory: /abs/path
  Workspace root folder: /abs/path
  Is directory a git repo: yes
  Platform: win32
  Today's date: Tue Sep 01 2026
</env>
```

For Pia this would state the effective root **after** `ResolveEffectiveRoot` — i.e. the sandbox root
narrowed by the chat's working subpath — so the model can reason about scope instead of guessing. The V2
variant is the better model: emit it as a context entry with an `update` framing so a mid-session
re-point says *"The environment you are running in is now:"* rather than silently contradicting the
baseline (`core/src/system-context/builtins.ts:29-31`). This is exactly the case Pia already handles
host-side (`OnSettingsChanged` evicts the staleness store) but never tells the model about.

**2. Spill oversized tool output to a searchable file.** `packages/opencode/src/tool/truncate.ts:129-131`
returns a preview plus, verbatim:

> `The tool call succeeded but the output was truncated. Full output saved to: <file>`
> `Use Grep to search the full content or Read with offset/limit to view specific sections.`

…and, when the agent has a delegation tool:

> `Use the Task tool to have explore agent process this file with Grep and Read (with offset/limit). Do NOT read the full file yourself - delegate to save context.`

The full text lands under `<data>/tool-output/` with 7-day retention, and V2 `grep` is explicitly allowed
to search *"an absolute managed tool-output file"* (`core/src/tool/grep.ts:65`). This turns a truncation
from a dead end into a follow-up query — directly applicable to Pia's `search_files` 500-result cap and
`read_file` 2000-line cap.

**3. Say which tool locates a file.** opencode's `read.txt` carries the routing advice inside the tool
description, where it can't be missed:

> - Use the grep tool to find specific content in large files or files with long lines.
> - If you are unsure of the correct file path, use the glob tool to look up filenames by glob pattern.
> - Avoid tiny repeated slices (30 line chunks). If you need more context, read a larger window.

Pia's decision tree (`AssistantPromptComposer.cs:160-161`) currently routes *both* jobs to one tool —
*"search_files to locate files or text"* — because there is no glob. That's a sensible workaround, but it
means a filename lookup is executed as a content scan.

**4. State the CWD contract in one place.** `kimi.txt:64-66` is the tightest statement of the rule in
either codebase, and it is worth reading as a template:

> ## Working Directory
>
> The working directory should be considered as the project root if you are instructed to perform tasks
> on the project. Every file system operation will be relative to the working directory if you do not
> explicitly specify the absolute path. Tools may require absolute paths for some parameters, IF SO, YOU
> MUST use absolute paths for these parameters.

…paired with `kimi.txt:62`: *"you should never access (read/write/execute) files outside of the working
directory."* Contrast `gemini.txt:13`, which takes the opposite doctrine — *"Before using any file system
tool… you must construct the full absolute path… Relative paths are not supported"* — and note that
opencode picks the prompt **per model family** (`session/system.ts:36-48`), i.e. it treats path doctrine
as a model-specific dialect, not a universal truth. The same holds for batching: `anthropic.txt:83`
demands maximum parallelism while `trinity.txt:84` demands exactly one tool per message. **The lesson for
Pia is the seam, not any one wording** — Pia sends one composed prompt to every model.

**5. Make a missed path self-correcting.** `read.ts:76-99` turns the commonest failure — a plausible but
wrong path — into a recoverable one by scanning the parent directory for case-insensitive basename
matches and answering *"File not found: X\n\nDid you mean one of these?"* with up to three candidates.
Pia's `read_file` already walks and filters the same tree for `ListRelativeFiles`, so the ingredients
exist; today a miss just returns an error and the model retries blind or falls back to `search_files`.
Highest value-per-line item in this document.

---

## 4. What Pia should not copy

- **A shell tool.** Its absence is the reason Pia needs no dangerous-command tokenizer
  (`hermes-comparison.md:65`) and why containment has a single chokepoint.
- **Absolute-path-only inputs** (`gemini.txt:53`). With no env block the model has no anchor; with
  Windows backslashes it invites escape corruption — which is exactly why `ListRelativeFiles` returns
  forward-slashed relative paths.
- **Relaxing read-before-edit.** opencode has no staleness store; Pia's is a genuine advantage.
- **LSP/symbol tooling.** Wrong altitude for a documents-folder assistant.

---

## 5. Ranked recommendations

| # | Change | Effort | Value |
|---|---|---|---|
| 1 | Add an `<env>`-style block naming the **effective** working folder (post-`ResolveEffectiveRoot`), platform, and date; re-emit on re-point. | XS | High |
| 2 | Add one batching sentence to the prompt ("independent file tool calls in parallel; do not re-run the same search"). | XS | High |
| 3 | "Did you mean one of these?" near-match suggestions on a `read_file` miss. | XS | High |
| 4 | Add an `include` file-glob filter to `search_files`. | XS | Med |
| 5 | Add a real `find_files` glob tool (path patterns, `**`), or lift `list_files`' no-path-separator restriction. | S | High |
| 6 | Spill over-cap `search_files`/`read_file` output to a sandbox file and tell the model to `search_files` it. | S | Med |
| 7 | Move routing advice into the tool descriptions themselves (`read_file` → "unsure of the path? use …"). | XS | Med |
| 8 | Normalize `/c/…`, `/mnt/c/…`, `/cygdrive/c/…` spellings before containment. | XS | Low |

Items 1, 2, 4, 7 and 8 are prompt/schema-only. Items 3 and 5 need traversal code and should reuse the
existing walk in `HandleListFiles` plus `SafeFolderPath` containment rather than introducing a second
enumerator.

---

## Appendix — primary sources

**opencode** (`ebece6efd7`): `packages/core/src/tool/{glob,grep,read,read-filesystem,bash}.ts` ·
`packages/core/src/tool/AGENTS.md` · `packages/core/src/{ripgrep.ts,filesystem/search.ts}` ·
`packages/core/src/system-context/builtins.ts` · `packages/opencode/src/session/system.ts` ·
`packages/opencode/src/session/prompt/{anthropic,default,gpt,codex,gemini,kimi,trinity,copilot-gpt-5}.txt` ·
`packages/opencode/src/agent/prompt/explore.txt` ·
`packages/opencode/src/tool/{glob,grep,read,task,lsp}.{ts,txt}` · `packages/opencode/src/tool/truncate.ts` ·
`packages/opencode/src/tool/shell/prompt.ts` · `packages/opencode/src/session/instruction.ts` ·
`packages/core/src/fs-util.ts` · `packages/opencode/src/project/instance-context.ts` · root `AGENTS.md`

**Pia** (`feature/agent_issues`): `src/Pia.Wpf/Services/FilesToolHandler.cs` ·
`src/Pia.Wpf/Services/Interfaces/IFilesToolHandler.cs` ·
`src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs` ·
`src/Pia.Wpf/Services/AssistantPromptComposer.cs` · `src/Pia.Wpf/Services/{AgentPlanner,AgentVerifier}.cs` ·
`src/Pia.Wpf/Services/{TaskAmbient,RunContext,RunWorkspaceService,GitToolHandler}.cs` ·
`src/Pia.Wpf/Infrastructure/{SafeFolderPath,SensitivePathGuard,AssistantWorkspace}.cs` ·
`docs/superpowers/specs/agent-roadmap/{06-run-workspace-isolation,hermes-comparison,17-trust-model}.md` ·
`docs/agent_run_e2e/2026-08-27-cross-step-tool-context.md`

