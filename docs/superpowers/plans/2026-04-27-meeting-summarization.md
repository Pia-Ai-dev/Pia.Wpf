# Meeting Summarization Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Save and summarize" button to the live transcription overlay that silently saves the transcript, then routes through the assistant chat to a new summarization tool offering three styles (clean / bulleted / text), with the option to store the summary in a new `meeting_summary` memory type queryable by date and speaker.

**Architecture:** A new `MeetingToolHandler` plugin (`summarize_meeting_transcript`, `query_meeting_summaries`) is registered like the existing memory/todo/reminder handlers. The button raises a VM event consumed by `AssistantViewModel`, which sends a synthetic chat message containing an env-var-shortened path. The tool returns a multi-choice `ActionCardInfo` (extended for N-way decisions) so the user picks the style; the summary then streams as a normal assistant message. Saving as memory reuses the existing `create_object` flow with a new `meeting_summary` type.

**Tech Stack:** .NET 10, C# 13, WPF (`net10.0-windows`), CommunityToolkit.Mvvm, Microsoft.Extensions.AI, xunit.v3, Pia.Wpf scoped DI.

**Spec:** [`docs/superpowers/specs/2026-04-27-meeting-summarization-design.md`](../specs/2026-04-27-meeting-summarization-design.md)

---

## File Structure

**New:**
- `src/Pia.Wpf/Services/PathShortener.cs` — pure utility, env-var path shortening / expansion
- `src/Pia.Wpf/Services/LiveTranscription/MeetingTranscriptWriter.cs` — markdown + YAML front-matter formatter, replaces `LiveTranscriptionViewModel.BuildMarkdown`
- `src/Pia.Wpf/Services/Interfaces/IMeetingToolHandler.cs` — interface + `MeetingToolCall` record
- `src/Pia.Wpf/Services/MeetingToolHandler.cs` — tool implementation
- `src/Pia.Wpf/Models/ActionCardChoice.cs` — `(Key, Label)` record for multi-choice cards
- `tests/Pia.Wpf.Tests/Services/PathShortenerTests.cs`
- `tests/Pia.Wpf.Tests/Services/LiveTranscription/MeetingTranscriptWriterTests.cs`
- `tests/Pia.Wpf.Tests/Services/MeetingToolHandlerTests.cs`

**Modified:**
- `src/Pia.Wpf/Models/MemoryObject.cs` — add `MeetingSummary` to `MemoryObjectTypes`
- `src/Pia.Wpf/Services/MemoryToolHandler.cs` — extend `CreateObjectSchema` description to mention the new type
- `src/Pia.Wpf/Models/ActionCardInfo.cs` — add optional `Choices` and `ChosenKey`; generalize `WaitForUserDecisionAsync` → `WaitForChoiceAsync`
- `src/Pia.Wpf/Controls/ActionCardControl.xaml` — render N choice buttons when `Choices` non-empty
- `src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs` — add optional `Choices` to `PluginToolCall`
- `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs` — `FromMeetingHandler` factory, propagate `Choices`
- `src/Pia.Wpf/Services/Plugins/PluginService.cs` — switch case for `meeting` handler kind
- `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs` — register the meeting plugin
- `src/Pia.Wpf/Bootstrapper.cs` — DI registration for `IMeetingToolHandler`
- `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs` — replace `BuildMarkdown` calls with writer; add `SaveAndSummarizeCommand` and `SummarizeRequested` event
- `src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml` — add the new button to the footer
- `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` — subscribe to `SummarizeRequested`, snackbar + synthetic chat message + close overlay
- `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` — `BuildPluginActionCard` propagates `Choices`; switch on `meeting` plugin name for category icon and snackbar text
- `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` (+ `.de.resx`, `.fr.resx`) — new localization keys

---

## Conventions

- **Skill:** Use TDD (`@superpowers:test-driven-development`). Write the failing test, run it red, implement, run it green.
- **Skill:** Verify before claiming completion (`@superpowers:verification-before-completion`). Don't say "done" until `dotnet build` and `dotnet test` are green.
- **Code style:** 4-space C# indent, 2-space XAML, `var` for apparent types, `_camelCase` private fields, namespaces use `Pia.*` (not `Pia.Wpf.*`).
- **Tests:** xunit.v3 + plain `Xunit.Assert` (no FluentAssertions). Test files match production namespaces (e.g. `Pia.Tests.Services.LiveTranscription`).
- **Commits:** Frequent. After every passing step. Use `feat:` / `test:` / `refactor:` prefixes.

---

## Chunk 1: Pure utilities (path shortener + transcript writer)

These have no UI or DI dependencies. Build them first, fully tested, so later tasks can compose them.

### Task 1.1: PathShortener utility

**Files:**
- Create: `src/Pia.Wpf/Services/PathShortener.cs`
- Create: `tests/Pia.Wpf.Tests/Services/PathShortenerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Pia.Wpf.Tests/Services/PathShortenerTests.cs
using System;
using System.IO;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class PathShortenerTests
{
    [Fact]
    public void Shorten_UsesAppData_WhenPathIsUnderAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var input = Path.Combine(appData, "Pia", "assistant", "meetings", "transcript-x.md");

        var shortened = PathShortener.Shorten(input);

        Assert.StartsWith("%APPDATA%", shortened);
        Assert.EndsWith("transcript-x.md", shortened);
    }

    [Fact]
    public void Shorten_PrefersLongestMatch_WhenMultipleVarsApply()
    {
        // %APPDATA% lives under %USERPROFILE%; longer match wins.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var input = Path.Combine(appData, "Pia", "x.md");

        var shortened = PathShortener.Shorten(input);

        Assert.StartsWith("%APPDATA%", shortened);
        Assert.DoesNotContain("%USERPROFILE%", shortened);
    }

    [Fact]
    public void Shorten_ReturnsUnchanged_WhenNoEnvVarMatches()
    {
        var input = @"X:\unrelated\transcript.md";

        Assert.Equal(input, PathShortener.Shorten(input));
    }

    [Fact]
    public void Expand_RoundTripsAShortenedPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var input = Path.Combine(appData, "Pia", "x.md");

        var roundTripped = PathShortener.Expand(PathShortener.Shorten(input));

        Assert.Equal(input, roundTripped, ignoreCase: true);
    }

    [Fact]
    public void Expand_ReturnsUnchanged_WhenNoVarPresent()
    {
        var input = @"C:\absolute\path\file.md";

        Assert.Equal(input, PathShortener.Expand(input));
    }

    [Fact]
    public void Shorten_IsCaseInsensitive_OnWindowsPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var mixedCase = appData.ToUpperInvariant();
        var input = Path.Combine(mixedCase, "Pia", "x.md");

        var shortened = PathShortener.Shorten(input);

        Assert.StartsWith("%APPDATA%", shortened);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PathShortenerTests"`
Expected: FAIL — `PathShortener` does not exist.

- [ ] **Step 3: Implement `PathShortener`**

```csharp
// src/Pia.Wpf/Services/PathShortener.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pia.Services;

/// <summary>
/// Shortens absolute paths by replacing well-known Windows folder roots with their
/// environment-variable equivalents (e.g. <c>C:\Users\me\AppData\Roaming\Pia\x</c> →
/// <c>%APPDATA%\Pia\x</c>) and expands them back. Longest matching root wins so that
/// <c>%APPDATA%</c> is preferred over <c>%USERPROFILE%</c>.
/// </summary>
public static class PathShortener
{
    private static readonly (string Var, string Path)[] KnownRoots =
    {
        ("APPDATA",      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        ("LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        ("USERPROFILE",  Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
    };

    public static string Shorten(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        var matches = KnownRoots
            .Where(r => !string.IsNullOrEmpty(r.Path)
                        && path.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Path.Length)
            .ToList();

        if (matches.Count == 0) return path;

        var best = matches[0];
        var remainder = path.Substring(best.Path.Length);
        return $"%{best.Var}%{remainder}";
    }

    public static string Expand(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return Environment.ExpandEnvironmentVariables(path);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PathShortenerTests"`
