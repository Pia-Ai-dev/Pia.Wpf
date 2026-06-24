# Migration Plan — Per-Speaker Voice Detection → Labeled Colored Chat Bubbles (attendee branch)

Target branch: `feature/meeting_attendee`
Source of salvaged code: branch **`origin/feature/meeting_transscription`** — specifically commits `dc3619f` ("latest") + `966deab` ("Keep current merge"), which carry the diarizer (`ISpeakerIdentificationService` + `SpeakerIdentificationService`), the rename command (`LiveTranscriptionViewModel`), the affordance XAML (`LiveTranscriptionOverlay.xaml`), and `SpeakerIdentificationServiceTests`. The original POC worktree `C:\projects\pia_meeting\src\Pia.Wpf` lives only on another machine and was never committed there; this state was pushed to the remote on 2026-06-24.

> **Salvage-source durability — port BEFORE this branch is pruned.** The salvaged files are reachable **only** via `feature/meeting_transscription` (verified: zero hits across every other ref, the reflog, and the stash; they were never committed elsewhere). Those commits become GC-eligible once the branch is pruned. **Pin to SHAs `dc3619f`/`966deab` and land the P1/P10 ports onto `feature/meeting_attendee` _before_ that branch is deleted** — ideally commit the ported files to the feature branch promptly so the salvage no longer depends on a delete-scheduled branch. The pushed state is a *post-merge* snapshot ("Keep current merge"), so it is now the source of truth and may differ from whatever the plan author first saw.

---

## 1. Summary & reframe

The attendee already renders chat bubbles, and they already split into left ("Them") / right ("You") aligned bubbles. The **real gap** is that every meeting-room voice is tagged `TranscriptSpeaker.Them` and merged into one ever-growing bubble (`GetOrCreateBubble` in `TranscriptOverlayViewModel.cs:175-190` keys only on the 2-value enum + a 25 s window). To split distinct voices we must add a **per-utterance speaker label** produced by a self-contained voice-embedding service and make that label part of the bubble merge key and the bubble color.

