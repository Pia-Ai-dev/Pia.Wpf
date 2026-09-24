# Plan: Drill down opencode's file-tooling advantages — difficulty × benefit classification and mirror order

## Context

`docs/agent_file_tooling/2026-09-01-pia-vs-opencode-file-tooling.md` compares Pia's assistant file tooling with opencode's and ends with 8 one-line ranked recommendations. The user wants each opencode advantage drilled down to implementation level, classified on two axes — implementation difficulty (easy → difficult) and benefit (small → huge) — and a grounded recommendation of which parts to mirror first.

## User decisions (2026-09-01)

- Deliverable: **a plan for the first implementation tranche that can run inside one workflow** — the classification exists to pick and justify the tranche.
- Coverage: **all opencode pros** from the comparison table; already-rejected items (shell tool, LSP) noted briefly as excluded.
- Tranche size: **quick wins + the glob tool** (env block, batching sentence, did-you-mean, include filter, routing advice, path normalization, AND the new `find_files`/glob capability). Spill-to-file and structural items go to later tranches.

Structure of this file: the first-tranche implementation plan (the executable part) comes first; the exploration findings follow as a grounding appendix, ending with the difficulty×benefit classification across all opencode pros and the mirror-first order that justifies the tranche cut.

# First-tranche implementation plan (one workflow run)

Seven items: env block, batching rule, did-you-mean, `include` filter, `find_files` tool, routing advice, POSIX path normalization. All line refs verified against the tree on 2026-09-01.

## Resolved design decisions

