# Sentence Splitting and Chunked Read-Aloud — Implementation Plan

**Status:** Ready for execution
**Owner:** Marco Altmann
**Written:** 2026-11-25
**Origin:** GitHub issue #69 ("Local speech stack: eight improvements"), item **3. Sentence splitting
and read-aloud**. Only item 3; the other seven stay open on #69.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Voice mode stops pausing in the middle of German ordinals and abbreviations, and Read aloud
starts after the first sentence instead of after the whole message, without ever speaking markup.

**Architecture:** `SentenceChunker` gets a boundary rule set (abbreviations, short ordinals, line
breaks, wait-for-lookahead, a length cap) behind its existing `AddToken`/`Flush` API, so voice mode
picks it up unchanged. A new pure helper `SpeakableText.FromMarkdown` walks the Markdig AST into one
speakable line per block. `TtsService.SpeakAsync` runs the message through both and plays it on the
existing chunked producer/consumer path. A small `PlaybackSlot` owns the playback
`CancellationTokenSource`, so a second Read aloud no longer disposes the first one's token or leaves
Stop with nothing to cancel.

**Tech Stack:** .NET 10 WPF, Markdig 1.3.2 (already referenced), sherpa-onnx `OfflineTts`, xunit v3,
NSubstitute.

**Spec:** issue #69, section "3. Sentence splitting and read-aloud". Its done-when: "`SentenceChunker`
tests cover the DE/EN/FR abbreviation and ordinal cases, and reading a long markdown answer starts
within one sentence's synthesis time without ever speaking markup."

## Why this item

It is the first entry in #69's suggested order whose files nothing in flight touches. On
2026-11-25, `feature/nemo_stt` changes `TranscriptionService`, `TranscriptionEngineFactory`,
`LiveTranscriptionModels` and the runtime-asset catalogue (items 2, 4, 5, 6.1).
`feature/mxc-sandboxed-mcp` changes the catalogue too (item 4). Item 8 needs a hardware benchmark.
No branch touches `SentenceChunker.cs` or `TtsService.cs`.

## Global Constraints

- Branch `feature/read-aloud-chunking` from `develop` through the `release-flow` skill. Its own PR
  into `develop`, merged with **Create a merge commit**.
- Zero warnings: `dotnet build -t:Rebuild -v:n` and `dotnet build -t:Rebuild -v:n -c Release` both
  end with `0 Warning(s)` and `0 Error(s)`.
- Tests run from the built exe, output redirected and never piped. One filter kind per run:
  `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe -class "<FQN>" > test.log 2>&1`,
  then `grep -E "TEST EXECUTION SUMMARY" -A 2 test.log`. Never `dotnet test`.
- Final gate: the unfiltered exe reports `Failed: 0`.
- Privacy: the text being spoken is user content. Log only lengths, counts and durations, never the
  text. A per-sentence trace goes through `SensitiveDebug`.
- Comments: default none, one short line when the WHY is hidden. No history, no issue numbers in code.
- No new strings. Code blocks and tables are **skipped silently**, not announced: the voice language
  can differ from the UI language, so an announcement string would often be in the wrong language.
- Voice mode's markdown is out of scope. Voice mode gets the new chunker rules through
  `AddToken`/`Flush` and nothing else.

## Review Focus

1. **Read aloud on message B while message A is playing, then Stop.** Today the first call's
   `finally` disposes and nulls the field that the second call owns, so Stop cancels nothing and
   `IsPlaying` flips to false while B plays. Expected: B plays, Stop silences B, and `IsPlaying` is
   false only after B ends. Pinned in Task 3 through `PlaybackSlotTests`.
2. **A token that ends exactly on a dot while streaming** (`"Pi is 3."` then `"14. Next"`). Today
   punctuation at the end of the buffer counts as a boundary, so this splits as "Pi is 3." and
   "14.". Expected: no split until the next character arrives. Pinned in Task 1.
3. **A message that is only a code block or a table.** Expected: nothing is spoken, no voice is
   loaded, and `IsPlaying` never turns true. Pinned in Task 3.