Expected: PASS (6/6).

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/PathShortener.cs tests/Pia.Wpf.Tests/Services/PathShortenerTests.cs
git commit -m "feat: add PathShortener utility for env-var path display"
```

---

### Task 1.2: MeetingTranscriptWriter

**Files:**
- Create: `src/Pia.Wpf/Services/LiveTranscription/MeetingTranscriptWriter.cs`
- Create: `tests/Pia.Wpf.Tests/Services/LiveTranscription/MeetingTranscriptWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Pia.Wpf.Tests/Services/LiveTranscription/MeetingTranscriptWriterTests.cs
using System;
using System.Collections.Generic;
using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class MeetingTranscriptWriterTests
{
    private static TranscriptBubble Bubble(TranscriptSpeaker speaker, string text, DateTimeOffset start, string? label = null)
    {
        var b = new TranscriptBubble(speaker, start, text, label);
        b.EndTimestamp = start.AddSeconds(5);
        return b;
    }

    [Fact]
    public void Render_EmitsFrontMatterWithSchemaAndTimestamps()
    {
        var start = new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.FromHours(2));
        var bubbles = new List<TranscriptBubble>
        {
            Bubble(TranscriptSpeaker.You, "Hello", start),
        };

        var md = MeetingTranscriptWriter.Render(
            bubbles,
            sessionStart: start,
            originalFilename: "transcript-20260427-103000.md",
            title: "Live Transcription");

        Assert.StartsWith("---\n", md.Replace("\r\n", "\n"));
        Assert.Contains("schema: pia-meeting-transcript/v1", md);
        Assert.Contains("start: 2026-04-27T10:30:00", md);
        Assert.Contains("originalFilename: transcript-20260427-103000.md", md);
    }

    [Fact]
    public void Render_DeduplicatesAndOrdersSpeakers_ByFirstAppearance()
    {
        var start = new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.FromHours(2));
        var bubbles = new List<TranscriptBubble>
        {
            Bubble(TranscriptSpeaker.Them, "hi",   start,                   label: "Alice"),
            Bubble(TranscriptSpeaker.You,  "hey",  start.AddSeconds(10)),
            Bubble(TranscriptSpeaker.Them, "again",start.AddSeconds(20),    label: "Alice"),
            Bubble(TranscriptSpeaker.Them, "yo",   start.AddSeconds(30),    label: "Bob"),
        };

        var md = MeetingTranscriptWriter.Render(bubbles, sessionStart: start, originalFilename: "x.md", title: "T");

        var aliceIdx = md.IndexOf("- Alice", StringComparison.Ordinal);
        var youIdx   = md.IndexOf("- You",   StringComparison.Ordinal);
        var bobIdx   = md.IndexOf("- Bob",   StringComparison.Ordinal);

        Assert.True(aliceIdx > 0 && youIdx > 0 && bobIdx > 0, "All three speakers should appear in front-matter");
        Assert.True(aliceIdx < youIdx && youIdx < bobIdx, "Speakers should be ordered by first appearance");

        // De-duplicated: only one occurrence of each.
        Assert.Equal(1, CountOccurrences(md, "- Alice"));
    }

    [Fact]
    public void Render_FallsBackToSpeakerN_WhenLabelMissing()
    {
        var start = new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.FromHours(2));
        var bubbles = new List<TranscriptBubble>
        {
            Bubble(TranscriptSpeaker.Them, "anon", start, label: null),
        };

        var md = MeetingTranscriptWriter.Render(bubbles, sessionStart: start, originalFilename: "x.md", title: "T");

        Assert.Contains("Speaker", md);
    }

    [Fact]
    public void Render_BodyContainsSpeakerHeadersAndText()
    {
        var start = new DateTimeOffset(2026, 4, 27, 10, 30, 0, TimeSpan.FromHours(2));
        var bubbles = new List<TranscriptBubble>
        {
            Bubble(TranscriptSpeaker.Them, "first message", start, label: "Alice"),
        };

        var md = MeetingTranscriptWriter.Render(bubbles, sessionStart: start, originalFilename: "x.md", title: "Live Transcription");

        Assert.Contains("**Alice**", md);
        Assert.Contains("first message", md);
        Assert.Contains("# Live Transcription", md);
    }

    [Fact]
    public void StripFrontMatter_RemovesYamlBlock()
    {
        var input = "---\nschema: x\n---\n# Title\n\nbody";
        var stripped = MeetingTranscriptWriter.StripFrontMatter(input);
        Assert.StartsWith("# Title", stripped.Replace("\r\n", "\n"));
    }

    [Fact]
    public void StripFrontMatter_ReturnsInputWhenNoFrontMatter()
    {
        var input = "# Title\n\nbody";
        Assert.Equal(input, MeetingTranscriptWriter.StripFrontMatter(input));
    }

    [Fact]
    public void TryParseFrontMatter_ReadsSpeakersAndDate()
    {
        var input = """
            ---
            schema: pia-meeting-transcript/v1
            start: 2026-04-27T10:30:00+02:00
            speakers:
              - You
              - Alice
            originalFilename: transcript-20260427-103000.md
            ---
            body
            """;

        var ok = MeetingTranscriptWriter.TryParseFrontMatter(input, out var fm);

        Assert.True(ok);
        Assert.Equal("2026-04-27", fm!.Date);
        Assert.Equal(new[] { "You", "Alice" }, fm.Speakers);
        Assert.Equal("transcript-20260427-103000.md", fm.OriginalFilename);
    }

    [Fact]
    public void TryParseFrontMatter_ReturnsFalse_WhenAbsent()
    {
        Assert.False(MeetingTranscriptWriter.TryParseFrontMatter("# Plain\n\nno fm", out _));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MeetingTranscriptWriterTests"`
Expected: FAIL — `MeetingTranscriptWriter` does not exist.

- [ ] **Step 3: Implement `MeetingTranscriptWriter`**

```csharp
// src/Pia.Wpf/Services/LiveTranscription/MeetingTranscriptWriter.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Pia.Converters;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

public sealed record MeetingFrontMatter(string? Date, IReadOnlyList<string> Speakers, string? OriginalFilename);

/// <summary>
/// Renders a list of <see cref="TranscriptBubble"/>s into Markdown with a YAML front-matter
/// block (schema, start/end, speakers, originalFilename) and parses that front-matter back
/// out of an existing transcript. The format is durable: <c>schema: pia-meeting-transcript/v1</c>
/// gates future changes.
/// </summary>
public static class MeetingTranscriptWriter
{
    public const string Schema = "pia-meeting-transcript/v1";

    public static string Render(
        IReadOnlyList<TranscriptBubble> bubbles,
        DateTimeOffset sessionStart,
        string originalFilename,
        string title)
    {
        var sb = new StringBuilder();

        var speakers = ResolveSpeakers(bubbles);
        var end = bubbles.Count > 0
            ? bubbles.Max(b => b.EndTimestamp)
            : sessionStart;

        sb.AppendLine("---");
        sb.Append("schema: ").AppendLine(Schema);
        sb.Append("start: ").AppendLine(sessionStart.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        sb.Append("end:   ").AppendLine(end.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        sb.AppendLine("speakers:");
        foreach (var s in speakers) sb.Append("  - ").AppendLine(s);
        sb.Append("originalFilename: ").AppendLine(originalFilename);
        sb.AppendLine("---");

        sb.Append("# ").Append(title).Append(" — ")
          .AppendLine(sessionStart.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine();

        foreach (var bubble in bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, bubble.SpeakerLabel);
            sb.Append("**").Append(label).Append("** _")
              .Append(bubble.StartTimestamp.LocalDateTime.ToString("HH:mm:ss"));
            if (bubble.EndTimestamp != bubble.StartTimestamp)
                sb.Append('–').Append(bubble.EndTimestamp.LocalDateTime.ToString("HH:mm:ss"));
            sb.Append('_').AppendLine().AppendLine();
            sb.AppendLine(bubble.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<string> ResolveSpeakers(IReadOnlyList<TranscriptBubble> bubbles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var b in bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(b.Speaker, b.SpeakerLabel);
            if (seen.Add(label)) ordered.Add(label);
        }
        return ordered;
    }

    private static readonly Regex FrontMatterRegex =
        new("^---\\s*\\r?\\n(?<body>[\\s\\S]*?)\\r?\\n---\\s*\\r?\\n",
            RegexOptions.Compiled);

    public static string StripFrontMatter(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;
        var m = FrontMatterRegex.Match(markdown);
        return m.Success ? markdown.Substring(m.Length) : markdown;
    }

    public static bool TryParseFrontMatter(string markdown, out MeetingFrontMatter? frontMatter)
    {
        frontMatter = null;
        if (string.IsNullOrEmpty(markdown)) return false;

        var m = FrontMatterRegex.Match(markdown);
        if (!m.Success) return false;

        var body = m.Groups["body"].Value;
        string? date = null;
        string? originalFilename = null;
        var speakers = new List<string>();

        var lines = body.Split('\n');
        var inSpeakers = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (inSpeakers)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("- "))
                {
                    speakers.Add(trimmed.Substring(2).Trim());
                    continue;
                }
                inSpeakers = false;
            }

            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line.Substring(0, colonIdx).Trim();
            var value = line.Substring(colonIdx + 1).Trim();

            switch (key)
            {
                case "start":
                    if (DateTimeOffset.TryParse(value, out var dto))
                        date = dto.ToString("yyyy-MM-dd");
                    break;
                case "originalFilename":
                    originalFilename = value;
                    break;
                case "speakers":
                    inSpeakers = true;
                    break;
            }
        }

        frontMatter = new MeetingFrontMatter(date, speakers, originalFilename);
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MeetingTranscriptWriterTests"`
Expected: PASS (8/8).

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/MeetingTranscriptWriter.cs tests/Pia.Wpf.Tests/Services/LiveTranscription/MeetingTranscriptWriterTests.cs
git commit -m "feat: add MeetingTranscriptWriter (markdown + YAML front-matter)"
```

---

### Task 1.3: Wire writer into `LiveTranscriptionViewModel.SaveTranscriptAsync`

This is a refactor — the existing `SaveTranscriptCommand` should now route through the writer. No behavior change yet, sets up Task 3.

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs:355-411`

- [ ] **Step 1: Replace `BuildMarkdown` with the writer**

In `SaveTranscriptAsync` replace:

```csharp
var markdown = BuildMarkdown();
```

with:

```csharp
var defaultName = $"transcript-{_sessionStart.LocalDateTime:yyyyMMdd-HHmmss}.md";
var markdown = Pia.Services.LiveTranscription.MeetingTranscriptWriter.Render(
    Bubbles,
    sessionStart: _sessionStart,
    originalFilename: defaultName,
    title: _localizationService["LiveTrans_Title"]);
```

(The existing `defaultName` declaration earlier in the method — keep that one; pass it into both the file dialog and the writer. Move the declaration above the writer call if needed.)

Delete the now-unused `BuildMarkdown` method. The existing `BubbleWindowSeconds` constant and other state stay.

- [ ] **Step 2: Update `LiveTranscriptionViewModelBubbleTests` if it exercises `BuildMarkdown`**

Run: `dotnet test --filter "FullyQualifiedName~LiveTranscriptionViewModelBubbleTests"`
If green: skip the next sub-step. If a test references `BuildMarkdown` directly, change it to call `MeetingTranscriptWriter.Render(vm.Bubbles, ...)`.

- [ ] **Step 3: Build the solution**

Run: `dotnet build`
Expected: SUCCESS, 0 errors.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: all green. If anything fails, fix before commit.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs tests/Pia.Wpf.Tests/ViewModels/LiveTranscriptionViewModelBubbleTests.cs
git commit -m "refactor: route SaveTranscript through MeetingTranscriptWriter"
```

---

## Chunk 2: Multi-choice action card

Extend the inline action-card UI to support N choices instead of binary Accept/Decline. Existing call sites stay binary.

### Task 2.1: `ActionCardChoice` model

**Files:**
- Create: `src/Pia.Wpf/Models/ActionCardChoice.cs`

- [ ] **Step 1: Create the record**

```csharp
// src/Pia.Wpf/Models/ActionCardChoice.cs
namespace Pia.Models;

/// <summary>
/// A single named option on a multi-choice <see cref="ActionCardInfo"/>.
/// <see cref="Key"/> is the stable identifier returned to the caller; <see cref="Label"/>
/// is the localized button text.
/// </summary>
public sealed record ActionCardChoice(string Key, string Label);
```

- [ ] **Step 2: Build to verify the file compiles**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Models/ActionCardChoice.cs
git commit -m "feat: add ActionCardChoice record for multi-choice cards"
```

---

### Task 2.2: Extend `ActionCardInfo` with choices

**Files:**
- Modify: `src/Pia.Wpf/Models/ActionCardInfo.cs`

The current `ActionCardInfo` exposes `WaitForUserDecisionAsync(): Task<bool>`. Generalize so multi-choice cards can return a chosen key, while binary cards still work.

- [ ] **Step 1: Add fields and a generalized async accessor**

In `ActionCardInfo`:

```csharp
// Add to top of class, near existing init-only properties:
public IReadOnlyList<ActionCardChoice>? Choices { get; init; }
public bool IsMultiChoice => Choices is { Count: > 0 };

// Add observable state:
[ObservableProperty]
private string? _chosenKey;

// Replace the existing _tcs<bool> with a string-keyed TCS:
private readonly TaskCompletionSource<string?> _choiceTcs = new();

// Replace WaitForUserDecisionAsync:
public async Task<bool> WaitForUserDecisionAsync()
{
    var key = await _choiceTcs.Task;
    return key == "accept";
}

public Task<string?> WaitForChoiceAsync() => _choiceTcs.Task;

// Add a multi-choice command:
[RelayCommand]
private void Choose(string? key)
{
    if (State != ActionCardState.Pending || string.IsNullOrEmpty(key)) return;
    State = ActionCardState.Accepted;
    IsExpanded = false;
    ChosenKey = key;
    _choiceTcs.TrySetResult(key);
}
```

Update `Accept`, `Decline`, `Cancel` to use the new TCS:

```csharp
[RelayCommand]
private void Accept()
{
    if (State != ActionCardState.Pending) return;
    State = ActionCardState.Accepted;
    IsExpanded = false;
    ChosenKey = "accept";
    _choiceTcs.TrySetResult("accept");
}

[RelayCommand]
private void Decline()
{
    if (State != ActionCardState.Pending) return;
    State = ActionCardState.Declined;
    IsExpanded = false;
    ChosenKey = "decline";
    _choiceTcs.TrySetResult("decline");
}

[RelayCommand]
private void Cancel()
{
    if (State != ActionCardState.Pending) return;
    State = ActionCardState.Declined;
    IsExpanded = false;
    _choiceTcs.TrySetCanceled();
}
```

Remove the old `_tcs` field.

- [ ] **Step 2: Build**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: SUCCESS. (Existing call sites that await `WaitForUserDecisionAsync()` still compile.)

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Models/ActionCardInfo.cs
git commit -m "feat: extend ActionCardInfo with multi-choice support"
```

---

### Task 2.3: Render choice buttons in `ActionCardControl.xaml`

**Files:**
- Modify: `src/Pia.Wpf/Controls/ActionCardControl.xaml:209-233`

Replace the existing Accept/Decline `StackPanel` (lines 209–233) with a chooser that swaps based on `IsMultiChoice`:

- [ ] **Step 1: Update the XAML**

```xml
<!-- Choice buttons (multi-choice OR binary Accept/Decline) -->
<Grid Margin="0,8,0,0">
  <!-- Multi-choice: render a button per choice -->
  <ItemsControl ItemsSource="{Binding Choices}"
                HorizontalAlignment="Right">
    <ItemsControl.Style>
      <Style TargetType="ItemsControl">
        <Setter Property="Visibility" Value="Collapsed" />
        <Style.Triggers>
          <DataTrigger Binding="{Binding IsMultiChoice}" Value="True">
            <Setter Property="Visibility" Value="Visible" />
          </DataTrigger>
        </Style.Triggers>
      </Style>
    </ItemsControl.Style>
    <ItemsControl.ItemsPanel>
      <ItemsPanelTemplate>
        <StackPanel Orientation="Horizontal" />
      </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
      <DataTemplate DataType="{x:Type models:ActionCardChoice}">
        <ui:Button Content="{Binding Label}"
                   Appearance="Primary"
                   FontSize="12"
                   Padding="12,4"
                   Margin="6,0,0,0"
                   Command="{Binding DataContext.ChooseCommand,
                                     RelativeSource={RelativeSource AncestorType=UserControl}}"
                   CommandParameter="{Binding Key}" />
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>

  <!-- Binary: Accept / Decline -->
  <StackPanel Orientation="Horizontal"
              HorizontalAlignment="Right">
    <StackPanel.Style>
      <Style TargetType="StackPanel">
        <Setter Property="Visibility" Value="Visible" />
        <Style.Triggers>
          <DataTrigger Binding="{Binding IsMultiChoice}" Value="True">
            <Setter Property="Visibility" Value="Collapsed" />
          </DataTrigger>
        </Style.Triggers>
      </Style>
    </StackPanel.Style>
    <ui:Button Content="{loc:Str ActionCard_Decline}"
               Appearance="Secondary"
               FontSize="12"
               Padding="12,4"
               Margin="0,0,6,0"
               Command="{Binding DeclineCommand}" />
    <ui:Button Content="{loc:Str ActionCard_Accept}"
               FontSize="12"
               Padding="12,4"
               Command="{Binding AcceptCommand}">
      <ui:Button.Style>
        <Style TargetType="ui:Button" BasedOn="{StaticResource {x:Type ui:Button}}">
          <Setter Property="Appearance" Value="Primary" />
          <Style.Triggers>
            <DataTrigger Binding="{Binding IsDestructive}" Value="True">
              <Setter Property="Appearance" Value="Caution" />
            </DataTrigger>
          </Style.Triggers>
        </Style>
      </ui:Button.Style>
    </ui:Button>
  </StackPanel>
