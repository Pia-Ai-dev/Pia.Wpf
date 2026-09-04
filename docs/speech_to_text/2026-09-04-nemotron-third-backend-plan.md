# Nemotron-3.5 streaming STT as a third backend — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Status:** Tasks 1-9 built on `feature/nemo_stt`. G1 passed 2026-09-04
([2026-09-04-nemotron-german-comparison.md](2026-09-04-nemotron-german-comparison.md)); Task 10 is
partly done and its remainder needs a human at the machine. Track it in the checklist below.
**Owner:** Marco Altmann
**Written:** 2026-09-04
**Origin:** Owner decision on 2026-09-04, taken against
[2026-08-30-nemotron-streaming-plan.md](2026-08-30-nemotron-streaming-plan.md). That plan left open
whether nemotron would *replace* Parakeet TDT v3 (its gate G2) and whether the streaming UX was in
scope. Both are now settled: nemotron is an **additional** third backend — Whisper and Parakeet both
stay, unchanged and selectable — and the streaming partial-text UX **is** in scope. The 560 ms
variant is the pinned chunk size.

**Tracking surface:**
[2026-09-04-nemotron-third-backend-checklist.md](2026-09-04-nemotron-third-backend-checklist.md).

**Goal:** Add `nvidia/nemotron-3.5-asr-streaming-0.6b` as a third selectable speech-to-text backend
beside Whisper and Parakeet, and use its cache-aware streaming to show transcript text that grows
while someone is still speaking.

**Architecture:** A new `NemotronStreamingEngine` wraps sherpa-onnx's `OnlineRecognizer` and
implements *both* the existing `ITranscriptionEngine` (decode a whole VAD segment, return final
text — so every current consumer keeps working untouched) and a new `IStreamingTranscriptionEngine`
(feed audio as it arrives, read back a partial hypothesis). `LiveTranscriptionEngineService` keeps
Silero VAD as the sole owner of segmentation and additionally pumps raw audio into the streaming
interface when the engine offers one, publishing partials on an event that is **separate** from the
utterance channel. `TranscriptOverlayViewModel` renders partials as transient draft text that never
enters its journal.

**Tech Stack:** .NET 10 / WPF, `org.k2fsa.sherpa.onnx` 1.13.5 (already pinned), CommunityToolkit.Mvvm,
xunit.v3.

**Spec:** [2026-08-30-nemotron-streaming-plan.md](2026-08-30-nemotron-streaming-plan.md) — read its
"What it actually is, measured" and "The sherpa API it needs" sections; the measurements there
(bundle contents, five chunk variants, per-stream `language` option, the CPU-provider
implementation-selection trap) are not repeated in full here.

---

## Global Constraints

Copied from `CLAUDE.md`; every task's requirements implicitly include this section.

- **Test gate:** `dotnet test` with **no filter**. The bar is `failed: 0`.
- **Zero-warning policy:** `0 Warning(s)` and `0 Error(s)` in **both** Debug and Release, verified
  with a **rebuild**: `dotnet build -t:Rebuild -v:n` and again with `-c Release`. Read the count off
  MSBuild's `N Warning(s)` summary line.
- **Namespaces use `Pia`** (not `Pia.Wpf`).
- **Code style:** 4-space indent C#, 2-space XAML. Fields `_camelCase`. `var` for apparent types.
  MVVM: logic in ViewModels, `[ObservableProperty]` / `[RelayCommand]`.
- **Comment discipline:** default to no comment. A surviving comment or `<summary>` gets **one short
  line**, only when the WHY is non-obvious. **Never cite this plan, a task number, or a step number
  in code.**
- **Privacy-first logging:** transcript text — partial or final — is user content. It may only be
  logged through `_logger.SensitiveDebug(...)`, never `LogInformation`/`LogDebug`. Lengths and
  counts are safe; text is not. Model file paths under `%LOCALAPPDATA%` are fine (the existing
  engines log them).
- **Data paths:** never `Environment.GetFolderPath`. Go through `Pia.Paths.PiaPaths`.
- **AutomationIds:** every new interactive control in a `UserControl` needs
  `AutomationProperties.AutomationId="<ViewPrefix>_<Field>"`, and the matching count in
  `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` updated in the same change.
- **Localization:** every new UI string needs a key in **all three** of `ViewStrings.resx`,
  `ViewStrings.de.resx`, `ViewStrings.fr.resx`.
  `tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs` enforces this.
- **Release notes:** `docs/release_notes/RELEASE.md` must actually change before this ships. Format
  rules in `docs/release_notes/README.md` (hard-wrap 80, one bullet level, no tables, four lines per
  bullet).

## Non-negotiable facts about this model and this sherpa version

Verified 2026-09-04 by reflecting over `sherpa-onnx.dll` 1.13.5 and reading `sherpa-onnx/csrc` at tag
`v1.13.5`. Getting any of these wrong costs a debugging session or a crash.

1. **A non-greedy decoding method kills the process.** Both streaming NeMo implementations
   (`online-recognizer-transducer-nemo-impl.h` and
   `online-recognizer-transducer-nemo-parakeet-unified-impl.h`) accept `greedy_search` and nothing
   else; the `else` branch is `SHERPA_ONNX_LOGE(...)` followed by `SHERPA_ONNX_EXIT(-1)`. That is a
   process kill from native code — **no .NET exception is thrown and nothing can catch it.** Pia
   would vanish. `OnlineRecognizerConfig.DecodingMethod` already defaults to `"greedy_search"`; set
   it explicitly anyway and lock it with the unit test in Task 3.
2. **There is no hotword / custom-vocabulary support on this path.** Neither streaming impl has
   `hotwords_graph_`, `ContextGraph`, `InitHotwords`, or a `CreateStream(hotwords)` overload.
   `OnlineRecognizerConfig` exposes `HotwordsFile` and `HotwordsScore`, but setting them on a NeMo
   streaming model does nothing — they are inert fields on a shared config struct, and the only way
   to activate them is `modified_beam_search`, which is fact 1. Do not wire them up, and do not
   promise them in the UI or the release notes. (Offline Parakeet TDT *does* support them, which is
   one reason Parakeet stays rather than being replaced.)
3. **Silero VAD keeps segmentation.** `EnableEndpoint` stays `0`. Sherpa's endpointer would compete
   with `SileroVadDetector`, which already owns segment boundaries and drives diarization,
   `IsSpeakingChanged`, and the echo gate.
4. **On the CPU provider, sherpa picks the implementation by inspecting the decoder ONNX**, not from
   `ModelConfig.ModelType`. Setting `ModelType = "nemo_transducer"` is harmless and load-bearing only
   for the QNN provider. Do not spend time debugging it.
5. **Chunk size is baked into the export.** The 560 ms variant is the pin. There is no runtime knob.

## File structure