4. **A 600-character sentence with no punctuation** (pasted log line, long German compound list).
   Expected: chunks of at most 300 characters, cut at a clause mark or a space, never mid-word unless
   there is no space at all. Pinned in Task 1.
5. **A sentence that ends in a year** ("Das war 2023. Dann kam 2024."). The ordinal rule must not
   swallow it. Expected: two sentences. Pinned in Task 1.

---

### Task 1: Boundary rules in `SentenceChunker`

**Files:**
- Modify: `src/Pia.Wpf/Helpers/SentenceChunker.cs` (whole file, 64 lines)
- Create: `tests/Pia.Wpf.Tests/Helpers/SentenceChunkerTests.cs` (namespace `Pia.Tests.Helpers`)

**Interfaces:**
- Produces: `IEnumerable<string> AddToken(string token)` and `string? Flush()`, both unchanged.
  New: `public const int MaxChunkLength = 300;` and
  `public static IReadOnlyList<string> Split(string text)`, which runs one `AddToken(text)` plus
  `Flush()`.

**Boundary rules.** These are the decisions; the implementer writes the scanner. Enders are `.`, `!`,
`?` and `…`.

1. An ender is a boundary only when whitespace follows it. If the ender is the **last character of the
   buffer**, it is not a boundary yet: wait for the next token. `Flush` emits what is left.
2. `.` is not a boundary when the token before it is one of the following. The token is the run of
   non-whitespace characters, with leading opening punctuation (`(` `[` `"` `'` `„` `“` `«`)
   stripped, so `(z.` and `„Dr.` match.
   - 1–2 digits only (`3.`, `24.`): ordinals and list numbers. Three or more digits (`2023.`) still
     end a sentence.
   - A single letter (`z.` `B.` `d.` `h.` `M.` `J.`). This covers spaced abbreviations and initials.
   - In this list, compared case-insensitively: `e.g`, `i.e`, `etc`, `mr`, `mrs`, `ms`, `dr`, `prof`,
     `vs`, `nr`, `bzw`, `ca`, `usw`, `vgl`, `ggf`, `evtl`, `inkl`, `zzgl`, `sog`, `z.b`, `d.h`, `u.a`,
     `mme`, `mlle`, `st`, `jr`, `sr`, `fig`, `str`, `tel`, `ex`.
   - English `no` is deliberately not in the list: "No." is a common whole answer in voice mode.
3. A `\n` ends the chunk when the text before it is not blank. This covers paragraphs, headings and
   list items.
4. When the buffered text reaches `MaxChunkLength` with no boundary, cut within the first
   `MaxChunkLength` characters at the last clause mark (`;` `:` `,` `—` `–` followed by whitespace).
   With none, cut at the last whitespace. With no whitespace either, cut hard at `MaxChunkLength`.

- [ ] **Step 1: Write the failing tests** in `SentenceChunkerTests`. Each test feeds whole strings
  through `Split` unless it says "streamed":