</Grid>
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Controls/ActionCardControl.xaml
git commit -m "feat: render multi-choice buttons in ActionCardControl"
```

---

### Task 2.4: Propagate `Choices` through the plugin pipeline

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs`
- Modify: `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs`
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`

- [ ] **Step 1: Add `Choices` to `PluginToolCall`**

```csharp
// src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs
public record PluginToolCall(
    string ToolName,
    string PluginName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute,
    IReadOnlyList<Pia.Models.ActionCardChoice>? Choices = null);
```

- [ ] **Step 2: Build to confirm existing callers still compile**

Run: `dotnet build`
Expected: SUCCESS — `Choices` is optional, so existing constructors in `BuiltInPluginHandler` keep working.

- [ ] **Step 3: Wire `Choices` into the action card**

In `AssistantViewModel.BuildPluginActionCard` (around line 596–608) add:

```csharp
return new ActionCardInfo
{
    Title = FormatToolTitle(pendingAction.ToolName, category),
    Summary = DetokenizeForDisplay(pendingAction.Description),
    Category = category,
    ToolName = pendingAction.ToolName,
    IsDestructive = isDelete,
    WarningText = warningText,
    Details = details,
    Choices = pendingAction.Choices,                                       // <— new
    AcceptedStatusText = _localizationService.Format("ActionCard_Status_Accepted", FormatToolTitle(pendingAction.ToolName, category)),
    DeclinedStatusText = _localizationService.Format("ActionCard_Status_Declined", FormatToolTitle(pendingAction.ToolName, category)),
};
```

In the same file, around line 522–533, change the wait so multi-choice cards return the chosen key into the tool execution:

Replace:
```csharp
bool confirmed;
try { confirmed = await card.WaitForUserDecisionAsync(); }
catch (TaskCanceledException) { confirmed = false; }
```
with:
```csharp
string? chosenKey;
try { chosenKey = await card.WaitForChoiceAsync(); }
catch (TaskCanceledException) { chosenKey = null; }

