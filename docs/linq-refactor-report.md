# LINQ / if-else cluster checkup

Sweep date: 2026-08-12 · branch `feature/agent-run-spine` · scope `src/`

## Summary

Two full sweeps of `src/`: **577 `foreach` occurrences across 170 files**, and **99 `else if`
occurrences across 56 files**.

**There is no general if/else-cluster problem in this codebase.** It is already strongly
LINQ-idiomatic — `Where`/`Select`/`OrderBy`/`FirstOrDefault`/`GroupBy` are used throughout, all 39
value converters either use switch expressions or are single-branch, and `src/Pia.Shared` is pure
DTO records with zero `foreach`. `else if` density peaks at **6 per file** (`SqliteContext.cs`),
and that file's 6 split into two unrelated chains — so **exactly two sites repo-wide have a 4+
`else if` mapping chain**.

What remains is a short, bounded list:

| Section | What | Sites | Approx. lines removed |
|---|---|---|---|
| A | `foreach` → LINQ | 14 (+4 minor) | ~110 |
| B | if-ladder / switch statement → switch expression or lookup table | 6 | ~40 |
| C | Loops that must **stay** imperative | ~25 | — |
| D | Named follow-ups, out of scope | 4 | ~120 |

This report proposes no code changes. It is a findings document.

---

## Section A — LINQ conversions

Every rewrite below was verified against the current source, not paraphrased. Ranked strongest
first.

### A1 — `ViewModels/TodoViewModel.cs:532-547` · `UpdateOverdueCount`

The only nested-loop `SelectMany` candidate in the codebase.

```csharp
var today = DateTime.Today;
var count = 0;
foreach (var columnVm in Columns)
{
    foreach (var todo in columnVm.Todos)
    {
        if (todo.Status == TodoStatus.Pending
            && todo.DueDate.HasValue
            && todo.DueDate.Value.Date < today)
            count++;
    }
}
OverdueCount = count;
```

```csharp
var today = DateTime.Today;
OverdueCount = Columns
    .SelectMany(c => c.Todos)
    .Count(t => t.Status == TodoStatus.Pending && t.DueDate.HasValue && t.DueDate.Value.Date < today);
```

16 → 5 lines.

### A2 — `Services/LiveTranscription/VoiceStatsCalculator.cs:12-61` · `Compute`

Biggest absolute reduction in the report: a manual `TryGetValue` accumulate (21-34), a projection
loop (38-44), and a 10-line hand-written `List.Sort` comparer (49-58).

```csharp
if (groups.TryGetValue(key, out var existing))
    groups[key] = (existing.Count + 1, existing.Total + duration);
else
    groups[key] = (1, duration);

grandTotal += duration;
```

```csharp
var byKey = samples
    .Select(s => (s.Speaker, Label: string.IsNullOrEmpty(s.SpeakerLabel) ? null : s.SpeakerLabel,
                  Duration: Math.Max(0, s.DurationSeconds)))
    .GroupBy(x => (x.Speaker, x.Label))
    .Select(g => (g.Key, Count: g.Count(), Total: g.Sum(x => x.Duration)))
    .ToList();
var grandTotal = byKey.Sum(g => g.Total);
return byKey
    .Select(g => new SpeakerVoiceStats(g.Key.Speaker, g.Key.Label, g.Count, g.Total,
        g.Count == 0 ? 0 : g.Total / g.Count, grandTotal == 0 ? 0 : g.Total / grandTotal))
    .OrderByDescending(s => s.TotalSpeechSeconds)
    .ThenBy(s => s.SpeakerLabel ?? string.Empty, StringComparer.Ordinal)
    .ThenBy(s => (int)s.Speaker)
    .ToList();
```

~40 → ~15 lines. **Two cares:** the intermediate `.ToList()` is load-bearing — `samples` is
`IEnumerable<VoiceSample>` and `grandTotal` needs a second read. And the XML doc's determinism
contract ("re-running Compute on the same input must always emit the same order") holds only
because the `OrderBy`/`ThenBy` chain is total and stable; keep that comment.

### A3 — `Infrastructure/Vault/WikiLinkReconciler.cs:102-113` · `IsSlugShaped`

```csharp
foreach (var c in s)
{
    if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
    {
        return false;
    }
}
return true;
```

```csharp
private static bool IsSlugShaped(string s) =>
    s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
```

12 → 2 lines.