```csharp
[Theory]
[InlineData("Wir treffen uns am 3. Oktober in Berlin.", new[] { "Wir treffen uns am 3. Oktober in Berlin." })]
[InlineData("Das gilt z. B. für Dr. Müller und Nr. 5.", new[] { "Das gilt z. B. für Dr. Müller und Nr. 5." })]
[InlineData("Use a tool, e.g. grep. Then stop.", new[] { "Use a tool, e.g. grep.", "Then stop." })]
[InlineData("Voir M. Dupont, p. ex. demain. Merci.", new[] { "Voir M. Dupont, p. ex. demain.", "Merci." })]
[InlineData("Das war 2023. Dann kam 2024.", new[] { "Das war 2023.", "Dann kam 2024." })]
[InlineData("No. I won't.", new[] { "No.", "I won't." })]
[InlineData("Wait… What?", new[] { "Wait…", "What?" })]
[InlineData("Nimm ein Werkzeug (z. B. grep). Fertig.", new[] { "Nimm ein Werkzeug (z. B. grep).", "Fertig." })]
public void Split_KeepsAbbreviationsAndOrdinalsInsideTheSentence(string text, string[] expected)
    => Assert.Equal(expected, SentenceChunker.Split(text));

[Fact]
public void Split_ALineBreakEndsAChunk()
    => Assert.Equal(["Einkaufsliste", "Milch", "Brot"], SentenceChunker.Split("Einkaufsliste\nMilch\n\nBrot"));

[Fact]
public void AddToken_Streamed_ADotAtTheEndOfTheBufferWaitsForTheNextToken()
{
    var chunker = new SentenceChunker();
    Assert.Empty(chunker.AddToken("Pi is 3."));
    Assert.Equal(["Pi is 3.14."], chunker.AddToken("14. Next"));
    Assert.Equal("Next", chunker.Flush());
}

[Fact]
public void Split_ALongRunWithoutPunctuation_IsCutAtAClauseMarkWithinTheCap()
{
    var text = string.Join(", ", Enumerable.Repeat("Apfel Birne Kirsche", 40)); // ~800 chars
    var chunks = SentenceChunker.Split(text);
    Assert.All(chunks, c => Assert.True(c.Length <= SentenceChunker.MaxChunkLength));
    Assert.All(chunks.SkipLast(1), c => Assert.EndsWith(",", c));
    Assert.Equal(text.Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
}

[Fact]
public void Split_ALongRunWithoutAnySpace_IsCutHardAtTheCap()
    => Assert.Equal(SentenceChunker.MaxChunkLength, SentenceChunker.Split(new string('x', 700))[0].Length);
```

- [ ] **Step 2: Build and run.** Use
  `dotnet build tests/Pia.Wpf.Tests` and then the exe with `-class "Pia.Tests.Helpers.SentenceChunkerTests"`.
  Expected: FAIL. `Split` and `MaxChunkLength` do not exist yet.
- [ ] **Step 3: Implement the four rules and `Split`** in `SentenceChunker.cs`. Bring the existing
  `<summary>` comments into line: one line each, or none.
- [ ] **Step 4: Run the same filter.** Expected: all pass. Voice mode has no unit tests. Its only
  coupling is `AddToken`/`Flush` in `VoiceModeViewModel.TransitionToSpeakingAsync` (around line 298),
  whose signatures do not change.
- [ ] **Step 5: Commit.**
  `git commit -m "fix: keep ordinals and abbreviations inside one spoken sentence"`

### Task 2: `SpeakableText.FromMarkdown`

**Files:**
- Create: `src/Pia.Wpf/Helpers/SpeakableText.cs`
- Create: `tests/Pia.Wpf.Tests/Helpers/SpeakableTextTests.cs`

**Interfaces:**
- Produces: `public static class SpeakableText { public static string FromMarkdown(string markdown); }`.
  It returns one line per spoken block, joined with `\n`. It returns an empty string when nothing is
  speakable.

**Decisions:**
- **Parsing.** Use `Markdown.Parse(markdown, pipeline)` with a pipeline built once:
  `new MarkdownPipelineBuilder().UsePipeTables().UseGridTables().UseEmphasisExtras().UseTaskLists().UseAutoLinks().Build()`.
  Tables have to parse as tables, or they come out as pipe-filled text.
- **Kept as text, one line each:** paragraphs, headings, list items (the marker dropped), and
  blockquote contents.
- **Inlines:** `LiteralInline` text is kept. `CodeInline` keeps its content. `EmphasisInline` keeps
  its children, so the markers go. `LinkInline` keeps its label. Autolinks and bare URLs are dropped.
  `LineBreakInline` becomes a space.
- **Dropped whole:** fenced and indented code blocks, tables, HTML blocks and inlines, images,
  thematic breaks, and link reference definitions.
- **Emoji are removed:** runes in category `OtherSymbol` or `ModifierSymbol` that are at `U+1F000` or
  above, or in `U+2600`–`U+27BF`, plus `U+FE0F` and `U+200D`. `°`, `©` and `€` stay.
- Every line is trimmed, and a line left blank is dropped.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public void FromMarkdown_DropsEmphasisAndHeadingMarkers()
    => Assert.Equal("Ergebnis\nDas ist wichtig und klar.", SpeakableText.FromMarkdown("## Ergebnis\n\nDas ist **wichtig** und _klar_."));