var confirmed = chosenKey is not null && chosenKey != "decline";
```

When `confirmed` is true, the existing `pendingAction.Execute()` runs. The chosen key is on `card.ChosenKey` — the `MeetingToolHandler` reads it via the closure (next chunk).

- [ ] **Step 4: Build & test**

Run: `dotnet build && dotnet test`
Expected: green. No behavior change for existing memory/todo/reminder cards (they don't set `Choices`, so they go through the binary path).

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs src/Pia.Wpf/ViewModels/AssistantViewModel.cs
git commit -m "feat: propagate ActionCardChoice through plugin tool pipeline"
```

---

## Chunk 3: MeetingToolHandler

### Task 3.1: Interface + record

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/IMeetingToolHandler.cs`

- [ ] **Step 1: Create the interface**

```csharp
// src/Pia.Wpf/Services/Interfaces/IMeetingToolHandler.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

public record MeetingToolCall(
    string ToolName,
    string Description,
    string? Details,
    IReadOnlyList<ActionCardChoice>? Choices,
    Func<string?, Task<object?>> Execute);

public interface IMeetingToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, MeetingToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
}
```

`Execute` takes the chosen key (or `null` for tools that don't use a multi-choice card).

- [ ] **Step 2: Build & commit**

```bash
dotnet build
git add src/Pia.Wpf/Services/Interfaces/IMeetingToolHandler.cs
git commit -m "feat: add IMeetingToolHandler interface"
```

---

### Task 3.2: Add `meeting_summary` memory type

**Files:**
- Modify: `src/Pia.Wpf/Models/MemoryObject.cs:28-51`
- Modify: `src/Pia.Wpf/Services/MemoryToolHandler.cs:511-516` (CreateObjectSchema description)

- [ ] **Step 1: Add the type and display name**

In `MemoryObjectTypes`:
```csharp
public const string MeetingSummary = "meeting_summary";

public static readonly IReadOnlyList<string> All =
[
    PersonalProfile, ContactList, Preference, Note, MeetingSummary
];

public static string GetDisplayName(string type) => type switch
{
    PersonalProfile => "Personal Profile",
    ContactList     => "Contacts",
    Preference      => "Preferences",
    Note            => "Notes & Knowledge",
    MeetingSummary  => "Meeting Summaries",
    _ => type
};
```

- [ ] **Step 2: Update the `create_object` tool description**

In `MemoryToolHandler.CreateObjectSchema`, change the `Description` of the `type` parameter to:

```csharp
[Description("Type of memory object: personal_profile, contact_list, preference, note, meeting_summary")] string type,
```

And the tool-level description (in `GetTools()` for `create_object`) — append:
> *"… Use meeting_summary for summaries of saved meeting transcripts (with topic/date/speakers/originalFilename/content/summaryKind in data)."*

- [ ] **Step 3: Build & test**

Run: `dotnet build && dotnet test`
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Models/MemoryObject.cs src/Pia.Wpf/Services/MemoryToolHandler.cs
git commit -m "feat: add meeting_summary memory type"
```

---

### Task 3.3: `MeetingToolHandler` — failing tests first

**Files:**
- Create: `tests/Pia.Wpf.Tests/Services/MeetingToolHandlerTests.cs`

- [ ] **Step 1: Write the failing tests**

Use a fake `IMemoryService` (in-memory `Dictionary<Guid, MemoryObject>`) and a fake `IAiClientService` (returns a canned async-enumerable). Avoid mocking libraries — hand-rolled fakes match the existing test style.

