# Attachment token budget: size dropped files by tokens and by the model that answers

**Status:** Implemented on `feature/files-tool-pdf`; in review as a PR into `develop`
**Owner:** Marco Altmann
**Written:** 2026-10-14
**Origin:** Owner brief 2026-10-14 ("Die Größengrenzen für Datei-Anhänge sollen nach Tokens entscheiden
statt nach Zeichen", with five open questions), on top of commit `6ef6a010` (PDFs through @Files,
`read_file`, `search_files`) on `feature/files-tool-pdf`. Support feedback: users report that attached
files "do not work" — the owner confirmed the failure is **only the beginning of the file arrives**. All
five questions were decided with the owner in a brainstorming session the same day; §2 records the
answers, §3 the measurements they were decided on.

Companion tracking surface: [2026-10-14-attachment-token-budget-checklist.md](2026-10-14-attachment-token-budget-checklist.md).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. **The owner's rule for this work overrides the skills' "commit" steps: never commit and
> never push until the owner says so.** Every "Checkpoint" below means: tests green, tick the checklist in
> the working tree, move on — no `git commit`.

**Goal:** A file dropped into the Assistant arrives whole far more often (6–10× today), and when it is
still cut, the user and the model both see how much arrived and the rest is reachable.

**Architecture:** A dependency-free, run-based `TokenEstimator` replaces character counts. An
`AttachmentBudget` turns the window of the provider that will answer (persona-aware) into a per-chat
token budget — 25 % of the input window, clamped 2K–50K — that every attachment still re-sent in the live
chat draws on. Files are still cut at drop time (owner decision), fairly across one drop, at a line
boundary; the model's note is built later, in `StartTurnAsync`, where it is known whether the turn
offers `read_file`.

**Tech Stack:** .NET 10 WPF (`net10.0-windows10.0.17763.0`), CommunityToolkit.Mvvm, WPF-UI 4.3,
xunit.v3 3.2.2 + NSubstitute 6.2, Microsoft.ML.Tokenizers 2.0.0 (already referenced), test-only
`Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0.

**Spec:** this document — §1 (what is there), §2 (decisions), §3 (measurements), §4 (design). The tasks
in §8 argue from those sections.

---

## 1. What is there today (verified 2026-10-14)

- **Drop → chip.** `AssistantViewModel.ExecuteHandleFilesDropped` (`src/Pia.Wpf/ViewModels/AssistantViewModel.cs:2050`)
  calls `DroppedFileAttachmentImporter.TryStageAsync` (`src/Pia.Wpf/Helpers/DroppedFileAttachmentImporter.cs:30`).
  Fixed caps `MaxFileChars = 20_000` per file and `MaxTotalChars = 40_000` across the open chips; the
  text is cut **at drop time**, mid-line, by character count. `PendingFileAttachment`
  (`src/Pia.Wpf/Models/PendingFileAttachment.cs`) carries `Truncated` + `OriginalCharCount`.
- **What the user sees.** `Msg_File_Truncated` = *"„{0}“ wurde gekürzt, damit es in die Nachricht
  passt."* — **no number**, gone after 4 s. The pending chip (`src/Pia.Wpf/Views/AssistantView.xaml:531-611`)
  shows nothing about the cut. `Msg_File_AttachBudget` names the message as full.
- **What the model sees.** `AssistantPromptComposer.BuildAttachedFileBlock`
  (`src/Pia.Wpf/Services/AssistantPromptComposer.cs:375`) adds `note="Showing the first N of M
  characters."`. Its comment says a dropped file has no path the model could re-open — **no longer true**:
  the chip's save button (`ExecuteSavePendingFile`, `AssistantViewModel.cs:2067`) copies the file into
  the chat's working directory, where `read_file` reaches it.
- **Hard refusals before that** (unchanged by this plan): `DroppedFileReader.MaxTextBytes` (1 M chars of
  extracted text) and `MaxContainerBytes` (8 MB file).
- **Re-sent every turn, never compacted.** The block is stored on `AssistantMessage.AttachedFileContext`
  and appended in `BuildChatMessage` (`src/Pia.Wpf/Models/AssistantMessage.cs:388`) on **every later
  turn**. Interactive chats are deliberately never compacted (`src/Pia.Wpf/ViewModels/Models/ChatSession.cs:402-408`).
  `AttachedFileContext` is not persisted (`AssistantMessageMapper` maps names only), so a reloaded chat
  stops paying for it. The 40K cap is **per message**; three messages with attachments re-send 120K chars
  every turn.
- **Provider resolution happens late.** `ChatSessionManager.StartTurnAsync` resolves
  `persona.PreferredProviderId ?? GetDefaultProviderForModeAsync(Assistant)` at
  `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs:802-805`, after the user message (with its
  `AttachedFileContext`) was built at `:747`. Whether `read_file` is offered is known only after
  `PrepareTurn` at `:868`.
- **Windows.** `ProviderService` stamps `ContextWindowDefaults.For(model)` on every loaded provider
  (`Fallback = 128_000`); `AgentContextBudget.From(provider)` (`src/Pia.Wpf/Models/AgentContextBudget.cs`)
  returns `(WindowTokens, MaxOutputTokens)` or null on a typo (output ≥ window).
- **@Files and read_file.** `FilesToolHandler.PromptPreviewMaxChars = 12 * 1024` ("~3K-token safety net")
  plus `FilePreviewLines = 100` (`ChatSessionManager.cs:80`); `read_file` windows are capped by
  `MaxReadLimit = 2000` lines and `MaxFormattedWindowChars = 100 * 1024`. Both cost **one turn only**: the
  preview is injected into the current user message only (`ChatSession.cs:419-433`), and interactive
  history carries no tool payloads (`ChatToolSummary`).
- **Docs promise the opposite.** Pia.Docs `wpf/guides/attachments.mdx` (EN/DE/FR, in the Pia repo) says
  Pia sends *"its whole text along with your message"* and that ordinary documents *"are never a
  problem"*.
- `Microsoft.ML.Tokenizers` 2.0.0 is referenced only for the SentencePiece embedding tokenizer
  (`EmbeddingService`).

## 2. Decisions (owner, 2026-10-14)