| File | Responsibility | Task |
|---|---|---|
| `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionModels.cs` | URL constant, `EnsureNemotronOnnxAsync`, `IsNemotronOnnxAvailable` | 1 |
| `src/Pia.Wpf/Services/Assets/RuntimeAsset.cs` | catalogue entry + `All` | 1 |
| `scripts/RuntimeAssetCatalogue.ps1` | mirror upload entry | 1 |
| `src/Pia.Wpf/Models/AppSettings.cs` | `SttBackend.Nemotron` | 2 |
| `src/Pia.Wpf/Converters/EnumToLocalizedStringConverter.cs` | enum to resource key | 2 |
| `src/Pia.Wpf/Services/VoiceInputService.cs` | availability switch | 2 |
| `src/Pia.Wpf/Services/LiveTranscription/NemotronStreamingEngine.cs` | **new** — the recognizer wrapper | 3, 6 |
| `src/Pia.Wpf/Services/LiveTranscription/TranscriptionEngineFactory.cs` | construct + ensure-models | 3 |
| `src/Pia.Wpf/Services/Interfaces/ITranscriptionService.cs` | interface member | 4 |
| `src/Pia.Wpf/Services/TranscriptionService.cs` | `DownloadNemotronModelAsync` | 4 |
| `src/Pia.Wpf/ViewModels/GeneralSettingsViewModel.cs` | `IsNemotronSelected`, download command | 4 |
| `src/Pia.Wpf/Views/SettingsViews/GeneralView.xaml` | backend panel | 4 |
| `src/Pia.Wpf/Services/LiveTranscription/IStreamingTranscriptionEngine.cs` | **new** — partial-hypothesis contract | 6 |
| `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs` | partial pump | 7 |
| `src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs` | `PartialText`, journal-safe | 8 |
| `src/Pia.Wpf/Views/DirectTranscriptionOverlay.xaml` | draft-text rendering | 9 |
| `src/Pia.Wpf/Views/MeetingAttendeeOverlay.xaml` | draft-text rendering | 9 |
| `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | UI strings | 2, 4 |

## Phase split

**Phase 1 (Tasks 1–5)** delivers nemotron as a third selectable backend with today's UX. It is
shippable on its own and touches nothing Whisper or Parakeet rely on.

**Phase 2 (Tasks 6–10)** delivers the streaming partial-text UX. **Task 5 is a decision gate**: if
nemotron loses to Parakeet TDT v3 on German badly enough that nobody would pick it, stop after
Task 4 and leave it as an option rather than building the interface work on top of a model that
will not be used.

---

## Task 1: Pin the nemotron bundle in every place a model URL lives

A model URL is pinned in four places on purpose, and `RuntimeAssetCatalogTests` compares them.
Adding one in three of the four leaves a test red or, worse, a silent mirror miss.

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionModels.cs`
- Modify: `src/Pia.Wpf/Services/Assets/RuntimeAsset.cs:33` (add property) and the `All` list
- Modify: `scripts/RuntimeAssetCatalogue.ps1` (after the Parakeet entry, ~line 93)
- Test: `tests/Pia.Wpf.Tests/Services/LiveTranscription/ModelDownloadUrlTests.cs`

**Interfaces:**
- Produces: `LiveTranscriptionModels.NemotronBundleUrl` (`internal static string`),
  `LiveTranscriptionModels.EnsureNemotronOnnxAsync(IAssetDownloader, IProgress<ModelDownloadProgress>?, ILogger, CancellationToken)`
  → `Task<string>` (the model directory), `LiveTranscriptionModels.IsNemotronOnnxAvailable()` → `bool`,
  `RuntimeAssetCatalog.Nemotron` (`RuntimeAsset`).
- Model directory name: `sherpa-nemotron-3.5-streaming-560ms`.

- [ ] **Step 1: Write the failing URL pin test**

Add to `tests/Pia.Wpf.Tests/Services/LiveTranscription/ModelDownloadUrlTests.cs`, right after
`ParakeetBundleUrl_is_pinned`:

```csharp
    /// <summary>The 560 ms variant. Chunk size is baked into the export, so the number is the model.</summary>
    [Fact]
    public void NemotronBundleUrl_is_pinned()
    {
        Assert.Equal(
            $"{SherpaAsr}/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11.tar.bz2",
            LiveTranscriptionModels.NemotronBundleUrl);
    }
```

- [ ] **Step 2: Run it and watch it fail to compile**

```bash
dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj
```

Expected: `CS0117: 'LiveTranscriptionModels' does not contain a definition for 'NemotronBundleUrl'`.

- [ ] **Step 3: Add the URL constant and the bundle helpers**

In `LiveTranscriptionModels.cs`, beside `ParakeetBundleUrl`:

```csharp
    internal static string NemotronBundleUrl =>
        $"{SherpaReleasesBase}/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11.tar.bz2";
```

Beside `IsParakeetOnnxAvailable` (~line 119):

```csharp
    public static bool IsNemotronOnnxAvailable()
    {
        var dir = Path.Combine(ModelsDirectory, NemotronDirectoryName);
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.onnx").Any();
    }

    /// <summary>
    /// Downloads (if missing) and extracts the sherpa-onnx nemotron-3.5 streaming bundle into
    /// <c>%LOCALAPPDATA%\Pia\Models\sherpa-nemotron-3.5-streaming-560ms\</c>. Returns the directory.
    /// </summary>
    public static Task<string> EnsureNemotronOnnxAsync(
        IAssetDownloader downloader,
        IProgress<ModelDownloadProgress>? progress,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var targetDir = Path.Combine(ModelsDirectory, NemotronDirectoryName);
        return EnsureBundleAsync(
            RuntimeAssetCatalog.Nemotron, targetDir, downloader, progress, logger, cancellationToken);
    }
```

And a private constant near the top of the class, beside `SileroVadFileName`:

```csharp
    // The chunk size is part of the directory name: switching variants must not silently reuse the
    // previous variant's extracted files.
    private const string NemotronDirectoryName = "sherpa-nemotron-3.5-streaming-560ms";
```

- [ ] **Step 4: Add the catalogue entry**

In `src/Pia.Wpf/Services/Assets/RuntimeAsset.cs`, after the `Parakeet` property:

```csharp
    public static RuntimeAsset Nemotron { get; } = Bundle(LiveTranscriptionModels.NemotronBundleUrl);
```

and add `Nemotron,` to `All` immediately after `Parakeet,`.

- [ ] **Step 5: Add the publishing-script entry**

In `scripts/RuntimeAssetCatalogue.ps1`, immediately after the Parakeet entry (~line 94), matching the
surrounding style exactly:

```powershell
            @{ Kind = 'Bundle'; Name = 'Nemotron 3.5 Streaming 560ms'
               Url = "$script:SherpaAsr/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11.tar.bz2"
               MirrorKey = 'models/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11.tar.bz2'
```

Copy the remaining keys of the Parakeet entry (whatever follows `MirrorKey` on lines 95–96) verbatim
into this entry — read them before writing, do not guess.

- [ ] **Step 6: Run the pinning tests**

```bash
dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.ModelDownloadUrlTests"
dotnet test --filter-class "Pia.Tests.Services.Assets.RuntimeAssetCatalogTests"
```

Expected: PASS. `RuntimeAssetCatalogTests` passing is the real signal — it proves the client list, the
upstream URLs, and the script agree.

- [ ] **Step 7: Verify the URL actually resolves**

The pin tests are offline by construction, so they cannot catch a 404. Check once, by hand:

```bash
curl -sIL -o /dev/null -w '%{http_code}\n' \
  "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11.tar.bz2"
```

Expected: `200`. A `404` means the release asset name drifted — fix the constant and the test
together, and do not proceed.

- [ ] **Step 8: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionModels.cs \
        src/Pia.Wpf/Services/Assets/RuntimeAsset.cs \
        scripts/RuntimeAssetCatalogue.ps1 \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/ModelDownloadUrlTests.cs
git commit -m "feat: pin the nemotron-3.5 streaming 560ms bundle"
```

---

## Task 2: Add the `Nemotron` backend value and route every switch site

**Eight** sites read `SttBackend`, and five of them are two-way `== SttBackend.Parakeet` ternaries
that treat "not Parakeet" as "Whisper". None of them is a compile error on a third value — every one
is silently wrong behaviour: the wrong model, the wrong download, the wrong name in a dialog, the
wrong id in saved meeting metadata. Enumerate them before you start:

```bash
grep -rn "SttBackend\." src/Pia.Wpf --include=*.cs | grep -v "/obj/\|/bin/"
```

Expected, as of 2026-09-04:

| Site | Shape | Handled by |
|---|---|---|
| `Converters/EnumToLocalizedStringConverter.cs:20-21` | switch arm | Step 4 |
| `Models/AppSettings.cs:54` | default value — **stays `Parakeet`** | not changed |
| `Services/LiveTranscription/TranscriptionEngineFactory.cs:23,30` | `switch` | Task 3, Step 5 |
| `Services/LiveTranscription/TranscriptionEngineFactory.cs:51` | ternary | Task 3, Step 5 |
| `Services/LiveTranscription/DirectTranscriptionService.cs:950` | ternary | Step 7 |
| `Services/VoiceInputService.cs:189` | switch arm | Step 6 |
| `Services/VoiceInputService.cs:194` | ternary | Step 6 |
| `Services/VoiceInputService.cs:204` | ternary | Step 6 |
| `ViewModels/GeneralSettingsViewModel.cs:144-145` | `Is…Selected` | Task 4, Step 3 |

**Files:**
- Modify: `src/Pia.Wpf/Models/AppSettings.cs:19-23`
- Modify: `src/Pia.Wpf/Converters/EnumToLocalizedStringConverter.cs:20-21`
- Modify: `src/Pia.Wpf/Services/VoiceInputService.cs:189`, `:194`, `:204`
- Modify: `src/Pia.Wpf/Services/LiveTranscription/DirectTranscriptionService.cs:950`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `.de.resx`, `.fr.resx`
- Test: `tests/Pia.Wpf.Tests/Services/LiveTranscription/SttBackendRoutingTests.cs` (**new**)

**Interfaces:**
- Consumes: `LiveTranscriptionModels.IsNemotronOnnxAvailable()` from Task 1.
- Produces: `SttBackend.Nemotron` — appended **last**, so existing persisted settings keep their
  ordinal values.

- [ ] **Step 1: Write the failing routing test**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/SttBackendRoutingTests.cs`:

```csharp
using Pia.Converters;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Every backend must be routed explicitly. The engine and availability switches fall through to
/// Whisper, so a missing arm is a silent wrong-model bug rather than a compile error.
/// </summary>
public class SttBackendRoutingTests
{
    [Fact]
    public void Nemotron_is_appended_last_so_persisted_settings_keep_their_values()
    {
        Assert.Equal(0, (int)SttBackend.Whisper);
        Assert.Equal(1, (int)SttBackend.Parakeet);
        Assert.Equal(2, (int)SttBackend.Nemotron);
    }

    [Fact]
    public void Every_backend_has_a_distinct_localized_display_name()
    {
        var converter = new EnumToLocalizedStringConverter();
        var names = Enum.GetValues<SttBackend>()
            .Select(b => (string)converter.Convert(b, typeof(string), null!, null!))
            .ToList();

        // A missing arm falls through to value.ToString(), i.e. the bare enum name.
        Assert.DoesNotContain(names, n => string.IsNullOrWhiteSpace(n));
        Assert.DoesNotContain("Nemotron", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }
}
```

`EnumToLocalizedStringConverter.Convert` reads `LocalizationSource.Instance`. Run this test first: if
that needs a live WPF `Application` and throws headless, drop the second test and instead assert that
each of the three resx files contains `Enum_SttNemotron`, plus that the converter's `switch` has an
arm — a `grep`-shaped assertion is worth less, so prefer the runtime one if it works.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj
```

Expected: `CS0117: 'SttBackend' does not contain a definition for 'Nemotron'`.

- [ ] **Step 3: Add the enum value**

`src/Pia.Wpf/Models/AppSettings.cs`:

```csharp
public enum SttBackend
{
    Whisper,
    Parakeet,
    Nemotron
}
```

- [ ] **Step 4: Add the converter arm**

`src/Pia.Wpf/Converters/EnumToLocalizedStringConverter.cs`, after the `SttBackend.Parakeet` line:

```csharp
            SttBackend.Nemotron => "Enum_SttNemotron",
```

- [ ] **Step 5: Add the enum display string to all three resx files**

In each of `ViewStrings.resx`, `ViewStrings.de.resx`, `ViewStrings.fr.resx`, beside the existing
`Enum_SttParakeet` entry, keeping the surrounding one-line `<data>` format:

```xml
  <data name="Enum_SttNemotron" xml:space="preserve"><value>Nemotron 3.5 Streaming</value></data>
```

The product name is identical in all three languages — add the same value to each file. The
localization guard checks key presence, not translation, and a model's name is not translated.

- [ ] **Step 6: Fix all three `VoiceInputService` sites**

`:189` is a switch; `:194` and `:204` are ternaries that would hand a Nemotron user the Whisper
download and the Whisper name in the progress dialog. Replace all three:

```csharp
        var alreadyDownloaded = settings.SttBackend switch
        {
            SttBackend.Nemotron => LiveTranscriptionModels.IsNemotronOnnxAvailable(),
            SttBackend.Parakeet => LiveTranscriptionModels.IsParakeetOnnxAvailable(),
            _ => LiveTranscriptionModels.IsWhisperOnnxAvailable(settings.WhisperModel),
        };
        if (alreadyDownloaded) return true;

        var modelDisplayName = settings.SttBackend switch
        {
            SttBackend.Nemotron => "Nemotron 3.5 Streaming",
            SttBackend.Parakeet => "Parakeet TDT v3",
            _ => TranscriptionService.GetModelName(settings.WhisperModel),
        };
```

and, inside the `try`:

```csharp
            var downloadTask = settings.SttBackend switch
            {
                SttBackend.Nemotron => _transcriptionService.DownloadNemotronModelAsync(progress, userCancelCts.Token),
                SttBackend.Parakeet => _transcriptionService.DownloadParakeetModelAsync(progress, userCancelCts.Token),
                _ => _transcriptionService.DownloadModelAsync(settings.WhisperModel, progress, userCancelCts.Token),
            };
```

`DownloadNemotronModelAsync` does not exist until Task 4, Step 1. Either do that step now or leave
this one arm until then — but do not leave the ternary in place and call the task done.

The display names here are deliberately unlocalized literals, matching the existing `"Parakeet TDT
v3"` beside them. Do not "fix" that in this task.

- [ ] **Step 7: Fix the saved meeting metadata's model id**

`src/Pia.Wpf/Services/LiveTranscription/DirectTranscriptionService.cs:950` stamps every saved meeting
with the engine that produced it. Left as a two-way ternary, a nemotron meeting is recorded as
`whisper-parakeet`-shaped nonsense — a wrong value in persisted data, which no test and no user will
notice until someone audits old transcripts.

Make it `internal` so it is testable, matching `ShouldUseAdaptiveDiarizer` directly above it, and
add the arm:

```csharp
    internal static string ComputeSttModelId(AppSettings settings) => settings.SttBackend switch
    {
        SttBackend.Nemotron => "nemotron-3.5-streaming-560ms",
        SttBackend.Parakeet => "parakeet-tdt-v3",
        _ => $"whisper-{settings.WhisperModel}".ToLowerInvariant(),
    };
```

Add the matching assertion to `SttBackendRoutingTests`:

```csharp
    [Theory]
    [InlineData(SttBackend.Whisper, "whisper-base")]
    [InlineData(SttBackend.Parakeet, "parakeet-tdt-v3")]
    [InlineData(SttBackend.Nemotron, "nemotron-3.5-streaming-560ms")]
    public void Every_backend_stamps_its_own_model_id_on_a_saved_meeting(SttBackend backend, string expected)
    {
        var settings = new AppSettings { SttBackend = backend, WhisperModel = WhisperModelSize.Base };
        Assert.Equal(expected, DirectTranscriptionService.ComputeSttModelId(settings));
    }
```

`AppSettings` may require more than these two properties to construct — read it and set whatever the
compiler demands, but assert only on the two that matter here.

- [ ] **Step 8: Run the tests**

```bash
dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.SttBackendRoutingTests"
dotnet test --filter-class "Pia.Tests.Architecture.LocalizationTests"
```

Expected: PASS both. The localization test is the one that catches a resx key added to only one of
the three files.

- [ ] **Step 9: Commit**

```bash
git add src/Pia.Wpf/Models/AppSettings.cs \
        src/Pia.Wpf/Converters/EnumToLocalizedStringConverter.cs \
        src/Pia.Wpf/Services/VoiceInputService.cs \
        src/Pia.Wpf/Services/LiveTranscription/DirectTranscriptionService.cs \
        src/Pia.Wpf/Resources/Strings/ViewStrings.resx \
        src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx \
        src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/SttBackendRoutingTests.cs
git commit -m "feat: add Nemotron as a third speech-to-text backend value"
```

---

## Task 3: The engine — `NemotronStreamingEngine` as an `ITranscriptionEngine`

Phase 1's deliverable. The engine drives an `OnlineRecognizer` but exposes only the existing
segment-in/text-out contract, so `LiveTranscriptionEngineService`, `TranscriptionService` and the
meeting recorder all work with zero changes.

`ParakeetSherpaEngine.cs` is the template for file resolution, the decode gate and disposal. Read it
first.

**Files:**
- Create: `src/Pia.Wpf/Services/LiveTranscription/NemotronStreamingEngine.cs`
- Modify: `src/Pia.Wpf/Services/LiveTranscription/TranscriptionEngineFactory.cs`
- Test: `tests/Pia.Wpf.Tests/Services/LiveTranscription/NemotronStreamingEngineTests.cs` (**new**)