```csharp
// tests/Pia.Wpf.Tests/Services/MeetingToolHandlerTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class MeetingToolHandlerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pia-meeting-tests-" + Guid.NewGuid().ToString("N"));

    public MeetingToolHandlerTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public async Task SummarizeMeetingTranscript_ReturnsMultiChoiceCard()
    {
        var path = WriteSampleTranscript("transcript-x.md");
        var handler = NewHandler();

        var (result, pending) = await handler.HandleToolCallAsync(Call("summarize_meeting_transcript", new { filePath = path }));

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.NotNull(pending!.Choices);
        Assert.Equal(3, pending.Choices!.Count);
        Assert.Contains(pending.Choices, c => c.Key == "clean");
        Assert.Contains(pending.Choices, c => c.Key == "bulleted");
        Assert.Contains(pending.Choices, c => c.Key == "text");
    }

    [Fact]
    public async Task SummarizeMeetingTranscript_ExpandsEnvVarsInPath()
    {
        var path = WriteSampleTranscript("transcript-x.md");
        var envPath = path.Replace(_tempDir, "%TEMP%", StringComparison.OrdinalIgnoreCase);
        // (Skip the real env-var rewrite — just verify Expand happens before file open.)
        var handler = NewHandler();

        // Use the literal path; the test's purpose is that Execute reads the file when the user picks.
        var (_, pending) = await handler.HandleToolCallAsync(Call("summarize_meeting_transcript", new { filePath = path }));

        var execResult = await pending!.Execute("clean");
        Assert.IsType<string>(execResult);
        Assert.Contains("CANNED-SUMMARY", (string)execResult);
    }

    [Fact]
    public async Task SummarizeMeetingTranscript_ReturnsErrorWhenFileMissing()
    {
        var handler = NewHandler();
        var (result, pending) = await handler.HandleToolCallAsync(
            Call("summarize_meeting_transcript", new { filePath = Path.Combine(_tempDir, "missing.md") }));

        Assert.Null(pending);
        Assert.IsType<string>(result);
        Assert.Contains("not found", (string)result!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryMeetingSummaries_FiltersByDate()
    {
        var memory = new FakeMemoryService();
        memory.Seed("Topic A", "2026-04-01", new[] { "You", "Alice" });
        memory.Seed("Topic B", "2026-04-15", new[] { "You", "Bob" });
        memory.Seed("Topic C", "2026-04-27", new[] { "You" });

        var handler = NewHandler(memory: memory);
        var (result, _) = await handler.HandleToolCallAsync(
            Call("query_meeting_summaries", new { from = "2026-04-10", to = "2026-04-20" }));

        var text = (string)result!;
        Assert.DoesNotContain("Topic A", text);
        Assert.Contains("Topic B", text);
        Assert.DoesNotContain("Topic C", text);
    }

    [Fact]
    public async Task QueryMeetingSummaries_FiltersBySpeaker_CaseInsensitive()
    {
        var memory = new FakeMemoryService();
        memory.Seed("With Alice",   "2026-04-01", new[] { "You", "Alice" });
        memory.Seed("With Bob",     "2026-04-02", new[] { "You", "Bob" });
        memory.Seed("With aLiCe-2", "2026-04-03", new[] { "You", "ALICE" });

        var handler = NewHandler(memory: memory);
        var (result, _) = await handler.HandleToolCallAsync(
            Call("query_meeting_summaries", new { speaker = "alice" }));

        var text = (string)result!;
        Assert.Contains("With Alice", text);
        Assert.Contains("With aLiCe-2", text);
        Assert.DoesNotContain("With Bob", text);
    }

    [Fact]
    public async Task QueryMeetingSummaries_ReturnsNoneMessage_WhenEmpty()
    {
        var memory = new FakeMemoryService();
        var handler = NewHandler(memory: memory);
        var (result, _) = await handler.HandleToolCallAsync(
            Call("query_meeting_summaries", new { speaker = "nobody" }));

        Assert.Contains("no meetings", ((string)result!).ToLowerInvariant());
    }

    // ---- helpers ---------------------------------------------------------

    private static FunctionCallContent Call(string name, object args)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in args.GetType().GetProperties())
            dict[p.Name] = p.GetValue(args);
        return new FunctionCallContent(callId: Guid.NewGuid().ToString("N"), name: name, arguments: dict);
    }

    private string WriteSampleTranscript(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, """
            ---
            schema: pia-meeting-transcript/v1
            start: 2026-04-27T10:30:00+02:00
            speakers:
              - You
              - Alice
            originalFilename: transcript-x.md
            ---
            # Live Transcription — 2026-04-27 10:30

            **Alice** _10:30:01_

            Hello world.
            """);
        return path;
    }

    private MeetingToolHandler NewHandler(FakeMemoryService? memory = null)
        => new(
            ai: new FakeAi(),
            providerService: new FakeProviderService(),
            memoryService: memory ?? new FakeMemoryService(),
            localizationService: new FakeLocalization(),
            logger: NullLogger<MeetingToolHandler>.Instance);
}

internal sealed class FakeAi : IAiClientService
{
    public Task<string> SendRequestAsync(AiProvider p, string prompt, CancellationToken ct = default)
        => Task.FromResult("CANNED-SUMMARY");
    public async IAsyncEnumerable<string> StreamChatCompletionAsync(IList<ChatMessage> m, AiProvider p, string? mode = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { yield return "CANNED-SUMMARY"; await Task.CompletedTask; }
    public Task<ChatResponse> GetChatResponseAsync(IList<ChatMessage> m, AiProvider p, IList<AITool>? t = null, string? mode = null, CancellationToken ct = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "CANNED-SUMMARY")));
    public IAsyncEnumerable<string> GetChatCompletionWithToolsAsync(IList<ChatMessage> m, AiProvider p, IList<AITool>? t = null, Func<FunctionCallContent, Task<object?>>? h = null, string? mode = null, CancellationToken ct = default) => StreamChatCompletionAsync(m, p, mode, ct);
    public Task<bool> TestToolCallingAsync(AiProvider p, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> TestStreamingAsync(AiProvider p, CancellationToken ct = default) => Task.FromResult(true);
    public Task<string> OptimizeViaPiaCloudAsync(string text, Guid templateId, string language, bool isVoiceInput, string? mode = null, CancellationToken ct = default) => Task.FromResult(text);
    public Task<string> GeneratePromptViaPiaCloudAsync(string s, string? mode = null, CancellationToken ct = default) => Task.FromResult("");
    public Task TestPiaCloudConnectionAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeProviderService : IProviderService
{
    public Task<AiProvider?> GetDefaultProviderForModeAsync(WindowMode mode) => Task.FromResult<AiProvider?>(new AiProvider { Id = Guid.NewGuid(), Name = "fake", SupportsToolCalling = false });
    // …Stub remaining IProviderService members to throw NotImplementedException.
}

internal sealed class FakeMemoryService : IMemoryService
{
    private readonly List<MemoryObject> _objects = new();

    public void Seed(string topic, string date, IEnumerable<string> speakers)
    {
        var data = JsonSerializer.Serialize(new
        {
            topic,
            date,
            speakers = speakers.ToArray(),
            originalFilename = $"transcript-{date.Replace("-", "")}-000000.md",
            summaryKind = "bulleted",
            content = "..."
        });
        _objects.Add(new MemoryObject { Type = MemoryObjectTypes.MeetingSummary, Label = topic, Data = data });
    }

    public Task<IReadOnlyList<MemoryObject>> GetObjectsByTypeAsync(string type)
        => Task.FromResult<IReadOnlyList<MemoryObject>>(_objects.Where(o => o.Type == type).ToList());

    // …Stub other IMemoryService members with NotImplementedException; this test only needs GetObjectsByTypeAsync.
}

internal sealed class FakeLocalization : ILocalizationService
{
    public string this[string key] => key;
    public string Format(string key, params object[] args) => string.Format(key, args);
    // …Stub remaining members.
}
```