**The migration's goal is contingent on one empirical fact: speaker-label stability on the real audio path.** The merge key splits/joins bubbles purely on label equality, so if the diarizer assigns *unstable* labels to a single physical voice — re-registering it as a new "Speaker N" mid-monologue — the result is not a cosmetic glitch. It **fragments that one voice's monologue into many separate bubbles**, which is a variant of the exact "lines moving out of the window" symptom this migration is meant to cure. Label stability on the attendee's mixed loopback stream is therefore the migration's **core unvalidated risk**, not a tuning footnote (see §6 risk #1). The plan is structured so the code compiles and degrades safely regardless, but "the migration achieves its goal" cannot be claimed until label stability is validated empirically on the real loopback path.

Orthogonally, there is a **separate, cheap layout bug**: each bubble body wraps its `TextBlock` inside a *horizontal* `StackPanel` (`MeetingAttendeeOverlay.xaml:127` and `:173`), which measures children at infinite width, so `TextWrapping="Wrap"` never fires and text runs off-screen (the user's "single line moving out of the window"). That fix is independent of the data-model work and ships first.

---

## 2. Scope

**IN**
- Per-speaker voice detection: port the POC `SpeakerIdentificationService` (SherpaOnnx `SpeakerEmbeddingExtractor`, centroid cosine matching) and call it at the segment tag point to produce `"Speaker 1"` / `"Speaker 2"` … labels.
- Labeled, colored bubbles: add a `SpeakerLabel` to the utterance + bubble, make it part of the merge key, assign a stable color slot per label, and render a 5-color palette in both themes.
- The wrap-bug fix (Deliverable A).
- **In-session speaker rename (Deliverable C, §5 Phase 10).** Confirmed in scope 2026-06-24 (decision #2). Right-click / click-to-edit a "Them" speaker label ("Speaker 2" → "Marco"); the rename retargets the **speaker label** (not `CounterpartName`), updates the live diarizer label map, re-keys the color slot, and retroactively relabels existing bubbles. **Within-meeting only** — discarded at meeting end (fresh service per meeting), no persistence.

**OUT (with rationale)**
- **The entire consent / biometric apparatus** (POC `TranscriptChannel` enum + `Channel` param on `TranscriptUtterance`, blocklist, consent gate/manager, pre-consent buffers, `SpeakerRegistered` consent hook, `IdentifyOrRegisterWithEmbedding`/`SetEmbedding` branches). The POC `SpeakerIdentificationService` is **self-contained**: consent merely *subscribed* to its `SpeakerRegistered` event; the service does not depend on consent to label speakers. Porting it without consent is functionally complete for "separate colored bubbles." (Re-verified against the pushed salvage `dc3619f`/`966deab`: both ported files reference zero consent *types* — the only consent mention is one comment in `SpeakerIdentificationService.cs:135` ("e.g. `ConsentStateManager`"), so port-verbatim compiles standalone. The salvage branch *also* carries the consent files themselves — `Services/Consent/*` (`PerSpeakerRingBufferRegistry`, `SpeakerConsentEntry`, `SpeakerRingBuffer`) + their tests — which are **not** ported.)
- **POC VAD swap** (`SherpaOnnxVadDetector`). The attendee `SileroVadDetector` already yields `float[]` 16 kHz mono segments; we do not touch it.
- ~~**In-session speaker rename UI**~~ — **MOVED TO IN SCOPE 2026-06-24 (decision #2), within-meeting only.** See the IN list above and §5 Phase 10.
- **Cross-meeting / persistent speaker memory** (persisting voice fingerprints + assigned names to disk so a returning voice is auto-recognized in a future meeting). Confirmed OUT 2026-06-24: voice embeddings are biometric data, and persisting them reopens the full consent question (decision #1, OUT on the assumption nothing is persisted). Renames and identities are **session-scoped and discarded at meeting end**. A persistent recognized-speaker store with a first-class biometric-consent surface + "forget" control is a deferred follow-up. (This was explicitly raised and declined on 2026-06-24 in favour of within-meeting-only.)

> ### PRIVACY NOTE (read before shipping)
> Voice embeddings are **biometric data**. This migration computes and centroids them in memory per session (never persisted), and the attendee already ships a consent acknowledgement checkbox before a meeting is joined. That is the minimum bar, but it is **not** a full consent model for biometric processing. **Recommended future follow-up:** a first-class consent surface (the POC had one) gating diarization, and an explicit "voice fingerprints are computed locally and discarded at meeting end" disclosure. Tracking this as a follow-up — out of scope here, but called out so it is not lost.

---

## 3. The two deliverables

### Deliverable A — Wrap-bug fix (independent, ~minutes)

`MeetingAttendeeOverlay.xaml` nests the body `TextBlock` + `ListeningIndicator` inside a **horizontal** `StackPanel` in both bubble bodies. A horizontal `StackPanel` gives children infinite available width, so `TextWrapping="Wrap"` is inert; the `ScrollViewer` (`:84`) has `HorizontalScrollBarVisibility="Disabled"`, so overflow is clipped. Replace each inner horizontal `StackPanel` with a 2-column `Grid` (`*` for the wrapping `TextBlock`, `Auto` for the indicator) — the POC-proven pattern (`LiveTranscriptionOverlay.xaml` You: 169-183, Them: 267-280). This touches **only** the two inner panels at `:127` and `:173`; the header speaker-name `StackPanel`s at `:106`/`:152` and all `Border` attributes are untouched, so it is fully orthogonal to the data-model decision and the `CounterpartName` vs `SpeakerLabel` question.

**Exact replacement — "You" body (`MeetingAttendeeOverlay.xaml:127-135`):**
```xml
<Border Padding="12,8"
        CornerRadius="12,12,0,12"
        Background="{DynamicResource AccentFillColorDefaultBrush}">
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="*" />
      <ColumnDefinition Width="Auto" />
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0"
               Text="{Binding Text}"
               TextWrapping="Wrap"
               FontSize="14"
               Foreground="White" />
    <controls:ListeningIndicator Grid.Column="1"
                                 Margin="8,0,0,0"
                                 VerticalAlignment="Center"
                                 Visibility="{Binding IsListening, Converter={StaticResource BooleanToVisibilityConverter}}" />
  </Grid>
</Border>
```

**Exact replacement — "Them" body (`MeetingAttendeeOverlay.xaml:173-180`)** — identical structure, no `Foreground="White"` (keep current default). **Phase-0 KEEPS the inline `Background="{DynamicResource ControlFillColorSecondaryBrush}"` on this `Border` so the build stays visually unchanged until B7. Phase 9 then DELETES that inline attribute and replaces it with a `Border.Style` (see §5 P9 — a must-not-regress deletion, not a supplement).**
```xml
<Border Padding="12,8" CornerRadius="12,12,12,0"
        Background="{DynamicResource ControlFillColorSecondaryBrush}">
  <!-- Phase 0: keep the inline Background above (visual no-op until B7).
       Phase 9 (B7): DELETE the inline Background attribute entirely and move
       coloring into a Border.Style (default Setter + ColorIndex DataTriggers).
       A surviving local-value Background would override the Style and silently
       disable ColorIndex coloring. -->
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="*" />
      <ColumnDefinition Width="Auto" />
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0"
               Text="{Binding Text}"
               TextWrapping="Wrap"
               FontSize="14" />
    <controls:ListeningIndicator Grid.Column="1"
                                 Margin="8,0,0,0"
                                 VerticalAlignment="Center"
                                 Visibility="{Binding IsListening, Converter={StaticResource BooleanToVisibilityConverter}}" />
  </Grid>
</Border>
```
Use `Grid`, not `DockPanel`. Reference: `scratchpad/trackD-wrapfix.xaml`.

**Auto-scroll companion fix** (also independent — ship with A): `MeetingAttendeeOverlay.xaml.cs:39-43` scrolls to end **only** on `NotifyCollectionChangedAction.Add`. Same-speaker utterances inside the 25 s window `Append` to the existing bubble (a `Text` `PropertyChanged`, not a collection `Add`), so a monologue grows below the fold with no scroll — the *vertical* re-incarnation of the same bug once wrapping works. When a bubble is added, also subscribe to its `PropertyChanged` (for `Text`/`EndTimestamp`) and `ScrollToEnd`, **gated on "the viewer is already at the bottom"** (`BubbleScroll.VerticalOffset >= BubbleScroll.ScrollableHeight - epsilon`) so it never hijacks a user who has scrolled up to read history.
**Leak guard — tie unsubscribe to the front-trim path explicitly:** `TrimIfNeeded` removes bubbles from the front via `RemoveAt(0)` once the collection exceeds 200 bubbles. Those removals arrive as `NotifyCollectionChangedAction.Remove` (with `OldItems`). The handler must therefore also handle `Remove`/`Reset` and **unsubscribe the `PropertyChanged` handler from every removed (`e.OldItems`) bubble**, or every trimmed bubble leaks its handler for the life of the session. Subscribe on `Add`, unsubscribe on `Remove`/`Reset` — symmetric with `TrimIfNeeded`'s `RemoveAt(0)`.

### Deliverable B — Speaker migration

The bulk of this document (§4–§5). Produces the `SpeakerLabel`, threads it through the engine → utterance → bubble → color, and renders per-speaker colored bubbles.

---

## 4. Data-model decision

**Two options considered:**

| | Keep enum + add string label + color index (**chosen**) | Replace enum with a richer speaker record |
|---|---|---|
| Alignment (You-right / Them-left) | Untouched — enum still drives the XAML alignment `DataTrigger`s | Must rewrite both template halves + alignment triggers |
| Diff size | Minimal; matches the salvaged POC exactly | Rewrites converter, both templates, base VM, alignment |
| Identity vs view concern | Cleanly separated (label = identity, ColorIndex = view) | Merges them |
| Salvage fit | Reuses working POC code | Throws it away |

**Decision: keep `TranscriptSpeaker { You, Them }` as the alignment axis; layer a string label + stable color index on top.** The enum encodes *alignment only* (You = right + accent; Them = left + palette). The attendee currently emits only `Them`, but the "You" bubble template is explicitly retained ("unused by the attendee, kept for template parity", `MeetingAttendeeOverlay.xaml:93`). Keeping the enum **preserves a future mic/You path for free**: on such a path the speaker-ID service is null, so the label is null → ColorIndex 0 → behavior unchanged. This is also the literal mechanism that guarantees "the mic path is unaffected."

Every data-model change below is **additive** (optional record param, optional ctor params, new observable props, unchanged enum). That is precisely what lets each phase compile in sequence: nothing that references `TranscriptSpeaker` or constructs these records breaks mid-sequence.

**Exact type shapes:**

`Models/TranscriptUtterance.cs` — add a 4th **optional positional** param (source-compatible with the lone call site at `LiveTranscriptionEngineService.cs:156`):
```csharp
public sealed record TranscriptUtterance(
    TranscriptSpeaker Speaker,
    string Text,
    DateTimeOffset Timestamp,
    string? SpeakerLabel = null);
```
Do **not** add the POC `TranscriptChannel Channel` param or the `TranscriptChannel` enum (consent-only).

`Models/TranscriptBubble.cs` — add two observable properties + extend the ctor:
```csharp
[ObservableProperty] private string? _speakerLabel;   // identity mirror, mutable for a future rename
[ObservableProperty] private int _colorIndex;         // view palette slot 0..4

public TranscriptBubble(TranscriptSpeaker speaker, DateTimeOffset startTimestamp,
                        string text = "", string? speakerLabel = null)
{
    Speaker = speaker;
    StartTimestamp = startTimestamp;
    _endTimestamp = startTimestamp;
    _text = text ?? string.Empty;
    _speakerLabel = speakerLabel;
}
```
`ColorIndex` is assigned by the VM via object initializer / property set, never a ctor param — identity stays decoupled from the view palette. Existing 3-arg call sites (`TranscriptBubbleTests.cs`) keep compiling.

**Merge key — THE load-bearing change (the single correctness gate of this migration), and where label stability becomes correctness.**
`GetOrCreateBubble` (`TranscriptOverlayViewModel.cs:175-190`) currently reuses the last bubble when `last.Speaker == speaker` AND within the window. Because every utterance is `Them`, adding `SpeakerLabel` *everywhere except this predicate* yields a migration that *looks* done but still collapses Speaker 1 + Speaker 2 into one bubble. The predicate MUST add the label:
```csharp
internal TranscriptBubble? GetOrCreateBubble(
    TranscriptSpeaker speaker, DateTimeOffset timestamp, string? speakerLabel, bool createIfMissing)
{
    var last = Bubbles.Count > 0 ? Bubbles[^1] : null;
    bool sameWindow = last is not null
        && last.Speaker == speaker
        && (timestamp - last.StartTimestamp).TotalSeconds < BubbleWindowSeconds;

    if (sameWindow && string.Equals(last!.SpeakerLabel, speakerLabel, StringComparison.Ordinal))
        return last;

    if (!createIfMissing) return null;

    var bubble = new TranscriptBubble(speaker, timestamp, speakerLabel: speakerLabel)
    {
        ColorIndex = GetOrAssignSpeakerColorIndex(speakerLabel),
    };
    Bubbles.Add(bubble);
    return bubble;
}
```

**The ordinal label equality is exactly what makes correctness hostage to label stability (see §1 and §6 risk #1).** Two consequences to internalize before execution:

1. **Re-registration fragments a monologue.** If the diarizer gives the same physical voice a fresh `"Speaker N"` (label instability), the `string.Equals` check fails and a *new* bubble is created mid-monologue. One speaker → many bubbles. This is the migration's failure mode of record, and it is the *same class of symptom* (a speaker's lines splintering / scrolling away) the migration set out to fix.
2. **Sub-1.5 s segments split a colored run.** A segment below `MinDiarizationSamples` (§5 P4) emits `SpeakerLabel = null` → ColorIndex 0 → a slot-0 bubble. If one arrives mid-run while a colored speaker is talking, the `string.Equals(null, "Speaker 2")` check fails and **interrupts/splits that colored speaker's bubble** with a one-off uncolored bubble. The `MinDiarizationSamples` guard is therefore not purely a centroid-poisoning protection — it has this bubble-splitting side effect. The plan ships the simple, deterministic merge (null splits the run) and treats "absorb a null-label mid-run segment into the previous bubble regardless of label" as an **open decision** (§8), not a silent merge-logic change — because a merge exception is itself unvalidated behavior and should not be buried in this migration.

Color assignment (port of POC `LiveTranscriptionViewModel.cs:26-30, 322-330`), added to the base VM:
```csharp
private const int SpeakerColorPaletteSize = 5;
private readonly Dictionary<string, int> _speakerColorIndex = new(StringComparer.Ordinal);
private int _nextSpeakerColorIndex;

private int GetOrAssignSpeakerColorIndex(string? speakerLabel)
{
    if (string.IsNullOrWhiteSpace(speakerLabel)) return 0;          // undiarized → slot 0
    if (_speakerColorIndex.TryGetValue(speakerLabel, out var idx)) return idx;
    idx = _nextSpeakerColorIndex % SpeakerColorPaletteSize;
    _speakerColorIndex[speakerLabel] = idx;
    _nextSpeakerColorIndex++;
    return idx;
}
```
**Palette wrap is a known cosmetic limitation.** The palette is mod 5 (`SpeakerColorPaletteSize = 5`). With 6+ distinct *stable* speakers, the 6th reuses slot 0's color. This is a **cosmetic color collision only** — identity is carried by `SpeakerLabel`, which is unaffected, so bubbles still split correctly per speaker; only two speakers happen to share a hue. Matches the POC; stated here so it is a known limitation rather than a surprise.

> **Verified — only one caller; no listening path on this branch.** `AddUtterance` (`:157`) is the **sole** caller of `GetOrCreateBubble` and always passes `createIfMissing: true`; `createIfMissing: false` is currently dead. The attendee VM has **no** `SpeakingChanged`/listening-dot path (`MeetingAttendeeViewModel.cs:18`: "the attendee only ever produces `Them`"), and `AddUtterance` appends text immediately so `last.Text` is never empty. Therefore the POC's empty-listening-placeholder **label-adoption branch is dead code here and is intentionally NOT ported** (no live producer of empty listening bubbles), and its POC test is **not** adapted. If a mic/listening path is added later, reintroduce the adoption branch then. The `speakerLabel` param is added to `GetOrCreateBubble` regardless; the single `AddUtterance` call site is updated in the same phase.

**Display-name contract — adopt the 3-value merge (reconciles the inventory conflict).**
Reports diverge: Track D proposed *flipping* the converter to `{Speaker, SpeakerLabel}` (You→"you", Them→label or `"Speaker"`), which forces rewriting the existing converter-test assertions and silently discards the persisted `LastCounterpartName`. Tracks A/B/C and the reviewer favor an **additive 3-value** binding `{Speaker, SpeakerLabel, CounterpartName}` → `SpeakerLabel` if non-blank, else `CounterpartName`, else `"them"`. This is the chosen contract: it keeps the existing assertions' **intent** valid (`Them + null → "them"`, `Them + "Alex" → "Alex"`) once the test input arrays are widened to 3 elements (§5 P8 / §7), and only **adds** a label-wins case, dissolving Track D's "two converter test files cannot both pass" conflict (which was only true for the flip). `LastCounterpartName` stays useful before anyone is diarized, then `"Speaker N"` wins once diarized.

---

## 5. File-by-file change list — ordered phases (each leaves the build green)

> Repo convention: **source files are CRLF**; the Write tool emits LF. Any *new* file (the two ported speaker-ID files) must be converted to CRLF after writing. Gate every phase with the MTP runner: `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` and fail only on failures **outside** that namespace (~18 known live-network failures inside it per the baseline gate).

### Phase 0 — Deliverable A (wrap fix + auto-scroll). Independent; can merge alone.
- **modify** `src/Pia.Wpf/Views/MeetingAttendeeOverlay.xaml` — replace inner horizontal `StackPanel`s at `:127-135` and `:173-180` with the 2-col `Grid` (XAML in §3). Leave header `StackPanel`s (`:106`, `:152`) and `Border` attributes unchanged — including the "Them" `Border`'s inline `Background` (KEEP it here; it is deleted in P9).
- **modify** `src/Pia.Wpf/Views/MeetingAttendeeOverlay.xaml.cs` — keep the Add-handler; additionally scroll on the tail bubble's `Text`/`EndTimestamp` `PropertyChanged`, gated on "already at bottom"; subscribe on `Add`, and **unsubscribe on `Remove`/`Reset` (tied to `TrimIfNeeded`'s `RemoveAt(0)` front-trim at >200 bubbles)** to avoid handler leaks.

### Phase 1 — Port the diarizer (no behavior change; compiles standalone).
- **new (port-verbatim)** `src/Pia.Wpf/Services/LiveTranscription/ISpeakerIdentificationService.cs` ← POC `…\ISpeakerIdentificationService.cs`. `interface : IDisposable`. Namespace `Pia.Services.LiveTranscription` already matches; usings already match. References zero consent types. Unused members (`IdentifyOrRegisterWithEmbedding`, `Rename`, `Reset`, `SpeakerRegistered`) are harmless — keep to minimize churn. **CRLF-convert.**
- **new (port-verbatim)** `src/Pia.Wpf/Services/LiveTranscription/SpeakerIdentificationService.cs` ← POC `…\SpeakerIdentificationService.cs`. `sealed class … : ISpeakerIdentificationService`. References zero consent types. Ctor `(string modelPath, float matchThreshold, ILogger logger)` builds `SpeakerEmbeddingExtractorConfig { Model=modelPath; NumThreads=1; Provider="cpu"; Debug=0 }` + a `SherpaOnnx.SpeakerEmbeddingExtractor`. `IdentifyOrRegister(float[] segmentSamples, int sampleRate) → string`. `const float BorderlineMargin = 0.07f`; three-zone cosine match with confidence-weighted L2-normalized centroids; internal ids `spk_N` → display `"Speaker N"`. **CRLF-convert.** Build to verify `SpeakerEmbeddingExtractor` resolves from the existing `org.k2fsa.sherpa.onnx 1.12.40` package (if it does not, the whole approach is blocked — no new NuGet is planned).

### Phase 2 — Model download (additive; no caller yet).
- **modify** `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionModels.cs` — add:
  - `private const string SherpaSpeakerReleasesBase = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models";` — **preserve the misspelled `recongition` tag verbatim** (Track A verified `Content-Length = 28,281,164` with the misspelling; "correcting" it 404s).
  - `private const string SpeakerEmbeddingFileName = "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";`
  - `public static string SpeakerEmbeddingModelPath { get; }` and `public static bool IsSpeakerEmbeddingAvailable()` — flat `Path.Combine(ModelsDirectory, SpeakerEmbeddingFileName)` (mirrors `EnsureSileroVadAsync`'s flat layout; do not nest like the POC, to match this branch's convention).
  - `public static async Task<string> EnsureSpeakerEmbeddingAsync(IHttpClientFactory httpClientFactory, ILogger logger, CancellationToken cancellationToken = default)` — **mirror the body of `EnsureSileroVadAsync` (`:31-54`)**: `Directory.CreateDirectory` → if-exists-and-nonzero short-circuit → own `http.GetAsync(url, ResponseHeadersRead, ct)` → copy to `path + ".tmp"` → `File.Move(tmp, path, overwrite: true)`. **Do NOT** reuse the POC's `IProgress`-taking signature or its `EnsureSingleFileAsync` helper (those won't compile against this branch's surface). Note: this branch's `DownloadWithProgressAsync` writes directly to its destination (no tmp/move), so mirroring `EnsureSileroVadAsync`'s self-contained body is the cleaner match. `ReaderFactory.OpenReader` / tar.bz2 extraction is irrelevant — the speaker model is a single `.onnx`.

### Phase 3 — Data model (compile-safe; lone call site still compiles).
- **modify** `src/Pia.Wpf/Models/TranscriptUtterance.cs` — add `string? SpeakerLabel = null` (§4).
- **modify** `src/Pia.Wpf/Models/TranscriptBubble.cs` — add `[ObservableProperty] string? _speakerLabel`, `[ObservableProperty] int _colorIndex`, and the `speakerLabel` ctor param (§4). `Append`, `Speaker`, `StartTimestamp` unchanged.

### Phase 4 — Engine (produce the label at the tag point).
- **modify** `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs`:
  - Add a **trailing optional** ctor param `ISpeakerIdentificationService? speakerId = null` (attendee ctor order: `speaker, source, sileroVadModelPath, engine, sink, logger, speakerId` — note this branch puts the path **before** the engine, unlike the POC; do not copy the POC signature) → store in `private readonly ISpeakerIdentificationService? _speakerId;`. Trailing+optional keeps the existing 6-arg callers (`LiveTranscriptionEngineDrainTests.cs`) compiling.
  - Add `private const int MinDiarizationSamples = 16000 * 3 / 2;` (1.5 s @16 kHz). **Required** — the attendee `SileroVadDetector` emits segments down to 0.5 s (`MinSegmentSamples = 8000`); sub-1.5 s embeddings poison centroids. (See §4 / §6 risk #4 for its bubble-splitting side effect on null-label segments.)
  - In `TranscribeSegmentAsync` (`:138`), **before** `_engine.TranscribeAsync` at `:144`, compute the label:
    ```csharp
    string? speakerLabel = null;
    if (_speakerId is not null && samples.Length >= MinDiarizationSamples)
    {
        try { speakerLabel = _speakerId.IdentifyOrRegister(samples, 16000); }
        catch (Exception ex) { _logger.LogWarning(ex, "Speaker identification failed for {Speaker}", _speaker); }
    }
    ```
    Use the **label-only** `IdentifyOrRegister` (sample rate `16000` hardcoded — confirmed against `WhisperSherpaEngine.TranscribeAsync` / `AcceptWaveform(16000,…)` and `SileroVadDetector`). Do **not** use the `…WithEmbedding` variant — the embedding is dead without consent/blocklist.
  - At `:156`, attach it: `var utt = new TranscriptUtterance(_speaker, text, DateTimeOffset.Now, speakerLabel);`.
  - The engine MUST NOT dispose `_speakerId` (caller-owned). When `_speakerId` is null (the mic/You path **or** a failed speaker-model download — §5 P6), `speakerLabel` stays null → ColorIndex 0 → existing single-bubble behavior — **this is both the mic-path-unaffected guarantee and the degrade-to-null safety net.**

### Phase 5 — Settings.
- **modify** `src/Pia.Wpf/Models/AppSettings.cs` — in the meeting block (`:107-114`, `MeetingAttendeeUseProcessLoopback` at `:114`) add:
  ```csharp
  public bool EnableMeetingDiarization { get; set; } = true;
  public float SpeakerEmbeddingThreshold { get; set; } = 0.70f;   // POC default confirmed
  ```
  Name `EnableMeetingDiarization` (not the POC's `EnableLoopbackDiarization` — *all* attendee audio is loopback, so "loopback" reads oddly). **Local-only**: `SyncSettings.cs` does not mirror `MeetingAttendeeUseProcessLoopback`/`MeetingTranscriptFolder`, so no SyncSettings counterpart.

### Phase 6 — Orchestrator (own / construct / thread / dispose the service — and DEGRADE TO NULL on download/construct failure).
- **modify** `src/Pia.Wpf/Services/MeetingAttendee/MeetingAttendeeService.cs`:
  - **Why the download must live in the seam closure (reconciles the inventory conflict):** Track A suggested constructing the service "as a dedicated step in `StartAsync`." That is **not** possible: there are no `_httpClientFactory` / `_loggerFactory` fields (loggerFactory is consumed once at `:142`; httpClientFactory is captured only by the production closures, `:97-124`). `EnsureSpeakerEmbeddingAsync` needs `httpClientFactory` and the typed logger needs `loggerFactory`, so the download + construction must live inside a seam closure. C# also forbids `this` in a `: this(...)` initializer, so that closure cannot assign `_speakerId` — the service must come **back as a return value**. → Follow Track C: **widen `_createTranscription`** to a 3-tuple.
  - Change the `_createTranscription` delegate type (`:44` and seam ctor `:136`) to `Func<CancellationToken, Task<(string SileroPath, ITranscriptionEngine Engine, ISpeakerIdentificationService? SpeakerId)>>`.
  - **(BLOCKING-ISSUE #1 FIX — degrade to null; a speaker-model failure must NEVER fail meeting join.)** Diarization is an *optional enhancement* to an already-working feature. Silero VAD may fail fatally (transcription genuinely needs it), but a speaker-model failure must not regress join. In the production `createTranscription` closure (`:98-107`): build `sileroPath` and `engine` **outside** any speaker try/catch (so a Silero failure still propagates fatally as today). Then, and **only** when `settings.EnableMeetingDiarization`, attempt the speaker setup inside a try/catch that wraps **only** the ensure+construct:
    ```csharp
    ISpeakerIdentificationService? speakerId = null;
    if (settings.EnableMeetingDiarization)
    {
        try
        {
            var speakerModelPath = await LiveTranscriptionModels
                .EnsureSpeakerEmbeddingAsync(httpClientFactory, log, ct);
            speakerId = new SpeakerIdentificationService(
                speakerModelPath,
                settings.SpeakerEmbeddingThreshold,
                loggerFactory.CreateLogger<SpeakerIdentificationService>());
        }
        catch (Exception ex)
        {
            // DEGRADE TO single-bubble behavior. A CDN hiccup, a 404 (e.g. if the
            // misspelled `recongition` tag is "fixed"), a corrupt download, or a native
            // SpeakerEmbeddingExtractor construction failure must NOT regress meeting join.
            log.LogWarning(ex, "Speaker diarization unavailable; continuing without per-speaker bubbles.");
            speakerId = null;
        }
    }
    return (sileroPath, engine, speakerId);
    ```
    Do **not** wrap the Silero/engine setup in this catch. Construct a **fresh** service per start (so "Speaker N" numbering resets per meeting; no `Reset()` needed). Downstream, `speakerId == null` → `speakerLabel` stays null (§5 P4) → ColorIndex 0 → single-bubble behavior, exactly as the pre-diarization attendee.
  - Add `private ISpeakerIdentificationService? _speakerId;` field. In `StartAsync`, unpack the 3-tuple (`:182`) and assign `_speakerId = speakerId;`. **Because the speaker setup degrades to null INSIDE the closure, the `await` at `:182` cannot throw on a speaker-model failure — so the `try` whose `catch` (`:233`) calls `DisposeAllAsync` and transitions to `MeetingAttendeeState.Error` is NOT entered for diarization failures.** `StartAsync` still reaches `Attending`. (This is the precise reversal of the original coupling: the POC awaited the ensure+construct inside the StartAsync try, so any download failure flipped the whole join to `Error`.)
  - Extend the `_engineServiceFactory` delegate type (`:52` and seam ctor `:139`) with a **trailing** `ISpeakerIdentificationService?` param: `Func<IAudioCaptureSource, string, ITranscriptionEngine, ChannelWriter<TranscriptUtterance>, ISpeakerIdentificationService?, CancellationToken, Task<IAsyncDisposable>>`. In the production closure (`:113-124`) pass `_speakerId` into the `new LiveTranscriptionEngineService(TranscriptSpeaker.Them, source, sileroPath, engine, sink, /* speakerId */ speakerId, loggerFactory…)`. Update the call site at `:207`.
  - In `DisposeAllAsync` (`:331-373`): after the `_transcriptionEngine` dispose block, add a guarded `_speakerId?.Dispose(); _speakerId = null;`. **Dispose order matters** — it wraps native ONNX resources; it must be disposed **after** `_engineService.DisposeAsync()` returns (the engine drains its segment loop there; an in-flight `IdentifyOrRegister` against a disposed extractor would crash natively).
  - **Same-phase test edit (required to keep the gate compiling):** `tests/.../Services/MeetingAttendee/MeetingAttendeeServiceStateTests.cs` — the `createTranscription` seam literal at `:349` (currently a 2-tuple) becomes the 3-tuple `("silero.onnx", transcriptionEngine, null)`; the `engineServiceFactory` lambda at `:360` goes from 5 to 6 discards → `(_,_,_,_,_,_) =>`. `EngineBuilt` (`:92`) still holds; default `AppSettings()` (`EnableMeetingDiarization=true`) is safe because the production download path is fully behind the substituted seams. **Add the degrade-to-null regression test here** (see §7).

### Phase 7 — DI / Bootstrapper.
- **reference-only** `src/Pia.Wpf/Bootstrapper.cs` — **no change.** `SpeakerIdentificationService` is per-session, constructed inside `MeetingAttendeeService` (mirrors the POC `LiveMeetingService`), not DI-registered. `IHttpClientFactory` + `ILoggerFactory` already flow into the `MeetingAttendeeService` ctor (`:92-93`).

### Phase 8 — Converter + Markdown (move together).
- **modify** `src/Pia.Wpf/Converters/SpeakerToDisplayNameConverter.cs` — adopt the 3-value contract (§4):
  ```csharp
  public static string Resolve(TranscriptSpeaker speaker, string? speakerLabel, string? counterpartName)
  {
      if (speaker == TranscriptSpeaker.You) return "you";
      if (!string.IsNullOrWhiteSpace(speakerLabel)) return speakerLabel!;
      return string.IsNullOrWhiteSpace(counterpartName) ? "them" : counterpartName!;
  }
  ```
  `Convert` reads `values[0]=Speaker, values[1]=SpeakerLabel, values[2]=CounterpartName` (require `values.Length >= 3`, else return `string.Empty`).
- **modify** `src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs` `BuildMarkdown` (`:267`) → `SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, bubble.SpeakerLabel, CounterpartName)`. **This call IS a real compile change** — `Resolve` goes 2-arg → 3-arg, so `BuildMarkdown` will not compile until updated (the `bubble.SpeakerLabel` member exists from P3). The **build stays green across the P8→P9 split**: the 2-value XAML MultiBindings still bind against the 3-value `Convert` and merely render an empty/fallback name at *runtime* until P9 adds the third `Binding`. So the XAML side is a runtime-completeness boundary, not a build break. (If you prefer, merge P8 and P9 into one phase.)
- **modify (same-phase, required to keep the gate green)** `tests/.../Converters/SpeakerToDisplayNameConverterTests.cs` — **NOTE the real failure mechanism (the earlier draft misdiagnosed this):** these tests do **NOT** call `Resolve`; they call `Convert(object[], …)` with **2-element** arrays (lines 14-47), and `Convert`'s signature (`object[]`) is unchanged, so the file still **COMPILES**. The break is at **runtime/assertion**: the new `Convert` requires `values.Length >= 3`, so every existing 2-element-array test hits the guard, returns `string.Empty`, and fails `Assert.Equal("you", …)` / `"Alex"` / `"them"`. **Fix = widen each test input array from 2 to 3 elements** (`{ Speaker, SpeakerLabel, CounterpartName }`); only AFTER widening do the existing assertions hold under the 3-value contract. As written before widening, the assertions are NOT automatically valid. Then **add** a label-wins case and a precedence case (see §7).

### Phase 9 — Base VM threading + theme brushes + template.
- **modify** `src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs`:
  - Add the color map + `GetOrAssignSpeakerColorIndex` (§4).
  - `AddUtterance` (`:157`): pass `utterance.SpeakerLabel` → `GetOrCreateBubble(utterance.Speaker, utterance.Timestamp, utterance.SpeakerLabel, createIfMissing: true)`.
  - `GetOrCreateBubble` (`:175-190`): new signature + label-keyed merge + ColorIndex (§4).
- **modify** `src/Pia.Wpf/Resources/Themes/Light.xaml` — add (before any DataTrigger references them) `SpeakerBubbleBackground1Brush..5Brush`: `#FFE4E9F0`, `#FFE4EFE1`, `#FFF5E8D8`, `#FFEEE2F0`, `#FFFAE3E3`.
- **modify** `src/Pia.Wpf/Resources/Themes/Dark.xaml` — add `SpeakerBubbleBackground1Brush..5Brush`: `#FF333842`, `#FF323D33`, `#FF433A30`, `#FF3A3340`, `#FF402F2F`.
- **modify** `src/Pia.Wpf/Views/MeetingAttendeeOverlay.xaml`:
  - Both speaker-name MultiBindings (`:116-120` You, `:159-163` Them): add a `<Binding Path="SpeakerLabel" />` so they match the 3-value `Resolve`. Order: `Speaker, SpeakerLabel, DataContext.CounterpartName`.
  - "Them" body `Border` (the one wrapped in Phase 0): **DELETE the inline `Background="{DynamicResource ControlFillColorSecondaryBrush}"` attribute (line 172) entirely** and replace it with a `Border.Style`. This is a **must-not-regress deletion, not a supplement**: in WPF a local-value `Background` attribute beats both a Style `Setter` and its `DataTrigger`s, so if the inline `Background` survives, the ColorIndex coloring is silently inert and every "Them" bubble stays one color. (Phase 0 deliberately KEPT that inline `Background`; P9 must affirmatively remove it.) The `Border.Style`'s **default `Setter`** is `Background = {DynamicResource SpeakerBubbleBackground1Brush}` (so ColorIndex 0 has a defined color, not transparent) + 4 `DataTrigger`s on `ColorIndex` (`1→2Brush, 2→3Brush, 3→4Brush, 4→5Brush`), per POC `LiveTranscriptionOverlay.xaml:234-254`. Port the trigger `Value="1".."4"` strings verbatim (WPF coerces to int). The "You" body keeps `AccentFillColorDefaultBrush`.

### Phase 10 — Deliverable C: in-session speaker rename (within-meeting only). Decision #2.
> Renames retarget a **speaker label** (`"Speaker 2"` → `"Marco"`), **not** `CounterpartName`. Session-scoped: the diarizer is constructed fresh per meeting (§5 P6), so renames die at meeting end — no persistence, no consent apparatus. All source ported from `origin/feature/meeting_transscription` (`dc3619f`/`966deab`): VM command `LiveTranscriptionViewModel.cs:496-524`, affordance `LiveTranscriptionOverlay.xaml:200-263`, service `SpeakerIdentificationService.cs:145-162`.

- **`ISpeakerIdentificationService.Rename(string oldLabel, string newLabel) → bool`** — already present from the P1 verbatim port (P1 kept it as "harmless dead code"); **it now goes live.** Verified **thread-safe**: the impl takes the same `lock (_lock)` that `IdentifyOrRegister(WithEmbedding)` holds, and is a single `_displayLabels[internalId] = newLabel` update — the service splits centroids (`_speakers`, keyed by internal `spk_N` id) from display labels (`_displayLabels`, internalId→label), so a rename does **not** disturb future matching (identify still matches the centroid → internalId → now-renamed label). Returns `false` if no label matches `oldLabel`. **No "add locking" work needed.**
- **modify** `src/Pia.Wpf/Services/MeetingAttendee/IMeetingAttendeeService.cs` — add `void RenameSpeaker(string oldLabel, string newLabel);` (sync; the underlying op is an in-memory dictionary update — the POC VM calls it without awaiting). The new member ripples to **`FakeMeetingAttendeeService`** (nested in `MeetingAttendeeViewModelTests.cs:223-261`) — implement it to record the `(old,new)` call.
- **modify** `src/Pia.Wpf/Services/MeetingAttendee/MeetingAttendeeService.cs` — passthrough: `public void RenameSpeaker(string oldLabel, string newLabel) => _speakerId?.Rename(oldLabel, newLabel);`. **No-op when `_speakerId` is null** (diarization off, or degraded-to-null per P6) — must not throw. Safe against a concurrent in-flight `IdentifyOrRegister` (shared `_lock`).
- **modify** `src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs` (base) — add `protected void RelabelSpeaker(string oldLabel, string newLabel)` that owns the base-VM state the rename mutates:
  - **Palette carry-over (re-key):** `if (_speakerColorIndex.Remove(oldLabel, out var carried)) _speakerColorIndex[newLabel] = carried;` — so the renamed speaker keeps its color slot and future utterances under `newLabel` hit the same entry. (Without this, `newLabel` grabs a fresh slot and the run changes hue.)
  - **Retroactive relabel:** `DispatchToUi(() => { foreach (var b in Bubbles) if (b.SpeakerLabel == oldLabel) b.SpeakerLabel = newLabel; });`.
  - **Why base/subclass split:** the attendee differs from the POC's single `LiveTranscriptionViewModel` — `_speakerColorIndex`/`Bubbles`/`DispatchToUi` live in the base (added in P9), but `_service` and the dialog live in the subclass. So the *state mutation* is a base helper; the *orchestration + service call* is the subclass command.
- **modify** `src/Pia.Wpf/ViewModels/MeetingAttendeeViewModel.cs`:
  - Inject **`IDialogService dialogService`** into the ctor (it already exists — `IDialogService.ShowInputDialogAsync(title, prompt)`, interface `IDialogService.cs:20`, impl `DialogService.cs:180`, already used by `TodoViewModel`); store in a field. Update the ctor + DI registration. **Only one existing test call site** to update (`MeetingAttendeeViewModelTests.cs:216`) — pass `Substitute.For<IDialogService>()` (the established pattern, `AssistantHistoryViewModelFilterTests.cs:17`).
  - Add the command (port of POC, names kept so the affordance XAML ports verbatim):
    ```csharp
    [RelayCommand(CanExecute = nameof(CanRenameSpeakerLabel))]
    private async Task RenameSpeakerLabelAsync(string? oldLabel)
    {
        if (string.IsNullOrWhiteSpace(oldLabel)) return;
        var title  = _localizationService["MeetingAttendee_RenameSpeaker_Title"];
        var prompt = string.Format(_localizationService["MeetingAttendee_RenameSpeaker_Prompt"], oldLabel);
        var newLabel = await _dialogService.ShowInputDialogAsync(title, prompt).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newLabel) || newLabel == oldLabel) return;
        _service.RenameSpeaker(oldLabel, newLabel);
        RelabelSpeaker(oldLabel, newLabel);   // base helper: palette re-key + bubble walk
    }
    private static bool CanRenameSpeakerLabel(string? oldLabel) => !string.IsNullOrWhiteSpace(oldLabel);
    ```
- **modify** `src/Pia.Wpf/Views/MeetingAttendeeOverlay.xaml` — port the affordance from POC `LiveTranscriptionOverlay.xaml:200-263`:
  - Wrap the "Them" speaker-name `TextBlock` in a transparent `ui:Button` (edit-pencil `ui:SymbolIcon Symbol="Edit24"`), `Command="{Binding DataContext.RenameSpeakerLabelCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"`, `CommandParameter="{Binding SpeakerLabel}"`. **Note:** the name MultiBinding here is the same one P9 widens to 3 values — keep `Speaker, SpeakerLabel, DataContext.CounterpartName` order.
  - Optionally the `Border.ContextMenu` (right-click) via the POC `PlacementTarget.Tag` pattern: set `Border.Tag` = `{Binding DataContext, RelativeSource={RelativeSource AncestorType=UserControl}}`, then `Command="{Binding PlacementTarget.Tag.RenameSpeakerLabelCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}"`, `CommandParameter="{Binding PlacementTarget.DataContext.SpeakerLabel, RelativeSource={RelativeSource AncestorType=ContextMenu}}"`. (This `Border` is the one P9 attaches the `ColorIndex` `Border.Style` to — both can coexist.)
- **add** localization keys `MeetingAttendee_RenameSpeaker_Title` / `_Prompt` / `_MenuItem` to the resx (mirror the POC `LiveTrans_RenameSpeaker_*` strings; `_Prompt` is a `string.Format` template taking the old label).
- **modify** `src/Pia.Wpf/Bootstrapper.cs` — if `MeetingAttendeeViewModel` is constructed manually (not pure container resolution), add the `IDialogService` argument; `IDialogService` is already DI-registered (used elsewhere).

**Known limitation (record, do not "fix"):** rename is eventually-consistent — a diarization segment already in flight when the user renames can land a stray *old-label* bubble just after the relabel walk completes. Cosmetic, self-corrects on the next utterance. The POC accepts this; do **not** claim "perfectly consistent."

### Ripple surface (full, from Track C — verified complete; verify each compiles in its phase)
- `Models/TranscriptUtterance.cs` (P3), `Models/TranscriptBubble.cs` (P3)
- `Services/LiveTranscription/LiveTranscriptionEngineService.cs` ctor `:32-38` + `TranscribeSegmentAsync :138-157` (P4)
- `Services/LiveTranscription/LiveTranscriptionModels.cs` after `:54` (P2)
- `Services/MeetingAttendee/MeetingAttendeeService.cs` `:44, :52, :98-124, :136-149, :182, :207, :331-373` (P6)
- `Models/AppSettings.cs` `:107-114` (P5)
- `ViewModels/TranscriptOverlayViewModel.cs` `:157, :175-190, :267` + new color fields (P8/P9)
- `ViewModels/MeetingAttendeeViewModel.cs` — **rename IS in scope (P10):** add `IDialogService` ctor param + `RenameSpeakerLabel` command. `CounterpartName`/`LastCounterpartName` seeding (`:63`, `:139`) stays (still the pre-diarization fallback)
- **(P10) rename ripple:** `Services/MeetingAttendee/IMeetingAttendeeService.cs` + `MeetingAttendeeService.cs` (`RenameSpeaker` passthrough, no-op when `_speakerId` null); `ViewModels/TranscriptOverlayViewModel.cs` (`protected RelabelSpeaker` = palette re-key + bubble walk); `Views/MeetingAttendeeOverlay.xaml` (edit-pencil `ui:Button` + optional `Border.ContextMenu`); `Bootstrapper.cs` (VM `IDialogService` arg); resx (`MeetingAttendee_RenameSpeaker_*`); `ISpeakerIdentificationService.Rename` goes live; `FakeMeetingAttendeeService` (in `MeetingAttendeeViewModelTests.cs`) implements the new member
- `Converters/SpeakerToDisplayNameConverter.cs` (P8)
- `Views/MeetingAttendeeOverlay.xaml` MultiBindings + Them Background-delete (P0/P9); `…Overlay.xaml.cs` auto-scroll (P0)
- `Resources/Themes/Light.xaml` + `Dark.xaml` (P9)
- Tests: see §7.

> **Ripple completeness (verified, no omissions).** Every enumerated `TranscriptSpeaker` / `TranscriptUtterance` / `TranscriptBubble` usage is present and addressed above. Confirmed by grep: the only production caller of `GetOrCreateBubble` is `AddUtterance`; the only caller of `SpeakerToDisplayNameConverter.Resolve` is `BuildMarkdown`; and the only constructor of `MeetingAttendeeService`'s seam ctor outside production is `MeetingAttendeeServiceStateTests`. No un-listed file is silently broken by the signature/tuple/delegate changes; the three feared hidden call sites do not exist.

---

## 6. Risk register

1. **(TOP product risk — and the migration's core unvalidated assumption) Speaker-label STABILITY on a single mixed downstream loopback stream, and its direct coupling to the merge key.** The attendee feeds **a single mixed downstream loopback stream blending all remote participants** — endpoint WASAPI loopback captures the Teams DOWNSTREAM render mix per `MeetingAttendeeUseProcessLoopback=false` and the `ResolveAudioSource` comment. This is **NOT a far-field room microphone** (so it is not a mic-placement problem; do not dismiss it as such). One mixed stream is the *worst case* for centroid speaker-ID: overlap, codec artifacts, and per-participant capture/level variance degrade embeddings. **This matters because the merge key (`string.Equals(last.SpeakerLabel, speakerLabel, Ordinal)`, §4) makes bubble correctness a direct function of label stability.** When the same physical voice is re-registered as a fresh `"Speaker N"`, the merge fails and the speaker's monologue **FRAGMENTS into many bubbles** — the *primary* symptom of label instability (NOT color collision), and a variant of the very "lines scrolling out of the window" problem this migration exists to fix. **Treat per-speaker accuracy AND label stability on this audio path as UNVALIDATED. "The migration achieves its goal" cannot be claimed until label stability is validated empirically on the real loopback path.** Mitigations reduce but do not prove it out: the 1.5 s `MinDiarizationSamples` guard, a tunable `SpeakerEmbeddingThreshold`, and a fresh service per meeting.
2. **Model is zh/en (3D-Speaker CAM++ `_zh_en_`).** Language coverage is Mandarin + English; other-language voices may embed poorly. This **feeds directly into risk #1**: poor embeddings for non-zh/non-en voices worsen label instability and therefore bubble fragmentation. Note this; do not claim general multilingual diarization.
3. **Palette wrap is cosmetic only (explicitly NOT the fragmentation failure mode).** With palette size 5, the 6th distinct *stable* speaker reuses slot 0's color — a **color collision, not an identity or bubble-split collision** (identity is `SpeakerLabel`; bubbles still split correctly). This is distinct from risk #1: fragmentation comes from label *instability*; palette wrap comes from having >5 *stable* speakers. Acceptable, matches POC; documented as a known limitation.
4. **Null-label segments split colored runs (the `MinDiarizationSamples` side effect).** Sub-1.5 s segments emit `SpeakerLabel=null` → ColorIndex 0; arriving mid-run they interrupt a colored speaker's bubble (§4 consequence 2). Shipped as the simple deterministic behavior; "absorb null-label mid-run into the previous bubble" is an open decision (§8), not silently changed here.
5. **Model download size / first-run latency.** ~27 MB (`28,281,164` bytes) one-time download in the `StartAsync` hot path (gated behind `EnableMeetingDiarization`, default true; **now degrades to null on any failure — does not fail join**, §5 P6). Mirrors `EnsureSileroVadAsync`'s silent (no-progress) latency profile; first-run users on slow links see an extra delay before `Attending`.
6. **Biometric / privacy dimension.** Voice embeddings are biometric. In-memory only, discarded at meeting end, behind the existing consent checkbox — but not a full consent model. See the §2 PRIVACY NOTE and §8.
7. **CRLF line endings.** Repo `.cs` is CRLF; Write emits LF. The two **new** ported files must be CRLF-converted or byte-identical/raw-string tests and diffs get noisy.
8. **Merge-key omission is silent.** If `SpeakerLabel` is added everywhere except `GetOrCreateBubble`'s predicate, the bug persists invisibly. The Phase-9 net-new separation test (§7) is the only guard.
9. **Disposal ordering (native crash).** `_speakerId` must be disposed strictly after `_engineService.DisposeAsync()` returns; the engine must not dispose it.
10. **Converter + Markdown coupling.** Changing `Resolve`'s signature breaks `BuildMarkdown` at COMPILE time; the two XAML MultiBindings render a fallback at runtime until P9; the converter tests break at RUNTIME (array width, not arity). They move in one phase (P8/P9).
11. **Auto-scroll hijack + handler leak.** Scroll-on-Append must be gated on "already at bottom" or it yanks a user reading history; the per-bubble `PropertyChanged` handler must be unsubscribed on `TrimIfNeeded`'s front-trim (`RemoveAt(0)`, >200 bubbles) or it leaks.
12. **(Project risk) Salvage-source durability.** The ported P1/P10 source exists **only** on `feature/meeting_transscription` (`dc3619f`/`966deab`) — a branch the plan header marked "scheduled for deletion"; the files were never committed anywhere else (verified across all refs/reflog/stash). If the branch is pruned before the port lands on `feature/meeting_attendee`, the commits become GC-eligible and the salvage is lost again (this already cost one recovery detour on 2026-06-24). Mitigation: pin the SHAs; land P1/P10 — or at minimum commit the ported files — onto the feature branch **before** deleting the source branch.
13. **Rename eventual-consistency (P10).** A diarization segment already in flight when the user renames can land a stray *old-label* bubble just after the retroactive relabel walk. Cosmetic, self-corrects on the next utterance; do not claim perfect consistency.

---

## 7. Test plan

Gate with the MTP runner; `--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`. **Each compile- or assertion-breaking test edit is folded into its triggering phase in §5** (orchestrator-state → P6; converter → P8; below they are also listed for a consolidated view). The additive suites land in their phase too.

- **Port-verbatim (model-gated), Phase 2/3** `tests/.../Services/LiveTranscription/SpeakerIdentificationServiceTests.cs` ← POC. Skips when `!IsSpeakerEmbeddingAvailable()`. Cases: same audio twice → same label; `Reset` restarts counter; `Rename` updates future labels; rename-unknown → false. Synthetic-speech helper (harmonics + envelope). Add only after Phase 2 (`EnsureSpeakerEmbeddingAsync`/`IsSpeakerEmbeddingAvailable`/`SpeakerEmbeddingModelPath` exist).
- **Degrade-to-null regression (NET-NEW, BLOCKING-ISSUE #1 GUARD), Phase 6** `tests/.../Services/MeetingAttendee/MeetingAttendeeServiceStateTests.cs`:
  - The production try/catch that degrades to null lives **inside the `createTranscription` production closure**, which the state-tests *replace* via the seam — so the seam cannot directly exercise the catch. Cover the guarantee at two levels:
    1. **Seam-level (in StateTests):** a `createTranscription` seam returning the degraded shape `("silero.onnx", transcriptionEngine, null)` must drive `StartAsync` to `Attending` (single-bubble behavior), **not** `Error`. This asserts the consumer side: a null `SpeakerId` is a normal, non-fatal path through `StartAsync` (`:182`) and `DisposeAllAsync`, and a 3-tuple with `null` SpeakerId does not enter the `:233` catch.
    2. **Closure-level (the actual catch):** to make the catch unit-testable, extract the speaker setup into a small internal helper (e.g. `internal static async Task<ISpeakerIdentificationService?> TryCreateSpeakerIdentificationAsync(IHttpClientFactory, ILoggerFactory, AppSettings, ILogger, CancellationToken)`) that returns `null` on any thrown ensure/construct failure. Test: with a fake `IHttpClientFactory` whose `EnsureSpeakerEmbeddingAsync` path throws, the helper returns `null` (not throws) when `EnableMeetingDiarization=true`. If extraction is declined, document that level-1 plus code review is the guard and that the catch wraps ONLY the speaker part (Silero failure still propagates).
  - Existing seam edits (folded into P6): `createTranscription` literal `:349` → 3-tuple `("silero.onnx", transcriptionEngine, null)`; `engineServiceFactory` lambda `:360` → `(_,_,_,_,_,_) =>` (6 discards, was 5). `EngineBuilt` (`:92`) holds.
- **Converter, Phase 8** `tests/.../Converters/SpeakerToDisplayNameConverterTests.cs` (**modify**): the existing tests call `Convert(object[])` with **2-element** arrays (lines 14-47) and currently compile fine; under the new `Length >= 3` contract they fail at runtime (return `string.Empty`). **Widen each input array from 2 to 3 elements** (`{ Speaker, SpeakerLabel, CounterpartName }`); after widening, existing assertions hold (`You→"you"`; `Them + null/blank SpeakerLabel + "Alex" → "Alex"`; `Them + null/blank both → "them"`). **Add** a label-wins case (`Them + SpeakerLabel "Speaker 2" + any CounterpartName → "Speaker 2"`) and a precedence case (`SpeakerLabel` beats `CounterpartName`).
- **VM, Phase 9** `tests/.../ViewModels/MeetingAttendeeViewModelTests.cs` (**modify**): existing null-label merge tests (`:132-155`, all `Them`) must still pass (null label → ColorIndex 0, same-label merge holds). **Add (NET-NEW headline test — the migration's core correctness gate):** two `Them` utterances with **different** `SpeakerLabel` (`"Speaker 1"`/`"Speaker 2"`) **within** the 25 s window → **TWO** bubbles. This fails against the current Speaker-only key and passes only after the Phase-9 merge-key change. **Add the fragmentation-shape regression** (the symptom risk #1 describes): a `Them`/`"Speaker 1"` utterance, then a `Them`/`null` (sub-1.5 s) utterance, then `Them`/`"Speaker 1"` again, all in-window → **THREE** bubbles (the null segment splits the run), pinning the shipped SPLIT behavior so a future "absorb-null" change (§8 decision 5) is a deliberate, tested diff. Add a `BuildMarkdown` assertion that distinct labels render distinct `**Speaker N**` headings (not one counterpart name). *(The POC `ListeningBubble_AdoptsLabel` test is intentionally NOT adapted — the attendee has no listening-dot producer; see §4 verified note.)*
- **Rename, Phase 10** `tests/.../ViewModels/MeetingAttendeeViewModelTests.cs` (**modify, additive**) + the ctor ripple: the existing `new MeetingAttendeeViewModel(...)` at `:216` gains an `IDialogService` arg → `Substitute.For<IDialogService>()` (pattern: `AssistantHistoryViewModelFilterTests.cs:17`); the nested `FakeMeetingAttendeeService` (`:223-261`) implements `RenameSpeaker` (record the `(old,new)` call). NET-NEW cases: (a) rename relabels existing in-window bubbles (`SpeakerLabel "Speaker 2" → "Marco"` updates all matching bubbles); (b) **palette carry-over** — the renamed label keeps its `ColorIndex`; (c) `CanRenameSpeakerLabel` is `false` on null/blank; (d) the input dialog returning null/blank or the unchanged name is a no-op (no `RenameSpeaker` call — stub `IDialogService.ShowInputDialogAsync`); (e) `RenameSpeaker` passthrough on `MeetingAttendeeService` is a safe no-op when `_speakerId == null`.
- **Bubble model, Phase 3** `tests/.../Models/TranscriptBubbleTests.cs` (**modify, additive**): existing 4 `Append` tests unchanged (the new ctor param is optional); add that the 4-arg ctor sets `SpeakerLabel` and that `SpeakerLabel`/`ColorIndex` are observable/mutable.
- **Engine drain** `tests/.../Services/LiveTranscription/LiveTranscriptionEngineDrainTests.cs` (**reference-only**): constructs the engine with 6 positional args + positional `TranscriptUtterance` — both keep compiling because the new params are trailing/optional. Verify no positional break after Phases 3–4.

---

## 8. Open decisions for the user

> **All 7 decisions resolved 2026-06-24 (Marco):**
> - **#1 Consent scope → OUT** (follow-up) — remains valid; nothing is persisted.
> - **#2 Rename UI → IN SCOPE, within-meeting only** (§5 Phase 10). Identities/renames carry through a meeting and are discarded at its end (fresh service per meeting); **no** cross-meeting persistence and **no** consent/ring-buffer/blocklist apparatus.
> - **#3 Diarization default → ON** (`EnableMeetingDiarization = true`, auto-download, degrade-to-null on failure).
> - **#4 Match threshold → `0.70f`, backing property only** (no settings UI now).
> - **#5 Null-label mid-run → SPLIT** (ship as-is), pinned by the Phase-9 regression test.
> - **#6 Visual shift → ACCEPTED** (slot-0 `SpeakerBubbleBackground1Brush`, matches POC).
> - **#7 CounterpartName → KEEP as-is** (user-editable pre-diarization fallback; superseded at display by "Speaker N"/rename when present).
>
> A persistent cross-meeting speaker memory was raised and **declined** in favour of within-meeting-only (see §2 OUT). Implementation is **not** scheduled — the plan is held as the deliverable.

1. **Consent scope** — ✅ **CONFIRMED: OUT.** Ship diarization behind the existing consent checkbox only, with the §2 PRIVACY NOTE follow-up logged. (A first-class biometric-consent surface is deferred to a future follow-up, not this migration.)
2. **Per-speaker rename UI** — ✅ **CONFIRMED: IN SCOPE, within-meeting only** (§5 Phase 10). Right-click / click-to-edit a "Them" label ("Speaker 2" → "Marco"): updates the live diarizer label map (`ISpeakerIdentificationService.Rename`, now live), re-keys the color slot, and retroactively relabels existing bubbles. **Session-scoped** — discarded at meeting end (fresh service per meeting). **No** cross-meeting persistence (that + a first-class biometric-consent surface is a deferred follow-up — see §2 OUT).
3. **Model auto-download vs gated** — ✅ **CONFIRMED: ON by default.** `EnableMeetingDiarization = true`, silent ~27 MB download on first diarized meeting (mirrors `EnsureSileroVadAsync`, no progress UI). A failed download **degrades to single-bubble behavior, not an error** (§5 P6). *(Still open within this decision: whether a download-progress dialog is wanted — default is none, matching `EnsureSileroVadAsync`.)*
4. **Match-threshold default** — ✅ **CONFIRMED: `0.70f`, backing property only (no settings UI).** Tunable via `SpeakerEmbeddingThreshold`; it is the primary knob against the label-stability risk (§6 #1), but meaningful tuning needs real-audio measurement first, so a settings UI is deferred.
5. **Null-label mid-run behavior** — ✅ **CONFIRMED: SPLIT (ship as-is).** A sub-1.5 s null-label segment mid-run creates a one-off uncolored bubble that splits the colored run (§4 consequence 2, §6 risk #4); pinned by the Phase-9 regression test so a future ABSORB is a deliberate, tested diff. ABSORB not pulled into scope.
6. **Intentional visual shift** — ✅ **CONFIRMED: ACCEPTED.** Undiarized/pre-diarization `Them` bubbles move from `ControlFillColorSecondaryBrush` → slot-0 `SpeakerBubbleBackground1Brush` (matches the POC).
7. **`CounterpartName` role** — ✅ **CONFIRMED: KEEP as-is.** Stays the user-editable pre-diarization fallback in the 3-value converter (`LastCounterpartName` persistence stays); superseded at display by "Speaker N"/rename once a label exists.
