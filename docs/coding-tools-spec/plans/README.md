# Coding Toolset — Implementation Plans (Overview)

This directory holds the per-tool planning docs for porting the Hermes-derived **coding toolset** into Pia.Wpf. The toolset is the set of file/search/shell/orchestration tools an agent needs to do real coding work (read/edit files, search, run commands, manage subprocesses, run scripts, delegate to subagents, etc.), specified under `../` (`../overview.md`, `../tool_registration.md`, and one `../<tool>.md` per tool).

**Scope guardrail — these are PLANS, not code.** Nothing here has been implemented, built, or tested. Each doc is a planning artifact that classifies the tool (reuse existing Pia plumbing vs. build from scratch), catalogs the existing code worth reusing, and enumerates the gaps and open questions that must be settled before authoring. No C#, DI wiring, `.csproj`, or XAML was touched. The deliverable is markdown only.

The user is returning to **discuss the open questions** (see the section near the end) before any implementation begins.

## Classification

Sorted Essential → Support → Infra. "Bucket" is whether the tool can largely **reuse** an existing Pia tool/handler or must be built **from scratch**.

| Tool | Tier | Bucket | Existing code to reuse | Plan doc |
|------|------|--------|------------------------|----------|
| read_file | Essential | Reuse | `FilesToolHandler` (existing `read_file`), `SafeFolderPath`, plugin registration chain (`BuiltInPluginDefaults` / `BuiltInPluginHandler` / `PluginService`), `Bootstrapper` DI | [read_file.reuse.md](./read_file.reuse.md) |
| search_files | Essential | From scratch | (none — no content/regex search engine exists; `list_files` only partially covers `target='files'`) | [search_files.plan.md](./search_files.plan.md) |
| patch | Essential | From scratch | (none — no diff/fuzzy-match facility anywhere) | [patch.plan.md](./patch.plan.md) |
| write_file | Essential | Reuse | `FilesToolHandler` (existing `write_file`), `SafeFolderPath`, `BuiltInPluginHandler`, `ActionCardInfo` / `ActionCardBuilder` approval card | [write_file.reuse.md](./write_file.reuse.md) |
| terminal | Essential | From scratch | Plugin scaffolding only (`PluginService`, `BuiltInPluginHandler`); `CabManagerService` `ProcessStartInfo` template as a pattern reference | [terminal.plan.md](./terminal.plan.md) |
| process | Essential | From scratch | (none — no process registry / lifecycle manager; designed jointly with `terminal`) | [process.plan.md](./process.plan.md) |
| execute_code | Essential | From scratch | (none — no runtime, RPC transport, or env-scrub; proxies the other tools so it is sequenced last) | [execute_code.plan.md](./execute_code.plan.md) |
| todo | Support | From scratch | (none — Pia's todo store is persistent SQLite+kanban; wrong lifecycle/shape for an ephemeral per-task list) | [todo.plan.md](./todo.plan.md) |
| clarify | Support | Reuse | `ActionCardInfo` / `ActionCardBuilder` approval flow, `ChatSession` `WaitForUserDecisionAsync`, `PluginService` routing, `IDialogService` | [clarify.reuse.md](./clarify.reuse.md) |
| delegate_task | Support | Reuse | `ChatSessionManager` / `ChatSession` session model, `AssistantPromptComposer` tool gating, `AiClientService` | [delegate_task.reuse.md](./delegate_task.reuse.md) |
| memory | Support | From scratch | Registration/dispatch scaffolding (`BuiltInPluginHandler` factory, `PluginService` route) and possibly `ActionCardInfo`; storage/surface/injection are net-new | [memory.plan.md](./memory.plan.md) |
| session_search | Support | Reuse | `AssistantChatService` + `AssistantChatsFts` (browse/title), `SqliteContext`, `ResearchHistoryToolHandler` read-only handler pattern | [session_search.reuse.md](./session_search.reuse.md) |
| tool_registration | Infra | Reuse | `PluginService` registry/dispatch, `IPluginToolHandler`, `BuiltInPluginHandler`, `McpPluginToolHandler`, `AiClientService`, `BuiltInPluginDefaults` | [tool_registration.reuse.md](./tool_registration.reuse.md) |

## Summary & build order

**Counts: 6 Reuse, 7 From scratch** (`read_file`, `write_file`, `clarify`, `delegate_task`, `session_search`, `tool_registration` reuse; `search_files`, `patch`, `terminal`, `process`, `execute_code`, `todo`, `memory` from scratch).

Note that "Reuse" tracks **substrate/identity reuse** (an existing tool by the same name, or directly applicable plumbing) — it does **not** mean the spec contract is mostly covered. For `read_file` and `write_file` in particular, the plumbing (plugin registration, two-phase dispatch, action cards, `SafeFolderPath`) is reusable but the actual read/write **contract** (path resolution model, line numbering, pagination, lint/delta, atomic write, line-ending/BOM preservation, staleness) is largely net-new.

The discriminator between the buckets is **I/O-substrate reuse vs. generic-plumbing-only reuse.** `read_file`/`write_file` reuse the actual file-I/O substrate (`FilesToolHandler`, `SafeFolderPath`, `DroppedFileReader`). `memory` is bucketed **from scratch** even though a same-named `MemoryToolHandler` exists, because it would reuse *only* the generic registration/dispatch/action-card plumbing that **every** tool reuses — its storage (SQLite+embeddings vs. flat Markdown), tool surface (8 CRUD tools vs. one batched `operations[]`), and prompt-injection lifecycle (none today vs. frozen snapshot at session start) are all disjoint from the spec.

**Recommended build order** (follows the spec overview's ordering):

1. **read_file**
2. **search_files**
3. **patch** — *highest-leverage and hardest.* The 9-strategy fuzzy find/replace engine, V4A multi-file parser, and unified-diff generation must be hand-rolled (per the minimal-dependency preference). Budget accordingly.
4. **write_file**
5. **terminal**
6. **process** (design jointly with `terminal` — shared process registry)
7. **todo**
8. **clarify**
9. **execute_code** — *must come after the core tools*: it proxies `terminal`/`patch`/`search_files`/`read_file`/`write_file`/`web_*`, so it is blocked until those exist.
10. **delegate_task**
11. **memory**
12. **session_search**

`tool_registration` (Infra) is foundational and underpins all of the above — in particular the **task_id threading** and **output budgeting** decisions below should be settled before tool #1.

## Open Questions to Discuss

### Cross-cutting architecture

These recur across most tools and should be decided once, up front. They are deduplicated from the per-tool docs.

1. **task_id threading (settle day one).** `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent, CancellationToken)` carries **no** session/task id. `ChatSession.Id` (`Guid?`) exists at the session level but is never threaded into handlers (`PluginService.RouteToolCallAsync` / `ChatSession.HandleToolCall` pass only the call). Yet per-task_id state is load-bearing for read-dedup, consecutive-loop guards, mtime staleness, the terminal/process cwd+env session, and child-task origination in `delegate_task`. **Decision:** change the handler signature (e.g. add a `ToolContext`/`task_id` param) now, or thread an ambient (`AsyncLocal`, mirroring `TokenMapAmbient.Current`) — and decide whether the key is `ChatSession.Id` or a new delegation-scoped id. Note: an ambient set inside `RunTurnAsync` may be too late for `PrepareTurn` (called earlier in `ChatSessionManager`), so the set-site matters. The spec stresses that retrofitting this is painful.

2. **Filesystem scope: sandbox vs. workspace root.** Every existing file tool is gated on a single configured `AppSettings.AssistantFilesFolder` sandbox via `SafeFolderPath.TryResolveInside`, which **rejects** absolute/rooted/UNC/`..` paths. Coding workflows require absolute paths, session-cwd-relative paths, `~` expansion, and repo/workspace-wide reach. **Decision:** introduce a "workspace root" concept distinct from the privacy sandbox (and decide how it coexists with the shipping sandboxed read/write/delete UX without regressing its security guarantee). Affects `read_file`, `write_file`, `patch`, `search_files`, `terminal`, `process`, `execute_code`, and delegated coding children. Keep all path logging on `SensitiveDebug` per the privacy rules.

3. **Extend-vs-rebuild `FilesToolHandler`.** The shipping `FilesToolHandler` has working sandboxed `read_file`/`write_file`/`delete_file`/`list_files` with action-card approval. The spec's contracts are far richer. **Decision (make once for `read_file`/`write_file`/`patch`/`search_files` together, since they share lint/delta + mtime-staleness machinery):** extend the handler in place (risks regressing the simple working sandbox UX and its `ActionCardBuilder` Details contract) vs. build a parallel coding-file handler with the new workspace-root resolution model alongside the existing sandbox handler.

4. **Native vs. MCP delegation.** `terminal`, `process`, and `search_files` could in principle be delivered through an external shell/filesystem MCP server via `McpPluginToolHandler` / `StdioClientTransport` instead of native built-in handlers. **Decision:** native gives the task_id-scoped cwd/env, rolling output buffer, and shared process registry the spec demands (hard to get from a generic MCP server); MCP avoids building process lifecycle. (For `delegate_task`, `memory`, and `session_search` this fork is effectively closed — they are intrinsic to Pia's own session/DB model and must be native.)

5. **Code-execution security / consent model.** `terminal` and `execute_code` run arbitrary commands/scripts on the user's machine — the apex concern for a privacy-first assistant. The existing per-write-op `ActionCard` approval guard is **not** a command-pattern risk guard. **Decision:** define the consent model — per-command action-card approval, a workspace-trust/allowlist gate, the spec's dangerous-pattern command guard (hardline=block, dangerous=ask-and-remember, safe=allow), `execute_code` env-scrub, and/or stronger sandboxing — and whether arbitrary shell exec is even in scope for release. Note headless children under `delegate_task` raise the same issue: a child's write/execute tools surface action cards no foreground user is watching, so strip write+execute tools from children or define an auto-decline policy.

6. **Python runtime + minimal-deps for `execute_code`.** No Python runtime exists or is referenced anywhere in the app. The spec's RPC transport is language-agnostic; only the helper stub is Python-specific. **Decision:** bundle CPython, depend on system Python, or reframe the sandbox language to C# scripting (Roslyn). The user prefers minimal/hand-rolled dependencies, which also bears on `patch` (hand-roll the diff generator, fuzzy matcher, and a `SequenceMatcher.ratio` equivalent rather than pulling DiffPlex/diff-match-patch/FuzzySharp) and on structured-doc extraction for `read_file` (`.ipynb`/`.docx`/`.xlsx` — hand-roll zip+XML/JSON, pull a library, or defer).

7. **Output budgeting + schema sanitization (registry-level).** No output budgeting exists today (`AiClientService` wraps the full result string; the only truncation is logging-only at 500 chars). No per-tool `max_result_size_chars` cap, no persisted-output temp-file mechanism, no per-turn aggregate spill. Schemas also pass raw to the provider with no sanitizer for the 6 hostile shapes. **Decision:** build the budgeting subsystem and decide whether to implement the full schema sanitizer now (justified once MCP/coding tools land) or defer the shapes Pia's current providers tolerate.

8. **Shared cross-tool state location.** `read_file`'s mtime-at-read store is consumed by `write_file` and `patch` for staleness warnings, so it must live in a cross-tool service keyed by (task_id + resolved_path), not as private handler state. Decide where this lives alongside the task_id decision (#1).

9. **Does Pia compress/summarize conversation context at all?** Verified **absent** today (no compress/summarize/prune-history path in `AiClientService` or `AssistantPromptComposer`). This is load-bearing for **three** tools: `read_file`'s `reset_read_dedup` hook (the spec has the host call it "after summarizing context"), `todo`'s headline "survives context compression" benefit, and `memory`'s frozen-snapshot lifecycle. **Decision:** if Pia never compresses context, these features need an alternative trigger (TTL / turn-count eviction for the read-dedup cache; an explicit refresh for memory) — or a compression step must be built. Settle before `read_file`/`todo`/`memory`. (This question was previously buried under `todo`; hoisted here because it spans three tools.)

### Per-tool questions

- **read_file** — Structured-doc extraction: `.docx`/`.xlsx` are already extracted by `Helpers/DroppedFileReader.cs` (`DocumentFormat.OpenXml`, already shipping) — reuse it; only `.ipynb` (JSON) is net-new. Confirm `DroppedFileReader`'s whole-document output composes with the line-numbered/windowed read contract. Separately, confirm Pia's actual vision-tool name for the image→vision redirect — there is **no** agent-callable vision tool today (images flow in as attachments), so the invariant-1 image branch likely returns "unsupported binary / attach the image instead" rather than naming a tool.
- **write_file** — Extend vs. rebuild `write_file` given the richer atomic-write/lint/delta/BOM/line-ending contract (decide together with `read_file`/`patch` — see cross-cutting #3); how the absolute `resolved_path` + workspace-divergence warning interact with the relative-only sandbox.
- **patch** — Note the deliberate path-model asymmetry (V4A header paths reject `..` but replace-mode `path=` allows `..` for worktree nav), which `SafeFolderPath` cannot honor verbatim; decide whether `patch` hard-depends on the (unbuilt) lint/LSP capability or degrades gracefully (diff without lint).
- **search_files** — ripgrep availability: bundle `rg.exe`, depend on system `rg`, or deliver via MCP (no `rg`/`grep`/`find` on Windows, only `findstr`); extend `list_files` in place vs. a separate coding-search handler; matched lines/paths are file-content payload, so logging must use `SensitiveDebug`.
- **terminal** — Confirm the consent model for arbitrary shell (see cross-cutting #5); whether terminal/process is native vs. MCP-backed (the shared process-registry requirement leans native).
- **process** — Designed jointly with `terminal`: process registry, terminal launch path, interactive stdin, rolling buffer, and process-tree kill all need building; consent gating; native vs. MCP.
- **execute_code** — Dependency/sequencing: it proxies tools that don't exist yet (build after the core 7); runtime fork (cross-cutting #6); whether the script-exec + tool-proxy is native vs. MCP; proxied calls re-enter real tools so they need the same task_id context (#1).
- **todo** — Carry task_id via ambient vs. signature change (cross-cutting #1); does Pia perform mid-conversation context compression/summarization at all? If not, the "survives compression" headline benefit is undeliverable by the handler alone. Should this ephemeral agent-todo be visible alongside the existing persistent user-facing todo tools, or namespaced/gated to coding contexts to avoid model confusion?
- **clarify** — Extend `ActionCardInfo`/`ActionCardBuilder` to carry a string result + up-to-4 choice rows vs. give `clarify` a first-class `ChatSession` path; background-session behavior (sits in `WaitingForTool`, surfaces via `BackgroundChatNotificationSurface`) — is that the desired UX? Cancel semantics — what tool-result string to return on user cancel.
- **delegate_task** — Child task_id == `ChatSession.Id` or a new delegation-scoped id (origin point for #1); headless action-card/consent policy for children (cross-cutting #5); enforce `max_concurrent_children` as a new gate distinct from the `MaxRetainedSessions=8` reaper; isolation completeness (children need own terminal/cwd/process-registry, none of which exists yet); the spec needs a deny-list (subtractive) tool filter but `AssistantPromptComposer` only supports allow-lists today.
- **memory** — **Coexistence (sharpest fork):** does the Hermes `memory` tool replace Pia's existing SQLite memory subsystem (objects + embeddings + `AtCommandDomain.Memory`), sit alongside it as a separate agent-facing store, or unify? Approval model (spec implies silent proactive writes; Pia gates every memory write behind an action card); storage location (recommend `%LOCALAPPDATA%\Pia`, not the sandbox); where the frozen-at-session-start snapshot attaches given multi-assistant + background-chats while preserving the cache-stable prompt prefix.
- **session_search** — Existing `AssistantChatsFts` is one aggregate row per chat (cannot supply `match_message_id`/±window/per-message snippet) — a new message-level FTS5 index is required for DISCOVERY; integer message-id contract vs. Pia's GUID `Id` (map to per-chat `Ordinal`); `Ordinal` is reassigned on every save, so returned ids are only short-lived-stable (document the caveat); `profile` param should be dropped (Pia is single-profile).

## Verification flags

Every per-tool doc was run through an independent verification pass (`verify.ok=true` and `bucketCorrect=true` for all 13, with the verifier's suggested bucket matching the assigned bucket in every case). The following items are surfaced for human scrutiny:

- **`todo.plan.md` — hallucinated references (already corrected in the doc).** The verifier caught three citations that were the plan's own framing presented as if they were spec citations, and fixed them in place:
  - A reference to "6 cross-cutting questions" / numbered question list that does not exist in `overview.md` or `tool_registration.md` (overview has 10 numbered design *principles*, no questions list) — relabeled as the plan's own design decisions.
  - An output-cap citation to overview "principles #1, #8" — those principles are about line-numbered reads and pagination; output budgeting actually lives in `tool_registration.md §4`.
  - A `TodoToolHandler.cs:62` `SensitiveDebug` call site — line 62 is a `#if DEBUG` `Debug.WriteLine`, not the `SensitiveDebug` helper (only `:83` is).
  - The same doc also had a **factual ordering correction**: `PrepareTurn` runs in `ChatSessionManager` *before* `RunTurnAsync`, so the proposed "set ambient in `RunTurnAsync`, composer reads it" wiring would read a null/stale ambient — flagged and corrected to require setting the ambient before `PrepareTurn` (ties into cross-cutting #1).
- **No other doc reported hallucinated references.** Several docs had minor, non-blocking corrections applied during verification (mostly directory-prefix fixes — e.g. plugin files live under `src/Pia.Wpf/Services/Plugins/` — and line-number drift), all noted as immaterial.
- **Confidence levels to weigh:** `read_file`, `delegate_task`, and `clarify` are tagged **medium** confidence (the rest **high**) — chiefly because of the extend-vs-rebuild path-model fork (`read_file`), the headless-await turn-entry design (`delegate_task`), and the card-extension-vs-dedicated-path fork (`clarify`). Scrutinize those three first.

## Critic notes

A completeness pass over the full set (all 13 docs present, none missing) plus an independent source re-check. The verification blocks already in the per-tool docs were a useful baseline but **not a safety net** — the items below were missed by every per-tool verify pass.

### 1. `read_file` — `.docx`/`.xlsx` extraction is a **false gap / misframed open question** (missed reuse)

`read_file.reuse.md` open-question Q5 frames structured-doc extraction (`.ipynb`/`.docx`/`.xlsx`, spec invariant 4) as a fork between *"pull a library (conflicts with the user's minimal-dependency preference)"* and *"hand-roll (zip+XML for docx/xlsx)."* **Both horns are wrong for docx/xlsx:**

- `DocumentFormat.OpenXml` **3.3.0 already ships** as a `PackageReference` (`src/Pia.Wpf/Pia.Wpf.csproj:51`). No new dependency, no minimal-deps tension.
- `src/Pia.Wpf/Helpers/DroppedFileReader.cs` **already extracts** `.docx` (paragraph text via `WordprocessingDocument`, `ReadDocxAsync`) and `.xlsx` (TSV with shared-string + inline-string resolution via `SpreadsheetDocument`, `ReadXlsxAsync`), plus BOM-detecting UTF-8 text reads (`ReadTextAsync`) and a `FileKind` classifier (Text/Docx/Xlsx/Pdf/Image/Audio extension sniff, `Classify`).

So `.docx`/`.xlsx` extraction is **reuse**, and the `FileKind` classifier partly serves invariant 1 (binary/image detection before read). Only `.ipynb` (JSON cell parsing) is genuinely net-new. The doc should cite `DroppedFileReader` as reusable substrate and narrow Q5 to "`.ipynb` only." `read_file`'s overall bucket (reuse) is unaffected.

> **✓ Resolved** — applied in `read_file.reuse.md` (§4.5, gap row G10, the build checklist, and open-question Q5 now cite `DroppedFileReader`/`DocumentFormat.OpenXml` and narrow net-new work to `.ipynb`). Provenance kept: this gap was missed by every per-tool verify pass and caught only at the completeness-critic stage.

### 2. `read_file` — `vision_analyze` redirect target does **not** exist as a tool (confirmed, doc already hedges)

Grep for vision/image-analyze tools finds only `TextOptimizationService`, persona fields, and `DroppedFileImporter` (image **attachment** import), no agent-callable vision tool. Pia surfaces images as message attachments, not via a `vision_analyze` tool. The doc correctly flags this as unverified — recorded here as confirmed-absent so the implementer knows the image branch of invariant 1 needs a real redirect target (or just "unsupported binary") rather than a tool name.

### 3. `memory` — scratch holds, but the README's own "Reuse" definition does not explain why (internal inconsistency)

By the README's stated criterion — *"Reuse tracks substrate/identity reuse (an existing tool **by the same name**, or directly applicable plumbing)"* — `memory` looks identical to the `read_file`/`write_file` rationale: there is an existing same-named subsystem (`MemoryToolHandler`, 8 tools; `MemoryService`) and the **same** registration/dispatch/`ActionCardInfo` plumbing those two get reuse credit for. Yet `memory` is bucketed scratch. The scratch verdict is **correct** on the merits (verified):

- **Storage disjoint:** spec = two flat Markdown files (`MEMORY.md`/`USER.md`); Pia = SQLite objects + embeddings + hybrid search.
- **Surface disjoint:** spec = one `memory` tool with an atomic `operations[]` batch + substring `old_text` matching; Pia = 8 CRUD/search tools each behind an action card.
- **Injection disjoint and absent:** spec = a **frozen snapshot into the system prompt at session start** (cache-stable prefix). `MemoryToolHandler` has **no** memory-content system-prompt injection at all (no `GetSystemPromptAddition` returning memory content; memory is reached only via the query tools), and the only injection path, `AssistantPromptComposer.BuildSystemPrompt` → `GetCombinedSystemPromptAdditions`, runs **per turn**, not snapshot-at-start.

The principled distinction is that file tools reuse actual read/write **I/O substrate** whereas `memory` reuses only the **generic plumbing every tool reuses** — but the README's one-line definition doesn't capture it. **Action:** either add one sentence to §"Summary & build order" distinguishing identity/substrate reuse from generic-plumbing reuse, or accept the scratch label as-is with this note. Not a bucket error.

> **✓ Resolved** — the distinguishing paragraph ("I/O-substrate reuse vs. generic-plumbing-only reuse") was added to §"Summary & build order".

### 4. False-scratch audit (the task's primary ask) — clean for the remaining five

`search_files`, `patch`, `terminal`, `process`, `execute_code` were re-checked against both the native codebase and the MCP path; **no missed reuse found:**

- No content/regex search engine (`ripgrep`/`rg`/`grep` absent; only `findstr` on Windows). `list_files` does not cover content mode.
- No diff/fuzzy-match facility anywhere (`DiffPlex`/`diff-match-patch`/`FuzzySharp`/`SequenceMatcher` all absent from packages and source).
- No Python runtime, no arbitrary-shell exec, no process registry. All `Process.Start` usages are narrow: URL/browser open (Auth, FirstRunWizard, PiaSourceChip, Markdown, AccountSettings), `expand.exe` cab extraction, `node --version`/`where.exe` preflight, and MCP stdio (which hides streams).
- **MCP path closed today:** `.mcp.json` configures only a **playwright** (browser-automation) server — no filesystem/shell MCP server is wired. So native-vs-MCP for terminal/process/search_files is a pure build decision, not a live reuse path. (`McpPluginToolHandler` confirms a generic MCP server cannot supply the task_id-scoped cwd/env + rolling buffer + shared process registry these tools require — `StdioClientTransport` owns the stdio internally.)

### 5. Cross-cutting confirmations and additional open questions

- **task_id threading (cross-cutting #1) verified absent:** `IPluginToolHandler.HandleToolCallAsync(FunctionCallContent, CancellationToken)` carries no session/task id (`src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs:19`). Every doc that depends on it is correct.
- **No mid-conversation context compression/summarization exists** (verified: no compress/summarize/prune-history path in `AiClientService` or `AssistantPromptComposer`; the lone "summarize" hit is a prompt-style instruction). This is owned by `todo.plan.md`'s open question, but it is broader: it **also** gates `read_file` invariant 9 (`reset_read_dedup` is called by the host "after summarizing context") and `memory`'s "frozen snapshot" model. Recommend hoisting "does Pia compress/summarize context at all, and if not, who builds it?" into the **cross-cutting** list, since three tools depend on a mechanism that does not exist. **✓ Resolved** — hoisted as cross-cutting question **#9**.
- **Additional open question — image ingestion vs `read_file`:** Pia already ingests images/docx/xlsx as **chat attachments** via `DroppedFileImporter`/`DroppedFileReader`. Decide whether coding `read_file` reuses that path for structured docs and defers images to attachment ingestion (no `vision_analyze` tool needed), avoiding a duplicate extraction stack.