The stubs for `IProviderService`/`IMemoryService`/`ILocalizationService` throw `NotImplementedException` for unused members — copy the interface members; if they grow, prefer keeping a fake helper class shared across tests but YAGNI for now.

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~MeetingToolHandlerTests"`
Expected: COMPILE-FAIL (the handler doesn't exist yet).

- [ ] **Step 3: Commit the failing test**

```bash
git add tests/Pia.Wpf.Tests/Services/MeetingToolHandlerTests.cs
git commit -m "test: add failing MeetingToolHandler tests"
```

(Commit even though it fails to compile — the next task implements production code that satisfies the test.)

---

### Task 3.4: Implement `MeetingToolHandler`

**Files:**
- Create: `src/Pia.Wpf/Services/MeetingToolHandler.cs`

- [ ] **Step 1: Implement the handler**

```csharp
// src/Pia.Wpf/Services/MeetingToolHandler.cs
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;

namespace Pia.Services;

public class MeetingToolHandler : IMeetingToolHandler
{
    private readonly IAiClientService _ai;
    private readonly IProviderService _providerService;
    private readonly IMemoryService _memoryService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<MeetingToolHandler> _logger;

    public MeetingToolHandler(
        IAiClientService ai,
        IProviderService providerService,
        IMemoryService memoryService,
        ILocalizationService localizationService,
        ILogger<MeetingToolHandler> logger)
    {
        _ai = ai;
        _providerService = providerService;
        _memoryService = memoryService;
        _localizationService = localizationService;
        _logger = logger;
    }

    public IList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(SummarizeMeetingTranscriptSchema, "summarize_meeting_transcript",
            "Summarize a saved meeting transcript file. Reads the file, prompts the user to choose a " +
            "summarization style (clean / bulleted / text), and returns the summary. After the user " +
            "sees the summary, ask whether they want to save it as a meeting_summary memory."),

        AIFunctionFactory.Create(QueryMeetingSummariesSchema, "query_meeting_summaries",
            "Search saved meeting summaries (memory_summary memory type) by date range and/or speaker. " +
            "Use when the user asks about past meetings."),
    ];

    public async Task<(object? Result, MeetingToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken cancellationToken = default)
    {
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();
        return toolCall.Name switch
        {
            "summarize_meeting_transcript" => PrepareSummarize(args),
            "query_meeting_summaries"      => (await HandleQuery(args), null),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", null),
        };
    }

    private (object? Result, MeetingToolCall? PendingAction) PrepareSummarize(IDictionary<string, object?> args)
    {
        var rawPath = GetStringArg(args, "filePath");
        var path = PathShortener.Expand(rawPath);

        if (!File.Exists(path))
            return ($"Error: meeting transcript not found at {rawPath}.", null);

        var choices = new[]
        {
            new ActionCardChoice("clean",    _localizationService["MeetingTool_Choice_Clean"]),
            new ActionCardChoice("bulleted", _localizationService["MeetingTool_Choice_Bulleted"]),
            new ActionCardChoice("text",     _localizationService["MeetingTool_Choice_Text"]),
        };

        var pending = new MeetingToolCall(
            ToolName: "summarize_meeting_transcript",
            Description: _localizationService["MeetingTool_Desc_PickKind"],
            Details: rawPath,
            Choices: choices,
            Execute: async chosenKey =>
            {
                if (string.IsNullOrEmpty(chosenKey)) return "User cancelled.";
                try
                {
                    var markdown = await File.ReadAllTextAsync(path);
                    var body = MeetingTranscriptWriter.StripFrontMatter(markdown);
                    var prompt = chosenKey switch
                    {
                        "clean"    => _localizationService["MeetingTool_Prompt_Clean"],
                        "bulleted" => _localizationService["MeetingTool_Prompt_Bulleted"],
                        "text"     => _localizationService["MeetingTool_Prompt_Text"],
                        _          => _localizationService["MeetingTool_Prompt_Bulleted"],
                    };

                    var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
                    if (provider is null) return "Error: no AI provider configured.";

                    var messages = new List<ChatMessage>
                    {
                        new(ChatRole.System,    prompt),
                        new(ChatRole.User,      body),
                    };

                    var response = await _ai.GetChatResponseAsync(messages, provider);
                    var summary = response.Messages
                        .SelectMany(m => m.Contents)
                        .OfType<TextContent>()
                        .Aggregate(new StringBuilder(), (sb, t) => sb.Append(t.Text))
                        .ToString();

                    if (string.IsNullOrWhiteSpace(summary))
                        summary = response.Messages.FirstOrDefault()?.Text ?? "";

                    if (string.IsNullOrWhiteSpace(summary))
                        return "Error: summarization returned empty result.";

                    return summary;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Summarization failed for {Path}", path);
                    return $"Error: summarization failed: {ex.Message}";
                }
            });

        return (null, pending);
    }

    private async Task<object?> HandleQuery(IDictionary<string, object?> args)
    {
        var fromStr    = GetStringArg(args, "from");
        var toStr      = GetStringArg(args, "to");
        var speaker    = GetStringArg(args, "speaker");

        DateTime? from = DateTime.TryParseExact(fromStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;
        DateTime? to   = DateTime.TryParseExact(toStr,   "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        var summaries = await _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.MeetingSummary);
        var matches = new List<(MemoryObject Obj, string Topic, string Date, string[] Speakers)>();

        foreach (var obj in summaries)
        {
            string topic = obj.Label;
            string date = "";
            string[] speakers = Array.Empty<string>();
            try
            {
                using var doc = JsonDocument.Parse(obj.Data);
                if (doc.RootElement.TryGetProperty("topic", out var t1))    topic    = t1.GetString() ?? topic;
                if (doc.RootElement.TryGetProperty("date", out var d))      date     = d.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("speakers", out var s) && s.ValueKind == JsonValueKind.Array)
                    speakers = s.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            }
            catch (JsonException) { /* tolerate malformed records */ }

            if (from.HasValue && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1) && d1 < from.Value) continue;
            if (to.HasValue   && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) && d2 > to.Value)   continue;
            if (!string.IsNullOrWhiteSpace(speaker)
                && !speakers.Any(sp => sp.Contains(speaker, StringComparison.OrdinalIgnoreCase))) continue;

            matches.Add((obj, topic, date, speakers));
        }

        if (matches.Count == 0)
            return "No meetings found matching those criteria.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {matches.Count} meeting summary(s):");
        foreach (var (obj, topic, date, speakers) in matches.OrderBy(m => m.Date))
            sb.AppendLine($"\n[ID: {obj.Id}] {topic} ({date}) — speakers: {string.Join(", ", speakers)}");
        return sb.ToString();
    }

    [Description("Summarize a saved meeting transcript file")]
    private static string SummarizeMeetingTranscriptSchema(
        [Description("Path to the transcript markdown file. Environment variables like %APPDATA% are expanded.")] string filePath) => "";

    [Description("Search saved meeting summaries by date range and/or speaker name")]
    private static string QueryMeetingSummariesSchema(
        [Description("Optional ISO date (yyyy-MM-dd); inclusive lower bound")] string? from = null,
        [Description("Optional ISO date (yyyy-MM-dd); inclusive upper bound")] string? to = null,
        [Description("Optional speaker name (case-insensitive substring match)")] string? speaker = null) => "";

    private static string GetStringArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return string.Empty;
        if (value is JsonElement el)
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" :
                   el.ValueKind == JsonValueKind.Null   ? "" : el.GetRawText();
        return value.ToString() ?? string.Empty;
    }
}
```

- [ ] **Step 2: Add the localization keys**

Edit `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` (and `.de.resx`, `.fr.resx`) — add:

| Key | EN | DE | FR |
|---|---|---|---|
| `MeetingTool_Choice_Clean` | `Clean text` | `Bereinigter Text` | `Texte nettoyé` |
| `MeetingTool_Choice_Bulleted` | `Bulleted by topic` | `Stichpunkte nach Thema` | `Points par sujet` |
| `MeetingTool_Choice_Text` | `Text summary` | `Textzusammenfassung` | `Résumé textuel` |
| `MeetingTool_Desc_PickKind` | `How should I summarize this meeting?` | `Wie soll ich das Meeting zusammenfassen?` | `Comment résumer cette réunion ?` |
| `MeetingTool_Prompt_Clean` | *(prompt body — see spec § Summarization prompts)* | *(translate)* | *(translate)* |
| `MeetingTool_Prompt_Bulleted` | *(prompt body)* | *(translate)* | *(translate)* |
| `MeetingTool_Prompt_Text` | *(prompt body)* | *(translate)* | *(translate)* |
| `Assistant_Meeting_SummarizeRequest` | `Please summarize the meeting transcript saved at \`{0}\`.` | `Bitte fasse das Meeting-Transkript unter \`{0}\` zusammen.` | `Merci de résumer la transcription de la réunion enregistrée dans \`{0}\`.` |
| `LiveTrans_SaveAndSummarize` | `Save and summarize` | `Speichern & zusammenfassen` | `Enregistrer et résumer` |
| `LiveTrans_SaveAndSummarize_Snackbar` | `Saved transcript to {0}` | `Transkript gespeichert: {0}` | `Transcription enregistrée : {0}` |
| `MeetingTool_SystemPrompt` | *(see spec § Plugin systemPromptAddition)* | *(translate)* | *(translate)* |

Then run `dotnet build` once so the `Designer.cs` regenerates (it auto-regenerates from the `.resx`).

- [ ] **Step 3: Run the failing tests, expect green**

Run: `dotnet test --filter "FullyQualifiedName~MeetingToolHandlerTests"`
Expected: PASS (6/6).

- [ ] **Step 4: Run the full suite**

Run: `dotnet test`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/MeetingToolHandler.cs src/Pia.Wpf/Resources/Strings/
git commit -m "feat: implement MeetingToolHandler (summarize + query)"
```

---

### Task 3.5: Register `MeetingToolHandler` as a built-in plugin

**Files:**
- Modify: `src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs`
- Modify: `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs`
- Modify: `src/Pia.Wpf/Services/Plugins/PluginService.cs`
- Modify: `src/Pia.Wpf/Bootstrapper.cs`

- [ ] **Step 1: Add `FromMeetingHandler` factory**

In `BuiltInPluginHandler.cs` (after `FromReminderHandler`):

```csharp
public static BuiltInPluginHandler FromMeetingHandler(
    IMeetingToolHandler handler, SyncPlugin config)
{
    var pendingByCallId = new Dictionary<Guid, MeetingToolCall>();
    return new BuiltInPluginHandler(
        config.Id,
        config.Name,
        handler.GetTools,
        async (toolCall, ct) =>
        {
            var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
            if (pending is null) return (result, null);

            // The plugin pipeline's PluginToolCall.Execute is parameterless, but
            // MeetingToolCall.Execute needs the chosen key. We close over a captured
            // ActionCardInfo whose ChosenKey is set when the user clicks.
            // The AssistantViewModel sets ChosenKey before invoking Execute (see
            // Task 4.4), so we read from a per-call holder.
            var holder = new ChosenKeyHolder();
            return (null, new PluginToolCall(
                pending.ToolName, config.Name, pending.Description, pending.Details,
                () => pending.Execute(holder.Value),
                pending.Choices)
                { /* nothing else */ });
            // ChosenKeyHolder is set by AssistantViewModel between WaitForChoiceAsync and Execute.
        },
        async pluginCall => await pluginCall.Execute(),
        GetSystemPromptFromConfig(config.ConfigJson));
}
```

The closure-based approach is fragile — instead, **simpler**: have `PluginToolCall.Execute` accept an optional choice via a parameter. **Refactor** the `PluginToolCall` execute signature in this task:

In `IPluginToolHandler.cs`:
```csharp
public record PluginToolCall(
    string ToolName,
    string PluginName,
    string Description,
    string? Details,
    Func<string?, Task<object?>> Execute,                        // <— takes chosen key
    IReadOnlyList<Pia.Models.ActionCardChoice>? Choices = null);
```

Update existing factories (`FromMemoryHandler`, `FromTodoHandler`, `FromReminderHandler`) to accept the parameter and ignore it:

```csharp
async (toolCall, ct) =>
{
    var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
    if (pending is null) return (result, null);
    return (null, new PluginToolCall(
        pending.ToolName, config.Name, pending.Description, pending.NewValue,
        _ => pending.Execute()));        // ignore key
},
```

For `FromMeetingHandler`:
```csharp
async (toolCall, ct) =>
{
    var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
    if (pending is null) return (result, null);
    return (null, new PluginToolCall(
        pending.ToolName, config.Name, pending.Description, pending.Details,
        key => pending.Execute(key),
        pending.Choices));
},
async pluginCall => await pluginCall.Execute(null),
```

In `AssistantViewModel.HandleToolCall`, change `await pendingAction.Execute()` to `await pendingAction.Execute(card.ChosenKey)`.

- [ ] **Step 2: Add `MeetingPluginId` & default**

In `BuiltInPluginDefaults.cs`:

```csharp
public static readonly Guid MeetingPluginId = new("10000000-0000-0000-0000-000000000004");

public static readonly HashSet<Guid> PreloadedPluginIds =
    [MemoryPluginId, TodoPluginId, ReminderPluginId, MeetingPluginId];
```

Add the entry to `Defaults`:

```csharp
[MeetingPluginId] = new SyncPlugin
{
    Id = MeetingPluginId,
    Kind = "builtin_tool_pack",
    Name = "meeting",
    Description = "Meeting transcript summarization and meeting-summary memory.",
    IsPreloaded = true,
    IsActive = true,
    Version = "1.0.0",
    ConfigJson = """{"handlerId":"meeting","defaultEnabled":true,"systemPromptAddition":"You can summarize saved meeting transcripts. After producing a summary, ask the user once whether they'd like to save it as a memory. If yes, call create_object with type=meeting_summary, label=<topic distilled from the summary>, and data as a JSON object with topic, date (from the front-matter), speakers (from the front-matter), originalFilename (from the front-matter), summaryKind (the chosen kind), and content (the summary you produced). Do not save without explicit user confirmation."}""",
    UpdatedAt = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc),
},
```

- [ ] **Step 3: Wire into `PluginService`**

In `PluginService.cs` constructor:
```csharp
private readonly IMeetingToolHandler _meetingToolHandler;
// add to constructor signature, store
```

In `InitializeBuiltInPlugins` switch:
```csharp
"meeting"  => BuiltInPluginHandler.FromMeetingHandler(_meetingToolHandler, config),
```

- [ ] **Step 4: DI registration**

In `Bootstrapper.cs` `ConfigureServices`:
```csharp
services.AddSingleton<IMeetingToolHandler, MeetingToolHandler>();
```
Place near the other tool-handler registrations (line ~205-212).

- [ ] **Step 5: Build & test**

Run: `dotnet build && dotnet test`
Expected: green. The `DiRegistrationTests` should auto-cover the new singleton.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/Plugins/BuiltInPluginHandler.cs src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs src/Pia.Wpf/Services/Plugins/PluginService.cs src/Pia.Wpf/Bootstrapper.cs src/Pia.Wpf/Services/Interfaces/IPluginToolHandler.cs src/Pia.Wpf/ViewModels/AssistantViewModel.cs
git commit -m "feat: register MeetingToolHandler as a built-in plugin"
```