[Fact] public void FromMarkdown_ListItemsBecomeLines_WithoutMarkers()
    => Assert.Equal("Milch\nBrot", SpeakableText.FromMarkdown("- Milch\n- Brot"));

[Fact] public void FromMarkdown_LinksKeepTheirLabel_BareUrlsGo()
    => Assert.Equal("Siehe die Doku.\nOder", SpeakableText.FromMarkdown("Siehe [die Doku](https://x.invalid).\n\nOder https://x.invalid"));

[Fact] public void FromMarkdown_SkipsCodeBlocksAndTables_KeepsInlineCode()
    => Assert.Equal("Run npm install first.\nDone.",
        SpeakableText.FromMarkdown("Run `npm install` first.\n\n```bash\nnpm install\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\nDone."));

[Fact] public void FromMarkdown_RemovesEmoji_KeepsDegreesAndCurrency()
    => Assert.Equal("Es sind 20 °C und 5 €", SpeakableText.FromMarkdown("Es sind 20 °C und 5 € 🙂"));

[Fact] public void FromMarkdown_OnlyCode_IsEmpty()
    => Assert.Equal("", SpeakableText.FromMarkdown("```\nvar x = 1;\n```"));
```

- [ ] **Step 2: Run** `-class "Pia.Tests.Helpers.SpeakableTextTests"`. Expected: FAIL, because the
  type does not exist.
- [ ] **Step 3: Implement `FromMarkdown`** as an AST walk over `MarkdownDocument`. Do not
  regex-strip the raw text.
- [ ] **Step 4: Run the same filter.** Expected: all pass.
- [ ] **Step 5: Commit.** `git commit -m "feat: turn a markdown answer into speakable lines"`

### Task 3: Read aloud through the chunked path, with an owned playback token

**Files:**
- Create: `src/Pia.Wpf/Services/Tts/PlaybackSlot.cs` (namespace `Pia.Services.Tts`, next to
  `TtsVoiceCatalog` and `WavWriter`; `internal` is visible to the tests through `InternalsVisibleTo`)
- Modify: `src/Pia.Wpf/Services/TtsService.cs`. Touch `SpeakAsync` (223–267), `SpeakChunkedAsync`
  (269–349), `Stop` (539–544) and the `_playbackCts` field (120).
- Create: `tests/Pia.Wpf.Tests/Services/Tts/PlaybackSlotTests.cs` and
  `tests/Pia.Wpf.Tests/Services/Tts/TtsReadAloudTests.cs`.

**Interfaces:**
- Consumes: `SentenceChunker.Split(string)` (Task 1) and `SpeakableText.FromMarkdown(string)` (Task 2).
- Produces:

```csharp
internal sealed class PlaybackSlot
{
    public CancellationTokenSource Begin(CancellationToken linkedTo); // cancels the previous owner's source
    public bool End(CancellationTokenSource mine);                     // disposes mine; true only if mine was still current
    public void Cancel();                                              // cancels the current owner, if any
}
```

  `ITtsService` is unchanged.

**Decisions:**
- `SpeakAsync(text, ct)` does the following, in order:
  1. Compute `SentenceChunker.Split(SpeakableText.FromMarkdown(text))`. If it is empty, return.
     **This happens before any voice loading.**
  2. Run the existing `_isReady`/`InitializeAsync` path, then the `_tts is null` warning.
  3. Call `Stop()` to silence the previous playback and the filler.
  4. Run the same core as `SpeakChunkedAsync` over the list.
- Feed the list to the core with `sentences.ToAsyncEnumerable()`. It ships in-box on net10.0
  (`System.Linq.AsyncEnumerable.dll`), so add no package. Do not write an async iterator without an
  `await`: it risks CS1998, which the zero-warning gate rejects.
- `PlaybackSlot`'s three methods share one `lock`. `Stop()` runs on the UI thread while `End` runs on
  the playback worker, and cancelling a source that is already disposed throws.
- `SpeakChunkedAsync` and `SpeakAsync` take their source from `_playback.Begin(ct)`. In `finally` they
  set `IsPlaying = false` **only when** `_playback.End(mine)` returns true. `Stop()` calls
  `_playback.Cancel()`.
- Add one `LogInformation` per read-aloud with the sentence count, the character count, and the
  milliseconds until the first chunk starts playing. It never logs the text. This line is what the
  in-app check in Task 4 reads.

- [ ] **Step 1: Write the failing tests.**
  In `PlaybackSlotTests`:

```csharp
[Fact] public void Begin_CancelsThePreviousOwner()
{ var slot = new PlaybackSlot(); var a = slot.Begin(default); var b = slot.Begin(default);
  Assert.True(a.IsCancellationRequested); Assert.False(b.IsCancellationRequested); }