### A4 — `Services/Interfaces/IAgentTimelineService.cs:100-111` and `:140-151`

`SanitizeUnroutedToolName` and `SanitizeCallId` run byte-identical validation loops.

```csharp
foreach (var c in toolName)
{
    if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '.' && c != ':' && c != '-')
        return "(unnamed)";
}
return toolName;
```

```csharp
private static bool IsToolIdChar(char c) =>
    char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-';

// SanitizeUnroutedToolName
return toolName.All(IsToolIdChar) ? toolName : "(unnamed)";
// SanitizeCallId
return callId.All(IsToolIdChar) ? callId : null;
```

**Correction to an earlier draft of this sweep:** the two charsets were reported as divergent. They
are **not** — both are `IsAsciiLetterOrDigit` plus `_ . : -`. Sharing one predicate is therefore
behavior-preserving, which matters because this is a privacy/sanitization gate feeding an
audit-table column contracted as "never an argument, never a result, never a path". It also
finally satisfies the file's own doc claim: *"Lives here so both gates share one definition rather
than one each."* The length bounds (64 / 128) differ and stay per-method.

### A5 — `Services/Consent/NamedConsentClassifier.cs:268-280` · `IsValidNameToken`

```csharp
return token.Length >= 2 && token.All(ch => char.IsLetter(ch) || ch is '-' or '\'');
```

13 → 2 lines.

### A6 — `Services/Consent/NamedConsentClassifier.cs:540-552` · `MatchesAnyLexiconToken`

```csharp
if (lexiconTokens.Contains(token)) return true;
foreach (var lexiconWord in lexiconTokens)
{
    if (TokenMatches(token, lexiconWord, out _)) return true;
}
return false;
```

```csharp
return lexiconTokens.Contains(token) || lexiconTokens.Any(w => TokenMatches(token, w, out _));
```

`out _` is legal in a lambda. 8 → 1 line.

### A7 — `Services/AgentContextCompactor.cs:368-380` and `:387-396`

```csharp
private static bool HasImageContent(ChatMessage message) =>
    message.Contents.Any(c => c is DataContent d
        && d.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

private static bool HasToolContent(ChatMessage message) =>
    message.Contents.Any(c => c is FunctionCallContent or FunctionResultContent);
```

26 → 6 lines for the pair. The original already uses `OrdinalIgnoreCase` — preserve it verbatim, it
is the comparison `PiaCloudChatClient`'s outbound converter uses and the XML doc pins the two
together.

### A8 — `Services/PolicyService.cs:63-75` · `ResolvePolicyFilePath`

```csharp
foreach (var dir in candidateDirectories)
{
    var path = Path.Combine(dir, PolicyFileName);
    if (File.Exists(path)) return path;
}
return Path.Combine(candidateDirectories[^1], PolicyFileName);
```

```csharp
return candidateDirectories.Select(d => Path.Combine(d, PolicyFileName)).FirstOrDefault(File.Exists)
    ?? Path.Combine(candidateDirectories[^1], PolicyFileName);
```

The "first existing, else last candidate" rule becomes readable in one glance.

### A9 — `Services/MeetingAttendee/TeamsMeetingUrl.cs:31-36` · `IsLikelyTeamsUrl`

```csharp
return TeamsHosts.Any(h => string.Equals(host, h, StringComparison.OrdinalIgnoreCase)
    || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
```

This is a security predicate (exact host or sub-domain only); the `Any` form makes the two accepted
shapes read as one rule.

### A10 — `Services/ExecutingRunStore.cs:29-38` · `IsExecuting`

```csharp
public bool IsExecuting(Guid chatId) => _chatByRun.Any(e => e.Value == chatId);
```

10 → 1 line. **Must be `.Any(...)` on the dictionary itself** — not `.Values.Any(...)`, not
`.ContainsValue(...)`. The class doc explains why: *"walks the live enumerator — not `Values`,
which snapshots under every bucket lock."* `Enumerable.Any` over the `ConcurrentDictionary` uses
that same live enumerator, so semantics hold — but keep the rationale in the doc comment, because
it becomes invisible once the body is one line.

### A11 — `Services/LiveTranscription/DirectTranscriptMarkdown.cs:115-125` · `ResolveDeduplicatedSpeakers`

```csharp
return bubbles
    .Select(b => SpeakerToDisplayNameConverter.Resolve(b.Speaker, b.SpeakerLabel, counterpartName))
    .Distinct(StringComparer.Ordinal)
    .ToList();
```