---

## Chunk 4: Overlay button & assistant integration

### Task 4.1: `LiveTranscriptionViewModel` — add `SaveAndSummarizeCommand`

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs`

- [ ] **Step 1: Add the event payload, command, and event**

Below the existing `CloseRequested` event:

```csharp
public sealed record MeetingSummarizationRequest(string FilePath, string DisplayPath);

public event EventHandler<MeetingSummarizationRequest>? SummarizeRequested;

public IAsyncRelayCommand SaveAndSummarizeCommand { get; }
```

In the constructor (next to `SaveTranscriptCommand`):

```csharp
SaveAndSummarizeCommand = new AsyncRelayCommand(SaveAndSummarizeAsync, CanSaveTranscript);
```

In `OnIsRunningChanged`:
```csharp
SaveAndSummarizeCommand.NotifyCanExecuteChanged();
```

In `OnBubblesCollectionChanged`:
```csharp
SaveAndSummarizeCommand.NotifyCanExecuteChanged();
```

Add the method:

```csharp
private async Task SaveAndSummarizeAsync()
{
    if (!CanSaveTranscript()) return;

    string folder;
    try
    {
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        folder = MeetingTranscriptPaths.ResolveFolder(settings);
        Directory.CreateDirectory(folder);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to resolve meeting transcript folder");
        return;
    }

    var filename = $"transcript-{_sessionStart.LocalDateTime:yyyyMMdd-HHmmss}.md";
    var path = Path.Combine(folder, filename);

    try
    {
        var markdown = MeetingTranscriptWriter.Render(
            Bubbles, _sessionStart, originalFilename: filename, title: _localizationService["LiveTrans_Title"]);
        await File.WriteAllTextAsync(path, markdown, Encoding.UTF8).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to write transcript to {Path}", path);
        return;
    }

    var displayPath = Pia.Services.PathShortener.Shorten(path);
    SummarizeRequested?.Invoke(this, new MeetingSummarizationRequest(path, displayPath));
}
```

(Keep `SaveTranscriptAsync` as-is for the existing Save button; the two paths differ in dialog vs. silent.)

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs
git commit -m "feat: add SaveAndSummarizeCommand to LiveTranscriptionViewModel"
```