[Fact] public void End_OfASupersededOwner_LeavesTheCurrentOneCancellable()
{ var slot = new PlaybackSlot(); var a = slot.Begin(default); var b = slot.Begin(default);
  Assert.False(slot.End(a)); slot.Cancel(); Assert.True(b.IsCancellationRequested); }

[Fact] public void End_OfTheCurrentOwner_ReturnsTrue_AndCancelAfterwardsIsANoOp()
{ var slot = new PlaybackSlot(); var a = slot.Begin(default); Assert.True(slot.End(a)); slot.Cancel(); }
```

  In `TtsReadAloudTests` (namespace `Pia.Tests.Services.Tts`), construct the service the way
  `TtsVoiceDeletionTests` does:
  `new TtsService(NullLogger<TtsService>.Instance, settings, Substitute.For<IAssetDownloader>())`,
  with `settings = Substitute.For<ISettingsService>()`. The constructor only stores the service and
  never reads settings, so any `GetSettingsAsync` call comes from `InitializeAsync`.

```csharp
[Theory]
[InlineData("```\nvar x = 1;\n```")]
[InlineData("| a | b |\n|---|---|\n| 1 | 2 |")]
[InlineData("   ")]
public async Task SpeakAsync_WithNothingSpeakable_ReturnsBeforeLoadingAVoice(string markdown)
{
    await service.SpeakAsync(markdown, TestContext.Current.CancellationToken);
    await settings.DidNotReceive().GetSettingsAsync();
    Assert.False(service.IsPlaying);
}
```

- [ ] **Step 2: Run each class on its own** with `-class`. Expected: FAIL. `PlaybackSlot` does not
  exist yet, and today `SpeakAsync("```…```")` calls `InitializeAsync` because the text is not blank.
- [ ] **Step 3: Implement `PlaybackSlot` and rewire `TtsService`** as described above. Bring the
  comments in the three touched methods into line, for example by deleting "// Expected when Stop() is
  called" if the catch makes it obvious.
- [ ] **Step 4: Run both classes, then** `-namespace "Pia.Tests.Services.Tts"`. Expected: all pass,
  `TtsVoiceDeletionTests` and `LegacyPiperTreeRemovalTests` included.
- [ ] **Step 5: Commit.** `git commit -m "fix: read an answer aloud sentence by sentence, without markup"`

### Task 4: Gate and in-app check

- [ ] **Step 1:** `dotnet build -t:Rebuild -v:n > build-debug.log 2>&1` and
  `dotnet build -t:Rebuild -v:n -c Release > build-release.log 2>&1`. Read the `N Warning(s)` summary
  lines. Expected: `0 Warning(s)` and `0 Error(s)` in both.
- [ ] **Step 2:** Run the unfiltered exe into `test.log`. Expected: `Failed: 0`.
- [ ] **Step 3 (owner, by ear):** use a throwaway profile (`docs/ui_automation/winwright-setup.md`)
  with a German voice downloaded. **Do not delete models from it**, see the playbook trap. Ask for a
  long markdown answer with a heading, a list, a code block and a table, and press Read aloud.
  Expected: speech starts after about one sentence, the log's "first chunk" line shows a delay of
  roughly one sentence's synthesis time, and no markup or code is spoken. Then press Read aloud on a
  second message mid-playback, then Stop. Expected: silence.
- [ ] **Step 4:** Tick nothing on #69 itself. Comment on #69 that item 3 is done, linking the PR.