**Interfaces:**
- Consumes: `LiveTranscriptionModels.EnsureNemotronOnnxAsync` (Task 1), `SttBackend.Nemotron`
  (Task 2).
- Produces: `public sealed class NemotronStreamingEngine : ITranscriptionEngine` with
  `NemotronStreamingEngine(string modelDirectory, string languageCode, ILogger logger)` and
  `internal static OnlineRecognizerConfig BuildConfig(string modelDirectory)` — internal so the
  guard test can assert on it without loading a 453 MiB model.

- [ ] **Step 1: Write the failing config-guard test**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/NemotronStreamingEngineTests.cs`:

```csharp
using System.IO;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Asserts on the config only — constructing the recognizer would need the 453 MiB bundle. The
/// decoding-method assertion is the important one: sherpa's streaming NeMo implementations call
/// exit(-1) on anything but greedy_search, which kills the process with no catchable exception.
/// </summary>
public class NemotronStreamingEngineTests
{
    private static string FakeModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PiaTests_nemotron_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx" })
            File.WriteAllBytes(Path.Combine(dir, name), [0]);
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "<blk> 0\n");
        return dir;
    }

    [Fact]
    public void BuildConfig_uses_greedy_search_because_anything_else_kills_the_process()
    {
        var dir = FakeModelDir();
        try
        {
            Assert.Equal("greedy_search", NemotronStreamingEngine.BuildConfig(dir).DecodingMethod);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BuildConfig_leaves_endpointing_to_silero_vad()
    {
        var dir = FakeModelDir();
        try
        {
            Assert.Equal(0, NemotronStreamingEngine.BuildConfig(dir).EnableEndpoint);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BuildConfig_resolves_the_int8_transducer_files()
    {
        var dir = FakeModelDir();
        try
        {
            var config = NemotronStreamingEngine.BuildConfig(dir);
            Assert.EndsWith("encoder.int8.onnx", config.ModelConfig.Transducer.Encoder);
            Assert.EndsWith("decoder.int8.onnx", config.ModelConfig.Transducer.Decoder);
            Assert.EndsWith("joiner.int8.onnx", config.ModelConfig.Transducer.Joiner);
            Assert.EndsWith("tokens.txt", config.ModelConfig.Tokens);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BuildConfig_does_not_set_hotwords_which_this_model_cannot_honour()
    {
        var dir = FakeModelDir();
        try
        {
            Assert.True(string.IsNullOrEmpty(NemotronStreamingEngine.BuildConfig(dir).HotwordsFile));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj
```

Expected: `CS0246: The type or namespace name 'NemotronStreamingEngine' could not be found`.

- [ ] **Step 3: Write the engine**

Create `src/Pia.Wpf/Services/LiveTranscription/NemotronStreamingEngine.cs`:

```csharp
using System.IO;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// sherpa-onnx OnlineRecognizer wrapper for NVIDIA nemotron-3.5 streaming (560 ms chunks).
/// Cache-aware streaming transducer; language is chosen per stream rather than baked into the config.
/// </summary>
public sealed class NemotronStreamingEngine : ITranscriptionEngine
{
    private const int SampleRate = 16000;

    private readonly OnlineRecognizer _recognizer;
    private readonly string _languageCode;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);

    public NemotronStreamingEngine(string modelDirectory, string languageCode, ILogger logger)
    {
        _languageCode = string.IsNullOrWhiteSpace(languageCode) ? "auto" : languageCode;
        _logger = logger;

        var config = BuildConfig(modelDirectory);

        _logger.LogInformation(
            "Nemotron sherpa-onnx engine init: dir='{Dir}' language={Language}",
            modelDirectory, _languageCode);

        _recognizer = new OnlineRecognizer(config);
    }

    internal static OnlineRecognizerConfig BuildConfig(string modelDirectory)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = ResolveTransducerFile(modelDirectory, "encoder");
        config.ModelConfig.Transducer.Decoder = ResolveTransducerFile(modelDirectory, "decoder");
        config.ModelConfig.Transducer.Joiner = ResolveTransducerFile(modelDirectory, "joiner");
        config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        // sherpa's streaming NeMo implementations exit(-1) on any other method — a process kill from
        // native code, not a catchable exception.
        config.DecodingMethod = "greedy_search";

        // SileroVadDetector owns segment boundaries; sherpa's endpointer would compete with it.
        config.EnableEndpoint = 0;

        return config;
    }

    public async Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The mic and loopback services share one recognizer and decode on their own threads.
        await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var stream = _recognizer.CreateStream();
                stream.SetOption("language", _languageCode);
                stream.AcceptWaveform(SampleRate, samples16kMono);
                stream.InputFinished();
                while (_recognizer.IsReady(stream)) _recognizer.Decode(stream);
                return _recognizer.GetResult(stream).Text?.Trim() ?? string.Empty;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private static string ResolveTransducerFile(string dir, string role)
    {
        var direct = Path.Combine(dir, $"{role}.onnx");
        if (File.Exists(direct)) return direct;

        var match = Directory.EnumerateFiles(dir, $"{role}*.onnx").FirstOrDefault()
                    ?? Directory.EnumerateFiles(dir, $"*{role}*.onnx").FirstOrDefault();
        if (match is not null) return match;

        throw new FileNotFoundException(
            $"Nemotron sherpa-onnx model file '{role}' not found in '{dir}'. Re-download the model.");
    }

    public ValueTask DisposeAsync()
    {
        _recognizer.Dispose();
        _decodeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.NemotronStreamingEngineTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Route the factory**

In `TranscriptionEngineFactory.CreateAsync`, a new case before `case SttBackend.Whisper:`:

```csharp
            case SttBackend.Nemotron:
            {
                var dir = await LiveTranscriptionModels
                    .EnsureNemotronOnnxAsync(downloader, downloadProgress, logger, cancellationToken)
                    .ConfigureAwait(false);
                return new NemotronStreamingEngine(dir, LanguageCode(settings.TargetSpeechLanguage), logger);
            }
```

Replace the two-way ternary in `EnsureModelsAsync` with a switch, because a third value makes the
ternary silently wrong:

```csharp
    public static Task<string> EnsureModelsAsync(
        AppSettings settings,
        IAssetDownloader downloader,
        IProgress<ModelDownloadProgress>? downloadProgress,
        ILogger logger,
        CancellationToken cancellationToken)
        => settings.SttBackend switch
        {
            SttBackend.Parakeet => LiveTranscriptionModels
                .EnsureParakeetOnnxAsync(downloader, downloadProgress, logger, cancellationToken),
            SttBackend.Nemotron => LiveTranscriptionModels
                .EnsureNemotronOnnxAsync(downloader, downloadProgress, logger, cancellationToken),
            _ => LiveTranscriptionModels
                .EnsureWhisperOnnxAsync(settings.WhisperModel, downloader, downloadProgress, logger, cancellationToken),
        };
```

- [ ] **Step 6: Run the full gate and a rebuild**

```bash
dotnet test
dotnet build -t:Rebuild -v:n
dotnet build -t:Rebuild -v:n -c Release
```

Expected: `failed: 0`, and `0 Warning(s)` on both rebuild summary lines.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/NemotronStreamingEngine.cs \
        src/Pia.Wpf/Services/LiveTranscription/TranscriptionEngineFactory.cs \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/NemotronStreamingEngineTests.cs
git commit -m "feat: add the nemotron streaming engine behind the existing engine contract"
```

---

## Task 4: Settings UI — pick it and download it

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/ITranscriptionService.cs`
- Modify: `src/Pia.Wpf/Services/TranscriptionService.cs:81`
- Modify: `src/Pia.Wpf/ViewModels/GeneralSettingsViewModel.cs:145`, `:199`, `:372`
- Modify: `src/Pia.Wpf/Views/SettingsViews/GeneralView.xaml:351` (after the Parakeet panel)
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx`
- Test: `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs:43`

**Interfaces:**
- Consumes: `LiveTranscriptionModels.EnsureNemotronOnnxAsync` (Task 1), `SttBackend.Nemotron`
  (Task 2).
- Produces: `ITranscriptionService.DownloadNemotronModelAsync(IProgress<ModelDownloadProgress>, CancellationToken)`,
  `GeneralSettingsViewModel.IsNemotronSelected` (`bool`), `DownloadNemotronModelCommand`.

- [ ] **Step 1: Add the interface member and its implementation**

`ITranscriptionService.cs`, after `DownloadParakeetModelAsync`:

```csharp
    Task DownloadNemotronModelAsync(IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken = default);
```

`TranscriptionService.cs`, after `DownloadParakeetModelAsync` (~line 86):

```csharp
    public async Task DownloadNemotronModelAsync(IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken = default)
    {
        await LiveTranscriptionModels
            .EnsureNemotronOnnxAsync(_downloader, progress, _logger, cancellationToken)
            .ConfigureAwait(false);
    }
```

- [ ] **Step 2: Build and fix every test fake that implements `ITranscriptionService`**

```bash
dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj
```

Expected: `CS0535` on each fake. Add the member to each, mirroring how that fake implements
`DownloadParakeetModelAsync` — usually `=> Task.CompletedTask;`. Do not guess; read each fake.

- [ ] **Step 3: Add the ViewModel surface**

`GeneralSettingsViewModel.cs`, beside `IsParakeetSelected` (~line 145):

```csharp
    public bool IsNemotronSelected => SttBackend == SttBackend.Nemotron;
```

In `OnSttBackendChanged` (~line 199), beside the existing two:

```csharp
        OnPropertyChanged(nameof(IsNemotronSelected));
```

Beside `DownloadParakeetModelAsync` (~line 372), copying the `[RelayCommand]` attribute form from the
Parakeet command above it — read it rather than assuming, since the attribute may carry arguments:

```csharp
    [RelayCommand]
    private async Task DownloadNemotronModelAsync()
    {
        await DownloadModelInternalAsync(
            _localizationService["Settings_Nemotron_DisplayName"],
            (progress, ct) => _transcriptionService.DownloadNemotronModelAsync(progress, ct));
    }
```

- [ ] **Step 4: Add the settings panel**

`GeneralView.xaml`, immediately after the Parakeet `StackPanel` closes (~line 366), copying that
panel's structure, margins and styles verbatim and changing only the bindings and keys:

```xml
            <!-- Nemotron (visible when Nemotron backend is selected) -->
            <StackPanel Visibility="{Binding IsNemotronSelected, Converter={StaticResource BooleanToVisibilityConverter}}">
              <TextBlock Text="{loc:Str Settings_Nemotron_DisplayName}" />
              <TextBlock Text="{loc:Str Settings_Nemotron_Description}" />
              <ui:Button Command="{Binding DownloadNemotronModelCommand}"
                         AutomationProperties.AutomationId="Settings_General_DownloadNemotronModel" />
            </StackPanel>
```

- [ ] **Step 5: Add the two settings strings to all three resx files**

English (`ViewStrings.resx`):

```xml
  <data name="Settings_Nemotron_DisplayName" xml:space="preserve"><value>Nemotron 3.5 Streaming (multilingual)</value></data>
  <data name="Settings_Nemotron_Description" xml:space="preserve"><value>Streaming recognition in 36 languages. Text appears while you are still speaking. Around 453 MB.</value></data>
```

German (`ViewStrings.de.resx`):

```xml
  <data name="Settings_Nemotron_DisplayName" xml:space="preserve"><value>Nemotron 3.5 Streaming (mehrsprachig)</value></data>
  <data name="Settings_Nemotron_Description" xml:space="preserve"><value>Streaming-Erkennung in 36 Sprachen. Der Text erscheint schon beim Sprechen. Etwa 453 MB.</value></data>
```

French (`ViewStrings.fr.resx`):

```xml
  <data name="Settings_Nemotron_DisplayName" xml:space="preserve"><value>Nemotron 3.5 Streaming (multilingue)</value></data>
  <data name="Settings_Nemotron_Description" xml:space="preserve"><value>Reconnaissance en continu dans 36 langues. Le texte apparaît pendant que vous parlez. Environ 453 Mo.</value></data>
```

The "text appears while you are still speaking" sentence describes Phase 2. If Task 5 cancels
Phase 2, come back and cut that sentence from all three files — a settings description promising a
UX the build does not have is worse than a vague one.

- [ ] **Step 6: Bump the automation-id count**

`tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs:43` — one new interactive control:

```csharp
    [InlineData(typeof(Pia.Views.SettingsViews.GeneralView), 26, 4, "")]
```

- [ ] **Step 7: Run the gate and a rebuild**

```bash
dotnet test
dotnet build -t:Rebuild -v:n && dotnet build -t:Rebuild -v:n -c Release
```

Expected: `failed: 0`; `0 Warning(s)` both configurations. If `ViewAutomationIdTests` reports a count
other than 26, take the number from the failure message rather than arguing with it — the walker
counts what it counts.

- [ ] **Step 8: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/ITranscriptionService.cs \
        src/Pia.Wpf/Services/TranscriptionService.cs \
        src/Pia.Wpf/ViewModels/GeneralSettingsViewModel.cs \
        src/Pia.Wpf/Views/SettingsViews/GeneralView.xaml \
        src/Pia.Wpf/Resources/Strings/ViewStrings.resx \
        src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx \
        src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx \
        tests/Pia.Wpf.Tests
git commit -m "feat: let users select and download the nemotron backend"
```

---

## Task 5: Decision gate — is nemotron good enough on German to build the UX on?

Not a code task. It is the gate that decides whether Phase 2 happens, and it is cheap compared to
Tasks 6–10.

**Files:**
- Create: `docs/speech_to_text/2026-09-04-nemotron-german-comparison.md`
- Modify: `docs/speech_to_text/2026-09-04-nemotron-third-backend-checklist.md` (record the verdict)

- [ ] **Step 1: Transcribe the same German audio with all three backends**

Launch Pia, and for each of `SttBackend.Whisper` (medium), `SttBackend.Parakeet`,
`SttBackend.Nemotron`, transcribe the same fixtures under `artifacts/wav/`. Save each transcript and
the wall-clock decode time per segment.

- [ ] **Step 2: Note punctuation and casing for nemotron**

The 2026-08-30 plan flagged this as unverified. Parakeet TDT v3 punctuates and capitalises, and the
downstream Summarize prompt benefits. Write down explicitly whether nemotron does.

- [ ] **Step 3: Write the comparison doc**

`docs/speech_to_text/2026-09-04-nemotron-german-comparison.md`, opening with **Status**, **Owner**,
**Written**, **Origin** (this plan) per the documentation layout rules. Record the fixtures used, the
three transcripts, decode times, the punctuation answer, and a one-line verdict.

There is no ground-truth reference transcript for `artifacts/wav/`, so this compares models against
each other with Whisper medium as a pseudo-reference. Say so in the doc rather than implying a WER
number you do not have.

- [ ] **Step 4: Decide and record**

- **Nemotron is competitive on German** → proceed to Task 6.
- **Nemotron is clearly worse** → stop here. Phase 1 already shipped a working third option; tick
  Task 5 in the checklist with the verdict, cut the streaming sentence from the three settings
  descriptions (Task 4, Step 5), and close the plan.

- [ ] **Step 5: Commit**

```bash
git add docs/speech_to_text/2026-09-04-nemotron-german-comparison.md \
        docs/speech_to_text/2026-09-04-nemotron-third-backend-checklist.md
git commit -m "docs: record the nemotron vs parakeet German comparison"
```

---

## Task 6: `IStreamingTranscriptionEngine` and the engine's streaming half

Phase 2 starts here. Gated on Task 5.

A streaming session is per-capture-source: the mic service and the loopback service each need their
own `OnlineStream` with its own decoder state, sharing one `OnlineRecognizer`. Handing both the same
stream would interleave two speakers into one hypothesis.

**Files:**
- Create: `src/Pia.Wpf/Services/LiveTranscription/IStreamingTranscriptionEngine.cs`
- Modify: `src/Pia.Wpf/Services/LiveTranscription/NemotronStreamingEngine.cs`
- Test: `tests/Pia.Wpf.Tests/Services/LiveTranscription/StreamingEngineContractTests.cs` (**new**)

**Interfaces:**
- Produces: `IStreamingTranscriptionEngine.BeginSession()` → `IStreamingSession`;
  `IStreamingSession : IDisposable` with `void Feed(float[] samples16kMono)`,
  `string CurrentPartial { get; }`, `void Reset()`.
- `NemotronStreamingEngine` gains `: ITranscriptionEngine, IStreamingTranscriptionEngine`.

- [ ] **Step 1: Write the failing contract test**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/StreamingEngineContractTests.cs`:

```csharp
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Whisper and Parakeet must NOT offer the streaming contract — the consumer feature-detects on it,
/// and a false positive would feed audio into an engine that cannot produce partials.
/// </summary>
public class StreamingEngineContractTests
{
    [Fact]
    public void Only_the_nemotron_engine_advertises_streaming()
    {
        Assert.True(typeof(IStreamingTranscriptionEngine).IsAssignableFrom(typeof(NemotronStreamingEngine)));
        Assert.False(typeof(IStreamingTranscriptionEngine).IsAssignableFrom(typeof(WhisperSherpaEngine)));
        Assert.False(typeof(IStreamingTranscriptionEngine).IsAssignableFrom(typeof(ParakeetSherpaEngine)));
    }

    [Fact]
    public void A_streaming_session_is_disposable_so_the_native_stream_is_released()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IStreamingSession)));
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj
```

Expected: `CS0246` on `IStreamingTranscriptionEngine`.

- [ ] **Step 3: Write the interface**

Create `src/Pia.Wpf/Services/LiveTranscription/IStreamingTranscriptionEngine.cs`:

```csharp
namespace Pia.Services.LiveTranscription;

/// <summary>
/// An engine that can emit a running hypothesis before a segment ends. Feature-detected: an engine
/// that does not implement this still works, it just produces nothing until the segment closes.
/// </summary>
public interface IStreamingTranscriptionEngine
{
    IStreamingSession BeginSession();
}

/// <summary>
/// One decoder state. Each capture source needs its own, or two speakers interleave into one
/// hypothesis.
/// </summary>
public interface IStreamingSession : IDisposable
{
    void Feed(float[] samples16kMono);

    string CurrentPartial { get; }

    void Reset();
}
```

- [ ] **Step 4: Implement it on the engine**

Change the declaration:

```csharp
public sealed class NemotronStreamingEngine : ITranscriptionEngine, IStreamingTranscriptionEngine
```

and add, after `TranscribeAsync`:

```csharp
    public IStreamingSession BeginSession() => new Session(_recognizer, _decodeGate, _languageCode);

    private sealed class Session : IStreamingSession
    {
        // A frame handed over while the gate is held by a segment-final decode must not park the
        // caller: the caller is the audio capture reader, and a stalled reader overruns its buffer.
        private static readonly float[] ResetSentinel = [];

        private readonly OnlineRecognizer _recognizer;
        private readonly SemaphoreSlim _gate;
        private readonly OnlineStream _stream;
        private readonly Channel<float[]> _pending = Channel.CreateBounded<float[]>(
            new BoundedChannelOptions(256)
            {
                // Never DropOldest: a hole in the audio corrupts the running hypothesis. Let the
                // backlog drain and the preview lag instead.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
        private readonly Task _drain;
        private string _partial = string.Empty;

        public Session(OnlineRecognizer recognizer, SemaphoreSlim gate, string languageCode)
        {
            _recognizer = recognizer;
            _gate = gate;
            _stream = recognizer.CreateStream();
            _stream.SetOption("language", languageCode);
            _drain = Task.Run(DrainAsync);
        }

        // Only the drain task writes _partial, so a plain read is enough for a preview.
        public string CurrentPartial => _partial;

        public void Feed(float[] samples16kMono) => _pending.Writer.TryWrite(samples16kMono);

        public void Reset() => _pending.Writer.TryWrite(ResetSentinel);

        private async Task DrainAsync()
        {
            await foreach (var frame in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(frame, ResetSentinel))
                    {
                        _recognizer.Reset(_stream);
                        _partial = string.Empty;
                        continue;
                    }

                    _stream.AcceptWaveform(SampleRate, frame);
                    while (_recognizer.IsReady(_stream)) _recognizer.Decode(_stream);
                    _partial = _recognizer.GetResult(_stream).Text?.Trim() ?? string.Empty;
                }
                finally { _gate.Release(); }
            }
        }

        public void Dispose()
        {
            _pending.Writer.TryComplete();
            try { _drain.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _stream.Dispose();
        }
    }
```

Add `using System.Threading.Channels;` to the engine file.

Two things this shape is buying, both worth understanding before simplifying it away:

- **The caller never blocks.** `Feed` and `Reset` are `TryWrite`. The engine's decode gate is real and
  necessary — one recognizer serves the mic session, the loopback session *and* the segment-final
  `TranscribeAsync`, and sherpa does not document concurrent `Decode` on one recognizer as safe — but
  the thread that waits on it is this session's own drain task, not the audio capture reader. A
  segment-final decode of a 20 s segment on `NumThreads=1` can hold that gate for seconds; parking the
  reader for that long overruns the capture buffer.
- **`Reset` goes through the queue, not around it.** Resetting directly would clear decoder state
  while frames captured *before* the reset were still queued behind it, and those frames would then
  be decoded into the next utterance.

- [ ] **Step 5: Decide what happens to frames captured during silence**

The reader loop hands over every frame, including the gaps between utterances. On `auto` the model
can surface junk from room noise as draft text. Two acceptable answers — pick one and write it into
the code:

- Gate `Feed` on the VAD's speaking state. Costs the first ~200 ms of each utterance in the *preview
  only* (the committed text still comes from the full segment), which is the cheaper trade.
- Feed unconditionally and rely on the model. Then make this the first thing you look at in Task 10,
  Step 3.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.StreamingEngineContractTests"
```

Expected: PASS, 2 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/IStreamingTranscriptionEngine.cs \
        src/Pia.Wpf/Services/LiveTranscription/NemotronStreamingEngine.cs \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/StreamingEngineContractTests.cs
git commit -m "feat: expose a per-source streaming session on the nemotron engine"
```

---

## Task 7: Pump partials out of `LiveTranscriptionEngineService`

The VAD reader loop already sees every audio frame. Feed the same frames to the streaming session and
raise the running hypothesis as an event. Segmentation, diarization and the utterance channel are
untouched — a partial is a **preview**, never a transcript entry.

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs`
- Modify: `src/Pia.Wpf/Services/LiveTranscription/IStreamingTranscriptionEngine.cs`
- Test: `tests/Pia.Wpf.Tests/Services/LiveTranscription/PartialHypothesisRelayTests.cs` (**new**)

**Interfaces:**
- Consumes: `IStreamingTranscriptionEngine`, `IStreamingSession` (Task 6).
- Produces: `internal sealed class PartialHypothesisRelay(Action<string> onChanged)` with
  `void Offer(string partial)` and `void Clear()`; and
  `public event EventHandler<string>? PartialTextChanged` on `LiveTranscriptionEngineService` —
  raised on the reader-loop thread, same as `IsSpeakingChanged`. Empty string means "no current
  hypothesis".

`LiveTranscriptionEngineService` cannot be constructed in a unit test — it needs a real
`IAudioCaptureSource` and a real Silero VAD model file on disk. `LiveTranscriptionEngineDrainTests`
works around that with a hand-rolled harness plus reflection; it has **no** reusable construction
helper, so do not try to reuse one.

Extract the de-duplication rule into a tiny class instead, the way
`DirectTranscriptionService.ShouldUseAdaptiveDiarizer` is extracted for exactly this reason
("a pure function ... so the invariant is independently testable without constructing a native
diarizer"). That gives a real test with no fixture at all.

- [ ] **Step 1: Write the failing relay test**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/PartialHypothesisRelayTests.cs`:

```csharp
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// The relay exists so the "only on change" rule is testable without a capture source or a VAD
/// model. A hypothesis is re-read every audio frame and is usually identical to the last one.
/// </summary>
public class PartialHypothesisRelayTests
{
    [Fact]
    public void An_unchanged_hypothesis_does_not_fire_again()
    {
        var seen = new List<string>();
        var relay = new PartialHypothesisRelay(seen.Add);

        relay.Offer("guten");
        relay.Offer("guten");
        relay.Offer("guten Morgen");

        Assert.Equal(["guten", "guten Morgen"], seen);
    }

    [Fact]
    public void Clear_emits_empty_once_and_then_stays_quiet()
    {
        var seen = new List<string>();
        var relay = new PartialHypothesisRelay(seen.Add);

        relay.Offer("guten Morgen");
        relay.Clear();
        relay.Clear();

        Assert.Equal(["guten Morgen", ""], seen);
    }

    [Fact]
    public void A_null_hypothesis_is_treated_as_empty()
    {
        var seen = new List<string>();
        var relay = new PartialHypothesisRelay(seen.Add);

        relay.Offer(null!);

        Assert.Empty(seen);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj
```

Expected: `CS0246: The type or namespace name 'PartialHypothesisRelay' could not be found`.

- [ ] **Step 3: Write the relay**

Add to `src/Pia.Wpf/Services/LiveTranscription/IStreamingTranscriptionEngine.cs`, or its own file if
you prefer — it is four lines of logic either way:

```csharp
/// <summary>
/// Raises a hypothesis only when it differs from the last one. The recognizer is polled every audio
/// frame and usually returns the same text, so the raw stream would be mostly redundant.
/// </summary>
internal sealed class PartialHypothesisRelay(Action<string> onChanged)
{
    private string _last = string.Empty;

    public void Offer(string partial)
    {
        var next = partial ?? string.Empty;
        if (string.Equals(next, _last, StringComparison.Ordinal)) return;
        _last = next;
        onChanged(next);
    }

    public void Clear() => Offer(string.Empty);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.PartialHypothesisRelayTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Add the session, the relay and the event to the service**

In `LiveTranscriptionEngineService`, beside `_engine` (~line 23):

```csharp
    private readonly IStreamingSession? _streamingSession;
    private readonly PartialHypothesisRelay _partialRelay;
```

In the constructor, after `_engine = engine;` (~line 52):

```csharp
        _streamingSession = (engine as IStreamingTranscriptionEngine)?.BeginSession();
        _partialRelay = new PartialHypothesisRelay(text =>
        {
            _logger.SensitiveDebug("Partial for {Speaker}: {Text}", _speaker, text);
            PartialTextChanged?.Invoke(this, text);
        });
```

`SensitiveDebug` is mandatory: a partial hypothesis is verbatim user speech, and
`%LOCALAPPDATA%\Pia\Logs\pia-*.log` is a file users attach to support mails. Add `using Pia.Logging;`
if the file does not already have it. Note the constructor's field-assignment order — `_logger` and
`_speaker` must already be assigned before this lambda is *invoked*, which they are, but confirm
rather than assume.

Beside `IsSpeakingChanged` (~line 155):

```csharp
    /// <summary>Running hypothesis for the segment being spoken, or empty when there is none.
    /// A preview: it is never journaled and never becomes a <see cref="TranscriptUtterance"/>.</summary>
    public event EventHandler<string>? PartialTextChanged;
```

- [ ] **Step 6: Feed the reader loop's frames and clear on speech end**

In `RunReaderLoopAsync` (~line 90), immediately after the frame is handed to the VAD:

```csharp
                if (_streamingSession is not null)
                {
                    _streamingSession.Feed(frame);
                    _partialRelay.Offer(_streamingSession.CurrentPartial);
                }
```

Read the loop before editing — use its actual frame variable name, not `frame`.

In `OnVadSpeechEnded` (~line 158):

```csharp
    private void OnVadSpeechEnded()
    {
        _streamingSession?.Reset();
        _partialRelay.Clear();
        RaiseSpeakingChanged(false);
    }
```

- [ ] **Step 7: Dispose the session**

In `DisposeAsync`, beside the comment noting `_engine` is owned by the caller (~line 245) — the
*session* is owned here, unlike the engine:

```csharp
        _streamingSession?.Dispose();
```

- [ ] **Step 8: Run the tests to verify they pass**

```bash
dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.PartialHypothesisRelayTests"
dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.LiveTranscriptionEngineDrainTests"
```

Expected: PASS both. The drain test passing proves the pump did not disturb segment handling.

- [ ] **Step 9: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs \
        src/Pia.Wpf/Services/LiveTranscription/IStreamingTranscriptionEngine.cs \
        tests/Pia.Wpf.Tests/Services/LiveTranscription/PartialHypothesisRelayTests.cs
git commit -m "feat: raise running hypotheses from the live transcription service"
```

---

## Task 8: Surface partials in the overlay ViewModel without touching the journal

**The single most important constraint in this plan.** `TranscriptOverlayViewModel` keeps a
`_journal` of `UtteranceEntry` and rebuilds `Bubbles` from it whenever a speaker is reassigned,
renamed or dropped (`RebuildBubblesFromJournal`, ~line 370). Its documented invariant is that the
rebuild replays the journal through the *same* incremental path — `GetOrCreateBubble` + `Append` — so
"rebuild-vs-incremental equivalence holds by construction".

A partial that mutates `TranscriptBubble.Text`, or that is appended into a bubble, **breaks that
invariant**: the next rebuild would silently drop it or duplicate it. Partials therefore live in a
separate property that the journal never sees and the rebuild never touches.

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs`
- Test: `tests/Pia.Wpf.Tests/ViewModels/TranscriptOverlayPartialTests.cs` (**new**)

**Interfaces:**
- Consumes: `LiveTranscriptionEngineService.PartialTextChanged` (Task 7).
- Produces: `[ObservableProperty] private string _partialText;` → `PartialText`,
  `[ObservableProperty] private TranscriptSpeaker _partialSpeaker;` → `PartialSpeaker`, and
  `internal void SetPartial(TranscriptSpeaker speaker, string text)`.

- [ ] **Step 1: Write the failing invariant test**

There are **no** existing tests for `TranscriptOverlayViewModel`, so the concrete subclass has to be
written here. The base class is abstract with an 11-parameter constructor and six abstract members;
`InlineUiDispatcher` (`tests/Pia.Wpf.Tests/Services/InlineUiDispatcher.cs`) makes `DispatchToUi` run
synchronously, which is what makes these assertions deterministic.

Create `tests/Pia.Wpf.Tests/ViewModels/TranscriptOverlayPartialTests.cs`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests.Services;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The overlay rebuilds its bubbles from the journal on every speaker reassignment. A partial that
/// reached a bubble or the journal would be duplicated or dropped by that rebuild, so the whole
/// point of these tests is that it reaches neither.
/// </summary>
public class TranscriptOverlayPartialTests
{
    private sealed class TestOverlay : TranscriptOverlayViewModel
    {
        private readonly Channel<TranscriptUtterance> _channel = Channel.CreateUnbounded<TranscriptUtterance>();

        public TestOverlay() : base(
            Substitute.For<ISettingsService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger.Instance,
            new InlineUiDispatcher(),
            chatSessionManager: null,
            workingDirectoryService: null)
        { }

        protected override ChannelReader<TranscriptUtterance> UtteranceReader => _channel.Reader;
        protected override string TitleKey => "Test_Title";
        protected override string SaveDialogTitleKey => "Test_SaveTitle";
        protected override string SaveDialogFilterKey => "Test_SaveFilter";
        protected override string SaveFileNamePrefix => "test";
        protected override string MeetingSourceKind => "test";
    }

    [Fact]
    public void A_partial_never_enters_the_bubbles_or_the_journal()
    {
        var vm = new TestOverlay();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "committed", DateTimeOffset.Now));
        vm.SetPartial(TranscriptSpeaker.You, "still being said");

        Assert.Equal("still being said", vm.PartialText);
        Assert.Single(vm.Bubbles);
        Assert.Equal("committed", vm.Bubbles[0].Text);
    }

    [Fact]
    public void A_final_utterance_clears_the_partial_it_replaces()
    {
        var vm = new TestOverlay();
        vm.SetPartial(TranscriptSpeaker.You, "still being said");
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.You, "still being said", DateTimeOffset.Now));

        Assert.Equal(string.Empty, vm.PartialText);
        Assert.Single(vm.Bubbles);
        Assert.Equal("still being said", vm.Bubbles[0].Text);
    }
}
```

Read the base constructor before writing this — the parameter list above is its 2026-09-04 shape and
is the thing most likely to have drifted. `NullLogger.Instance` needs
`Microsoft.Extensions.Logging.Abstractions`; the repo's convention elsewhere is
`NullLogger<T>.Instance`, but the base takes a non-generic `ILogger`.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test --filter-class "Pia.Tests.ViewModels.TranscriptOverlayPartialTests"
```

Expected: FAIL — `SetPartial` and `PartialText` do not exist.

- [ ] **Step 3: Add the properties and the setter**

In `TranscriptOverlayViewModel`, beside the other observable properties:

```csharp
    /// <summary>Running hypothesis shown below the last bubble. Deliberately outside the journal:
    /// a rebuild replays only committed utterances, so a journaled partial would duplicate or vanish.</summary>
    [ObservableProperty]
    private string _partialText = string.Empty;

    [ObservableProperty]
    private TranscriptSpeaker _partialSpeaker;
```

and, next to `AddUtterance`:

```csharp
    internal void SetPartial(TranscriptSpeaker speaker, string text)
    {
        DispatchToUi(() =>
        {
            PartialSpeaker = speaker;
            PartialText = text ?? string.Empty;
        });
    }
```

- [ ] **Step 4: Clear the partial when the final arrives**

As the first statement inside `AddUtterance`'s `DispatchToUi` body, before the journal write:

```csharp
                PartialText = string.Empty;
```

Without this the preview lingers next to the committed bubble that superseded it.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test --filter-class "Pia.Tests.ViewModels.TranscriptOverlayPartialTests"
dotnet test --filter-namespace "Pia.Tests.ViewModels"
```

Expected: PASS. The namespace run matters — it covers the reassignment and rebuild tests that guard
the invariant this task is built around.

- [ ] **Step 6: Subscribe the overlay to the engine service's event**

Find where the concrete overlay ViewModels subscribe to `IsSpeakingChanged` on their
`LiveTranscriptionEngineService` instances and add the parallel subscription there:

```csharp
        service.PartialTextChanged += (_, text) => SetPartial(service.Speaker, text);
```

Unsubscribe in the same place the `IsSpeakingChanged` handler is unsubscribed. A live event handler
into a disposed ViewModel is a leak the overlay's own dispose path exists to prevent.

**Two services, one slot.** The mic and loopback services both raise this event, and there is one
`PartialText`. During cross-talk the draft flips between speakers. That is **last-writer-wins by
design** — `PartialSpeaker` tells the view who the current draft belongs to, and a single draft line
matches how the committed bubbles already serialise an interleaved conversation. Do not silently
leave this implicit; if Task 10, Step 3 shows the flipping is distracting, the fix is two slots keyed
by `TranscriptSpeaker`, not a lock.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs \
        tests/Pia.Wpf.Tests/ViewModels/TranscriptOverlayPartialTests.cs
git commit -m "feat: show a running hypothesis outside the transcript journal"
```

---

## Task 9: Render the draft text in both overlays

**Files:**
- Modify: `src/Pia.Wpf/Views/DirectTranscriptionOverlay.xaml`
- Modify: `src/Pia.Wpf/Views/MeetingAttendeeOverlay.xaml`

- [ ] **Step 1: Add the draft block below the bubble list**

In each overlay, after the items control bound to `Bubbles`, add a block bound to `PartialText`,
hidden when empty:

```xml
        <TextBlock Text="{Binding PartialText}"
                   Opacity="0.6"
                   FontStyle="Italic"
                   TextWrapping="Wrap" />
```

Read the surrounding file for the converter names actually registered there for
string-to-visibility. If none exists, use whatever that file already uses for the same job rather
than introducing a new converter.

`TextBlock` is not in the AutomationId-required control list (`ButtonBase`, `ComboBox`,
`TextBoxBase`/`RichTextBox`, `PasswordBox`, `Slider`, `Expander`, `TabItem`), so no id and no
`ViewAutomationIdTests` row change — unless you make it interactive, in which case both are required.

- [ ] **Step 2: Verify the distinction reads visually**

The draft must not look like committed text. Dimmed and italic is the intent: a user glancing at the
overlay should be able to tell what is settled from what is still being guessed, because a partial
hypothesis routinely rewrites itself mid-sentence.

- [ ] **Step 3: Run the gate and a rebuild**

```bash
dotnet test
dotnet build -t:Rebuild -v:n && dotnet build -t:Rebuild -v:n -c Release
```

Expected: `failed: 0`; `0 Warning(s)` both configurations.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Views/DirectTranscriptionOverlay.xaml \
        src/Pia.Wpf/Views/MeetingAttendeeOverlay.xaml
git commit -m "feat: render the running hypothesis as dimmed draft text"
```

---

## Task 10: Prove it against the real desktop, then write the release notes

**Files:**
- Create: `tests/ui-scripts/<name>.json`
- Modify: `docs/release_notes/RELEASE.md`
- Modify: `docs/speech_to_text/2026-09-04-nemotron-third-backend-checklist.md`

- [ ] **Step 1: Record a UI script covering backend selection**

Read `tests/ui-scripts/README.md` first — the recorder has sharp edges (`stop` is a one-way door,
only `ww_assert_value` is recorded). The flow: open Settings → Speech, select Nemotron from
`Settings_General_SttEngine`, assert `Settings_General_DownloadNemotronModel` is present.

- [ ] **Step 2: Replay it**

```bash
pwsh ./tests/ui-scripts/Invoke-UiScripts.ps1
```

Expected: pass. These are not part of the `dotnet test` gate — they launch the real app against a
throwaway data directory and verify by hash that your profile was not touched.

- [ ] **Step 3: Drive a real meeting and watch the partials**

Start a live transcription on the Nemotron backend and confirm text grows while you speak and is
replaced by the committed bubble when the segment closes. Then trigger a speaker rename — which
forces `RebuildBubblesFromJournal` — and confirm no partial text is duplicated into a bubble or lost
from one. That is the Task 8 invariant, checked against the running app rather than a unit test.

- [ ] **Step 4: Write the release notes**

`docs/release_notes/RELEASE.md`, per `docs/release_notes/README.md` (hard-wrap 80, one bullet level,
no tables, four lines per bullet). Say what a user gets: a third speech-to-text engine, 36 languages,
text that appears while they are still speaking. Do not mention custom vocabulary or name
dictionaries — this backend cannot do it.

- [ ] **Step 5: Final gate**

```bash
dotnet test
dotnet build -t:Rebuild -v:n && dotnet build -t:Rebuild -v:n -c Release
```

Expected: `failed: 0`; `0 Warning(s)` both configurations.

- [ ] **Step 6: Commit**

```bash
git add tests/ui-scripts docs/release_notes/RELEASE.md \
        docs/speech_to_text/2026-09-04-nemotron-third-backend-checklist.md
git commit -m "test: cover nemotron backend selection with a UI script"
```

---

## Open questions

- **Every segment is decoded twice.** The streaming session decodes the audio for the preview, and
  then `TranscribeAsync` decodes the same audio again for the committed text — 2× CPU on the hot
  path, on `NumThreads=1`. The obvious saving is to take `CurrentPartial` at speech-end as the final
  and skip `TranscribeAsync` for engines that implement `IStreamingTranscriptionEngine`. It is
  deliberately **not** in this plan: doing it bypasses the segment loop, which is where diarization
  and `SegmentId` assignment happen, so it is a change to `LiveTranscriptionEngineService`'s shape
  rather than an optimisation. Task 5's timing numbers say whether it is worth a follow-up.
- **Does nemotron punctuate and capitalise?** Inherited unanswered from the 2026-08-30 plan. Task 5,
  Step 2 answers it. It matters because the downstream Summarize prompt benefits from Parakeet's
  punctuation, so a bare lowercase stream would be a quality regression that only shows up in
  summaries.
- **Is 560 ms the right chunk size once partials are visible?** The pin is a judgement, not a
  measurement. If Task 10, Step 3 shows the text lurching rather than growing, 320 ms is the next
  variant to try — and re-pinning means Task 1 again, in all four places.
- **Should the partial be diarized?** It is not, and cannot easily be: diarization runs on the
  finished segment. The draft text therefore shows without a speaker label. Acceptable as long as it
  is visually distinct from committed bubbles (Task 9, Step 2).