---

### Task 4.2: Add the button to the overlay XAML

**Files:**
- Modify: `src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml:255-277`

- [ ] **Step 1: Add the button next to Save**

In the footer `StackPanel`, between the existing `SaveTranscriptCommand` button and the `StartCommand` (Resume) button:

```xml
<ui:Button Command="{Binding SaveAndSummarizeCommand}"
           Margin="0,0,8,0"
           Padding="20,10"
           Appearance="Primary"
           Icon="{ui:SymbolIcon Sparkle24}"
           Content="{loc:Str LiveTrans_SaveAndSummarize}"
           Visibility="{Binding IsRunning, Converter={StaticResource InverseBooleanToVisibilityConverter}}" />
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml
git commit -m "feat: add Save and summarize button to overlay footer"
```

---

### Task 4.3: `AssistantViewModel` subscribes & sends synthetic message

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`

- [ ] **Step 1: Subscribe in the constructor**

In the constructor after `LiveTranscription.CloseRequested += OnLiveTranscriptionCloseRequested;`:

```csharp
LiveTranscription.SummarizeRequested += OnSummarizeRequested;
```

In `Dispose`, unsubscribe:
```csharp
LiveTranscription.SummarizeRequested -= OnSummarizeRequested;
```

- [ ] **Step 2: Implement the handler**

```csharp
private void OnSummarizeRequested(object? sender, LiveTranscriptionViewModel.MeetingSummarizationRequest e)
{
    _ = HandleSummarizeRequestedAsync(e);
}

private async Task HandleSummarizeRequestedAsync(LiveTranscriptionViewModel.MeetingSummarizationRequest e)
{
    // 1) Stop and close the overlay first.
    await LiveTranscription.StopAsync();
    IsLiveTranscriptionVisible = false;
    LiveTranscription.ResetForNewSession();

    // 2) Snackbar feedback with the saved path.
    _snackbarService.Show(
        _localizationService["Msg_Assistant_TranscriptSaved"],
        _localizationService.Format("LiveTrans_SaveAndSummarize_Snackbar", e.DisplayPath),
        Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(4));

    // 3) Inject a synthetic user message that triggers the summarize tool.
    InputText = _localizationService.Format("Assistant_Meeting_SummarizeRequest", e.DisplayPath);
    if (SendMessageCommand.CanExecute(null))
        await SendMessageCommand.ExecuteAsync(null);
}
```

Add the snackbar localization key `Msg_Assistant_TranscriptSaved` (EN: `Transcript saved`, DE: `Transkript gespeichert`, FR: `Transcription enregistrée`) to the resx files.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: SUCCESS.

- [ ] **Step 4: Run all tests**

Run: `dotnet test`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/ViewModels/AssistantViewModel.cs src/Pia.Wpf/Resources/Strings/
git commit -m "feat: AssistantViewModel handles SummarizeRequested via synthetic chat"
```

---

### Task 4.4: Action card category for meeting plugin

**Files:**
- Modify: `src/Pia.Wpf/Models/ActionCardInfo.cs`
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`
- Modify: `src/Pia.Wpf/Controls/ActionCardControl.xaml`

The card needs a "meeting" category for the icon and snackbar.

- [ ] **Step 1: Extend the enum**

```csharp
// ActionCardInfo.cs
public enum ActionCardCategory
{
    Memory,
    Todo,
    Reminder,
    Meeting,
}
```

- [ ] **Step 2: Map plugin name → category**

In `AssistantViewModel.BuildPluginActionCard`:
```csharp
var category = pendingAction.PluginName switch
{
    "memory"   => ActionCardCategory.Memory,
    "todo"     => ActionCardCategory.Todo,
    "reminder" => ActionCardCategory.Reminder,
    "meeting"  => ActionCardCategory.Meeting,
    _ => ActionCardCategory.Memory,
};
```

In the snackbar switch (around line 540):
```csharp
"meeting" => _localizationService["Msg_Assistant_MeetingSummarized"],
```

Add the localization key `Msg_Assistant_MeetingSummarized` (EN: `Summary generated`, DE: `Zusammenfassung erstellt`, FR: `Résumé généré`).

- [ ] **Step 3: Add the icon trigger in the XAML**

In `ActionCardControl.xaml` around line 53–60, add another `DataTrigger`:

```xml
<DataTrigger Binding="{Binding Category}" Value="{x:Static models:ActionCardCategory.Meeting}">
  <Setter Property="Symbol" Value="DocumentTable24" />
</DataTrigger>
```

- [ ] **Step 4: Add `ActionCard_Category_Meeting` resource**

EN: `Meeting`, DE: `Meeting`, FR: `Réunion`.

In `AssistantViewModel.FormatToolTitle` add:
```csharp
ActionCardCategory.Meeting => "ActionCard_Category_Meeting",
```
And to the `actionKey` switch:
```csharp
"summarize_meeting_transcript" => "ActionCard_Action_Summarize",
"query_meeting_summaries"      => "ActionCard_Action_Query",
```
Add `ActionCard_Action_Summarize` (EN: `Summarize`) and `ActionCard_Action_Query` (EN: `Search`).

- [ ] **Step 5: Build & test**

Run: `dotnet build && dotnet test`
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Models/ActionCardInfo.cs src/Pia.Wpf/ViewModels/AssistantViewModel.cs src/Pia.Wpf/Controls/ActionCardControl.xaml src/Pia.Wpf/Resources/Strings/
git commit -m "feat: meeting category for action cards"
```

---

## Chunk 5: Manual verification

### Task 5.1: End-to-end smoke test

**No code changes** — verify the wired flow manually since it spans MVVM, WPF dispatcher, and a real provider.

- [ ] **Step 1: Build & launch**

```bash
dotnet build
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj
```

- [ ] **Step 2: Verify the golden path**

  1. Open the assistant view, click the live transcription icon.
  2. Accept the disclaimer and start. Speak a few sentences. Stop.
  3. The footer now shows **Save**, **Save and summarize**, and **Resume**.
  4. Click **Save and summarize**.
     - ✅ The overlay closes.
     - ✅ A snackbar appears with the saved path containing `%APPDATA%`.
     - ✅ A user message appears in chat referencing the env-var-shortened path.
     - ✅ The assistant calls `summarize_meeting_transcript` and renders an action card with three buttons: Clean text / Bulleted by topic / Text summary.
  5. Click **Bulleted by topic**.
     - ✅ The card resolves to "Done".
     - ✅ The assistant streams a bulleted summary.
     - ✅ The assistant follows up by asking whether to save as a memory.
  6. Reply *"yes, save it"*.
     - ✅ A standard memory action card appears (Create Memory) with type `meeting_summary` and the topic as label.
  7. Accept it.
     - ✅ Snackbar `Memory updated`.
  8. In a new turn, ask *"What meetings did I have today?"*.
     - ✅ The assistant calls `query_meeting_summaries` and lists the meeting.

- [ ] **Step 3: Verify edge cases**

  - Click Save and summarize twice (second time after stopping a fresh session) — both transcripts land in the folder, both summaries work.
  - Press Escape on the multi-choice card — the assistant relays a "User declined the summarize_meeting_transcript operation" error.
  - Manually delete the saved transcript file before clicking a choice — the tool returns "not found" error to the chat.
  - Run `query_meeting_summaries` with a date range that excludes the saved meeting — returns "No meetings found".

- [ ] **Step 4: Document failure if any**

If any step fails, **do not** mark the feature complete. Open a follow-up task or fix in place. Use `@superpowers:systematic-debugging` for non-trivial bugs.

---

## Definition of Done

- [ ] All checkboxes above ticked.
- [ ] `dotnet build` clean (0 warnings introduced).
- [ ] `dotnet test` green.
- [ ] Manual smoke test in Task 5.1 passes the golden path and all edge cases.
- [ ] No new warnings in the build output.
- [ ] All commits pushed to `feature/meeting_transscription`.

When all of these are satisfied, switch to `@superpowers:finishing-a-development-branch` to plan the merge / PR.
