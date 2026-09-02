# find_files / working-folder hint — live UI walkthrough

**Status:** Complete — 13 of 13 planned checks ran, 13 passed; both findings fixed same day and
live-validated 2026-09-02 (see Findings, then Live validation)
**Owner:** Marco Altmann
**Written:** 2026-09-01
**Origin:** Commit `dfa7d763` "Let the assistant find files by glob, and tell it where it is
working", merged to `feature/agent_issues` as `4e6f811c`. Requested as a live-app test of the new
folder/file capabilities.

## What was under test

`dfa7d763` added six behaviours. This run exercised all of them against the real app:

1. A `find_files` tool taking a path glob.
2. An `include` file glob on `search_files`.
3. An `## Environment` prompt section naming the folder the file tools resolve against.
4. A `read_file` miss that suggests near-matching sibling names.
5. Unix-style drive spellings (`/c/x`, `/mnt/c/x`, `/cygdrive/c/x`) accepted as paths.
6. Folder-shaped (`docs/`) and backslash-spelled (`docs\x`) globs matching what they plainly mean.

Plus the batching rule the same commit added to the system prompt, and the rewritten tool
descriptions that are supposed to steer name lookups to `find_files` and content lookups to
`search_files`.

## Setup

- Build: Debug at `4e6f811c` (Debug matters — `IsDevMode` puts the logger at `Debug`, which is what
  makes the `SensitiveDebug` tool-args and tool-result lines visible). `src/` built clean; the test
  project did **not** compile at the time of the run (`IngestStateStoreTests.cs(24,9): CS0103
  TempPath`, from another session's in-flight edits), so `dotnet test` was never the pre-flight here
  — the merge was checked by direct inspection instead. All of `find_files`, the `include` glob,
  `NormalizePathArg` at its six call sites, `DescribeEffectiveRoot`, `BuildEnvironmentSection`,
  `SuggestSimilarFiles`/`Directories`, `IsUnderIgnoredDirectory` and the rewritten plugin prompt are
  present in the merged tree.
- Profile: the real one. Sandbox root `C:\Users\maltm\Documents\Pia Assistant`,
  `assistantDefaultWorkingDirectory = Playground`, so the effective root for every chat below was
  `C:\Users\maltm\Documents\Pia Assistant\Playground`.
- Provider: DeepSeek (`819d7d72`), resolving to `deepseek/deepseek-v4-flash` via OpenRouter. Set for
  the run and **restored to Pia Cloud afterwards**.
- Driver: WinWright/UIA. Playwright drives browsers only and cannot reach a WPF window.

### Verification channel

The UI shows a reply, not the tool traffic, so the reply alone cannot tell you whether a dictated
glob reached the handler intact. Two Debug-only log lines in `AiClientService` do:

- `Tool call <name> (callId=…) args: {…}` — the gate for *was this case actually exercised*.
- `Tool <name> handler result (N chars): …` — the gate for *did it behave*.

Every result below is read off those, not off the model's prose. That mattered: in the four-pattern
case the model's summary was correct but lossy, and the truncation notice only appears in the raw
result.

### Fixture

Reproducible — the whole run rebuilds from this:

```sh
P="C:/Users/maltm/Documents/Pia Assistant/Playground"; G="$P/GlobLab"
mkdir -p "$G/notes/archive" "$G/src/deep" "$G/vaultish" "$G/node_modules/package-a" "$G/bin"
printf 'GlobLab root readme. Token ZEBRAFISH appears here.\n'          > "$G/README.md"
printf 'Meeting notes.\nZEBRAFISH was discussed at length.\n'          > "$G/notes/meeting-notes.md"
printf 'Roadmap\n- phase one\n- ZEBRAFISH milestone\n'                 > "$G/notes/roadmap.md"
printf 'scratch pad\nZEBRAFISH scribble\n'                             > "$G/notes/scratch.txt"
printf 'stale backup with ZEBRAFISH\n'                                 > "$G/notes/old.ignoreme"
printf 'Archive readme, no token.\n'                                   > "$G/notes/archive/README.md"
printf 'Legacy notes, no token.\n'                                     > "$G/notes/archive/legacy-notes.md"
printf 'public class Widget { /* ZEBRAFISH */ }\n'                     > "$G/src/Widget.cs"
printf 'public class Helper { }\n'                                     > "$G/src/Helper.cs"
printf 'Source folder readme.\n'                                       > "$G/src/README.md"
printf 'public class Nested { /* ZEBRAFISH */ }\n'                     > "$G/src/deep/Nested.cs"
printf '{ "token": "ZEBRAFISH" }\n'                                    > "$G/src/deep/config.json"
printf 'hidden notes in an ignored folder. ZEBRAFISH.\n'               > "$G/vaultish/hidden-notes.md"
printf 'secret in an ignored folder. ZEBRAFISH.\n'                     > "$G/vaultish/secret.md"
printf 'package index with ZEBRAFISH\n'                                > "$G/node_modules/package-a/index.md"
printf 'build output with ZEBRAFISH\n'                                 > "$G/bin/build-output.md"
printf 'vaultish/\n*.ignoreme\n'                                       > "$P/.piaignore"
```

Three design points, each of which a naive fixture gets wrong:

- **`.piaignore` goes at the effective root, not in `GlobLab`.** `SandboxIgnore.ForRoot` reads the
  ignore files from the root it is handed and nowhere else, so an ignore file inside the fixture
  folder would have been silently inert and every ignore assertion below would have been vacuous.
- **Three `README.md` at three depths** — the only way to tell an anchored pattern from an
  unanchored one.
- **`hidden-notes.md` inside the ignored `vaultish/`.** `"hidden-notes.md".Contains("notes.md")` is
  true, so it is exactly what a missing ignore check would leak as a "did you mean" suggestion.

Both the fixture and the root `.piaignore` were deleted after the run.

## Results

`root` below means `…\Pia Assistant\Playground`. "Predicted" was computed from `GlobPattern.Compile`'s
anchoring rules before the run, not from shell-glob intuition.

### Group A — the Environment section

| # | Check | Result |
|---|---|---|
| A1 | "Which folder do your file tools resolve against? Do not call a tool." | **PASS** — replied `C:\Users\maltm\Documents\Pia Assistant\Playground`, zero tool calls. |
| A2 | File tools OFF → no folder named | **PASS** — tool count dropped 50 → 43 and the reply was `NO FOLDER NAMED. The instructions refer to "the assistant files folder" … but never specify an exact path`. |

A1 is the whole point of the change and it lands: the folder is named, and it is the *effective*
root (sandbox + working directory), not the bare sandbox root.

There is a behavioural corroboration of A1 in B0 below — the model emitted a root-anchored relative
glob unprompted and it resolved on the first call — but it is the same observation, not a separate
check, and it is not counted twice. This run never exercised the file tools *without* the
Environment section, so it says nothing about what the model would have done instead.

### Group B — `find_files`

| # | Pattern (verbatim in args) | Predicted | Actual | |
|---|---|---|---|---|
| B0 | `GlobLab/**/*.md` — chosen by the model, not dictated | 6 | 6 | **PASS** |
| B1 | `README.md` | 3 | 3 | **PASS** |
| B2 | `GlobLab/notes/` | 5 | 5 | **PASS** |
| B3 | `GlobLab\notes\*.md` | 2 | 2 | **PASS** |
| B4 | `GlobLab/**/*` with `limit=3` | 11 found, 3 shown | 11 found, 3 shown | **PASS** |

- **B0** was the natural-language prompt "list every markdown file under GlobLab". The model reached
  straight for `find_files` — not `list_files`, not `search_files` — and anchored the glob itself.
  Returned all six `.md` under `GlobLab` and correctly excluded `Demo/PrioritizedActionPlan.md`,
  which is a `.md` sibling one level up.
- **B1** returned `GlobLab/notes/archive/README.md`, `GlobLab/README.md`, `GlobLab/src/README.md` —
  a bare name matching a trailing segment at three different depths, while B2/B3's interior slash
  anchored to the root. Both halves of the anchoring rule confirmed in one round.
- **B2** is the folder-shaped fix. `GlobLab/notes/` returned all five non-ignored files under
  `notes`, including the two in `archive/`. Before the fix this compiled to a pattern no file path
  can satisfy.
- **B3** is the backslash fix, and it survived the JSON round-trip as `"GlobLab\\notes\\*.md"`.
- **B4** produced the truncation notice verbatim: `(Results are truncated: showing first 3 results.
  Consider using a more specific path or pattern.)`

All four dictated patterns reached the handler **character-for-character** — the model did not
silently normalise any of them, so every one of these is a real test of the handler and not of the
model's spelling.

**Ignore handling, measured rather than asserted:** every `find_files` run from the root reported
`scanned 23 file(s)`, and 23 is exactly the number of files the walk can still reach — `.git/`,
`node_modules/`, `bin/` and `vaultish/` were pruned as *directories*, so their contents were never
enumerated at all. (`scanned` is incremented before the per-file ignore test, so the one remaining
file-rule casualty, `notes/old.ignoreme`, is counted as visited but excluded from the hits.) B4's 11
hits is 23 minus the 11 files outside `GlobLab` minus that one `.ignoreme` — the same arithmetic
from the other side.

### Group C — `search_files include`

| # | Call | Predicted | Actual | |
|---|---|---|---|---|
| C1 | `pattern=ZEBRAFISH, path=GlobLab` | 7 | 7 | **PASS** |
| C2 | `pattern=ZEBRAFISH, path=GlobLab, include=*.md` | 3 | 3 | **PASS** |

C1 returned `README.md`, `src/Widget.cs`, `src/deep/config.json`, `src/deep/Nested.cs`,
`notes/meeting-notes.md`, `notes/roadmap.md`, `notes/scratch.txt`. C2 returned exactly the three
`.md` of those. The token is also present in `vaultish/`, `node_modules/` and `bin/` and appeared in
neither run.

### Group D — `read_file` paths and suggestions

| # | Path (verbatim in args) | Result | |
|---|---|---|---|
| D1 | `GlobLab/notes/notes.md` | `Error: File '…' not found. Did you mean: GlobLab/notes/meeting-notes.md?` | **PASS** |
| D2 | `GlobLab/vaultish/notes.md` | `Error: File '…' not found.` — no suggestion | **PASS** |
| D3 | `/c/Users/…/GlobLab/README.md` | read; normalised to `C:/Users/…` | **PASS** |
| D4 | `/mnt/c/Users/…/GlobLab/src/Helper.cs` | read; normalised to `C:/Users/…` | **PASS** |
| D5 | `find_files pattern=*.md path=GlobLab/note` | `Error: Path 'GlobLab/note' was not found … Did you mean: GlobLab\notes?` | **PASS** |

D1 and D2 are the pair that matters. Both ask for `notes.md`; both have a sibling whose name
contains it. D1 suggests, D2 stays silent — `IsUnderIgnoredDirectory` is doing its job and the names
inside the ignored folder did not leak.

Note the suggestion rule is **substring**, not edit distance: a transposition typo like
`meting-notes.md` produces no suggestion at all, because neither name contains the other. That is
the design, but it means the feature helps with truncations and wrong extensions, not with typos.

### Group E — containment, unchanged by the new path spellings

`NormalizePathArg` runs *before* the containment check, so a POSIX drive spelling is the one place
this commit could have opened an escape. It did not:

| Path | Normalised to | Result |
|---|---|---|
| `/c/Windows/win.ini` | `C:/Windows/win.ini` | `Error: Path is outside the assistant files folder.` |
| `/mnt/c/Windows/win.ini` | `C:/Windows/win.ini` | `Error: Path is outside the assistant files folder.` |
| `../../../Windows/win.ini` | — | `Error: Path is outside the assistant files folder.` |

All three logged `WARN read_file rejected path outside sandbox`.

### Group F — tool batching and tool choice

The commit also tells the model to batch independent lookups. On DeepSeek it does, every time:

| Prompt | Round 1 |
|---|---|
| four dictated `find_files` | `4 tool call(s) detected: find_files ×4` |
| two dictated `search_files` | `2 tool call(s) detected: search_files ×2` |
| five dictated path calls | `5 tool call(s) detected: read_file ×4, find_files` |
| three containment calls | `3 tool call(s) detected: read_file ×3` |

Every one of these resolved in a single round — previously they would have been up to five
round-trips.

The sharpest check of the rewritten tool descriptions was a single natural-language prompt carrying
one name question and one content question: *"where does the file called roadmap live, and which
files mention ZEBRAFISH?"*

```
Round 1: 2 tool call(s) detected: find_files, search_files
  find_files   args: {"pattern":"**/*roadmap*"}          → 1 match
  search_files args: {"pattern":"ZEBRAFISH","mode":"files"} → 7 matches
```

It split them correctly and ran both in one round. This is the behaviour the commit's "reach for
find_files when you know what the file is called and search_files when you know what is written in
it" sentence was written to produce.

## Findings

Two, both cosmetic. No correctness or containment defect was found.

1. **The two tools disagree about path separators in their output.** `find_files` emits
   `GlobLab/notes/roadmap.md`; `search_files` emits `GlobLab\notes\roadmap.md`; the `find_files`
   directory suggestion emits `GlobLab\notes`. The new `## Environment` section tells the model
   "use forward slashes", and `find_files` obeys, so the model now sees the instruction contradicted
   by two of its own tool results. Backslash paths are accepted on input, so nothing breaks — but
   `find_files`'s `NormalizeSeparators` on the hit list has no counterpart in `search_files`'s
   emitter or in `SuggestSimilarDirectories`, which returns a raw `sub.Substring(...)`.

2. **`search_files`'s `scanned` count cannot show that `include` narrowed anything.** Both C1 and C2
   logged `scanned 12 file(s)` despite C2 filtering down to three files. Confirmed in the code, not
   inferred: `filesScanned++` (`FilesToolHandler.cs:686`) runs before the ignore test and before the
   `includeGlob` continue, so the counter measures directory enumeration, not work done. The 12 is
   every file under `GlobLab` outside a pruned directory, `notes/old.ignoreme` included. Log-only —
   the results are right — but that counter is the cheapest signal that a glob had any effect, and
   for `include` it is flat.

### Fixed (2026-09-01, same branch)

Both findings, plus three smaller observations from this run, were fixed in a follow-up pass on
`FilesToolHandler.cs`:

1. **Separators** — `search_files` result rows and the `SuggestSimilarDirectories` suggestion now
   pass through `NormalizeSeparators`, so every tool result spells paths with forward slashes.
   Backslash input is still accepted; four existing tests that asserted the backslash form were
   updated to the forward-slash contract.
2. **Counters** — the log line now carries a second counter for files actually read:
   `search_files scanned {N} file(s), searched {M}, {K} match(es), mode {mode}`, so an `include`
   that narrowed the search is visible. `scanned` deliberately stays pre-filter — it is what
   `MaxFilesScanned` bounds, and moving it would let a narrow `include` over a huge tree walk
   unbounded.

And from the run notes:

- The `find_files` truncation notice now names the total (`showing first 3 of 11 results`), so
  the caller can tell "3 of 4" from "3 of 5000" — previously the total only appeared handler-side
  in the log (B4).
- The "did you mean" suggestions got a bounded edit-distance fallback (OSA so a transposition costs
  one keystroke; threshold scales with name length) behind the unchanged substring rule, so
  `meting-notes.md` now suggests `meeting-notes.md`. The ignored-folder guard is untouched and
  the D2 no-leak case is locked by tests on both the file and directory paths.
- `find_files`' `limit` description now says results are sorted alphabetically, so a small limit
  is understood as a name-ordered prefix, not a representative sample.

## Not covered, and why

Three cases were out of reach for a live chat walkthrough and remain unit-test-only:

- **The planned-run negative** (`planned ? null` in `ChatSessionManager`) — an agent run's steps work
  in an isolated workspace that is not provisioned at prompt-composition time, so the folder is
  deliberately not named. Reaching it needs a full planned run with an approval round, which is a
  different walkthrough. Covered by `AssistantPromptComposerEnvironmentTests`.
- **The voice-turn case** (`DescribeEffectiveRoot(null)` in `AssistantViewModel`) — un-narrowed on
  purpose, so it resolves at the sandbox root whatever chat is on screen. Needs a live microphone
  turn.
- **An invalid glob** (`Error: Invalid glob pattern: …`) — `GlobPattern.Compile` handles unterminated
  classes and empty classes by falling back to literals, so no glob a model would plausibly emit
  reaches the `ArgumentException` path.

## Housekeeping

- Fixture `Playground/GlobLab` and the root `Playground/.piaignore` deleted. The `.piaignore` in
  particular had to go — left behind it would silently change what every future file-tool call in
  that profile can see.
- Provider restored to Pia Cloud; `assistantFileToolsEnabled` restored to on.
- The six test chats were **left in place** in the real chat history (titles all begin with the
  prompt text, e.g. "Make exactly two search_files calls…"). They are the primary record of the run
  and were synced to the cloud like any other chat; delete them if you want them gone.

## Live validation of the fixes (2026-09-02)

**Status:** Complete — 8 of 8 checks ran, 8 passed, no findings
**Driver:** WinWright/UIA, Debug build of `dd08f52d` plus the uncommitted `FilesToolHandler.cs`
fixes. Left uncommitted deliberately; this run does not change that.
**Provider:** DeepSeek (`819d7d72`) again, set for the run and restored to Pia Cloud afterwards.
**Profile:** the real one, same effective root `…\Pia Assistant\Playground`, fixture rebuilt
verbatim from the block above.

The "Fixed" list was written from unit tests and a green gate. This run is the live evidence for
it. Every row below is read off the Debug-only `Tool call … args:` / `Tool … handler result` lines,
one fresh chat per check.

Two gates before any check counted: `Data directories: … Overridden=False` for the real profile,
and `Resolved provider for mode Assistant: DeepSeek` for the provider — the settings file on disk
is not evidence for either. `useSameProviderForAllModes` is on, so the provider had to be switched
through the *Optimize* combo, which drives both modes; there is no separate Assistant combo in that
state.

| # | Fix under test | Args as they reached the handler | Observed | |
|---|---|---|---|---|
| G1 | Truncation names the total | `{"pattern":"GlobLab/**/*","limit":3}` | `(Results are truncated: showing first 3 of 11 results. …)` | **PASS** |
| G2 | `search_files` rows forward-slashed | `{"pattern":"ZEBRAFISH","path":"GlobLab","include":"*.md"}` | `GlobLab/README.md`, `GlobLab/notes/meeting-notes.md`, `GlobLab/notes/roadmap.md` — zero backslashes | **PASS** |
| G3 | Directory suggestion forward-slashed | `{"pattern":"*.md","path":"GlobLab/archiv"}` | `Did you mean: GlobLab/notes/archive?` | **PASS** |
| G4 | File fuzzy fallback | `{"path":"GlobLab/notes/meting-notes.md"}` | `Did you mean: GlobLab/notes/meeting-notes.md?` | **PASS** |
| G5 | Directory fuzzy fallback | `{"pattern":"*","path":"GlobLab/nots"}` | `Did you mean: GlobLab/notes?` | **PASS** |
| G6 | Second counter shows narrowing | (the G2 call) | `search_files scanned 12 file(s), searched 6, 3 match(es), mode content` | **PASS** |
| G7 | Ignored tree still not suggested | `{"pattern":"*","path":"GlobLab/vaultis"}` | error, **no** suggestion | **PASS** |
| G8 | `limit` description reaches the model | `{"pattern":"GlobLab/**/*"}` | all 11 in case-insensitive path order; model quoted the description verbatim | **PASS** |

Notes on the ones that carry real information:

- **G6 is the fix, G2 is the same call.** `scanned 12` is the pre-filter walk; `searched 6` is the
  six non-ignored `.md` files the `include` left. Before the second counter the line reported only
  the 12, identically whatever `include` was passed — which is exactly the flat-counter finding.
  `searched 6` was predicted from the fixture before the run and landed. `scanned` was predicted as
  13 and came back 12; recounting the fixture gives 12 (`notes/` holds four files, not five), so
  that was a miscount on the prediction side, not a surprise from the handler.
- **G7 is the check that could have regressed.** `vaultish` matches `vaultis` by the substring rule
  *and* at edit distance 1, so the ignored-tree guard had to hold on the new fuzzy branch as well as
  the old one. It does — the prune sits above the name test, so a fuzzy candidate never reaches the
  suggestion list. This is the D2 no-leak case, re-run against the changed loop body.
- **G8 needed a channel that does not exist in the log.** Tool *schemas* are logged by name only,
  so the description text is not in the tool traffic. Two substitutes, and they agree: the model
  quoted it back verbatim including the novel clause "not a representative sample" (confabulation
  would not reproduce that string), and the unrestricted call returned all 11 paths in exactly the
  case-insensitive order the description claims, so the sentence is also behaviourally true.
- **Binary provenance came free.** The `searched` counter exists only in the patched code, so its
  presence in the log proves the running binary carried the uncommitted fixes rather than a stale
  build. A UTF-16 scan of `Pia.Wpf.dll` for `searched {Searched}` and `sorted alphabetically`
  confirmed the same thing before launch.
- **The typo cases survived the model.** DeepSeek honoured "do not correct the spelling" in all
  four of them (`archiv`, `meting-notes.md`, `nots`, `vaultis`), each arriving at the handler
  unaltered. A silent autocorrect was the most likely way one of these checks would have
  degenerated into a vacuous pass, so the `args:` line is what makes them count.
- Batching still works: G5 and G7 were dictated as one batch and arrived as
  `Round 1: 2 tool call(s) detected: find_files, find_files`.

Nothing in the fixes needed a write tool, so this run drove read-only tools only — which is also
why the real profile was the safer choice than a throwaway one (a throwaway roaming dir seeds
`assistantFilesFolder` at the *real* vault and its ingest migration then deletes topic pages).

### Housekeeping

- Fixture `Playground/GlobLab` and `Playground/.piaignore` deleted; `Playground` verified back to
  its four pre-run entries.
- Provider restored to the value recorded before the run — `modeProviderDefaults` back to
  `00000000-…-0001` for both modes, confirmed in the UI and on disk.
- The six test chats were **left in place** in the real chat history, same call as the first run:
  they are the record of this run, and deleting them is the irreversible option.