`Distinct` preserves first-occurrence order, so the method name stays honest. 11 → 4 lines.

### A12 — `ViewModels/Models/ChatSession.cs:918-923` · `BuildStepChatMessagesAsync`

```csharp
chatMessages.AddRange(Messages.Where(m => m != assistantMessage).Select(m => m.ToChatMessage()));
```

6 → 1 line, reference-equality semantics unchanged.

### A13 — `ViewModels/AssistantViewModel.cs:1660-1664` · `StreamVoiceModeResponse`

```csharp
chatMessages.AddRange(Messages.Select(m => m.ToChatMessage()));
```

5 → 1 line, and the `// Include existing conversation history` comment above it becomes redundant
(matches the repo's "default to no comment" discipline).

**A12/A13 care:** both locals are declared `new List<ChatMessage>` (`ChatSession.cs:913`,
`AssistantViewModel.cs:1655`), so `AddRange` compiles. Worth stating because the consumer
`RunModelExchangeAsync` takes `IList<ChatMessage>` (`ChatSession.cs:512`), which has no `AddRange`.

### A14 — `Infrastructure/GitignoreMatcher.cs:42-51` · `FromLines`

```csharp
return new GitignoreMatcher(lines.Select(ParseLine).OfType<Rule>().ToList());
```

`Rule` is a `sealed record` (line 29), so `OfType<Rule>()` filters nulls with no boxing and no
null-forgiving `!`. Order is preserved, which matters — gitignore negation is order-dependent.
8 → 1 line.

### Minor wins

| Site | Method | Rewrite |
|---|---|---|
| `Infrastructure/Vault/VaultSlug.cs:34-42` | `Slugify` | `StringBuilder` loop → `string.Concat(decomposed.Where(...))`. Heading slugification, not a hot path — the builder buys nothing. 9 → 3 |
| `Services/AgentContextCompactor.cs:315-321` | `CompactAsync` | `compacted.AddRange(kept.Where(m => pinnedSystem is null \|\| !pinnedSystem.Contains(m)));` — the `ReferenceEqualityComparer` is untouched. 5 → 1 |
| `Services/MeetingAttendee/TeamsMeetingSession.cs:550-556` | `GetAttendeeNamesAsync` | see care note below. 7 → 1 |
| `ViewModels/TodoViewModel.cs:549-558` | `ClampColumnWidth` | → `double.IsNaN(width) \|\| width <= 0 ? KanbanColumnViewModel.DefaultWidth : Math.Clamp(width, 200.0, 600.0)`. Not LINQ; the BCL name states the intent. 10 → 2 |

**`TeamsMeetingSession` care — write it as:**

```csharp
return names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).ToList();
```

The obvious form `.Select(n => n?.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToList()!` yields
`List<string?>`; the `!` applies to the list reference, not the elements, so it trips a
CS8619-class nullable warning — blocking under the Zero-Warning Policy. Filtering before trimming is
equivalent (`IsNullOrWhiteSpace` on untrimmed == `IsNullOrEmpty` on trimmed) and sidesteps it.

### Deliberately demoted

**`Helpers/FilePicker.cs:27-35`** — not listed as a win. The LINQ form calls `Path.GetExtension`
twice, and the actual defect is that `Behaviors/FileDropBehavior.cs:154-167` is a near-duplicate of
the same filter. The fix is deduping the two, not LINQ-ifying both. See Section D.

---

## Section B — switch expressions and lookup tables

These are "if/else clusters" in the everyday sense but are **not** LINQ. Listed separately so the
distinction stays visible.

### B1 — `Controls/Markdown/PiaMarkdownRenderer.cs:55-80` · `RenderBlock`

9 cases plus `default`, every one `case T x: return F(x);`.

```csharp
private static WpfBlock? RenderBlock(MdBlock block) => block switch
{
    HeadingBlock heading     => RenderHeading(heading),
    ParagraphBlock paragraph => RenderParagraph(paragraph),
    ListBlock list           => RenderList(list),
    QuoteBlock quote         => RenderQuote(quote),
    FencedCodeBlock fenced   => RenderCodeCard(fenced.Info, JoinCodeLines(fenced)),
    CodeBlock code           => RenderCodeCard(string.Empty, JoinCodeLines(code)),
    ThematicBreakBlock       => RenderThematicBreak(),
    MdTable table            => RenderTable(table),
    HtmlBlock html           => RenderHtmlBlock(html),
    _                        => null,
};
```

Order matters only for `FencedCodeBlock` before `CodeBlock` (subtype). Switch expressions preserve
top-to-bottom order and the compiler errors on an unreachable arm, so this is safe.

### B2 — `Controls/Markdown/PiaMarkdownRenderer.cs:281-310` · `RenderInline`

8 `case: return` plus a `default` containing an `if`. Same conversion, folding the default into a
pattern arm — `_ => inline is ContainerInline container ? RenderContainer(container) : null` —
which needs the 4-line `Span`/`AppendInlines` body extracted into `RenderContainer`. Marginally
weaker than B1 for that reason.

### B3 — `ViewModels/FirstRunWizardViewModel.cs:346-372` and `:374-397`

`ExecuteNext` / `ExecuteBack`, 5 `else if` between them. **The only true 4+-branch mapping chain in
the repo.** Every branch's sole effect is assigning `CurrentStep`, with one
`NotifyNavigationChanged()` after.

```csharp
CurrentStep = CurrentStep switch
{
    1 when IsE2EESetupVisible => 2,
    1 when IsLoggedIn         => 4,   // skip both E2EE (2) and Provider (3)
    1                         => 3,
    2                         => IsLoggedIn ? 4 : 3,
    _                         => CurrentStep + 1,
};
```

`ExecuteBack` likewise. The `if (CurrentStep >= TotalSteps - 1) return;` guard stays as-is.

### B4 — `Helpers/DroppedFileReader.cs:41-53` · `Classify`

6 sequential `if … return FileKind.X`, mixing inline `string.Equals` with three pre-built
`HashSet`s. Extensions are disjoint, so branch order is irrelevant — the purest input→output map in
the repo. One `static readonly FrozenDictionary<string, FileKind>` (OrdinalIgnoreCase) built from
the three existing sets replaces all of it.

**Regression trap:** the tempting one-liner
`KindByExtension.GetValueOrDefault(Path.GetExtension(path), FileKind.Unsupported)` **drops** the
existing `if (string.IsNullOrEmpty(ext)) return FileKind.Unsupported;` guard at line 44.
`Path.GetExtension` can return null, and `GetValueOrDefault(null)` on an `OrdinalIgnoreCase`
dictionary throws. Keep the guard.

### B5 — `Services/Plugins/PluginIconLoaderService.cs:95-109` · `IsSupportedImage`

6 magic-byte checks, all `return true`, all differing only by constants.

```csharp
private static readonly byte[][] Signatures =
[
    [0x89, 0x50, 0x4E, 0x47],       // PNG
    [0xFF, 0xD8, 0xFF],             // JPEG
    [0x47, 0x49, 0x46, 0x38],       // GIF
    [0x42, 0x4D],                   // BMP
    [0x00, 0x00, 0x01, 0x00],       // ICO
    [0x49, 0x49, 0x2A, 0x00],       // TIFF LE
    [0x4D, 0x4D, 0x00, 0x2A],       // TIFF BE
];

… => Signatures.Any(s => data.AsSpan(0, s.Length).SequenceEqual(s));
```

The existing `if (data.Length < 8) return false;` guard already makes every index safe. The table
is also where the format names finally become visible.

### B6 — `Models/ActionCardInfo.cs:202-255`

`AllowOnce`, `AllowForSession`, `AlwaysAllow`, `Decline`, `Cancel` — five 5-line `[RelayCommand]`
bodies differing only by a state and a decision constant.

```csharp
private void Resolve(ActionCardState state, ToolDecision? decision)
{
    if (State != ActionCardState.Pending) return;
    State = state;
    IsExpanded = false;
    IsDiffExpanded = false;
    if (decision is { } d) _tcs.TrySetResult(d); else _tcs.TrySetCanceled();
}

[RelayCommand] private void AllowOnce() => Resolve(ActionCardState.Accepted, ToolDecision.AllowOnce);
```

Keep the five attributed methods — the source generator needs them. The nullable `decision` handles
`Cancel`'s `TrySetCanceled`.

### Also noted — `Emoji/EmojiScanner.cs:103-129` · `IsDefaultEmojiScalar`

7 range `if … return true;` sit **immediately above a switch expression in the same method**. Fold
the ranges in as relational-pattern arms (`>= 0x1F300 and <= 0x1F5FF => true, // Misc Symbols`) so
the method is one construct instead of two, comments intact.

A `(lo, hi)[]` range table + `Any` would be **worse** here: this runs per codepoint on every
rendered string, and the array form loses the inline Unicode block names.

---

## Section C — leave imperative

Half the value of a checkup is the list of things that look convertible and are not. Grouped by the
reason conversion would break them.

**WPF per-frame layout.** `Controls/ColumnsPanel.cs:109-119, 139-151, 163-…, 207-215` — four loops
in `MeasureOverride`/`ArrangeOverride`, running height accumulators, `child.Measure`/`Arrange` side
effects. All four are correctly imperative.

**Loop state feeds back into the predicate.**
- `Controls/Markdown/CodeColorizer.cs:78-92` — `FindInnermostScope` looks like `MinBy`, but
  `bestLength` is passed *back into* `MatchScope(scope, start, end, bestLength)` as a pruning bound.
  Not a pure min, and it is a render path.
- `Services/LiveTranscription/SpeakerClusterer.cs:62-66, 101-108, 114-122` — union-find with
  `parent[]` mutation, `clusters--`, and a sorted-input `break`.
- `Services/LiveTranscription/AdaptiveSpeakerIdentificationService.cs:193-197, 213-218` — greedy
  matching where `stableByNew`/`takenPrev` mutate the predicate for later items.
- `Services/WebCitationExtractor.cs:66-71, 120-125` — `consumedTo` / `byUrl` carry state forward
  (`ordered.Count + 1` is the citation number).

**The source is mutated while it is enumerated — that *is* the semantics.**
`Services/MeetingAttendee/MeetingAttendeeService.cs:585-592` (checks `_attendees.Any(...)` while
adding to `_attendees`), `Services/Flow/FlowService.cs:201-206` (same shape).

**`out` parameter or early return carrying an error.** `Services/GitToolHandler.cs:380-385,
454-459` (`out var error` plus `return (error, null)` from inside the loop),
`Infrastructure/SensitivePathGuard.cs:51-61` (writes `out reason`; the *first* loop at 42-49 could
be `Any(...)`, but a half-converted method reads worse than either consistent form).

**Two accumulators in one pass, and the count is logged.**
`Services/AssistantChatService.cs:161-167` and `ViewModels/Models/ChatSessionManager.cs:1073-1079`
(dedup HashSet **and** an `absorbed++`/`added++` counter),
`Services/ScheduledJobToolHandler.cs:382-388` (`accepted`/`rejected` split),
`ViewModels/TranscriptOverlayViewModel.cs:264-271` (mutates `entry.Label` and sets a rebuild flag).

**Per-item side effects.** `ViewModels/MemoryViewModel.cs:536-544` (`File.Copy`),
`Services/OutputService.cs:55-64` (`await Task.Delay` + `KeyboardInput.SendCharacter`),
`Services/Wiki/VaultIndexService.cs:282-286` (`await CategoryForTargetAsync` inside the grouping
loop), `Services/Providers/Http/MistralThinkingResponseHandler.cs:101-112` (`changed |= …` mutating
the JSON tree in place).

**`ObservableCollection<T>` has no `AddRange`.** ~8 fill loops — `HistoryViewModel.cs:155/162/224`,
`MemoryViewModel.cs:243`, `OptimizeViewModel.cs:616`, `GeneralSettingsViewModel.cs:360`,
`AssistantSettingsViewModel.cs:417`, `ToolPermissionsSettingsViewModel.cs:133`. Mandatory, not a
smell.

**Ascending-threshold ladders are not lookups.**
`Converters/NextFireAtToShortStringConverter.cs:17-42` (7 branches) and
`Converters/FlowRelativeTimeConverter.cs:25-33` (4). These look like textbook mapping chains but are
ordered `TimeSpan` comparisons. A switch expression needs `_ when delta.TotalMinutes < 60 => …`
arms that are strictly longer, and a table loses the "first match wins" reading.

**Validation guards where order is the contract.** `Infrastructure/SafeFolderPath.cs:33-41, 62-67,
242` — 11 guards across 3 methods, each rejecting a different attack (rooted path, NUL byte,
invalid chars); order is security-relevant. `Views/Dialogs/{PersonaEdit,ProviderEdit,TemplateEdit}
ContentDialog.xaml.cs` — distinct validation messages per guard.
`Services/GitToolHandler.cs` has 36 `if … return`, but they are short-circuits spread across ~15
separate methods, not a ladder.

**Switch statements whose cases have side effects.** `ViewModels/Models/ChatSession.cs:583-611,
1187-1210`, `ViewModels/Flow/FlowItemViewModel.cs:238-274`, `Services/Plugins/PluginService.cs:456+`
— logging, `await`, buffer mutation. These cannot become switch expressions; converting them would
be actively harmful.

**The model answer — `ViewModels/MemoryViewModel.cs:320-329` (`ProjectRecallHits`).** The code
*already documents* why `ToDictionary` was rejected: a hand-edited file may carry two identical `##`
headings, and `ToDictionary` would throw and crash search, so last-wins is deliberate. This is
exactly the reasoning every other entry in this section is an instance of.

---

## Section D — follow-ups, out of scope for this checkup

### D1 — `Infrastructure/SqliteContext.cs` (largest single cleanup in the repo)

Roughly **12 near-identical PRAGMA-probe blocks** (~lines 578, 600, 655, 693, 762, 797, 837, 874,
896, 927, 953, 1002), each a variant of:

```csharp
var hasProcessingTimeMs = false;
using var pragma = _connection!.CreateCommand();
pragma.CommandText = "PRAGMA table_info(Sessions)";
using var reader = pragma.ExecuteReader();
while (reader.Read())
{
    if (reader.GetString(1) == "ProcessingTimeMs") { hasProcessingTimeMs = true; break; }
}
reader.Close();
if (!hasProcessingTimeMs) { /* ALTER TABLE … */ }
```

The fix is two helpers — `bool ColumnExists(string table, string column)` and
`HashSet<string> ColumnsOf(string table)` for the multi-flag blocks — collapsing each site to
`if (!ColumnExists("Sessions", "ProcessingTimeMs")) { … }`. On the order of 100+ lines removed.

**The sharpest form of the finding: the file is already inconsistent with itself.** Line ~957 uses a
`switch` over `r.GetString(1)` for exactly the multi-flag shape that lines 703-707 and 766-769 write
as `else if` chains. The helper makes the style question moot.

Excluded from this checkup because it is a duplicate-block refactor rather than an if/else or LINQ
issue, and because `MigrateSchema` runs at every startup against real user databases. Existing
coverage: `SqliteContextTests` (4 hits), `AssistantChatsMigrationTests` (2).

### D2 — Duplicated date-bucket classification

`ViewModels/HistoryViewModel.cs:230-238` and `ViewModels/AssistantHistoryViewModel.cs:289-297`
contain a **byte-identical** `Classify` (only the parameter name differs, `createdLocal` vs
`updatedLocal`), and `BucketResourceKey` right below is identical too. The ladder itself is fine
(ordered date thresholds — see Section C); the *duplication* is the finding. A shared
`HistoryDateBuckets` static helper fixes it.

### D3 — Duplicated dropped-file extension filter

`Helpers/FilePicker.cs:27-35` and `Behaviors/FileDropBehavior.cs:154-167` are near-duplicates of the
same filter. Dedupe rather than LINQ-ify both.

### D4 — Untested surfaces

`DroppedFileReader`, `FilePicker` and `FileDropBehavior` have **zero test coverage** — which is
precisely inverted from how mechanical they look. Any future conversion there wants tests first.

---

## Test-coverage map

For whoever executes these later. Counts are grep hits in `tests/`:

| Component | Hits |
|---|---|
| `GitignoreMatcher` | 22 |
| `DirectTranscriptMarkdown` | 13 |
| `VoiceStatsCalculator` | 10 |
| `ExecutingRunStore` | 5 |
| `SqliteContext` (+ `AssistantChatsMigration`) | 4 (+2) |
| `WikiLinkReconciler` | 3 |
| `EmojiScanner` | 2 |
| `DroppedFileReader` / `FilePicker` / `FileDropBehavior` | **0** |

## If these are applied

The gate is the project standard, not just `dotnet test`:

```bash
dotnet test                      # bar: failed: 0
dotnet build -t:Rebuild -v:n     # Debug
dotnet build -t:Rebuild -v:n -c Release
```

Both rebuilds matter: several rewrites change nullable annotations, and an incremental build does
not re-emit warnings from projects it skips.