| # | Question | Decision |
|---|---|---|
| Q1 | Estimator | **(b+) run-based heuristic with a calibrated letter weight**, dependency-free. Not Length/4 (2.6× under on numbers, 2.5× under on CJK), not tiktoken (exact only for OpenAI, +1.2 MB). Not per Pia UI language (§3.2). Calibration rule: on no reference sample may the estimate be more than **5 % under** o200k. |
| Q2 | Budget source | **(C) a share of the active model's input window, per chat**: `clamp(0.25 × (WindowTokens − MaxOutputTokens), 2_000, 50_000)`, shared by every attachment the live chat still re-sends plus the open chips. One file may use all of it; the files of one drop share it fairly (water-filling). |
| Q3 | When to cut | **(a) at drop time**, as today — against the provider the turn *would* use (persona-aware). A provider change before send does **not** re-cut. |
| Q4 | What users and the model see | All four: snackbar with a **percentage**; chip badge **"gekürzt · ~60 %"** while pending (no marker after send); the chip's existing **save** button is highlighted on a cut file so the rest becomes reachable (click only, never automatic); the model note is in **lines**, cut at a line boundary, and names `read_file` with `offset` only when the file is reachable and the turn offers `read_file`. |
| Q5 | @Files preview and `read_file` caps | **(b) switch the character safety nets to the token estimate**, same magnitude: preview ~3,000 tokens, `read_file` window ~25,000 tokens; no provider coupling. |
| — | Test reference | o200k counted **live** in tests — `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0 in the **test project only**. |
| — | Calibration outcome | Measured on the planned corpus (§3.3), the 5 % rule pins the letter weight at **1.0**: the PDF-table sample is already 0.96 at 1.0 and 0.92 at 0.9. The weight stays a single constant held by the corpus test, so relaxing the rule later is a one-line change. |

Unchanged on purpose: `DroppedFileImporter` (Optimize, Advanced Creation — inserts into the composer, no
cap besides `MaxTextBytes`), `MaxTextBytes`, `MaxContainerBytes`, `MaxPendingFiles = 5`, the
`AgentContextCompactor`.

## 3. Measurements

Method: Appendix A. Texts went through the real `DroppedFileReader` (what the model receives). "÷"
columns are o200k tokens ÷ estimate: **above 1 = the estimate undercounts** (context overflow), below 1 =
overcounts (cuts early, safe).

### 3.1 Length/4 versus the heuristic (letter weight 1.0)

| Sample | chars | Length/4 | o200k | cl100k | o200k ÷ Length/4 | o200k ÷ heuristic |
|---|---:|---:|---:|---:|---:|---:|
| DE prose, docx (contract) | 49,138 | 12,284 | 11,257 | 14,386 | 0.92 | 0.79 |
| DE prose, PDF (terms) | 16,851 | 4,212 | 3,859 | 4,929 | 0.92 | 0.78 |
| PDF with number tables (BMF travel allowances 2026) | 15,373 | 3,843 | 5,172 | 5,773 | **1.35** | 1.00 |
| xlsx → TSV, numbers (git statistics) | 41,230 | 10,307 | 26,915 | 26,916 | **2.61** | 1.00 |
| xlsx → TSV, import template | 408 | 102 | 134 | 139 | 1.31 | 0.89 |
| C# code | 118,979 | 29,744 | 25,410 | 25,086 | 0.85 | 0.74 |
| XAML | 69,665 | 17,416 | 12,594 | 12,490 | 0.72 | 0.73 |
| JSON (deps.json) | 93,046 | 23,261 | 29,847 | 29,705 | 1.28 | 0.88 |
| EN prose (CLAUDE.md) | 15,483 | 3,870 | 3,952 | 3,938 | 1.02 | 0.76 |
| ZH, CJK lines only | 213 | 53 | 127 | 170 | **2.40** | 0.77 |
| JA, CJK lines only | 357 | 89 | 224 | 292 | **2.52** | 0.75 |

Cost of o200k as a runtime dependency (rejected): 1.2 MB DLL, 122 ms cold start, 131 ms per 1 M chars.
The heuristic: 9 ms per 1 M chars. A SentencePiece tokenizer (the embedding one) counts the numeric TSV
as 15,005 instead of 26,915 — vendors differ by up to 1.8× on the same text, so no local tokenizer is
"right" for Claude, Gemini or local models.

### 3.2 Per Pia UI language — same content (Pia.Docs `wpf/**`, ~160–190K chars each)

| Language | Length/4 vs o200k | heuristic vs o200k | Length/4 vs cl100k | heuristic vs cl100k |
|---|---:|---:|---:|---:|
| EN | 0.90 | 0.73 | 0.91 | 0.74 |
| DE | 0.95 | 0.78 | **1.14** | 0.94 |
| FR | 0.95 | 0.76 | **1.09** | 0.87 |

o200k chars/token: EN 4.45, DE 4.19, FR 4.19 — 6 % apart, while content type moves Length/4 from 0.95
to 2.61 within German alone. The UI language also says nothing about the language of a dropped file.

### 3.3 Calibration on the planned reference corpus (estimate ÷ o200k; the test's metric)

| Sample (Task 1 fixtures) | chars | o200k | w 1.0 | w 0.95 | w 0.9 | w 0.85 |
|---|---:|---:|---:|---:|---:|---:|
| cjk | 181 | 127 | 1.43 | 1.43 | 1.43 | 1.43 |
| csharp | 6,043 | 1,406 | 1.32 | 1.28 | 1.24 | 1.19 |
| de-pdf-table | 3,822 | 1,528 | **0.96** | 0.94 | 0.92 | 0.90 |
| de-prose | 4,849 | 1,200 | 1.25 | 1.19 | 1.14 | 1.09 |
| en-prose | 4,247 | 981 | 1.36 | 1.31 | 1.25 | 1.20 |
| fr-prose | 4,625 | 1,106 | 1.32 | 1.27 | 1.21 | 1.16 |
| xaml | 5,331 | 846 | 1.39 | 1.35 | 1.31 | 1.27 |
| numeric TSV (generated) | 13,769 | 8,578 | 1.00 | 1.00 | 1.00 | 1.00 |

Rule "≥ 0.95" → **weight 1.0**. Upper bound held by the test: ≤ 1.5 (CJK is the ceiling at 1.43).

### 3.4 What the budget means (one file, prose ≈ 4 chars/token, ≈ 2,800 chars/page)

| Model window | Budget | German prose | Numeric xlsx rows | Today |
|---|---:|---|---|---|
| 128K (GPT-4o, Mistral, fallback) | 32,000 | ~45 pages | ~1,700 | ~7 pages / ~500 rows |
| 200K (Claude) | 48,000–50,000 | ~70 pages | ~2,600 | same |
| 1M (Gemini, GPT-4.1/5) | 50,000 (cap) | ~70 pages | ~2,600 | same |
| 8K local (when configured correctly) | 2,000 | ~3 pages | ~100 | same (overflows today) |

## 4. Design

```
drop ─► ExecuteHandleFilesDropped
          └► TryStageAsync(paths, pending, availableTokens: AvailableAttachmentTokensAsync, …)
               phase 1: classify / dedupe / count cap / read / refuse   (unchanged rules)
               phase 2 (only if ≥1 readable file):
                 remaining = await availableTokens() − Σ Estimate(open chips)
                   availableTokens = AttachmentBudget.ForChat(persona-aware provider)
                                     − Σ Estimate(AttachedFileContext of Messages)
                 grants   = AttachmentBudget.Distribute(Estimate(file) for each file, remaining)
                 grant < need and < MinShownTokens → skip, Msg_File_AttachBudget
                 cut      = TokenEstimator.CutToBudget(text, grant)   (line boundary; inside the next line when whole lines fill < ½ grant)
                 chip.Cut = AttachmentCut(ShownLines, TotalLines, EndsMidLine, ShownPercent) | null
                 cut → Msg_File_Truncated(name, percent)
chip    ─► badge "shortened · ~N %" + save button highlighted while Truncated
send    ─► StartTurnAsync(…, files: chips)
             user message gets a provisional block (no read_file hint) — survives a setup failure
             after PrepareTurn: block rebuilt with readFilePath resolver
               resolver = turn offers "read_file" ? f ⇒ FilesToolHandler.ToReadFilePath(f.SavedRelativePath ?? f.FullPath, workingDir) : null
             note: "Showing lines 1-N of M. The rest was not sent[; read it with read_file (path: P, offset: N+1)]."
@Files  ─► CapForPrompt(text, 100 lines, 3,000 tokens)
read_file ─► FormatWindow refuses over ~25,000 estimated tokens
```

**Amendment 2026-10-15 (owner, after the final review):** the cut is at a line boundary unless whole
lines would fill less than half the grant — then it continues inside the next line (`EndsMidLine`).
This replaces "unless line one alone is over" (Task 1's code), which let a blank or short first line
before one huge line (a minified file, an export) arrive as nothing or as a few words.

New units (each testable alone):

- `Pia.Helpers.TokenEstimator` — `int Estimate(string?)`, `TextCut CutToBudget(string, int)`.
- `Pia.Helpers.AttachmentBudget` — `int ForChat(AiProvider?)`, `int[] Distribute(IReadOnlyList<int>, int)`.
- `Pia.Services.AssistantTurnProvider` — `Task<AiProvider?> ResolveAsync(Persona?, IProviderService)`,
  the one place the persona-over-default rule lives.
- `Pia.Models.AttachmentCut` — `record (int ShownLines, int TotalLines, bool EndsMidLine, int ShownPercent)`.
- `IFilesToolHandler.ToReadFilePath(string path, string? workingSubpath)` — the path `read_file` accepts
  for a file, or null.

## 5. Global constraints

Every task's requirements implicitly include this list.

- **No commits, no pushes** until the owner says so (owner rule for this work).
- Test gate = the **built exe**, never `dotnet test`, output **redirected, never piped**:
  `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1` then
  `grep -E "TEST EXECUTION SUMMARY" -A 2 test.log`. Bar: `Failed: 0` on the full unfiltered run.
- Filtered runs: one filter **kind** per run — several `-class "<FQN>"` are OR-ed; never combine
  `-class` with `-namespace` (that runs 0 tests). Build first:
  `dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -v:q`.
- A locked build output is usually a stale runner: `taskkill /IM Pia.Wpf.Tests.exe /F`.
- Zero warnings: `Directory.Build.props` sets `TreatWarningsAsErrors`; finish with
  `dotnet build -t:Rebuild -v:n` and `dotnet build -t:Rebuild -v:n -c Release`, reading
  `0 Warning(s)` / `0 Error(s)` off the summary.
- Logging: no file contents and no file names outside `SensitiveDebug`; token counts, line counts and
  percentages are fine at Information.
- Comments: default none; one short line when the WHY is not obvious; no history, no task/decision ids;
  no `<summary>` on private/internal members unless the signature cannot carry the meaning. When you
  touch a member, bring **its** comments into line.
- Namespaces are `Pia.*` (not `Pia.Wpf.*`).
- Strings: every new/changed key in `*.resx`, `*.de.resx`, `*.fr.resx`; German uses "du", French "tu".
- Constants (exact): `TokenEstimator.LetterWeight = 1.0`; `AttachmentBudget.WindowShare = 0.25`,
  `MinTokens = 2_000`, `MaxTokens = 50_000`; `DroppedFileAttachmentImporter.MinShownTokens = 200`;
  `FilesToolHandler.PromptPreviewMaxTokens = 3_000`, `MaxFormattedWindowTokens = 25_000`; corpus band
  `[0.95, 1.5]`; test packages `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0 and
  `Microsoft.Bcl.Memory` 10.0.11.
- Release notes: `docs/release_notes/README.md` rules (80 columns, one bullet level, ≤ 4 lines). Do not
  touch the existing PDF bullet in `RELEASE.md`.

## 6. Review focus

The inputs most likely to bite a user that no single happy-path test exercises — each has its test in the
owning task:

1. **A chat that already re-sends attachments** — a new drop must see the reduced budget and, when it is
   spent, skip the file with the "remove an attached file or start a new chat" message rather than overflow. → Task 6,
   `HandleFilesDropped_SharesTheBudgetWithAttachmentsTheChatAlreadySends`.
2. **One line longer than the whole budget** (minified JSON, a CSV export without line breaks) — cut
   inside the line, never an empty chip, and the note re-reads that line (`offset = N`). → Task 1
   `CutToBudget_CutsInsideALineThatAloneIsTooLong`, Task 4 `…_ReReadsALineThatWasCutShort`, Task 5
   `TryStageAsync_ASingleHugeLine_ArrivesCutMidLine`.
3. **A persona pinned to a small provider** — the budget must come from that provider, not the Assistant
   default. → Task 3 `ResolveAsync_PrefersThePersonasProvider`, Task 6
   `HandleFilesDropped_UsesThePersonasProviderForTheBudget`.
4. **`read_file` hint where it cannot work** — a turn without `read_file`, a file outside the chat's
   working directory, never an absolute user path. → Task 7 (`ToReadFilePath_*`), Task 8
   `StartTurnAsync_ACutFile_WithoutReadFileOnTheTurn_SaysTheRestWasNotSent`.
5. **A send whose setup fails** (no provider yet) — the user message must still carry the file text so
   Regenerate works after the user fixes the provider. → Task 8
   `StartTurnAsync_WithFiles_SetupFails_KeepsTheFileTextForRegenerate`.

## 7. File map

| File | Change |
|---|---|
| `src/Pia.Wpf/Helpers/TokenEstimator.cs` | **new** — estimate + line-boundary cut |
| `src/Pia.Wpf/Helpers/AttachmentBudget.cs` | **new** — per-chat budget + fair distribution |
| `src/Pia.Wpf/Services/AssistantTurnProvider.cs` | **new** — persona-aware provider resolution |
| `src/Pia.Wpf/Models/PendingFileAttachment.cs` | `AttachmentCut` record; `Cut` replaces `Truncated`/`OriginalCharCount` |
| `src/Pia.Wpf/Helpers/DroppedFileAttachmentImporter.cs` | budget-driven two-phase staging |
| `src/Pia.Wpf/Services/AssistantPromptComposer.cs` | note in lines, optional `read_file` hint |
| `src/Pia.Wpf/Services/Interfaces/IFilesToolHandler.cs`, `src/Pia.Wpf/Services/FilesToolHandler.cs` | `ToReadFilePath`; token caps for preview and window |
| `src/Pia.Wpf/ViewModels/Models/IChatSessionManager.cs`, `ChatSessionManager.cs` | `files` parameter, late block build, shared provider rule |
| `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` | budget callback on drop; pass chips on send |
| `src/Pia.Wpf/Views/AssistantView.xaml` | chip badge, save-button highlight |
| `src/Pia.Wpf/Resources/Strings/MessageStrings{,.de,.fr}.resx` | `Msg_File_Truncated` (2 args), `Msg_File_AttachBudget` |
| `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | 3 new keys |
| `tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj` | 2 test packages, corpus copy rule |
| `tests/Pia.Wpf.Tests/TestData/TokenCorpus/*.txt` | **new** — 7 reference samples |
| `tests/Pia.Wpf.Tests/Helpers/TokenEstimatorTests.cs`, `TokenEstimatorCorpusTests.cs`, `AttachmentBudgetTests.cs` | **new** |
| `tests/Pia.Wpf.Tests/Services/AssistantTurnProviderTests.cs`, `FilesToolHandlerReadFilePathTests.cs` | **new** |
| existing tests (listed per task) | migrated |
| `docs/release_notes/RELEASE.md` | one bullet |
| Pia repo: `src/Pia.Docs/src/content/docs/{,de/,fr/}wpf/guides/attachments.mdx` | size section, "whole text" claim |

## 8. Tasks

### Task 1: `TokenEstimator` and the reference corpus

**Files:**
- Modify: `tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
- Create: `tests/Pia.Wpf.Tests/TestData/TokenCorpus/{en-prose,de-prose,fr-prose,de-pdf-table,csharp,xaml,cjk}.txt`
- Create: `src/Pia.Wpf/Helpers/TokenEstimator.cs`
- Test: `tests/Pia.Wpf.Tests/Helpers/TokenEstimatorTests.cs`, `tests/Pia.Wpf.Tests/Helpers/TokenEstimatorCorpusTests.cs`

**Interfaces:**
- Produces: `public static class Pia.Helpers.TokenEstimator { int Estimate(string? text); TextCut CutToBudget(string text, int maxTokens); internal const double LetterWeight = 1.0; }`
  and `public readonly record struct Pia.Helpers.TextCut(string Text, int ShownLines, int TotalLines, bool EndsMidLine) { bool Truncated { get; } }`.
  `CutToBudget` throws `ArgumentOutOfRangeException` for `maxTokens <= 0`.

- [ ] **Step 1: Test packages and corpus copy rule.** In `tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`, add to the
  first `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.ML.Tokenizers.Data.O200kBase" Version="2.0.0" />
    <!-- O200kBase ships only a netstandard2.0 dependency group, which pulls Microsoft.Bcl.Memory 9.0.4 (NU1903). -->
    <PackageReference Include="Microsoft.Bcl.Memory" Version="10.0.11" />
```

  and extend the `TestData` item group:

```xml
    <None Update="TestData\TokenCorpus\*.txt">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
```

  Run `dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -v:q`. Expected: build succeeds. If NuGet
  audit reports any other `NU190x` for a package the data package pulls in, lift that package the same
  way (direct reference to its patched version, one comment line) — never `<NoWarn>`.

- [ ] **Step 2: Create the corpus.** From the repo root in Git Bash (Pia repo checked out at `G:\Git\Pia`):

```bash
C=tests/Pia.Wpf.Tests/TestData/TokenCorpus; mkdir -p "$C"; D=/g/Git/Pia/src/Pia.Docs/src/content/docs
for l in en de fr; do src=$D/wpf/guides/coding-tools.mdx; [ $l != en ] && src=$D/$l/wpf/guides/coding-tools.mdx
  awk '/^---$/{fm=!fm; next} fm{next} /^```/{code=!code; next} code{next} /^[[:space:]]*[A-Za-zÀ-ÿ]/ && !/\|/ && !/^import / && !/^[[:space:]]*</' "$src" > "$C/$l-prose.txt"; done
sed -n 1,150p src/Pia.Wpf/Helpers/DroppedFileReader.cs > "$C/csharp.txt"
sed -n 530,612p src/Pia.Wpf/Views/AssistantView.xaml > "$C/xaml.txt"
```

  `cjk.txt` (UTF-8, exactly these six lines):

```text
请帮我总结这份季度报告的要点，并列出需要跟进的事项。
我们下周一上午十点开会，讨论预算、人员安排和项目进度。
如果数据有问题，请直接联系财务部门的负责人。
この資料の要点をまとめて、来週の会議で話し合うべき課題を三つ挙げてください。
予算と担当者の割り当ても確認したいので、表にしてもらえると助かります。
締め切りは金曜日の午後五時です。よろしくお願いします。
```

  `de-pdf-table.txt`: the PDF-table text is the sample that pins the calibration, so it must be real
  PdfPig output, not retyped. Build the Appendix A tool and run
  `dotnet run -- extract "G:\Git\Neo42.SideQuest\docs\bmf\2025-12-05-steuerliche-behandlung-reisekosten-2026.pdf" bmf.txt`,
  then `sed -n 80,420p bmf.txt > tests/Pia.Wpf.Tests/TestData/TokenCorpus/de-pdf-table.txt` (a BMF
  letter is an official work, § 5 UrhG). Check sizes against §3.3 (± a few percent is fine). Do **not**
  add the confidential contract/terms texts measured in §3.1.

- [ ] **Step 3: Write the failing unit tests** — `tests/Pia.Wpf.Tests/Helpers/TokenEstimatorTests.cs`:

```csharp
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

public sealed class TokenEstimatorTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("1", 1)]
    [InlineData("123", 1)]
    [InlineData("1234", 2)]
    [InlineData("1234567", 3)]
    public void Estimate_CountsDigitRunsInGroupsOfThree(string? text, int expected) =>
        Assert.Equal(expected, TokenEstimator.Estimate(text));

    [Fact]
    public void Estimate_CountsEveryCjkCharacter() =>
        Assert.Equal(6, TokenEstimator.Estimate("请帮我总结这"));

    [Fact]
    public void Estimate_FoldsASingleSpaceIntoTheNextWord() =>
        Assert.Equal(2, TokenEstimator.Estimate("word word"));

    [Fact]
    public void Estimate_ChargesAWhitespaceRunOnce() =>
        Assert.Equal(3, TokenEstimator.Estimate("word\t\t\tword"));

    [Fact]
    public void Estimate_ChargesPunctuationRunsInPairs() =>
        Assert.Equal(2, TokenEstimator.Estimate("();"));

    [Fact]
    public void Estimate_ChargesNumbersFarAboveLengthOverFour()
    {
        var text = string.Concat(Enumerable.Repeat("2026-10-14\t13\t238\t111\t127\t0.318\n", 100));

        Assert.True(TokenEstimator.Estimate(text) > 2 * (text.Length / 4));
    }

    [Fact]
    public void CutToBudget_KeepsTextThatFits()
    {
        var cut = TokenEstimator.CutToBudget("alpha\nbeta\n", 100);

        Assert.False(cut.Truncated);
        Assert.Equal("alpha\nbeta\n", cut.Text);
        Assert.Equal(2, cut.ShownLines);
        Assert.Equal(2, cut.TotalLines);
    }

    [Fact]
    public void CutToBudget_CountsCrLfLinesOnce() =>
        Assert.Equal(2, TokenEstimator.CutToBudget("a\r\nb\r\n", 100).TotalLines);

    [Fact]
    public void CutToBudget_ACrLfTextGrantedItsOwnEstimate_IsNotCut()
    {
        // docx and xlsx extracts end their lines with CRLF.
        var text = string.Concat(Enumerable.Repeat("alpha beta gamma\r\n", 50));

        Assert.False(TokenEstimator.CutToBudget(text, TokenEstimator.Estimate(text)).Truncated);
    }

    [Fact]
    public void CutToBudget_CutsAtALineBoundary()
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i} of the report"));

        var cut = TokenEstimator.CutToBudget(text, 50);

        Assert.True(cut.Truncated);
        Assert.False(cut.EndsMidLine);
        Assert.Equal(100, cut.TotalLines);
        Assert.InRange(cut.ShownLines, 1, 99);
        Assert.Equal(string.Join('\n', text.Split('\n').Take(cut.ShownLines)), cut.Text);
        Assert.True(TokenEstimator.Estimate(cut.Text) <= 50);
    }

    [Fact]
    public void CutToBudget_CutsInsideALineThatAloneIsTooLong()
    {
        var oneLine = string.Join(',', Enumerable.Range(0, 5000));

        var cut = TokenEstimator.CutToBudget(oneLine, 100);

        Assert.True(cut.Truncated);
        Assert.True(cut.EndsMidLine);
        Assert.Equal(1, cut.ShownLines);
        Assert.Equal(1, cut.TotalLines);
        Assert.NotEmpty(cut.Text);
        Assert.StartsWith(cut.Text, oneLine, StringComparison.Ordinal);
        Assert.True(TokenEstimator.Estimate(cut.Text) <= 100);
    }

    [Fact]
    public void CutToBudget_NeverSplitsASurrogatePair()
    {
        // The '#' joins the emoji's punctuation run, so the largest fitting prefix ends on a high surrogate.
        var text = "#" + string.Concat(Enumerable.Repeat("😀", 400));

        var cut = TokenEstimator.CutToBudget(text, 51);

        Assert.False(char.IsHighSurrogate(cut.Text[^1]));
    }

    [Fact]
    public void CutToBudget_RejectsAnEmptyBudget() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenEstimator.CutToBudget("text", 0));
}
```

- [ ] **Step 4: Write the failing corpus test** — `tests/Pia.Wpf.Tests/Helpers/TokenEstimatorCorpusTests.cs`:

```csharp
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.ML.Tokenizers;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>Holds the estimate to o200k on real text: never more than 5 % under, never more than 1.5× over.</summary>
public sealed class TokenEstimatorCorpusTests
{
    private const double MaxUnder = 0.95;
    private const double MaxOver = 1.5;

    private static readonly Tokenizer O200k = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static TheoryData<string> Samples() =>
        ["en-prose.txt", "de-prose.txt", "fr-prose.txt", "de-pdf-table.txt", "csharp.txt", "xaml.txt", "cjk.txt"];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Estimate_StaysInTheBandAroundO200k(string sample) =>
        AssertInBand(sample, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "TokenCorpus", sample)));

    [Fact]
    public void Estimate_StaysInTheBandOnANumericSheet() => AssertInBand("numeric TSV", NumericTsv());

    private static void AssertInBand(string name, string text)
    {
        var actual = O200k.CountTokens(text);
        var ratio = TokenEstimator.Estimate(text) / (double)actual;

        Assert.True(ratio is >= MaxUnder and <= MaxOver,
            $"{name}: estimate/o200k = {ratio.ToString("F2", CultureInfo.InvariantCulture)} at o200k {actual}, " +
            $"outside [{MaxUnder}, {MaxOver}]");
    }

    // An xlsx extract is what Length/4 undercounts 2.6×; generated, so the shape is fixed without a fixture.
    internal static string NumericTsv()
    {
        var random = new Random(42);
        var sb = new StringBuilder("Datum\tDateien\tHinzugefügt\tEntfernt\tNetto\tQuote\n");
        var day = new DateOnly(2026, 1, 1);
        for (var i = 0; i < 400; i++)
        {
            var files = random.Next(1, 120);
            var added = random.Next(0, 7000);
            var removed = random.Next(0, 3000);
            var quote = added + removed == 0 ? 0 : Math.Round(removed / (double)(added + removed), 3);
            sb.Append(day.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\t')
              .Append(files).Append('\t').Append(added).Append('\t').Append(removed).Append('\t')
              .Append(added - removed).Append('\t').Append(quote.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }
}
```

  If `TheoryData<string>` does not accept a collection expression in this xunit.v3 version, use
  `new TheoryData<string> { "en-prose.txt", … }`.

- [ ] **Step 5: Run to see them fail.** Build the test project; it fails to compile (`TokenEstimator` does not
  exist). That is the expected red.

- [ ] **Step 6: Implement** — `src/Pia.Wpf/Helpers/TokenEstimator.cs`:

```csharp
namespace Pia.Helpers;

/// <summary>A conservative token estimate from character runs, so numbers and CJK cost what tokenizers charge.</summary>
public static class TokenEstimator
{
    // Calibrated on TestData/TokenCorpus: the PDF-table sample allows nothing below 1.0 within 5 % of o200k.
    internal const double LetterWeight = 1.0;

    public static int Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(Raw(text.AsSpan()));

    /// <summary>The longest prefix within <paramref name="maxTokens"/>, cut at a line break unless line one alone is over.</summary>
    public static TextCut CutToBudget(string text, int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);

        var lines = text.Split('\n');
        var total = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        // Whole-text check first: per-line costs charge a CRLF's '\r' separately, so a text granted exactly
        // its own Estimate would otherwise lose its last lines.
        if (Raw(text.AsSpan()) <= maxTokens) return new TextCut(text, total, total, EndsMidLine: false);

        double used = 0;
        var end = 0;
        var shown = 0;
        while (shown < total)
        {
            var cost = Raw(lines[shown].AsSpan()) + (shown == 0 ? 0 : 1);
            if (used + cost > maxTokens) break;
            used += cost;
            end += (shown == 0 ? 0 : 1) + lines[shown].Length;
            shown++;
        }

        if (shown == total) return new TextCut(text, total, total, EndsMidLine: false);
        if (shown > 0) return new TextCut(text[..end], shown, total, EndsMidLine: false);
        return new TextCut(lines[0][..PrefixWithin(lines[0], maxTokens)], 1, total, EndsMidLine: true);
    }

    private static int PrefixWithin(string line, int maxTokens)
    {
        int lo = 0, hi = line.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (Raw(line.AsSpan(0, mid)) <= maxTokens) lo = mid;
            else hi = mid - 1;
        }
        return lo > 0 && char.IsHighSurrogate(line[lo - 1]) ? lo - 1 : lo;
    }

    private static double Raw(ReadOnlySpan<char> text)
    {
        double tokens = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsCjk(c))
            {
                tokens += 1;
                i++;
            }
            else if (char.IsLetter(c))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]) && !IsCjk(text[i])) i++;
                tokens += LetterWeight * Math.Ceiling((i - start) / 4.0);
            }
            else if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                tokens += Math.Ceiling((i - start) / 3.0);
            }
            else if (c == ' ' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                // A single space rides on the token after it.
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                tokens += 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsLetterOrDigit(text[i]) && !char.IsWhiteSpace(text[i]) && !IsCjk(text[i])) i++;
                tokens += Math.Ceiling((i - start) / 2.0);
            }
        }
        return tokens;
    }

    private static bool IsCjk(char c) =>
        c is (>= '\u3040' and <= '\u30FF') or (>= '\u3400' and <= '\u4DBF') or (>= '\u4E00' and <= '\u9FFF')
            or (>= '\uAC00' and <= '\uD7AF') or (>= '\uF900' and <= '\uFAFF') or (>= '\uFF00' and <= '\uFFEF');
}

public readonly record struct TextCut(string Text, int ShownLines, int TotalLines, bool EndsMidLine)
{
    public bool Truncated => EndsMidLine || ShownLines < TotalLines;
}
```

- [ ] **Step 7: Run to see them pass.** Build, then
  `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe -class "Pia.Tests.Helpers.TokenEstimatorTests" -class "Pia.Tests.Helpers.TokenEstimatorCorpusTests" > test.log 2>&1`.
  Expected: all pass. If a corpus sample falls outside the band, do not widen the band: first check that
  the fixture is the real extract (Step 2); if it is, stop and report the sample and its ratio to the
  owner — moving `LetterWeight` or the rule is the owner's call (§2).

- [ ] **Checkpoint** — tick checklist A1.

### Task 2: `AttachmentBudget`

**Files:**
- Create: `src/Pia.Wpf/Helpers/AttachmentBudget.cs`
- Test: `tests/Pia.Wpf.Tests/Helpers/AttachmentBudgetTests.cs`

**Interfaces:**
- Consumes: `Pia.Models.AgentContextBudget.From(AiProvider?)`, `Pia.Models.ContextWindowDefaults.For(string?)`.
- Produces: `public static class Pia.Helpers.AttachmentBudget { const double WindowShare = 0.25; const int MinTokens = 2_000; const int MaxTokens = 50_000; int ForChat(AiProvider? provider); int[] Distribute(IReadOnlyList<int> needs, int available); }`.

- [ ] **Step 1: Write the failing tests:**

```csharp
using Pia.Helpers;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Helpers;

public sealed class AttachmentBudgetTests
{
    private static AiProvider Provider(int? window, int? maxOutput, string model = "some-model") => new()
    {
        Name = "Test",
        Endpoint = "https://example.test",
        ModelName = model,
        MaxContextWindowTokens = window,
        MaxOutputTokens = maxOutput,
    };

    [Theory]
    [InlineData(128_000, null, 32_000)]
    [InlineData(200_000, 8_000, 48_000)]
    [InlineData(1_000_000, 32_000, AttachmentBudget.MaxTokens)]
    [InlineData(8_000, 2_000, AttachmentBudget.MinTokens)]
    public void ForChat_IsAQuarterOfTheInputWindow_Clamped(int window, int? maxOutput, int expected) =>
        Assert.Equal(expected, AttachmentBudget.ForChat(Provider(window, maxOutput)));

    [Fact]
    public void ForChat_WithoutAProvider_UsesTheDefaultWindow() =>
        Assert.Equal((int)(ContextWindowDefaults.Fallback * AttachmentBudget.WindowShare), AttachmentBudget.ForChat(null));

    [Fact]
    public void ForChat_WithAnOutputAboveTheWindow_UsesTheModelsDefaultWindow() =>
        Assert.Equal(
            (int)(ContextWindowDefaults.For("unlisted-local-model") * AttachmentBudget.WindowShare),
            AttachmentBudget.ForChat(Provider(4_000, 8_000, "unlisted-local-model")));

    [Fact]
    public void Distribute_OneFileMayUseEverything() =>
        Assert.Equal([32_000], AttachmentBudget.Distribute([50_000], 32_000));

    [Fact]
    public void Distribute_ASmallFileArrivesWholeAndTheBigOneGetsTheRest() =>
        Assert.Equal([31_000, 1_000], AttachmentBudget.Distribute([40_000, 1_000], 32_000));

    [Fact]
    public void Distribute_TwoBigFilesShareEqually() =>
        Assert.Equal([16_000, 16_000], AttachmentBudget.Distribute([30_000, 30_000], 32_000));

    [Fact]
    public void Distribute_NothingAvailable_GrantsNothing() =>
        Assert.Equal([0, 0], AttachmentBudget.Distribute([10, 20], -5));
}
```

  If `Assert.Equal([32_000], …)` does not resolve against `int[]`, write `new[] { 32_000 }`.

- [ ] **Step 2: Run to see them fail** (compile error: `AttachmentBudget` missing).

- [ ] **Step 3: Implement** — `src/Pia.Wpf/Helpers/AttachmentBudget.cs`:

```csharp
using Pia.Models;

namespace Pia.Helpers;

/// <summary>How many tokens dropped files may take in one chat — re-sent every turn, since a live chat is never compacted.</summary>
public static class AttachmentBudget
{
    public const double WindowShare = 0.25;
    public const int MinTokens = 2_000;
    public const int MaxTokens = 50_000;

    public static int ForChat(AiProvider? provider)
    {
        var input = AgentContextBudget.From(provider) is { } budget
            ? budget.WindowTokens - budget.MaxOutputTokens
            : ContextWindowDefaults.For(provider?.ModelName);
        return Math.Clamp((int)(input * WindowShare), MinTokens, MaxTokens);
    }

    /// <summary>Smallest first: a file under its fair share gets all it needs, the rest split what is left.</summary>
    public static int[] Distribute(IReadOnlyList<int> needs, int available)
    {
        var grants = new int[needs.Count];
        if (available <= 0) return grants;

        var order = Enumerable.Range(0, needs.Count).OrderBy(i => needs[i]).ToArray();
        var remaining = available;
        for (var k = 0; k < order.Length; k++)
        {
            var grant = Math.Min(needs[order[k]], remaining / (order.Length - k));
            grants[order[k]] = grant;
            remaining -= grant;
        }
        return grants;
    }
}
```

- [ ] **Step 4: Run to see them pass** (`-class "Pia.Tests.Helpers.AttachmentBudgetTests"`).
- [ ] **Checkpoint** — tick A2.

### Task 3: one rule for the provider a turn runs on

**Files:**
- Create: `src/Pia.Wpf/Services/AssistantTurnProvider.cs`
- Modify: `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs:802-805`
- Test: `tests/Pia.Wpf.Tests/Services/AssistantTurnProviderTests.cs`

**Interfaces:**
- Produces: `public static class Pia.Services.AssistantTurnProvider { Task<AiProvider?> ResolveAsync(Persona? persona, IProviderService providers); }`.

- [ ] **Step 1: Write the failing tests:**

```csharp
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AssistantTurnProviderTests
{
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly AiProvider _default = new() { Name = "Default", Endpoint = "https://default.test" };
    private readonly AiProvider _local = new() { Name = "Local", Endpoint = "http://localhost:11434" };

    public AssistantTurnProviderTests() =>
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(_default);

    [Fact]
    public async Task ResolveAsync_PrefersThePersonasProvider()
    {
        _providers.GetProviderAsync(_local.Id).Returns(_local);

        var provider = await AssistantTurnProvider.ResolveAsync(
            new Persona { Name = "Local", SystemPrompt = "x", PreferredProviderId = _local.Id }, _providers);

        Assert.Same(_local, provider);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackWhenThePreferredProviderIsGone()
    {
        var provider = await AssistantTurnProvider.ResolveAsync(
            new Persona { Name = "Local", SystemPrompt = "x", PreferredProviderId = Guid.NewGuid() }, _providers);

        Assert.Same(_default, provider);
    }

    [Fact]
    public async Task ResolveAsync_WithoutAPersona_UsesTheAssistantDefault() =>
        Assert.Same(_default, await AssistantTurnProvider.ResolveAsync(null, _providers));
}
```

- [ ] **Step 2: Run to see them fail** (compile error).
- [ ] **Step 3: Implement** — `src/Pia.Wpf/Services/AssistantTurnProvider.cs`:

```csharp
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>The provider an Assistant turn runs on; the drop-time attachment budget must read the same one.</summary>
public static class AssistantTurnProvider
{
    public static async Task<AiProvider?> ResolveAsync(Persona? persona, IProviderService providers)
    {
        var provider = persona?.PreferredProviderId is { } id ? await providers.GetProviderAsync(id) : null;
        return provider ?? await providers.GetDefaultProviderForModeAsync(WindowMode.Assistant);
    }
}
```

  In `ChatSessionManager.StartTurnAsync` replace lines 802-805:

```csharp
            var provider = await AssistantTurnProvider.ResolveAsync(persona, _providerService);
```

  (keep the following `if (provider is null)` block as is).

- [ ] **Step 4: Run** `-class "Pia.Tests.Services.AssistantTurnProviderTests" -class "Pia.Tests.ViewModels.ChatSessionManagerTests"`. Expected: all pass (the refactor changes no behaviour).
- [ ] **Checkpoint** — tick A3.

### Task 4: the cut on the chip, and the note in lines

**Files:**
- Modify: `src/Pia.Wpf/Models/PendingFileAttachment.cs`
- Modify: `src/Pia.Wpf/Services/AssistantPromptComposer.cs:372-397`
- Modify (compile, no behaviour): `src/Pia.Wpf/Helpers/DroppedFileAttachmentImporter.cs:126-134` (temporary, rewritten in Task 5)
- Test: `tests/Pia.Wpf.Tests/Services/AssistantPromptComposerAttachedFileTests.cs`
- Migrate: `tests/Pia.Wpf.Tests/Models/PendingFileAttachmentTests.cs:16-24,37-45`,
  `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelHotkeyDismissTests.cs:40-46`,
  `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelPendingFilesTests.cs:127-135`,
  `tests/Pia.Wpf.Tests/Views/AssistantViewParseTests.cs:338-346`,
  `tests/Pia.Wpf.Tests/Helpers/DroppedFileAttachmentImporterTests.cs:49-57` (helper only)

**Interfaces:**
- Produces: `public sealed record Pia.Models.AttachmentCut(int ShownLines, int TotalLines, bool EndsMidLine, int ShownPercent);`
  `PendingFileAttachment.Cut { get; init; }` (`AttachmentCut?`), `PendingFileAttachment.Truncated` (computed).
  `AssistantPromptComposer.BuildAttachedFileBlock(IReadOnlyList<PendingFileAttachment> files, Func<PendingFileAttachment, string?>? readFilePath = null)`;
  `internal static string AssistantPromptComposer.TruncationNote(AttachmentCut cut, string? readFilePath)`.

- [ ] **Step 1: Write the failing composer tests.** Replace the `File(...)` helper and
  `BuildAttachedFileBlock_AddsTruncatedAttributesOnlyWhenTruncated` in
  `AssistantPromptComposerAttachedFileTests.cs`, and add the new facts:

```csharp
    private static PendingFileAttachment File(
        string fileName = "notes.txt",
        PendingFileKind kind = PendingFileKind.Text,
        string text = "hello",
        AttachmentCut? cut = null,
        string? fullPath = null) =>
        new()
        {
            FullPath = fullPath ?? @"C:\work\" + fileName,
            FileName = fileName,
            Kind = kind,
            Text = text,
            Cut = cut,
        };

    private static readonly AttachmentCut ThreeOf900 = new(ShownLines: 3, TotalLines: 900, EndsMidLine: false, ShownPercent: 1);

    [Fact]
    public void BuildAttachedFileBlock_AddsTruncatedAttributesOnlyWhenCut()
    {
        var whole = AssistantPromptComposer.BuildAttachedFileBlock([File(text: "12345")]);
        Assert.DoesNotContain("truncated=", whole);
        Assert.DoesNotContain("note=", whole);

        var cut = AssistantPromptComposer.BuildAttachedFileBlock([File(text: "a\nb\nc", cut: ThreeOf900)]);
        Assert.Contains("truncated=\"true\"", cut);
        Assert.Contains("note=\"Showing lines 1-3 of 900. The rest was not sent.\"", cut);
    }

    [Fact]
    public void BuildAttachedFileBlock_PointsAtReadFile_WhenThePathIsKnown()
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock(
            [File(cut: new AttachmentCut(40, 900, EndsMidLine: false, ShownPercent: 4))], _ => "Playground/notes.txt");

        Assert.Contains("The rest was not sent; read it with read_file (path: Playground/notes.txt, offset: 41).", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_ReReadsALineThatWasCutShort()
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock(
            [File(cut: new AttachmentCut(1, 1, EndsMidLine: true, ShownPercent: 7))], _ => "data.json");

        Assert.Contains("Showing lines 1-1 of 1; line 1 is cut short.", block);
        Assert.Contains("offset: 1)", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_AFileWithoutAPath_SaysTheRestWasNotSent()
    {
        var block = AssistantPromptComposer.BuildAttachedFileBlock([File(cut: ThreeOf900)], _ => null);

        Assert.Contains("The rest was not sent.", block);
        Assert.DoesNotContain("read_file", block);
    }

    [Fact]
    public void BuildAttachedFileBlock_AWholeFile_NeverNamesReadFile() =>
        Assert.DoesNotContain("read_file",
            AssistantPromptComposer.BuildAttachedFileBlock([File()], _ => "notes.txt"));
```

- [ ] **Step 2: Run to see them fail** (compile error: `AttachmentCut`, `Cut`).

- [ ] **Step 3: Implement the model.** In `PendingFileAttachment.cs`, replace

```csharp
    public required bool Truncated { get; init; }
    public required int OriginalCharCount { get; init; }
```

  with

```csharp
    /// <summary>Null when the whole text fits the attachment budget.</summary>
    public AttachmentCut? Cut { get; init; }

    public bool Truncated => Cut is not null;
```

  and add at the end of the file:

```csharp
public sealed record AttachmentCut(int ShownLines, int TotalLines, bool EndsMidLine, int ShownPercent);
```

- [ ] **Step 4: Implement the composer.** Replace `BuildAttachedFileBlock` (with its `<summary>`) in
  `AssistantPromptComposer.cs`:

```csharp
    /// <summary>A cut file's note names read_file only when <paramref name="readFilePath"/> can locate it.</summary>
    public static string BuildAttachedFileBlock(
        IReadOnlyList<PendingFileAttachment> files, Func<PendingFileAttachment, string?>? readFilePath = null)
    {
        if (files.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("The user attached the following file(s) to this message. Use them as context for the request.");

        foreach (var f in files)
        {
            sb.Append("\n\n");
            sb.Append($"<attached_file name=\"{EscapeAttr(f.FileName)}\" type=\"{f.Kind.ToString().ToLowerInvariant()}\"");
            if (f.Cut is { } cut)
            {
                var note = TruncationNote(cut, readFilePath?.Invoke(f));
                sb.Append($" truncated=\"true\" note=\"{EscapeAttr(note)}\"");
            }
            sb.Append(">\n");
            sb.Append(f.Text);
            sb.Append("\n</attached_file>");
        }

        return sb.ToString();
    }

    internal static string TruncationNote(AttachmentCut cut, string? readFilePath)
    {
        var shown = cut.EndsMidLine
            ? $"Showing lines 1-{cut.ShownLines} of {cut.TotalLines}; line {cut.ShownLines} is cut short."
            : $"Showing lines 1-{cut.ShownLines} of {cut.TotalLines}.";
        if (readFilePath is null)
            return $"{shown} The rest was not sent.";

        var offset = cut.EndsMidLine ? cut.ShownLines : cut.ShownLines + 1;
        return $"{shown} The rest was not sent; read it with read_file (path: {readFilePath}, offset: {offset}).";
    }
```

- [ ] **Step 5: Keep the importer compiling** until Task 5 rewrites it: in
  `DroppedFileAttachmentImporter.cs`, delete the two lines `Truncated = truncated,` and
  `OriginalCharCount = originalCharCount,` from the `new PendingFileAttachment { … }` initializer and
  set `Cut = truncated ? new AttachmentCut(1, 1, EndsMidLine: true, ShownPercent: Math.Clamp(100 * text.Length / originalCharCount, 1, 99)) : null,`
  — a stopgap that Task 5 replaces wholesale.

- [ ] **Step 6: Migrate the other constructions.** In each file listed under *Migrate*, delete the
  `Truncated = false,` and `OriginalCharCount = …,` lines of every `PendingFileAttachment` initializer.
  In `DroppedFileAttachmentImporterTests.cs` also change the three `OriginalCharCount` assertions
  (lines 77, 129, 149) to `Assert.Null(staged.Cut)` / `Assert.NotNull(…Cut)` for now; Task 5 rewrites
  those tests. Find stragglers with `grep -rn "OriginalCharCount" src tests --include=*.cs`.

- [ ] **Step 7: Run** `-class "Pia.Tests.Services.AssistantPromptComposerAttachedFileTests" -class "Pia.Tests.Models.PendingFileAttachmentTests"`, then the full exe run (the migration touches many files). Expected: `Failed: 0`.
- [ ] **Checkpoint** — tick B1.

### Task 5: stage by budget — two phases, fair shares, line cuts, a percentage

**Files:**
- Modify: `src/Pia.Wpf/Helpers/DroppedFileAttachmentImporter.cs`
- Modify: `src/Pia.Wpf/Resources/Strings/MessageStrings.resx:70,75`, `MessageStrings.de.resx:67,72`, `MessageStrings.fr.resx:67,72`
- Modify: `tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs:661`
- Test: `tests/Pia.Wpf.Tests/Helpers/DroppedFileAttachmentImporterTests.cs`
- Modify (call site only): `src/Pia.Wpf/ViewModels/AssistantViewModel.cs:2055` — pass `() => Task.FromResult(AttachmentBudget.MaxTokens)` until Task 6

**Interfaces:**
- Consumes: `TokenEstimator` (Task 1), `AttachmentBudget.Distribute` (Task 2), `AttachmentCut` (Task 4).
- Produces: `DroppedFileAttachmentImporter.TryStageAsync(IReadOnlyList<string> paths, IReadOnlyCollection<PendingFileAttachment> alreadyPending, Func<Task<int>> availableTokens, ILogger logger, ISnackbarService snackbarService, ILocalizationService localizationService, CancellationToken ct = default)`;
  `public const int MinShownTokens = 200`. `availableTokens` = the chat budget minus what the live chat
  already re-sends; awaited at most once, and only when at least one file was readable.
  `Msg_File_Truncated` takes `{0}` file name, `{1}` percent.

- [ ] **Step 1: Rewrite the importer tests.** In `DroppedFileAttachmentImporterTests.cs` replace the
  `StageAsync` and `Pending` helpers:

```csharp
    private int _budgetReads;

    private Task<DroppedFileAttachmentImporter.StageResult> StageAsync(
        IReadOnlyList<string> paths, int availableTokens = AttachmentBudget.MaxTokens,
        params PendingFileAttachment[] alreadyPending) =>
        DroppedFileAttachmentImporter.TryStageAsync(
            paths, alreadyPending,
            () => { _budgetReads++; return Task.FromResult(availableTokens); },
            NullLogger.Instance, _snackbar, _loc, TestContext.Current.CancellationToken);

    private static PendingFileAttachment Pending(string fullPath, string text) => new()
    {
        FullPath = fullPath,
        FileName = Path.GetFileName(fullPath),
        Kind = PendingFileKind.Text,
        Text = text,
    };

    private static string Report(int lines) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(i => $"line {i} of the report"));
```

  Delete `TryStageAsync_TruncatesAFileOverMaxFileChars`, `TryStageAsync_TruncatesAgainstTheRunningTotal`,
  `TryStageAsync_CountsAlreadyPendingFilesTowardBothCaps`,
  `TryStageAsync_WithTheCharacterBudgetSpent_NamesTheBudgetNotTheFileCount` and
  `TryStageAsync_ThreeFilesInOneDrop_RefuseTheThirdOnTheBudget`. In
  `TryStageAsync_StagesATextFile` replace the `OriginalCharCount` assertion with `Assert.Null(staged.Cut);`.
  Add:

```csharp
    [Fact]
    public async Task TryStageAsync_AFileFarOverTheOldCharacterCaps_ArrivesWhole()
    {
        var text = Report(3000);
        Assert.True(text.Length > 40_000);
        var path = Write("long.txt", text);

        var staged = Assert.Single((await StageAsync([path], availableTokens: 32_000)).Staged);

        Assert.Null(staged.Cut);
        Assert.Equal(text, staged.Text);
    }

    [Fact]
    public async Task TryStageAsync_CutsAFileOverTheBudget_AtALineBoundary_AndSaysHowMuchFits()
    {
        var path = Write("big.txt", Report(2000));

        var staged = Assert.Single((await StageAsync([path], availableTokens: 1_000)).Staged);

        var cut = Assert.IsType<AttachmentCut>(staged.Cut);
        Assert.Equal(2000, cut.TotalLines);
        Assert.InRange(cut.ShownLines, 1, 1999);
        Assert.False(cut.EndsMidLine);
        Assert.InRange(cut.ShownPercent, 1, 99);
        Assert.True(TokenEstimator.Estimate(staged.Text) <= 1_000);
        AssertShown("Msg_File_Truncated");
        _loc.Received(1).Format("Msg_File_Truncated",
            Arg.Is<object[]>(a => a.Length == 2 && (string)a[0] == "big.txt" && (int)a[1] == cut.ShownPercent));
    }

    [Fact]
    public async Task TryStageAsync_SharesTheBudgetFairlyAcrossOneDrop()
    {
        var small = Write("small.txt", "a short note");
        var first = Write("first.txt", Report(2000));
        var second = Write("second.txt", Report(2000));

        var result = await StageAsync([first, small, second], availableTokens: 3_000);

        Assert.Equal(3, result.Staged.Count);
        Assert.Null(result.Staged.Single(s => s.FileName == "small.txt").Cut);
        var a = result.Staged.Single(s => s.FileName == "first.txt");
        var b = result.Staged.Single(s => s.FileName == "second.txt");
        Assert.NotNull(a.Cut);
        Assert.NotNull(b.Cut);
        Assert.InRange(Math.Abs(TokenEstimator.Estimate(a.Text) - TokenEstimator.Estimate(b.Text)), 0, 10);
    }

    [Fact]
    public async Task TryStageAsync_CountsOpenChipsAgainstTheBudget()
    {
        var chip = Pending(@"C:\work\earlier.txt", new string('x', 4_000));
        var path = Write("next.txt", Report(2000));

        var staged = Assert.Single((await StageAsync([path], 1_000 + TokenEstimator.Estimate(chip.Text), chip)).Staged);

        Assert.True(TokenEstimator.Estimate(staged.Text) <= 1_000);
    }

    [Fact]
    public async Task TryStageAsync_WithTheBudgetSpent_SkipsTheFile_AndNamesTheBudget()
    {
        var chip = Pending(@"C:\work\first.txt", new string('x', 4_000));
        var path = Write("second.txt", "still worth reading");

        var result = await StageAsync([path], availableTokens: 100, chip);

        Assert.Empty(result.Staged);
        _loc.Received(1).Format("Msg_File_AttachBudget",
            Arg.Is<object[]>(a => a.Length == 1 && (string)a[0] == "second.txt"));
        _loc.DidNotReceive().Format("Msg_File_AttachLimit", Arg.Any<object[]>());
    }

    [Fact]
    public async Task TryStageAsync_ASliverOfBudget_SkipsRatherThanAttachAFewWords()
    {
        var path = Write("big.txt", Report(2000));

        var result = await StageAsync([path], availableTokens: DroppedFileAttachmentImporter.MinShownTokens - 1);

        Assert.Empty(result.Staged);
        AssertShown("Msg_File_AttachBudget");
    }

    [Fact]
    public async Task TryStageAsync_ASingleHugeLine_ArrivesCutMidLine()
    {
        var path = Write("export.json", string.Join(',', Enumerable.Range(0, 50_000)));

        var staged = Assert.Single((await StageAsync([path], availableTokens: 1_000)).Staged);

        Assert.True(staged.Cut!.EndsMidLine);
        Assert.False(string.IsNullOrWhiteSpace(staged.Text));
    }

    [Fact]
    public async Task TryStageAsync_DoesNotAskForTheBudget_WithoutAReadableFile()
    {
        var archive = Write("bundle.zip", "PK");
        var image = Path.Combine(_dir, "shot.png");

        await StageAsync([archive, image]);

        Assert.Equal(0, _budgetReads);
    }
```

  `TryStageAsync_StopsAtMaxPendingFiles`, `…SkipsADuplicatePath`, `…SkipsAnEmptyFile`,
  `…SkipsAnUnsupportedFileButKeepsTheRest`, `…StagesAPdfAsADocument`, `…OverTheReadCeiling…` stay as they
  are (they call `StageAsync([…])`, which still compiles).

- [ ] **Step 2: Update the placeholder test** in `LocalizationTests.cs`: `[InlineData("Msg_File_Truncated", 2)]`.

- [ ] **Step 3: Run to see them fail** (compile error on the new signature / `MinShownTokens`).

- [ ] **Step 4: Implement.** In `DroppedFileAttachmentImporter.cs`: delete `MaxFileChars` and
  `MaxTotalChars`, add `public const int MinShownTokens = 200;` (comment: `// Below this a cut file is a
  few words, not context.`), and replace `TryStageAsync` and `IsStaged`:

```csharp
    /// <summary>Reads the non-image drops into pending chips and hands back the image paths untouched,
    /// so one drop can carry both.</summary>
    public static async Task<StageResult> TryStageAsync(
        IReadOnlyList<string> paths,
        IReadOnlyCollection<PendingFileAttachment> alreadyPending,
        Func<Task<int>> availableTokens,
        ILogger logger,
        ISnackbarService snackbarService,
        ILocalizationService localizationService,
        CancellationToken ct = default)
    {
        var staged = new List<PendingFileAttachment>();
        var images = new List<string>();
        var readable = new List<(string Path, string FileName, FileKind Kind, string Text)>();

        foreach (var path in paths)
        {
            var kind = DroppedFileReader.Classify(path);
            var fileName = Path.GetFileName(path);

            if (kind == FileKind.Image)
            {
                images.Add(path);
                continue;
            }

            if (DroppedFileReader.ReaderFor(kind) is not { } read)
            {
                logger.LogInformation("File attach rejected for kind {Kind}", kind);
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_UnsupportedAttachment", fileName));
                continue;
            }

            if (IsStaged(alreadyPending, readable, path))
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_DuplicateAttachment", fileName));
                continue;
            }

            if (alreadyPending.Count + readable.Count >= MaxPendingFiles)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_AttachLimit", MaxPendingFiles, fileName));
                continue;
            }

            var result = await read(path, ct);

            if (result.Status == DroppedFileReader.ReadStatus.TooLarge)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_TooLargeAttachment", fileName, DroppedFileReader.FormatLimit(result.LimitBytes)));
                continue;
            }

            if (result.Status == DroppedFileReader.ReadStatus.Failed
                && result.Error == DroppedFileReader.NoTextLayer)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_PdfNoText", fileName));
                continue;
            }

            if (result.Status == DroppedFileReader.ReadStatus.Failed)
            {
                logger.LogError("File attach read failed for kind {Kind}", kind);
                logger.SensitiveDebug("File attach read failed for {FileName}: {Error}", fileName, result.Error);
                snackbarService.Show(
                    localizationService["Msg_Error"],
                    localizationService.Format("Msg_File_ReadFailed", fileName, result.Error ?? string.Empty),
                    ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
                continue;
            }

            var text = result.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_Empty", fileName));
                continue;
            }

            readable.Add((path, fileName, kind, text));
        }

        if (readable.Count == 0) return new StageResult(staged, images);

        var remaining = await availableTokens() - alreadyPending.Sum(p => TokenEstimator.Estimate(p.Text));
        var needs = readable.Select(r => TokenEstimator.Estimate(r.Text)).ToArray();
        var grants = AttachmentBudget.Distribute(needs, remaining);

        for (var i = 0; i < readable.Count; i++)
        {
            var (path, fileName, kind, text) = readable[i];
            var cut = grants[i] >= needs[i] || grants[i] >= MinShownTokens
                ? TokenEstimator.CutToBudget(text, Math.Max(grants[i], 1))
                : default;

            if (string.IsNullOrWhiteSpace(cut.Text))
            {
                logger.LogInformation("File attach skipped: ~{Needed} tokens against {Remaining} left in the chat budget",
                    needs[i], Math.Max(remaining, 0));
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_AttachBudget", fileName));
                continue;
            }

            var attachmentCut = cut.Truncated
                ? new AttachmentCut(cut.ShownLines, cut.TotalLines, cut.EndsMidLine,
                    Math.Clamp((int)Math.Round(100.0 * TokenEstimator.Estimate(cut.Text) / needs[i]), 1, 99))
                : null;

            staged.Add(new PendingFileAttachment
            {
                FullPath = path,
                FileName = fileName,
                Kind = ToPendingKind(kind),
                Text = cut.Text,
                Cut = attachmentCut,
            });

            logger.LogInformation("Attached a {Kind} file (~{Shown} of ~{Needed} tokens, grant {Grant})",
                kind, TokenEstimator.Estimate(cut.Text), needs[i], grants[i]);
            logger.SensitiveDebug("Attached {FileName}", fileName);

            if (attachmentCut is not null)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_Truncated", fileName, attachmentCut.ShownPercent));
            }
        }

        return new StageResult(staged, images);
    }

    private static bool IsStaged(
        IReadOnlyCollection<PendingFileAttachment> alreadyPending,
        List<(string Path, string FileName, FileKind Kind, string Text)> readable,
        string path) =>
        alreadyPending.Any(p => string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase))
        || readable.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
```

  (`default(TextCut).Text` is null, which `string.IsNullOrWhiteSpace` treats as the skip case.) Remove
  the Task 4 stopgap. In `AssistantViewModel.cs:2055` pass `() => Task.FromResult(AttachmentBudget.MaxTokens)`
  as the third argument (Task 6 replaces it).

- [ ] **Step 5: Strings.** Replace the two values (keep keys and `xml:space`):

| Key | `.resx` | `.de.resx` | `.fr.resx` |
|---|---|---|---|
| `Msg_File_Truncated` | `Only about {1}% of "{0}" fits in this message.` | `Von „{0}“ passen nur etwa {1} % in diese Nachricht.` | `Seuls environ {1} % de « {0} » tiennent dans ce message.` |
| `Msg_File_AttachBudget` | `This chat's attachment budget is used up — "{0}" was skipped. Remove an attached file or start a new chat.` | `Das Anhang-Budget dieses Chats ist ausgeschöpft – „{0}“ wurde übersprungen. Entferne einen Anhang oder starte einen neuen Chat.` | `Le budget de pièces jointes de cette conversation est épuisé – « {0} » a été ignoré. Retire une pièce jointe ou démarre une nouvelle conversation.` |

- [ ] **Step 6: Run** `-class "Pia.Tests.Helpers.DroppedFileAttachmentImporterTests" -class "Pia.Tests.Architecture.LocalizationTests"`. Expected: all pass.
- [ ] **Checkpoint** — tick B2, B3.

### Task 6: the drop reads the real budget

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs:2050-2063`
- Test: `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelPendingFilesTests.cs`

**Interfaces:**
- Consumes: `AssistantTurnProvider.ResolveAsync` (Task 3), `AttachmentBudget.ForChat` (Task 2),
  `TokenEstimator.Estimate` (Task 1), the `TryStageAsync` signature (Task 5).

- [ ] **Step 1: Write the failing tests.** In `AssistantViewModelPendingFilesTests.cs` add a field
  `private readonly IPersonaService _personas = Substitute.For<IPersonaService>();` and pass `_personas`
  instead of `Substitute.For<IPersonaService>()` in `CreateSut`. Change
  `HandleFilesDropped_RoutesImagesAndTextSeparately`'s last assertion to
  `await _providers.Received(2).GetDefaultProviderForModeAsync(WindowMode.Assistant);` with the comment
  `// Once for the text file's attachment budget, once for the image path's vision check.` Add:

```csharp
    private static string Report(int lines) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(i => $"line {i} of the report"));

    [Fact]
    public async Task HandleFilesDropped_SharesTheBudgetWithAttachmentsTheChatAlreadySends()
    {
        var vm = CreateSut();
        // ~31,950 of the 32,000 a default 128K window allows are already re-sent every turn.
        vm.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "earlier")
        {
            AttachedFileContext = new string('x', 127_800),
        });
        var path = Write("report.txt", Report(200));

        await vm.HandleFilesDroppedCommand.ExecuteAsync(new[] { path });

        Assert.Empty(vm.PendingFiles);
    }

    [Fact]
    public async Task HandleFilesDropped_UsesThePersonasProviderForTheBudget()
    {
        var vm = CreateSut();
        var local = new AiProvider
        {
            Name = "Local", Endpoint = "http://localhost:11434",
            MaxContextWindowTokens = 8_000, MaxOutputTokens = 2_000,
        };
        _personas.ResolveActiveAsync(WindowMode.Assistant, Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Local", SystemPrompt = "x", PreferredProviderId = local.Id });
        _providers.GetProviderAsync(local.Id).Returns(local);
        var path = Write("report.txt", Report(700));

        await vm.HandleFilesDroppedCommand.ExecuteAsync(new[] { path });

        Assert.True(Assert.Single(vm.PendingFiles).Truncated);
    }

    [Fact]
    public async Task HandleFilesDropped_TheSameFileOnTheDefaultWindow_ArrivesWhole()
    {
        var vm = CreateSut();
        var path = Write("report.txt", Report(700));

        await vm.HandleFilesDroppedCommand.ExecuteAsync(new[] { path });

        Assert.False(Assert.Single(vm.PendingFiles).Truncated);
    }
```

- [ ] **Step 2: Run to see them fail** — the first two fail (the stub budget is 50,000).

- [ ] **Step 3: Implement.** In `ExecuteHandleFilesDropped` pass `AvailableAttachmentTokensAsync` as
  the third argument, and add below it:

```csharp
    // What the chat budget leaves after the attachments this live chat already re-sends every turn.
    private async Task<int> AvailableAttachmentTokensAsync()
    {
        AiProvider? provider = null;
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var persona = await _personaService.ResolveActiveAsync(
                WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal);
            provider = await AssistantTurnProvider.ResolveAsync(persona, _providerService);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the Assistant provider for the attachment budget; using the default window");
        }

        return AttachmentBudget.ForChat(provider) - Messages.Sum(m => TokenEstimator.Estimate(m.AttachedFileContext));
    }
```

- [ ] **Step 4: Run** `-class "Pia.Tests.ViewModels.AssistantViewModelPendingFilesTests"`. Expected: all pass.
- [ ] **Checkpoint** — tick B4.

### Task 7: the path `read_file` would accept

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IFilesToolHandler.cs`, `src/Pia.Wpf/Services/FilesToolHandler.cs`
- Test: `tests/Pia.Wpf.Tests/Services/FilesToolHandlerReadFilePathTests.cs`

**Interfaces:**
- Produces: `string? IFilesToolHandler.ToReadFilePath(string path, string? workingSubpath)` — `path` is
  absolute or relative to the assistant files folder; returns forward-slash path relative to the chat's
  effective root, or null when unavailable, outside that root, blocked or missing.

- [ ] **Step 1: Write the failing tests:**

```csharp
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class FilesToolHandlerReadFilePathTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerReadFilePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-readpath-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "proj", "src"));
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "proj", "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_root, "other", "b.txt"), "x");

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });
        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose() => TempPath.Remove(_root);

    [Fact]
    public void ToReadFilePath_AFileAtTheRoot_IsItsName() =>
        Assert.Equal("notes.txt", _handler.ToReadFilePath(Path.Combine(_root, "notes.txt"), workingSubpath: null));

    [Fact]
    public void ToReadFilePath_ASandboxRelativePath_ResolvesAgainstTheWorkingSubpath() =>
        Assert.Equal("src/a.cs", _handler.ToReadFilePath("proj/src/a.cs", workingSubpath: "proj"));

    [Fact]
    public void ToReadFilePath_AnAbsolutePathInsideTheWorkingSubpath_IsRelative() =>
        Assert.Equal("src/a.cs", _handler.ToReadFilePath(Path.Combine(_root, "proj", "src", "a.cs"), "proj"));

    [Fact]
    public void ToReadFilePath_OutsideTheWorkingSubpath_IsNull() =>
        Assert.Null(_handler.ToReadFilePath(Path.Combine(_root, "other", "b.txt"), "proj"));

    [Fact]
    public void ToReadFilePath_OutsideTheFilesFolder_IsNull()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pia-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "x");
        try
        {
            Assert.Null(_handler.ToReadFilePath(outside, workingSubpath: null));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void ToReadFilePath_AMissingFile_IsNull() =>
        Assert.Null(_handler.ToReadFilePath(Path.Combine(_root, "gone.txt"), workingSubpath: null));
}
```

- [ ] **Step 2: Run to see them fail** (compile error).
- [ ] **Step 3: Implement.** In `IFilesToolHandler.cs` add:

```csharp
    /// <summary>The path read_file accepts for <paramref name="path"/> in a chat scoped to <paramref name="workingSubpath"/>, or null.</summary>
    string? ToReadFilePath(string path, string? workingSubpath);
```

  In `FilesToolHandler.cs`, next to `DescribeEffectiveRoot`:

```csharp
    public string? ToReadFilePath(string path, string? workingSubpath)
    {
        var baseRoot = _currentFolder;
        if (!IsAvailable || baseRoot is null || !Directory.Exists(baseRoot)) return null;

        var root = ResolveEffectiveRoot(baseRoot, workingSubpath);
        // A relative path is the attached-file store's form: relative to the files folder, not the working dir.
        var candidate = Path.IsPathRooted(path) ? path : Path.Combine(baseRoot, path);
        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, candidate, out var safePath)) return null;
        if (SensitivePathGuard.IsBlocked(safePath, out _) || !File.Exists(safePath)) return null;

        return Path.GetRelativePath(root, safePath).Replace('\\', '/');
    }
```

- [ ] **Step 4: Run** `-class "Pia.Tests.Services.FilesToolHandlerReadFilePathTests"`. Expected: all pass.
- [ ] **Checkpoint** — tick C1.

### Task 8: build the note where the turn's tools are known

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/Models/IChatSessionManager.cs:61-65`
- Modify: `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs:714-750,868-888,1086-1097`
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs:1340-1365`
- Test: `tests/Pia.Wpf.Tests/ViewModels/ChatSessionManagerTests.cs`
- Migrate substitutes: `tests/Pia.Wpf.Tests/ViewModels/{AssistantViewModelAgentContextTests,AssistantViewModelLeverTests,AssistantViewModelOverlayHostingTests,AssistantViewModelPendingFilesTests,AssistantViewModelRegenerateTests,AssistantViewModelScreenCaptureTests}.cs`

**Interfaces:**
- Consumes: `BuildAttachedFileBlock(files, readFilePath)` (Task 4), `IFilesToolHandler.ToReadFilePath` (Task 7).
- Produces: `IChatSessionManager.StartTurnAsync(ChatSession session, string userText, IReadOnlyList<ImageAttachment>? attachments, string? regenerationInstruction = null, bool planned = false, string? attachedFileContext = null, IReadOnlyList<AttachedFileRef>? attachedFiles = null, IReadOnlyList<PendingFileAttachment>? files = null)`.
  `attachedFileContext` stays for Regenerate (an already built block); `files` is the composer's chips.

- [ ] **Step 1: Write the failing tests** in `ChatSessionManagerTests.cs`:

```csharp
    private static PendingFileAttachment CutChip(string? savedRelativePath = null) => new()
    {
        FullPath = @"C:\work\notes.txt",
        FileName = "notes.txt",
        Kind = PendingFileKind.Text,
        Text = "a\nb\nc",
        Cut = new AttachmentCut(ShownLines: 3, TotalLines: 900, EndsMidLine: false, ShownPercent: 1),
        SavedRelativePath = savedRelativePath,
    };

    private void ArrangeTurn(IList<Microsoft.Extensions.AI.AITool>? tools)
    {
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Tester", SystemPrompt = "be helpful" });
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Test", Endpoint = "https://example.test" });
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", tools, tools is { Count: > 0 }, false));
    }

    private static IList<Microsoft.Extensions.AI.AITool> ReadFileTool() =>
        [Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "", "read_file")];

    [Fact]
    public async Task StartTurnAsync_ACutFile_PointsAtReadFile_WhenTheTurnOffersIt()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        ArrangeTurn(ReadFileTool());
        _files.ToReadFilePath(@"C:\work\notes.txt", Arg.Any<string?>()).Returns("notes.txt");

        await sut.StartTurnAsync(session, "summarize this", null, files: [CutChip()]);

        Assert.Contains("read it with read_file (path: notes.txt, offset: 4).", session.Messages[0].AttachedFileContext);
    }

    [Fact]
    public async Task StartTurnAsync_ACutSavedFile_PointsAtTheSavedCopy()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        ArrangeTurn(ReadFileTool());
        _files.ToReadFilePath("Playground/notes.txt", Arg.Any<string?>()).Returns("notes.txt");

        await sut.StartTurnAsync(session, "summarize this", null, files: [CutChip("Playground/notes.txt")]);

        Assert.Contains("(path: notes.txt, offset: 4)", session.Messages[0].AttachedFileContext);
    }

    [Fact]
    public async Task StartTurnAsync_ACutFile_WithoutReadFileOnTheTurn_SaysTheRestWasNotSent()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        ArrangeTurn(tools: null);

        await sut.StartTurnAsync(session, "summarize this", null, files: [CutChip()]);

        Assert.Contains("The rest was not sent.", session.Messages[0].AttachedFileContext);
        Assert.DoesNotContain("read_file", session.Messages[0].AttachedFileContext);
        _files.DidNotReceive().ToReadFilePath(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task StartTurnAsync_WithFiles_SetupFails_KeepsTheFileTextForRegenerate()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Tester", SystemPrompt = "be helpful" });
        // No provider configured: setup fails before PrepareTurn.

        await sut.StartTurnAsync(session, "summarize this", null, files: [CutChip()]);

        Assert.Contains("a\nb\nc", session.Messages[0].AttachedFileContext);
    }

    [Fact]
    public async Task StartTurnAsync_RunParkedForClarification_WithAStagedFile_StartsAnOrdinaryTurn()
    {
        var sut = CreateResumingSut();
        var session = sut.GetOrCreateActiveForNewChat();
        AttachParkedRun(session, "needs-goal");
        ArrangeTurn(tools: null);

        await sut.StartTurnAsync(session, "the printed catalogue", null, files: [CutChip()]);

        await _resumeService.DidNotReceive().ResumeAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, session.Messages.Count);
    }
```

- [ ] **Step 2: Run to see them fail** (compile error: no `files` parameter).

- [ ] **Step 3: Implement the manager.** `IChatSessionManager.StartTurnAsync` gains the trailing
  parameter `IReadOnlyList<PendingFileAttachment>? files = null`. If the method's XML doc already lists
  `<param>` entries, add `/// <param name="files">The composer's chips; their block is built once the turn's tools are known.</param>`;
  otherwise add no doc. In `ChatSessionManager`:

```csharp
    public async Task<bool> StartTurnAsync(
        ChatSession session, string userText, IReadOnlyList<ImageAttachment>? attachments,
        string? regenerationInstruction = null,
        bool planned = false, string? attachedFileContext = null,
        IReadOnlyList<AttachedFileRef>? attachedFiles = null,
        IReadOnlyList<PendingFileAttachment>? files = null)
```

  the parked-run call becomes
  `TryAnswerParkedRunAsync(session, userText, attachments, regenerationInstruction, attachedFileContext, hasFiles: files is { Count: > 0 }, parkReason)`;
  the user message:

```csharp
        var userMessage = new AssistantMessage(ChatRole.User, userText)
        {
            // Provisional, without the read_file hint: a setup failure below must still leave Regenerate the text.
            AttachedFileContext = files is { Count: > 0 } ? AssistantPromptComposer.BuildAttachedFileBlock(files) : attachedFileContext,
        };
```

  right after the `turnSetup` log lines (before `BuildInjectedFileContextAsync`):

```csharp
            if (files is { Count: > 0 })
                userMessage.AttachedFileContext = AssistantPromptComposer.BuildAttachedFileBlock(
                    files, ReadFilePathResolver(turnSetup, session.WorkingDirectory));
```

  a private helper:

```csharp
    // read_file is named in a truncation note only on a turn that offers it.
    private Func<PendingFileAttachment, string?>? ReadFilePathResolver(AssistantTurnSetup turnSetup, string? workingDirectory) =>
        turnSetup.Tools?.Any(t => t.Name == "read_file") == true
            ? file => _filesToolHandler.ToReadFilePath(file.SavedRelativePath ?? file.FullPath, workingDirectory)
            : null;
```

  and `TryAnswerParkedRunAsync` takes `bool hasFiles` after `attachedFileContext`, refusing with
  `|| attachedFileContext is not null || hasFiles`.

- [ ] **Step 4: Implement the send.** In `AssistantViewModel.ExecuteSendMessage` delete the
  `attachedFileContext` local (lines 1342-1344) and call:

```csharp
        var accepted = await _chatSessionManager.StartTurnAsync(
            session, userText, attachments, planned: planned,
            attachedFiles: attachedFiles, files: files.Length > 0 ? files : null);
```

  Replace the comment above `attachedFiles` with:

```csharp
        // Names (and the sandbox path of the ones that were saved) outlive the send as chips under the
        // bubble; the extracted text does not, so a resumed chat shows what was attached but no
        // longer feeds it to the model.
```

- [ ] **Step 5: Migrate the substitutes.** Every `_manager.StartTurnAsync(…)` configuration or
  `Received` check in the six files listed gets an eighth matcher — after
  `Arg.Any<IReadOnlyList<AttachedFileRef>?>()` add `, Arg.Any<IReadOnlyList<PendingFileAttachment>?>()`
  (or `files: Arg.Any<IReadOnlyList<PendingFileAttachment>?>()` where the call uses named arguments).
  List them with `grep -n "StartTurnAsync(" tests/Pia.Wpf.Tests/ViewModels/AssistantViewModel*.cs`. In
  `AssistantViewModelPendingFilesTests.AgentModeSendWithAPendingFile_IsNotPlanned` replace the
  `attachedFileContext:` matcher with

```csharp
            attachedFileContext: null,
            attachedFiles: Arg.Any<IReadOnlyList<AttachedFileRef>?>(),
            files: Arg.Is<IReadOnlyList<PendingFileAttachment>?>(f => f != null && f.Single().FileName == "notes.txt"));
```

- [ ] **Step 6: Run** `-class "Pia.Tests.ViewModels.ChatSessionManagerTests"`, then the full exe run. Expected: `Failed: 0`.
- [ ] **Checkpoint** — tick C2, C3.

### Task 9: the chip says it was cut and asks to be saved

**Files:**
- Modify: `src/Pia.Wpf/Views/AssistantView.xaml:552-586`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `ViewStrings.de.resx`, `ViewStrings.fr.resx` (beside `Assistant_SavePendingFile_Tooltip`)

**Interfaces:**
- Consumes: `PendingFileAttachment.Truncated`, `PendingFileAttachment.Cut.ShownPercent` (Task 4).

- [ ] **Step 1: Strings** (three new keys):

| Key | `.resx` | `.de.resx` | `.fr.resx` |
|---|---|---|---|
| `Assistant_PendingFileCut_Badge` | `shortened` | `gekürzt` | `raccourci` |
| `Assistant_PendingFileCut_Tooltip` | `Only part of this file fits in the message. Save it to the working directory so Pia can read the rest.` | `Nur ein Teil dieser Datei passt in die Nachricht. Speichere sie im Arbeitsverzeichnis, damit Pia den Rest nachlesen kann.` | `Seule une partie de ce fichier tient dans le message. Enregistre-le dans le répertoire de travail pour que Pia puisse lire la suite.` |
| `Assistant_SavePendingFile_CutTooltip` | `Save to the working directory so Pia can read the rest` | `Im Arbeitsverzeichnis speichern, damit Pia den Rest nachlesen kann` | `Enregistrer dans le répertoire de travail pour que Pia puisse lire la suite` |

- [ ] **Step 2: Badge.** In the pending-chip `DataTemplate`, directly after the `FileName` `TextBlock`
  (`AssistantView.xaml:555-558`), insert:

```xml
                      <StackPanel Orientation="Horizontal"
                                  Margin="6,0,0,0"
                                  VerticalAlignment="Center"
                                  ToolTip="{loc:Str Assistant_PendingFileCut_Tooltip}"
                                  Visibility="{Binding Truncated, Converter={StaticResource BooleanToVisibilityConverter}}">
                        <TextBlock Text="{loc:Str Assistant_PendingFileCut_Badge}"
                                   FontSize="11"
                                   Foreground="{DynamicResource WarningBrush}" />
                        <TextBlock Text="{Binding Cut.ShownPercent, StringFormat='· ~{0} %'}"
                                   Margin="3,0,0,0"
                                   FontSize="11"
                                   Foreground="{DynamicResource WarningBrush}" />
                      </StackPanel>
```

  Non-interactive, so no AutomationId and no `ViewAutomationIdTests` change (the row stays `34, 7`).

- [ ] **Step 3: Save button.** On the save `ui:Button` remove the local `Appearance="Transparent"` and
  `ToolTip="…"` attributes (a local value beats a style trigger) and extend its style:

```xml
                          <Style TargetType="ui:Button" BasedOn="{StaticResource {x:Type ui:Button}}">
                            <Setter Property="Visibility" Value="Visible" />
                            <Setter Property="Appearance" Value="Transparent" />
                            <Setter Property="ToolTip" Value="{loc:Str Assistant_SavePendingFile_Tooltip}" />
                            <Style.Triggers>
                              <!-- A cut file's rest is reachable only once saved. -->
                              <DataTrigger Binding="{Binding Truncated}" Value="True">
                                <Setter Property="Appearance" Value="Caution" />
                                <Setter Property="ToolTip" Value="{loc:Str Assistant_SavePendingFile_CutTooltip}" />
                              </DataTrigger>
                              <!-- Saved, or nothing to save into: the tick below takes over. -->
                              <DataTrigger Binding="{Binding IsSaved}" Value="True">
                                <Setter Property="Visibility" Value="Collapsed" />
                              </DataTrigger>
                              <DataTrigger Binding="{Binding DataContext.CanSavePendingFiles,
                                                             RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                           Value="False">
                                <Setter Property="Visibility" Value="Collapsed" />
                              </DataTrigger>
                            </Style.Triggers>
                          </Style>
```

  Keep `AutomationProperties.Name="{loc:Str Assistant_SavePendingFile_Tooltip}"` and the AutomationId.

- [ ] **Step 4: Run** `-class "Pia.Tests.Views.AssistantViewParseTests" -class "Pia.Tests.Views.ViewAutomationIdTests" -class "Pia.Tests.Architecture.LocalizationTests"`. Expected: all pass.
- [ ] **Step 5: Real-app check** (not part of the gate; needs the owner's go to launch the app).
  Run the app against a throwaway profile (`docs/ui_automation/ui-automation-playbook.md`), drop a
  ~200-page PDF or a 5,000-row xlsx into the Assistant: the snackbar names a percentage, the chip shows
  "shortened · ~N %", the save button is highlighted; save, send, ask about the end of the file — the
  answer should come from a `read_file` call.
- [ ] **Checkpoint** — tick D1, D2.

### Task 10: @Files preview and `read_file` windows by tokens

**Files:**
- Modify: `src/Pia.Wpf/Services/FilesToolHandler.cs:47,1152-1155,1198,1207-1238,1268-1286`
- Modify: `src/Pia.Wpf/Services/Interfaces/IFilesToolHandler.cs` (the `ReadPromptPreviewAsync` summary)
- Test: `tests/Pia.Wpf.Tests/Services/FilesToolHandlerReadTests.cs:153-165,396-421`

**Interfaces:**
- Consumes: `TokenEstimator.Estimate` (Task 1).
- Produces: `internal static (string Text, int TotalLines, int ShownLines, bool Truncated) FilesToolHandler.CapForPrompt(string text, int maxLines, int maxTokens)`.

- [ ] **Step 1: Write the failing tests.** Replace the two `CapForPrompt` tests and add the numeric ones:

```csharp
    [Fact]
    public void CapForPrompt_TokenBudgetBinds_BeforeLineCap()
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(_ => new string('x', 100)));

        var (capped, total, shown, truncated) = FilesToolHandler.CapForPrompt(text, maxLines: 100, maxTokens: 250);

        Assert.Equal(100, total);
        Assert.True(truncated);
        Assert.True(shown < 100);
        Assert.True(TokenEstimator.Estimate(capped) <= 250);
    }

    [Fact]
    public void CapForPrompt_AlwaysEmitsFirstLine_EvenWhenItAloneExceedsBudget()
    {
        var huge = new string('y', 5000);

        var (capped, total, shown, truncated) = FilesToolHandler.CapForPrompt(huge, maxLines: 100, maxTokens: 250);

        Assert.Equal(1, total);
        Assert.Equal(1, shown);
        Assert.False(truncated);
        Assert.Equal(huge, capped);
    }

    [Fact]
    public void CapForPrompt_ShowsFewerRowsOfNumbersThanOfWords_AtTheSameLength()
    {
        const string numbers = "2026-10-14\t13\t238\t111\t127\t0.318";
        const string words = "the quarterly report is in here";
        Assert.Equal(numbers.Length, words.Length);

        var numberRows = FilesToolHandler.CapForPrompt(string.Join('\n', Enumerable.Repeat(numbers, 100)), 100, 500).ShownLines;
        var wordRows = FilesToolHandler.CapForPrompt(string.Join('\n', Enumerable.Repeat(words, 100)), 100, 500).ShownLines;

        Assert.True(numberRows < wordRows, $"numbers {numberRows} rows, words {wordRows} rows");
    }

    [Fact]
    public async Task Read_NumericWindow_IsRefusedOnTokens_ThoughUnderTheOldCharCap()
    {
        // ≈ 74K formatted chars (under the former 100K-char cap) but ≈ 42K estimated tokens.
        WriteFile("sheet.txt", string.Join('\n', Enumerable.Repeat("2026-10-14\t13\t238\t111\t127\t0.318", 2000)));

        var result = (string)(await ReadAsync("sheet.txt", offset: 1, limit: 2000))!;

        Assert.Contains("too large to return", result);
    }
```

  In `Read_FormattedWindowOverflow_ReturnsGuidance_NotTruncated` change only the comment to
  `// 800 lines of 200 chars ≈ 42K estimated tokens: over the 25K cap, under the 2000-line cap.`

- [ ] **Step 2: Run to see them fail** (compile error on `maxTokens:`; the numeric read passes today's char cap).

- [ ] **Step 3: Implement.** Replace the preview constant and its three-line comment with

```csharp
    // Bounds a preview of very long lines (minified code, wide sheets), which the line cap alone would not.
    private const int PromptPreviewMaxTokens = 3_000;
```

  pass it at line 1198 (`CapForPrompt(text!, maxLines, PromptPreviewMaxTokens)`), and rewrite
  `CapForPrompt` (summary cut to one line):

```csharp
    /// <summary>The first lines within both caps; the first line always shows, however long.</summary>
    internal static (string Text, int TotalLines, int ShownLines, bool Truncated) CapForPrompt(
        string text, int maxLines, int maxTokens)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];

        // A trailing newline yields a spurious empty final element — don't count it as a line.
        int totalLines = lines.Length;
        if (totalLines > 0 && lines[^1].Length == 0 && text.EndsWith('\n'))
            totalLines--;

        var sb = new StringBuilder();
        int shown = 0;
        int used = 0;
        for (int i = 0; i < totalLines && shown < maxLines; i++)
        {
            int cost = TokenEstimator.Estimate(lines[i]) + (shown == 0 ? 0 : 1); // +1 for the joining '\n'
            if (shown > 0 && used + cost > maxTokens) break;
            if (shown > 0) sb.Append('\n');
            sb.Append(lines[i]);
            used += cost;
            shown++;
        }

        return (sb.ToString(), totalLines, shown, shown < totalLines);
    }
```

  Replace `MaxFormattedWindowChars` at line 47 with
  `private const int MaxFormattedWindowTokens = 25_000;` and in `FormatWindow` add `int estimated = 0;`
  before the loop and replace the guard:

```csharp
            sb.Append(i + 1).Append('|').Append(lines[i]).Append('\n');
            estimated += TokenEstimator.Estimate(lines[i]) + 3; // the "N|" prefix and the newline

            if (estimated > MaxFormattedWindowTokens)
                return $"Error: The requested window is too large to return (over ~{MaxFormattedWindowTokens / 1000}K tokens). " +
                       $"Narrow the read with a smaller 'limit' or a more specific 'offset'. total_lines={totalLines}.";
```

  Replace the multi-line `ReadPromptPreviewAsync` summary in `IFilesToolHandler.cs` with
  `/// <summary>A bounded preview (line cap and token budget) of a sandbox file for @Files injection; never throws for an unreadable file.</summary>`.
  Leave `ClampResult`'s separate 100K serialized-result cap alone.

- [ ] **Step 4: Run** `-class "Pia.Tests.Services.FilesToolHandlerReadTests" -class "Pia.Tests.Services.FilesToolHandlerReadImageTests" -class "Pia.Tests.ViewModels.ChatSessionManagerTests"`. Expected: all pass.
- [ ] **Checkpoint** — tick E1.

### Task 11: release note and user guide

**Files:**
- Modify: `docs/release_notes/RELEASE.md` (section `## Assistant`, **after** the PDF bullet)
- Modify (Pia repo): `src/Pia.Docs/src/content/docs/wpf/guides/attachments.mdx`, `…/de/wpf/guides/attachments.mdx`, `…/fr/wpf/guides/attachments.mdx`

- [ ] **Step 1: Release note** — append to `## Assistant` (each line ≤ 80 columns):

```markdown
- Files you attach in the Assistant now arrive whole far more often: how
  much fits grows with the model's context window. A file that is still
  too long says how much of it fits and shows "shortened" on its chip;
  save it to the working directory and the assistant can read the rest.
```

- [ ] **Step 2: Docs branch.** The Pia repo (`G:\Git\Pia`) is on another branch with unrelated work.
  **Ask the owner** which base branch the docs change goes on; then use a worktree
  (`git -C G:/Git/Pia worktree add ../Pia-attachment-docs -b feature/attachment-token-budget-docs <base>`),
  never a branch switch in that dirty tree.

- [ ] **Step 3: English** (`wpf/guides/attachments.mdx`, section `## How large a file can be`):
  in the first paragraph replace *"so ordinary documents, logs and source files are never a problem."*
  with *"so ordinary documents, logs and source files stay well under it."* After the paragraph ending
  *"…paste in just the part you need."* insert:

```markdown
### How much of a file reaches the AI

Below that limit, how much of the text Pia sends depends on the AI model you use. The files attached
in one chat share about a quarter of the model's context window, at most around 50,000 tokens —
roughly 70 pages of prose, fewer rows for a spreadsheet full of numbers. One file can use all of it;
several files share it fairly.

A file that does not fit whole is shortened: Pia says how much of it fits, and its chip shows
**shortened · ~60 %** until you send. Save the file to the working directory with the chip's save
button and Pia can read the rest with the [file tools](/wpf/guides/coding-tools/) when it needs to.

An attached file is sent again with every later message in the same chat, so earlier attachments
count against that budget too. When it is used up, Pia skips the next file and says so — start a new
chat to attach more.
```

  In `## What an attachment sends to your AI provider` replace *"sends **its whole text along with your
  message**"* with *"sends **its text along with your message** — all of it, or as much as fits (see
  above)"*.

- [ ] **Step 4: German** (`de/…`, section `## Wie groß eine Datei sein darf`): replace *"sodass
  gewöhnliche Dokumente, Logs und Quellcode-Dateien nie zum Problem werden."* with *"sodass
  gewöhnliche Dokumente, Logs und Quellcode-Dateien weit darunter bleiben."*; after the paragraph
  ending *"…nur den Teil ein, den du brauchst."* insert:

```markdown
### Wie viel einer Datei bei der KI ankommt

Unterhalb dieser Grenze hängt es vom KI-Modell ab, wie viel Text Pia mitschickt. Die Dateien in einem
Chat teilen sich etwa ein Viertel des Kontextfensters des Modells, höchstens rund 50.000 Tokens — grob
70 Seiten Fließtext, bei einer Tabelle voller Zahlen weniger Zeilen. Eine einzelne Datei darf alles
davon nutzen; mehrere Dateien teilen es gerecht.

Passt eine Datei nicht ganz hinein, wird sie gekürzt: Pia sagt, wie viel davon passt, und ihr Chip
zeigt bis zum Senden **gekürzt · ~60 %**. Speichere die Datei über den Speichern-Button des Chips im
Arbeitsverzeichnis, dann kann Pia den Rest bei Bedarf mit den [Datei-Tools](/de/wpf/guides/coding-tools/) lesen.

Eine angehängte Datei wird mit jeder weiteren Nachricht im selben Chat erneut mitgeschickt, deshalb
zählen frühere Anhänge zu diesem Budget. Ist es aufgebraucht, überspringt Pia die nächste Datei und
sagt das — starte dann einen neuen Chat, um weitere Dateien anzuhängen.
```

  and in `## Was ein Anhang an deinen KI-Anbieter sendet` replace *"sendet **ihren ganzen Text
  zusammen mit deiner Nachricht**"* with *"sendet **ihren Text zusammen mit deiner Nachricht** —
  vollständig oder so viel, wie hineinpasst (siehe oben)"*.

- [ ] **Step 5: French** (`fr/…`, section `## Quelle taille peut faire un fichier`): replace *"donc les
  documents, journaux et fichiers source ordinaires ne posent jamais de problème."* with *"donc les
  documents, journaux et fichiers source ordinaires restent bien en dessous."*; after the paragraph
  ending *"…la partie dont tu as besoin."* insert:

```markdown
### Quelle part d'un fichier parvient à l'IA

En dessous de cette limite, la quantité de texte que Pia envoie dépend du modèle d'IA utilisé. Les
fichiers joints dans une conversation se partagent environ un quart de la fenêtre de contexte du
modèle, au plus quelque 50 000 tokens — environ 70 pages de texte suivi, moins de lignes pour un
tableau rempli de chiffres. Un seul fichier peut tout utiliser ; plusieurs fichiers se le partagent
équitablement.

Un fichier qui ne tient pas en entier est raccourci : Pia indique quelle part tient, et sa chip
affiche **raccourci · ~60 %** jusqu'à l'envoi. Enregistre le fichier dans le répertoire de travail avec
le bouton d'enregistrement de la chip, et Pia pourra lire la suite avec les [outils de fichiers](/fr/wpf/guides/coding-tools/) si nécessaire.

Un fichier joint est renvoyé avec chaque message suivant de la même conversation, donc les pièces
jointes précédentes comptent aussi dans ce budget. Une fois épuisé, Pia ignore le fichier suivant et
le signale — démarre alors une nouvelle conversation pour joindre d'autres fichiers.
```

  and in `## Ce qu'une pièce jointe envoie à ton fournisseur d'IA` replace *"envoie **tout son texte
  avec ton message**"* with *"envoie **son texte avec ton message** — en entier ou autant qu'il en
  tient (voir ci-dessus)"*.

- [ ] **Step 6: Help corpus.** Nothing to run now: the corpus is refreshed by the `help-corpus` skill
  before the next push to `main`, after the docs change has landed in Pia.Docs. Note it in the
  hand-off to the owner.
- [ ] **Checkpoint** — tick F1, F2.

### Task 12: the gate

- [ ] **Step 1: Comment pass.** `git diff` over every touched member: no history words, no task ids,
  summaries one line, no `<summary>` on private members that do not need one.
- [ ] **Step 2: Zero warnings.** `dotnet build -t:Rebuild -v:n > build-debug.log 2>&1` and
  `dotnet build -t:Rebuild -v:n -c Release > build-release.log 2>&1`; read the `N Warning(s)` /
  `N Error(s)` summary lines. Expected: `0 Warning(s)`, `0 Error(s)` in both.
- [ ] **Step 3: Full suite.** `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1`,
  then `grep -E "TEST EXECUTION SUMMARY" -A 2 test.log`. Expected: `Failed: 0`. A known-flaky test that
  fails once: re-run it alone and report both results; never claim green without the summary line.
- [ ] **Step 4: Hand-off.** Report to the owner: what changed, the gate output, the real-app check
  (done or not), the docs branch, and that nothing is committed. Commit only on the owner's word.
- [ ] **Checkpoint** — tick G1.

## 9. Not planned

- **`AgentContextCompactor` charges text at Length/4** (`AgentContextCompactor.cs:361`), so a numeric
  attachment pinned as the newest user message of an agent step is charged at ~1/2.6 of its cost.
  `TokenEstimator` makes the fix one line; it changes agent-run compaction, so it wants its own review.
- **The interactive tool loop has no budget** (`ChatSession.cs:449-451` passes none), so several large
  `read_file` results in one turn can still overflow a small window.
- **No "shortened" marker after send** — would need a field on `AttachedFileRef` and the sync DTO.
- **No re-cut at send time** when the provider changed after the drop (owner decision Q3).
- **Optimize / Advanced Creation** (`DroppedFileImporter`) keep their only cap, `MaxTextBytes`.

## Appendix A: measurement and extraction tool (throwaway — never commit)

Create it outside the repo (e.g. the session scratchpad); build Pia.Wpf Debug first.

`tokmeasure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.ML.Tokenizers" Version="2.0.0" />
    <PackageReference Include="Microsoft.ML.Tokenizers.Data.O200kBase" Version="2.0.0" />
    <PackageReference Include="Microsoft.ML.Tokenizers.Data.Cl100kBase" Version="2.0.0" />
    <PackageReference Include="DocumentFormat.OpenXml" Version="3.5.1" />
    <PackageReference Include="PdfPig" Version="0.1.16" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Pia.Wpf">
      <HintPath>G:\Git\Pia.Wpf\src\Pia.Wpf\bin\Debug\net10.0-windows10.0.17763.0\Pia.Wpf.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

Add a `nuget.config` with `<clear />` and only `https://api.nuget.org/v3/index.json` (the internal feed
is unreachable from here and fails the restore). `Program.cs`:

```csharp
using System.Globalization;
using System.IO;
using Microsoft.ML.Tokenizers;
using Pia.Helpers;

if (args is ["extract", var source, var target])
{
    var reader = DroppedFileReader.ReaderFor(DroppedFileReader.Classify(source)) ?? DroppedFileReader.ReadTextAsync;
    var result = await reader(source, CancellationToken.None);
    File.WriteAllText(target, result.Text ?? throw new InvalidOperationException($"{result.Status} {result.Error}"));
    return;
}

// measure <dir>: every *.txt — chars, Length/4, o200k, cl100k, o200k ÷ (Length/4)
var o200k = TiktokenTokenizer.CreateForEncoding("o200k_base");
var cl100k = TiktokenTokenizer.CreateForEncoding("cl100k_base");
Console.WriteLine("file\tchars\tlen/4\to200k\tcl100k\to200k/(len/4)");
foreach (var file in Directory.GetFiles(args[1], "*.txt").Order())
{
    var text = File.ReadAllText(file);
    var quarter = text.Length / 4;
    var o = o200k.CountTokens(text);
    Console.WriteLine(string.Join('\t', Path.GetFileName(file), text.Length, quarter, o, cl100k.CountTokens(text),
        (o / (double)quarter).ToString("F2", CultureInfo.InvariantCulture)));
}
```

`dotnet run -- extract <file> <out.txt>` writes exactly what `DroppedFileReader` hands the model;
`dotnet run -- measure <dir>` reproduces the §3.1 columns. After Task 1 lands, add
`TokenEstimator.Estimate(text)` as a column to re-check §3.3.