| # | Decision | Choice |
|---|---|---|
| D1 | Env-root plumbing | Optional trailing `string? environmentRoot = null` on `IAssistantPromptComposer.PrepareTurn` (repo's documented trailing-default convention); default null = byte-identical prompts, so it lands green before any caller passes it. |
| D2 | Who resolves the root | New `string? DescribeEffectiveRoot(string? workingSubpath)` on `IFilesToolHandler`, thin wrapper over the existing internal `ResolveEffectiveRoot` — ChatSessionManager and AssistantViewModel already hold `IFilesToolHandler`, so no 5th duplication of the narrowing logic. |
| D3 | Env call sites (tranche 1) | Interactive (`ChatSessionManager.cs:818`, subpath = `session.WorkingDirectory`), voice (`AssistantViewModel.cs:2177`, subpath = `ActiveUiWorkingSubpath`), headless (`HeadlessTurnExecutor.cs:259`, workspace root or settings-folder degrade). BackgroundAssistantTurnRunner / StepPersonaResolver / LiveTurnExecutor stay null — documented gap for tranche 2. |
| D4 | No "environment changed" message needed | Interactive/voice recompose per turn → block self-updates; a headless run's workspace root is fixed for the run's life. Update-framing is tranche 2, if ever. |
| D5 | Env block fields | Working folder + Platform + relative-path sentence. No date (`PersonaPromptShape` owns it), no git-repo line (extra IO; git plugin covers it). |
| D6 | Batching rule placement | New const next to `DeclinedActionRule` (`AssistantPromptComposer.cs:118-119`), appended at `:188` — the only always-present tools-path slot (`## Tool Selection` is skipped on @-turns). Wording promises saved round-trips, never parallel execution (dispatch is serial, `AiClientService.cs:387-390`). |
| D7 | Glob helper | New `Pia.Infrastructure.GlobPattern`: MOVE `TranslateGlob`/`FindClassEnd`/`AppendClass` verbatim out of `GitignoreMatcher.cs:127+` (pure code motion, GitignoreMatcher delegates, its tests stay green) + `Compile(glob)` with gitignore-style anchoring (bare name matches at any depth; slash-bearing anchors), `IgnoreCase|CultureInvariant|NonBacktracking`. |
| D8 | Glob semantics | Shared by `include` and `find_files`. NO brace sets `{md,txt}` (TranslateGlob escapes braces) — tool descriptions must only show supported examples (`*.md`, `docs/**/*.md`). |
| D9 | Match base | Glob matched against the SEARCHED folder's relative path; results emitted ROOT-relative forward-slash (Pia round-trip contract). |
| D10 | find_files walk | Own walk cloned from `HandleSearchFiles`'s (`:431-535`, minus file reads) — already handles searchRoot, scratch carve-out, containment, ignore pruning, MaxFilesScanned. Do NOT touch shared `CollectRelativeFiles` (list_files/@Files depend on it). |
| D11 | find_files output | Sorted `OrdinalIgnoreCase`, default limit 100 clamp [1,500], empty → `No files found.`, truncation note verbatim opencode V1: `(Results are truncated: showing first {N} results. Consider using a more specific path or pattern.)`. |
| D12 | did-you-mean shape | Keep Pia's inline `" Did you mean: a, b?"` (search_files precedent `:398-400`) appended to the unchanged `Error: File '{requested}' not found.` — existing tests only assert `Contains("not found")`. |
| D13 | NormalizePathArg scope | `internal static` in FilesToolHandler, applied ONLY at the 5 path-arg sites (read `:676`, write `:965`, delete `:1233`, search path `:380`, find_files path) — NOT in SafeFolderPath (shared with GitToolHandler/RunWorkspaceRedirects). No platform gate (Windows-only app). |
| D14 | Env placement | Trailing section in `BuildSystemPrompt` (tools path) only; `BuildSystemPromptNoTools` unchanged — the block describes file tools. |

## Exact new prompt text

Batching rule (const `ToolBatchingRule`, appended right after `{DeclinedActionRule}` at `AssistantPromptComposer.cs:188`):

> `- When you need several independent lookups (file reads, searches, listings, recall), issue all of those tool calls together in one reply instead of one per turn — you get every result back in a single round-trip. Do not re-issue a search or read that has already returned its result.`

Env block (rendered when `supportsTools && environmentRoot != null`):

```
## Environment

Here is useful information about the environment you are running in:
<env>
Working folder: {environmentRoot}
Platform: Windows
</env>
Paths passed to file tools are resolved relative to this working folder; use forward slashes. Absolute paths are accepted only when they stay inside it.
```

Decision-tree step 4 (`AssistantPromptComposer.cs:160-161`) becomes:

> `- YES → Use the file tools: find_files to locate files by name or path glob, search_files to find text inside files, read_file to inspect content (request a windowed slice with offset/limit for large files), and write_file to apply edits (the user approves a diff before any change is written).`

## Steps

### Step 0 — shared seams (single agent, lands first, gate-checked)

- NEW `src/Pia.Wpf/Infrastructure/GlobPattern.cs` — verbatim move of `TranslateGlob`/`FindClassEnd`/`AppendClass` from `GitignoreMatcher.cs:127-223`; add `Compile`. `GitignoreMatcher.ParseLine` delegates.
- `IFilesToolHandler.cs` + `FilesToolHandler.cs` — add `DescribeEffectiveRoot(string? workingSubpath)`: null when tools disabled/folder missing, else `ResolveEffectiveRoot(_currentFolder, workingSubpath)`.
- `IAssistantPromptComposer.cs` + `AssistantPromptComposer.cs` — add trailing `string? environmentRoot = null` to `PrepareTurn`, threaded to `BuildSystemPrompt` (rendering included; null = no section).
- Tests: new `GlobPatternTests` (`*`, `?`, `**/`, trailing `**`, `[Dd]ebug`, `[!a]` excludes `/`, anchoring with/without `/`, leading-`/` trim, literal braces); extend effective-root tests with `DescribeEffectiveRoot` (null folder → null, subpath narrowing, bad subpath → base root). Full gate green before A/B start.

### Group A — file-tool features (one agent; sole owner of FilesToolHandler.cs, BuiltInPluginDefaults.cs, handler tests; A1→A5 sequential)

- **A1 NormalizePathArg**: 3 compiled regexes — `^/(?:cygdrive|mnt)/([a-zA-Z])(?=/|$)`, `^/([a-zA-Z]):(?=/|$)`, `^/([a-zA-Z])(?=/|$)` → `{UPPER}:/rest`; apply at the 5 path sites. Tests: each spelling + unchanged cases (`docs/readme.md`, `C:/x`, `/docs/x`, empty) + one end-to-end read via the POSIX spelling of the temp-sandbox path.
- **A2 read_file did-you-mean**: at `:694` miss, new `SuggestSimilarFiles(root, safePath, ignore)` — parent dir enumeration, case-insensitive bidirectional basename substring, per candidate: containment + `SensitivePathGuard.IsBlocked` + ignore check on the file AND each ancestor dir (dir-only rules like `secret/` must not leak — load-bearing, keep under test), cap 3, forward-slash root-relative. Tests: near-miss suggestion, cap 3, ignored sibling never suggested, dir-only-ignored parent never leaks, blocked-name non-leak.
- **A3 search_files `include`**: optional arg; `GlobPattern.Compile` once pre-walk; filter in file loop after ignore check (`:480`), before the byte read. Schema description: `Optional file glob restricting which files are searched, matched against the path relative to the searched folder — e.g. '*.cs' or 'docs/**/*.md'.` Tests: narrows hits, bare-name matches nested, anchored glob, include+path, never resurfaces an ignored file.
- **A4 find_files tool + AITool routing descriptions**: `FindFilesSchema` (`pattern` required, `path` optional, `limit` default 100/max 500); `HandleFindFiles` per D9–D11 with search-walk clone incl. `SuggestSimilarDirectories` on path miss and the MaxFilesScanned warning; `GetTools()` entry + switch case (route table self-updates, read-only → no permission work). Expand read_file/search_files/find_files one-liners with routing advice (unpinned by tests). Tests: new `FilesToolHandlerFindFilesTests` — `*.md` nested (gitignore anchoring), `docs/**/*.md`, `?`/`[...]`, path narrowing, path-miss suggestions, sorted/forward-slash/root-relative, limit + verbatim truncation note, `No files found.`, ignored/sensitive/scratch excluded, `../x` rejected, separator-bearing pattern accepted.
- **A5 plugin prompt + name-list tests**: `BuiltInPluginDefaults.cs:93-94` — add find_files to the enumeration + one routing sentence + mention `include`; do NOT reword pinned phrases (`LINE|CONTENT`, `RELATIVE`, `'..'`, `Settings > Assistant`, vault block); bump `UpdatedAt` to 2026-09-01. Update `PluginServiceFileToolRoutingTests.cs:17-18` (add name) and `FilesPluginPromptScaffoldingTests` (assert `find_files`).

### Group B — prompt env + batching + routing tree (one agent, parallel to A; sole owner of AssistantPromptComposer.cs, the 3 caller files, composer/executor test fixtures)

- **B1 composer rendering**: `ToolBatchingRule` const + append at `:188`; env section per D5/D14; decision-tree step-4 rewrite (`:160-161`); add `find_files` to the `@Files` mapping (`:214`) — string edits only, no compile dependency on Group A. Tests: new `AssistantPromptComposerEnvironmentTests` — batching rule present in tools path (incl. @-turns), absent no-tools; env block present iff root passed AND tools path; working folder appears inside `<env>`; tree mentions find_files.
- **B2 call-site plumbing**: ChatSessionManager (`:818`), AssistantViewModel (`:2177`), HeadlessTurnExecutor (`:259`, workspace root via `NormalizeWorkspaceRoot`, settings-folder degrade; subpath null headless, matching `:223`). Fix NSubstitute explicit-matcher stubs by appending `Arg.Any<string?>()` wherever the suite surfaces mismatches (headless fixtures expected; `ReturnsForAnyArgs` sites immune). Add one positive test: ChatSessionManager passes the described root to `PrepareTurn`.

### Step C — integration review + gate (single agent, after A and B)

Optional codemaid format on touched files; grep sanity (`find_files` present in GetTools/switch/`:214`/ConfigJson/tests; `NormalizePathArg` not referenced from SafeFolderPath/GitToolHandler; no local TranslateGlob left in GitignoreMatcher); then the full gate.

## Verification

- `dotnet build -t:Rebuild -v:n` Debug AND `-c Release` → `0 Warning(s)` each (read the MSBuild summary line).
- `dotnet test` (no filter) → `failed: 0` (~5855 + new tests, ~1m).
- Prompt sanity: batching sentence contains no "parallel"/"simultaneously"; env block contains no date.
- Runtime spot-check (optional): run the app, ask the assistant "find every md file under docs" in a configured sandbox → expect a single find_files call, not a search_files scan.

## Risks

- **PrepareTurn signature × NSubstitute**: explicit-matcher stubs mismatch where non-null env now flows (headless fixtures) — mechanical `Arg.Any<string?>()` fixes; run the suite and fix every mismatch.
- **GlobPattern extraction** must be verbatim code motion; unchanged GitignoreMatcherTests are the regression net.
- **@-turn prompt bytes change** (env + batching now render on @-turns) — existing tests pin the tool list, not bytes; gate surfaces any pin.
- **did-you-mean ancestor-ignore check** is load-bearing (dir-only ignore rules would otherwise leak names); keep it under test.

Full design detail (per-decision rationale, test enumerations) in the sibling agent plan file `docs-agent-file-tooling-2026-09-01-pia-cached-dewdrop-agent-a5038573893cedb66.md`.

## Exploration findings — Pia FilesToolHandler (grounding for difficulty ratings)

**Adding a new tool (`find_files`) — Effort S.** Route table is auto-built from `GetTools()` (`PluginService.cs:696-707`); no registry edit needed. Touch points: `AIFunctionFactory.Create` entry (`FilesToolHandler.cs:126-142`), schema stub w/ `[Description]` (`:1289-1315`), switch case (`:172-180`), files plugin `ConfigJson` prompt text + `UpdatedAt` (`BuiltInPluginDefaults.cs:93-94`), `@Files` at-command allow-list (`AssistantPromptComposer.cs:211-214`). Tests to update: `PluginServiceFileToolRoutingTests.cs:17-18` (hardcoded 5-name array), `FilesPluginPromptScaffoldingTests.cs:20-24`. No schema-shape snapshot tests → description edits cheap. Read-only tool needs no ToolClassifier/permission changes.

**Did-you-mean on read_file miss — Effort XS.** `read_file` miss today: bare `"Error: File '{requested}' not found."` (`FilesToolHandler.cs:694`). A working directory-level did-you-mean ALREADY EXISTS: `SuggestSimilarDirectories` (`:632-671`), used by search_files (`:396-400`), cap 3, case-insensitive leaf substring both directions. File variant = near-copy with `Directory.EnumerateFiles` on parent. Must keep `SensitivePathGuard.IsBlocked` + ignore filters so suggestions can't leak blocked names. Existing test only asserts `Contains("not found")` → suffix-safe.

**search_files `include` filter — Effort XS-S.** Real loop is `HandleSearchFiles` (`:364-556`), NOT :1309 (that's the schema stub). Insertion point: file loop after ignore check at `:480`. A hand-rolled glob→regex translator exists: `GitignoreMatcher.TranslateGlob` (`Infrastructure/GitignoreMatcher.cs:127+`, supports `*`, `?`, `**`, `[...]`, NonBacktracking, IgnoreCase). No Microsoft.Extensions.FileSystemGlobbing in repo. Cleanest: extract `TranslateGlob` into a shared `GlobPattern` helper — the SAME helper `find_files` needs, so #4 and #5 amortize.

**Truncation/spill — Effort M (design cost, not code).** Real dead ends spill would fix: `read_file` 100K-char window error (`:911-913`) and 1 MB byte ceiling (`:768-770`) — line caps already paginate fine. Write path: `PrepareWriteFile` goes through USER APPROVAL action cards; a spill must be a private direct write, a policy decision. Best location: `.scratch/` (`RunScratchFolder.cs:11`) — already excluded from list_files, searchable when explicitly pointed at (`FilesToolHandler.cs:436,454`), excluded from run promotion. A new `%LOCALAPPDATA%\Pia\tool-output\` would need a third SensitivePathGuard carve-out (`SensitivePathGuard.cs:140-171`) — avoid. No retention sweeper exists except 30-day run cleanup.

**POSIX path normalization — Effort XS.** Single choke point: `SafeFolderPath.TryResolveInsideAllowingAbsolute` (`SafeFolderPath.cs:59-118`), ~12 call sites. `Path.GetFullPath(trimmed, fullRoot)` already accepts `/` separators; `/c/foo` currently roots to current drive then fails containment with generic error. Recommended: normalize in a dedicated `NormalizePathArg` helper at the FilesToolHandler arg-accessor layer (option 2), NOT inside SafeFolderPath (shared with GitToolHandler/RunWorkspaceRedirects — wider blast radius, rewrites input before containment check).

**Shared walk for reuse.** `CollectRelativeFiles` (`:269-308`): iterative DFS, prune-before-descend, containment + SensitivePathGuard + ignore per entry, hard cap `MaxListEntries=500`, UNSORTED. `find_files` shape = add a pattern-predicate param to `CollectRelativeFiles` (fed by glob translator) + sort at call site. Known quirk: `ListRelativeFiles` caps at 500 then filters → `@Files` picker can miss files in big folders; `list_files` output unsorted.

## Exploration findings — Pia prompt composition

**System prompt lifecycle — three paths.** Per-turn recompose: interactive chat (`ChatSessionManager.cs:818`), voice (`AssistantViewModel.cs:2177`), background turn (`BackgroundAssistantTurnRunner.cs:126`). Once-per-RUN cached: `HeadlessTurnExecutor.cs:259-303` (`_runDefault`, deliberate). So env block updates flow automatically next turn on interactive paths; headless run needs separate treatment (frozen system prompt → appended message is the only channel mid-run).

**Env block — Effort S-M.** Composer has NO access to settings/IFilesToolHandler/TaskAmbient (ambient set AFTER compose at `ChatSession.cs:339`). Recommended shape: **optional trailing param on `PrepareTurn`** carrying caller-resolved effective root (repo convention: `IAssistantPromptComposer.cs:20-21`, `AgentPlanner.cs:112-116`); avoids ctor-dep churn in Bootstrapper + 4 test files. Caller `ChatSessionManager` already has both halves: `settings.AssistantFilesFolder` (`:775`) + `session.WorkingDirectory` (`:833`). Narrowing logic is DELIBERATELY duplicated 4 ways (`AgentPlanner.cs:471-477` records "consolidating is bigger and riskier"); a 5th caller-side narrowing is consistent. Date already emitted by `PersonaPromptShape.cs:16-18` ("The current date and time is…") — env block must not duplicate it; either it owns the date or identity line keeps it.

**Mid-session re-point.** `ISettingsService.SettingsChanged` (`ISettingsService.cs:7`, raised `SettingsService.cs:48` sync); FilesToolHandler subscribes at `:80→:93-101`; `AssistantFolderRelocationService.cs:78,115` raises deliberately on folder move. Interactive path picks new root up automatically at next compose (no subscriber needed if env block computed per turn). Event fires on saving thread — dispatcher-post if UI-bound.

**Batching sentence — Effort trivial, one placement trap.** No batching guidance anywhere (verified). Runtime already answers ALL FunctionCallContent per round (`AiClientService.cs:387-390`, dispatch loop `:581,614`) but dispatch is SERIAL — wording must promise round-trips saved, not parallel execution. Placement: alongside `DeclinedActionRule` (`AssistantPromptComposer.cs:118-119`, injected `:188` after `## Output Format`) — the only always-present tools-path slot; `## Tool Selection` is SKIPPED whenever the turn has @-commands (`:41`, `:144`), so a line there vanishes on @-command turns.

**Tool descriptions — Effort trivial.** AITool one-liners at `FilesToolHandler.cs:122-143` are UNPINNED by tests → free to expand with routing advice. Files plugin ConfigJson (`BuiltInPluginDefaults.cs:93`) additions safe; do NOT reword phrases pinned by `FilesPluginPromptScaffoldingTests.cs:20-62` (`"LINE|CONTENT"`, `"RELATIVE"`, `"'..'"`, `"Settings > Assistant"`, vault wording). Files plugin has no server row → hardcoded text authoritative, ships immediately.

**Per-model prompts.** Zero per-model branching (verified); only per-provider capability branches (`SupportsToolCalling` `:88-89`, web search `:373-374`). One composed prompt for all models — the per-model seam is a structural (later) item.

## Exploration findings — opencode reference implementations (V1 = packages/opencode, V2 = packages/core; don't build a chimera)

**Did-you-mean (V1 `read.ts:76-99`).** Case-folded BIDIRECTIONAL substring on basenames (entry contains base OR base contains entry), first 3 in raw dir order, no fuzzy/scoring, no perf guard beyond catch→[]. Message: `File not found: {path}\n\nDid you mean one of these?\n{candidates}` (absolute, one/line; Pia: relative per its contract). Permission checks run BEFORE the suggestion scan.

**Glob tool (V2 `glob.ts`).** Schema: `pattern` (required, "Glob pattern to match files against"), `path` (optional relative, "Defaults to the active Location."), `limit` (optional positive int). Description: "Find files by glob pattern within the active Location. Returns concise relative file resources. Use a relative path to narrow the search and limit to bound the result count." No mtime sort in V2 (raw traversal order); V1 hardcodes limit 100 + verbatim truncation: `(Results are truncated: showing first 100 results. Consider using a more specific path or pattern.)`; `No files found` when empty. V2 model-visible output = one path per line.

**grep `include` (V2).** Single glob string, optional, annotated `File glob to include in the search (for example, "*.js" or "*.{ts,tsx}")` — passed as one ripgrep `--glob=` inclusion. Not a list, not regex.

**windowsPath (`fs-util.ts:257-264`).** Win32-only no-op guard, then 4 prefix-anchored regex replaces, drive letter uppercased, emits `X:/...`: (1) `/c:/…`, (2) Git-Bash `/c/…` (requires `/` or end after the letter, so no false match on 3/4), (3) `/cygdrive/c/…`, (4) `/mnt/c/…`. Everything else unchanged (relative, UNC, already-Windows).

**Env block (V2 `builtins.ts:16-39`).** Block:
```
<env>
  Working directory: {dir}
  Workspace root folder: {root}
  Is directory a git repo: yes|no
  Platform: {platform}
</env>
```
Baseline framing: `Here is some useful information about the environment you are running in:\n{env}` → system prompt. Update framing: `The environment you are running in is now:\n{env}` → appended system-role message when a snapshot-diff detects change. Date is a SEPARATE source in V2 (`Today's date: {d}` / `Today's date is now: {d}`) — matches Pia's existing PersonaPromptShape date line, so Pia's env block should OMIT the date.

**Spill (2nd tranche; for the record).** V1: >2000 lines or >50KB → write `tool_<id>` under data/tool-output, 7-day mtime sweep hourly; inline head-or-tail preview + `The tool call succeeded but the output was truncated. Full output saved to: {file}\nUse Grep to search the full content or Read with offset/limit to view specific sections.` V2: head+tail 50/50 preview, marker only. Access: grep may target the absolute tool-output file (permitted by omission of the external-dir gate — deliberate). Pia mapping: spill under `.scratch/` in the effective root (already list-invisible, search-targetable).

## The drill-down: difficulty × benefit across all opencode pros

Difficulty scale: **Trivial** (strings only) · **XS** (<½ day, no new types) · **S** (1–2 days) · **M** (3–5 days or real design decisions) · **L** (a subsystem). Benefit scale: **Small** · **Med** · **Large** · **Huge**.

| # | opencode pro | Difficulty | Why (grounded) | Benefit | Why |
|---|---|---|---|---|---|
| 1 | Batching sentence | **Trivial** | One prompt const next to `DeclinedActionRule` (`AssistantPromptComposer.cs:118-119`, always-present slot at `:188`); runtime already answers all calls per round | **Large** | Every multi-file turn saves N−1 model round-trips; zero risk |
| 2 | Routing advice in tool descriptions | **Trivial** | AITool one-liners (`FilesToolHandler.cs:122-143`) unpinned by any test; ConfigJson additions safe | **Med** | Fewer filename-lookups executed as content scans; compounds with find_files |
| 3 | Did-you-mean on read_file miss | **XS** | Near-copy of existing `SuggestSimilarDirectories` (`:632-671`); tests suffix-safe | **Large** | Converts the most common failure (plausible-but-wrong path) from retry-blind into one-shot recovery |
| 4 | POSIX path normalization | **XS** | 4 prefix regexes in a path-arg-specific helper; single containment choke point stays untouched | **Small–Med** | Only fires when a model emits `/c/…`-style spellings — common for models trained on POSIX tooling, invisible otherwise |
| 5 | `include` filter on search_files | **XS–S** | Insertion point known (`:480`); glob→regex translator already exists (`GitignoreMatcher.TranslateGlob`) | **Med** | Narrows content scans; also the schema-level prerequisite habit for find_files patterns |
| 6 | `find_files` glob tool | **S** | New tool = 2 declaration sites + switch case + prompt text + 2 test files; route table auto-builds; walk (`CollectRelativeFiles`) and glob translator both reusable | **Huge** | Closes the doc's "real capability gap": `docs/**/*.md` is impossible today; name-lookup stops being a content scan |
| 7 | Env block (effective root, git-repo, platform) | **S–M** | Optional param on `PrepareTurn` + resolve at 4-5 call sites; interactive paths recompose per turn (self-updating); headless computes at `BeginRunAsync`; date stays with `PersonaPromptShape` | **Huge** | The doc's "biggest single gap": model can finally reason about scope instead of guessing |
| 8 | Spill oversized output to file | **M** | Write mechanics trivial; real cost = policy (bypasses write-approval contract), location (`.scratch/` is the answer), retention | **Med** | Fixes two genuine dead ends (100K-char window error, 1 MB ceiling); line caps already paginate fine |
| 9 | Per-model prompt seam | **M–L** | No branching exists anywhere; would introduce a new axis through composer + personas + tests | **Small–Med** (today) | Pia ships one prompt to all models; worth it only when a concrete per-model conflict shows up |
| 10 | Model-callable search delegation (task tool) | **L** | Host-side fan-out exists (`AgentRunOrchestrator`) but a model-callable tool is a new trust/permission surface | **Med** | Context savings on big searches; a desktop documents assistant rarely needs explore-scale sweeps |
| 11 | Per-folder instruction files | **M** | Lazy injection on read + nearest-wins walk; no concept exists in Pia | **Small** | A documents folder has no AGENTS.md culture; doc already rates it low priority |
| — | Shell tool, LSP, absolute-path outputs | excluded | Doc §4: deliberate non-goals — absence of shell is Pia's containment story; LSP is wrong altitude | — | — |

### Mirror-first order (the recommendation)

**Tranche 1 (this plan, one workflow run):** #1 batching, #2 routing advice, #3 did-you-mean, #4 path normalization, #5 include filter, #6 find_files, #7 env block. Rationale: everything Trivial–S with Large/Huge benefit lands together; #5 and #6 share the extracted glob helper so doing them together amortizes the only new type; #2's routing text should be written *after* #6 exists so it can name find_files.

**Tranche 2:** #8 spill-to-file (needs an owner decision on the approval-bypass policy and `.scratch/` retention; mechanics are small once decided).

**Later/structural, on demand:** #9 per-model seam (when a real per-model prompt conflict appears), #10 task tool (only if agent-mode searches start blowing context), #11 per-folder instructions (only if users actually put instruction files in their folders).

(Sections below to be filled from exploration results.)
