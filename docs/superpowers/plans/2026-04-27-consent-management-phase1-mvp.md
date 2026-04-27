# Consent-Management Phase 1 (MVP) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a DSGVO/§201-StGB compliant consent gate to the live meeting pipeline — single-speaker, rule-based classifier, RAM-only ring buffer, Strict Mode only, append-only audit log.

**Architecture:** A new `Pia.Services.Consent` namespace introduces a per-speaker state machine, a RAM ring buffer, a rule-based classifier, and a pre-STT/post-STT gate pair. The existing `LiveTranscriptionEngineService` is extended to consult the gate before forwarding VAD segments to the STT engine, and to consult the post-STT defense filter before emitting `TranscriptUtterance`s. The existing `ITtsService` is reused to play prompts; the existing `SpeakerIdentificationService` continues to label speakers on the loopback channel.

**Tech Stack:** C# 13 / .NET 10, CommunityToolkit.MVVM, xUnit v3 + plain `Xunit.Assert` (no FluentAssertions), Serilog via `ILogger<T>`.

**Spec reference:** `docs/consent-management-spezifikation.md` (sections 1–4, 6, 7 Strict Mode, 8 Phase 1).

**Scope (in):**
- Per-speaker `ConsentState` machine (UNKNOWN, PROMPTED, GRANTED, DENIED, REVOKED, TIMEOUT, AMBIGUOUS).
- Per-speaker RAM ring buffer (no disk spill).
- Rule-based DE/EN consent classifier.
- Pre-STT gate routing PROMPTED audio to a consent-classification path and dropping all non-GRANTED audio from the regular transcript path.
- Post-STT defense filter dropping any utterance whose speaker is not GRANTED.
- TTS prompt playback for `INITIAL_CONSENT_LOCAL_ONLY`, `CLARIFICATION_AMBIGUOUS`, `REVOCATION_CONFIRM`.
- Append-only JSONL audit log (no hash chain yet — Phase 2).
- Strict Mode default: TIMEOUT → DENIED, cloud disabled, no embedding persistence.

**Scope (out, deferred to later phases):**
- Phase 2: Multi-speaker Strategy B, hash-chained audit log, LLM classifier fallback.
- Phase 3: Strategy A, cross-talk handling, Standard/Permissive modes, persistent voice-embedding blocklist.
- Phase 4: PII pseudonymisation, cloud pipeline, revocation tooling, optional consent-audio-snippet persistence.
- Phase 5: Cross-session embedding persistence.

---

## File Structure

**New files (production):**
- `src/Pia.Wpf/Services/Consent/ConsentState.cs` — `enum ConsentState`.
- `src/Pia.Wpf/Services/Consent/ConsentEvidence.cs` — record with transcript text, classification, timestamp, prompt hash, stt model id.
- `src/Pia.Wpf/Services/Consent/SpeakerConsentEntry.cs` — per-speaker mutable record (state, evidence, last-prompt timestamp).
- `src/Pia.Wpf/Services/Consent/IConsentStateManager.cs` — interface (state queries, transitions, events).
- `src/Pia.Wpf/Services/Consent/ConsentStateManager.cs` — implementation; owns the in-memory dictionary; raises `StateChanged`.
- `src/Pia.Wpf/Services/Consent/IConsentClassifier.cs` — interface.
- `src/Pia.Wpf/Services/Consent/RuleBasedConsentClassifier.cs` — DE/EN phrase matcher returning `(Decision, Confidence)`.
- `src/Pia.Wpf/Services/Consent/ConsentClassification.cs` — record `(ConsentDecision, float Confidence)` and enum `ConsentDecision { Grant, Deny, Ambiguous }`.
- `src/Pia.Wpf/Services/Consent/SpeakerRingBuffer.cs` — bounded RAM-only `float[]` queue per speaker.
- `src/Pia.Wpf/Services/Consent/IConsentGate.cs` — interface; `GateDecision Evaluate(speaker)`.
- `src/Pia.Wpf/Services/Consent/ConsentGate.cs` — implementation.
- `src/Pia.Wpf/Services/Consent/ConsentPromptTemplates.cs` — static template library (id, language, text, version hash).
- `src/Pia.Wpf/Services/Consent/IConsentAuditLog.cs` — interface.
- `src/Pia.Wpf/Services/Consent/JsonlConsentAuditLog.cs` — append-only JSONL writer under `%LOCALAPPDATA%\Pia\ConsentAudit\session_<uuid>.jsonl`.
- `src/Pia.Wpf/Services/Consent/AuditEvent.cs` — record with `EventId`, `Timestamp`, `EventType`, `SpeakerId?`, `Details`.

**Modified files (production):**
- `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs` — inject `IConsentGate`, route segments per gate decision; consult `IConsentStateManager` post-STT before writing to sink.
- `src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs` — construct + own consent components; wire to engines; raise consent-state events on bus.
- `src/Pia.Wpf/Bootstrapper.cs` — DI registrations for the new services.
- `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs` — surface consent state to UI; observable property `ConsentState`.
- `src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml` — small badge showing consent state per speaker.

**New files (tests):**
- `tests/Pia.Wpf.Tests/Consent/RuleBasedConsentClassifierTests.cs`
- `tests/Pia.Wpf.Tests/Consent/ConsentStateManagerTests.cs`
- `tests/Pia.Wpf.Tests/Consent/SpeakerRingBufferTests.cs`
- `tests/Pia.Wpf.Tests/Consent/ConsentGateTests.cs`
- `tests/Pia.Wpf.Tests/Consent/JsonlConsentAuditLogTests.cs`
- `tests/Pia.Wpf.Tests/Consent/LiveTranscriptionEngineConsentIntegrationTests.cs`

---

## Chunk 1: Data model + state machine

### Task 1: ConsentState enum + ConsentDecision enum

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/ConsentState.cs`
- Create: `src/Pia.Wpf/Services/Consent/ConsentClassification.cs`

- [ ] **Step 1: Create `ConsentState.cs`**

```csharp
namespace Pia.Services.Consent;

public enum ConsentState
{
    Unknown,
    Prompted,
    Granted,
    Denied,
    Revoked,
    Timeout,
    Ambiguous
}
```

- [ ] **Step 2: Create `ConsentClassification.cs`**

```csharp
namespace Pia.Services.Consent;

public enum ConsentDecision { Grant, Deny, Ambiguous }

public sealed record ConsentClassification(ConsentDecision Decision, float Confidence);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeds with no warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Services/Consent/ConsentState.cs src/Pia.Wpf/Services/Consent/ConsentClassification.cs
git commit -m "feat(consent): add ConsentState and ConsentClassification types"
```

### Task 2: ConsentEvidence record

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/ConsentEvidence.cs`

- [ ] **Step 1: Create file**

```csharp
namespace Pia.Services.Consent;

public sealed record ConsentEvidence(
    string TranscriptText,
    float ClassificationConfidence,
    DateTimeOffset Timestamp,
    string PromptVersionHash,
    string PromptTextPlayed,
    string SttModelId);
```

> Note: Phase 1 omits `consent_scope` (Strict Mode is fixed) and `cryptographic_signature` (Phase 2 hash-chain). Re-add fields when those phases land.

- [ ] **Step 2: Build + commit**

```bash
dotnet build src/Pia.Wpf/Pia.Wpf.csproj
git add src/Pia.Wpf/Services/Consent/ConsentEvidence.cs
git commit -m "feat(consent): add ConsentEvidence record"
```

### Task 3: SpeakerConsentEntry

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/SpeakerConsentEntry.cs`

- [ ] **Step 1: Create file**

```csharp
namespace Pia.Services.Consent;

public sealed class SpeakerConsentEntry
{
    public string SpeakerLabel { get; }
    public DateTimeOffset FirstDetected { get; }
    public ConsentState State { get; set; } = ConsentState.Unknown;
    public ConsentEvidence? Evidence { get; set; }
    public DateTimeOffset? PromptedAt { get; set; }

    public SpeakerConsentEntry(string speakerLabel, DateTimeOffset firstDetected)
    {
        SpeakerLabel = speakerLabel;
        FirstDetected = firstDetected;
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build src/Pia.Wpf/Pia.Wpf.csproj
git add src/Pia.Wpf/Services/Consent/SpeakerConsentEntry.cs
git commit -m "feat(consent): add SpeakerConsentEntry"
```

### Task 4: IConsentStateManager + ConsentStateManager (TDD)

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/IConsentStateManager.cs`
- Create: `src/Pia.Wpf/Services/Consent/ConsentStateManager.cs`
- Test: `tests/Pia.Wpf.Tests/Consent/ConsentStateManagerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class ConsentStateManagerTests
{
    [Fact]
    public void NewSpeaker_StartsInUnknown()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var entry = sut.GetOrCreate("Speaker 1");
        Assert.Equal(ConsentState.Unknown, entry.State);
    }

    [Fact]
    public void TransitionToPrompted_RaisesStateChanged()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        var raised = false;
        sut.StateChanged += (_, e) => { if (e.SpeakerLabel == "Speaker 1" && e.NewState == ConsentState.Prompted) raised = true; };

        sut.MarkPrompted("Speaker 1");

        Assert.True(raised);
        Assert.Equal(ConsentState.Prompted, sut.GetOrCreate("Speaker 1").State);
    }

    [Fact]
    public void RecordDecision_Grant_SetsGrantedAndStoresEvidence()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        sut.MarkPrompted("Speaker 1");

        sut.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Grant, 0.95f),
            transcriptText: "ja",
            promptHash: "v1",
            promptText: "Sind Sie einverstanden?",
            sttModelId: "whisper-base");

        var entry = sut.GetOrCreate("Speaker 1");
        Assert.Equal(ConsentState.Granted, entry.State);
        Assert.NotNull(entry.Evidence);
        Assert.Equal("ja", entry.Evidence!.TranscriptText);
    }

    [Fact]
    public void RecordDecision_Ambiguous_BelowThreshold_SetsAmbiguous()
    {
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        sut.GetOrCreate("Speaker 1");
        sut.MarkPrompted("Speaker 1");

        sut.RecordClassification("Speaker 1",
            new ConsentClassification(ConsentDecision.Grant, 0.5f),
            "vielleicht", "v1", "...", "whisper-base");

        Assert.Equal(ConsentState.Ambiguous, sut.GetOrCreate("Speaker 1").State);
    }

    [Fact]
    public void Timeout_AfterPromptWindow_TransitionsToTimeout()
    {
        var clock = new FakeTimeProvider();
        var sut = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, clock)
        {
            PromptTimeout = TimeSpan.FromSeconds(15)
        };
        sut.GetOrCreate("Speaker 1");
        sut.MarkPrompted("Speaker 1");
        clock.Advance(TimeSpan.FromSeconds(16));

        sut.SweepTimeouts();

        Assert.Equal(ConsentState.Timeout, sut.GetOrCreate("Speaker 1").State);
    }
}
```

A `FakeTimeProvider` helper is needed — use `Microsoft.Extensions.TimeProvider.Testing` (add NuGet ref) or implement a minimal one inline.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~ConsentStateManagerTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create `IConsentStateManager.cs`**

```csharp
namespace Pia.Services.Consent;

public sealed record ConsentStateChangedEventArgs(string SpeakerLabel, ConsentState OldState, ConsentState NewState);

public interface IConsentStateManager
{
    event EventHandler<ConsentStateChangedEventArgs>? StateChanged;

    SpeakerConsentEntry GetOrCreate(string speakerLabel);
    bool TryGet(string speakerLabel, out SpeakerConsentEntry entry);
    ConsentState CurrentState(string speakerLabel);

    void MarkPrompted(string speakerLabel);
    void RecordClassification(
        string speakerLabel,
        ConsentClassification classification,
        string transcriptText,
        string promptHash,
        string promptText,
        string sttModelId);
    void Revoke(string speakerLabel);
    void SweepTimeouts();

    TimeSpan PromptTimeout { get; set; }
    float GrantConfidenceThreshold { get; set; }
}
```

- [ ] **Step 4: Implement `ConsentStateManager.cs`**

Notes for implementer:
- Backing store: `Dictionary<string, SpeakerConsentEntry>` guarded by a `lock`.
- `GrantConfidenceThreshold` default `0.9f` (per spec §3.7).
- `PromptTimeout` default `TimeSpan.FromSeconds(15)`.
- `RecordClassification`: if `confidence < GrantConfidenceThreshold` → `Ambiguous`. Else map decision: Grant → Granted, Deny → Denied, Ambiguous → Ambiguous. Always store `Evidence`.
- Log every transition at `Information`.
- Raise `StateChanged` outside the lock.

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~ConsentStateManagerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/Consent/IConsentStateManager.cs src/Pia.Wpf/Services/Consent/ConsentStateManager.cs tests/Pia.Wpf.Tests/Consent/ConsentStateManagerTests.cs
git commit -m "feat(consent): add ConsentStateManager with state machine + tests"
```

---

## Chunk 2: Classifier + ring buffer

### Task 5: RuleBasedConsentClassifier (TDD)

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/IConsentClassifier.cs`
- Create: `src/Pia.Wpf/Services/Consent/RuleBasedConsentClassifier.cs`
- Test: `tests/Pia.Wpf.Tests/Consent/RuleBasedConsentClassifierTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class RuleBasedConsentClassifierTests
{
    private readonly IConsentClassifier _sut = new RuleBasedConsentClassifier();

    [Theory]
    [InlineData("ja")]
    [InlineData("Ja, gerne.")]
    [InlineData("einverstanden")]
    [InlineData("kein Problem")]
    [InlineData("yes")]
    [InlineData("sure, go ahead")]
    public void GrantPhrases_ReturnGrantWithHighConfidence(string text)
    {
        var result = _sut.Classify(text);
        Assert.Equal(ConsentDecision.Grant, result.Decision);
        Assert.True(result.Confidence >= 0.9f, $"confidence was {result.Confidence}");
    }

    [Theory]
    [InlineData("nein")]
    [InlineData("nicht einverstanden")]
    [InlineData("auf keinen Fall")]
    [InlineData("no")]
    [InlineData("absolutely not")]
    public void DenyPhrases_ReturnDenyWithHighConfidence(string text)
    {
        var result = _sut.Classify(text);
        Assert.Equal(ConsentDecision.Deny, result.Decision);
        Assert.True(result.Confidence >= 0.9f);
    }

    [Theory]
    [InlineData("vielleicht")]
    [InlineData("ich weiß nicht")]
    [InlineData("warum genau?")]
    [InlineData("was meinen Sie damit")]
    public void AmbiguousPhrases_ReturnAmbiguous(string text)
    {
        var result = _sut.Classify(text);
        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
    }

    [Fact]
    public void EmptyInput_ReturnsAmbiguousWithLowConfidence()
    {
        var result = _sut.Classify("");
        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
        Assert.True(result.Confidence < 0.5f);
    }

    [Fact]
    public void GrantAndDenyTogether_ReturnsAmbiguous()
    {
        var result = _sut.Classify("ja aber nein eigentlich");
        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
    }
}
```

- [ ] **Step 2: Run tests, verify fail**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --filter "FullyQualifiedName~RuleBasedConsentClassifierTests"`

- [ ] **Step 3: Create `IConsentClassifier.cs`**

```csharp
namespace Pia.Services.Consent;

public interface IConsentClassifier
{
    ConsentClassification Classify(string transcriptText);
}
```

- [ ] **Step 4: Implement `RuleBasedConsentClassifier.cs`**

Implementation notes:
- Word-boundary, case-insensitive matching against three phrase lists per language (DE, EN).
- `GRANT_PATTERNS_DE = ["ja", "einverstanden", "okay", "kein problem", "in ordnung", "von mir aus", "passt", "geht klar", "gerne"]`.
- `DENY_PATTERNS_DE = ["nein", "nicht einverstanden", "lieber nicht", "stopp", "kein einverständnis", "auf keinen fall"]`.
- `AMBIGUOUS_DE = ["vielleicht", "ich weiß nicht", "warum", "was genau", "was meinen sie", "moment"]`.
- Mirror lists for EN.
- Algorithm: count grant/deny/ambiguous hits. If both grant and deny hit → Ambiguous (0.5). If only ambiguous markers → Ambiguous (0.7). If only grant → Grant (0.95). If only deny → Deny (0.95). Empty → Ambiguous (0.0).
- Normalise input: trim, lowercase, strip punctuation other than spaces.

- [ ] **Step 5: Run tests, verify pass**

Run: `dotnet test ...filter RuleBasedConsentClassifierTests`

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/Consent/IConsentClassifier.cs src/Pia.Wpf/Services/Consent/RuleBasedConsentClassifier.cs tests/Pia.Wpf.Tests/Consent/RuleBasedConsentClassifierTests.cs
git commit -m "feat(consent): add rule-based DE/EN consent classifier"
```

### Task 6: SpeakerRingBuffer (TDD)

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/SpeakerRingBuffer.cs`
- Test: `tests/Pia.Wpf.Tests/Consent/SpeakerRingBufferTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class SpeakerRingBufferTests
{
    [Fact]
    public void Append_BelowCapacity_RetainsAll()
    {
        var sut = new SpeakerRingBuffer(capacitySamples: 1000);
        sut.Append(new float[] { 1, 2, 3 });
        sut.Append(new float[] { 4, 5 });
        var snapshot = sut.Snapshot();
        Assert.Equal(new float[] { 1, 2, 3, 4, 5 }, snapshot);
    }

    [Fact]
    public void Append_OverCapacity_DropsOldest()
    {
        var sut = new SpeakerRingBuffer(capacitySamples: 4);
        sut.Append(new float[] { 1, 2, 3 });
        sut.Append(new float[] { 4, 5, 6 });
        var snapshot = sut.Snapshot();
        Assert.Equal(new float[] { 3, 4, 5, 6 }, snapshot);
    }

    [Fact]
    public void Drain_ReturnsAndClears()
    {
        var sut = new SpeakerRingBuffer(capacitySamples: 100);
        sut.Append(new float[] { 1, 2, 3 });
        var drained = sut.Drain();
        Assert.Equal(new float[] { 1, 2, 3 }, drained);
        Assert.Empty(sut.Snapshot());
    }

    [Fact]
    public void Clear_ZeroesUnderlyingStorage()
    {
        var sut = new SpeakerRingBuffer(capacitySamples: 4);
        sut.Append(new float[] { 1, 2, 3, 4 });
        sut.Clear();
        Assert.Empty(sut.Snapshot());
    }
}
```

- [ ] **Step 2: Run, verify fail**

- [ ] **Step 3: Implement `SpeakerRingBuffer.cs`**

```csharp
namespace Pia.Services.Consent;

/// Bounded RAM-only circular sample queue. Phase 1: single-speaker, single buffer.
/// Disk spill is forbidden — when capacity is exceeded, oldest samples are overwritten.
public sealed class SpeakerRingBuffer
{
    private readonly float[] _buffer;
    private int _start;
    private int _count;
    private readonly object _lock = new();

    public SpeakerRingBuffer(int capacitySamples)
    {
        if (capacitySamples <= 0) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        _buffer = new float[capacitySamples];
    }

    public int Capacity => _buffer.Length;
    public int Count { get { lock (_lock) return _count; } }

    public void Append(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            foreach (var s in samples)
            {
                var write = (_start + _count) % _buffer.Length;
                _buffer[write] = s;
                if (_count < _buffer.Length) _count++;
                else _start = (_start + 1) % _buffer.Length;
            }
        }
    }

    public float[] Snapshot()
    {
        lock (_lock)
        {
            var result = new float[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(_start + i) % _buffer.Length];
            return result;
        }
    }

    public float[] Drain()
    {
        lock (_lock)
        {
            var result = Snapshot();
            Clear_NoLock();
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock) Clear_NoLock();
    }

    private void Clear_NoLock()
    {
        Array.Clear(_buffer);
        _start = 0;
        _count = 0;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/Consent/SpeakerRingBuffer.cs tests/Pia.Wpf.Tests/Consent/SpeakerRingBufferTests.cs
git commit -m "feat(consent): add per-speaker RAM ring buffer"
```

---

## Chunk 3: Gate + audit log

### Task 7: ConsentGate (TDD)

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/IConsentGate.cs`
- Create: `src/Pia.Wpf/Services/Consent/ConsentGate.cs`
- Test: `tests/Pia.Wpf.Tests/Consent/ConsentGateTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class ConsentGateTests
{
    private static (ConsentStateManager mgr, ConsentGate gate) Build()
    {
        var mgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var gate = new ConsentGate(mgr, NullLogger<ConsentGate>.Instance);
        return (mgr, gate);
    }

    [Fact]
    public void UnknownSpeaker_ReturnsDrop()
    {
        var (_, gate) = Build();
        Assert.Equal(GateDecision.Drop, gate.Evaluate("Speaker 1"));
    }

    [Fact]
    public void GrantedSpeaker_ReturnsPassToTranscript()
    {
        var (mgr, gate) = Build();
        mgr.GetOrCreate("Speaker 1");
        mgr.MarkPrompted("Speaker 1");
        mgr.RecordClassification("Speaker 1", new ConsentClassification(ConsentDecision.Grant, 0.95f),
            "ja", "v1", "...", "whisper-base");
        Assert.Equal(GateDecision.PassToTranscript, gate.Evaluate("Speaker 1"));
    }

    [Fact]
    public void PromptedSpeaker_ReturnsPassToConsentClassifier()
    {
        var (mgr, gate) = Build();
        mgr.GetOrCreate("Speaker 1");
        mgr.MarkPrompted("Speaker 1");
        Assert.Equal(GateDecision.PassToConsentClassifier, gate.Evaluate("Speaker 1"));
    }

    [Theory]
    [InlineData(ConsentState.Denied)]
    [InlineData(ConsentState.Revoked)]
    [InlineData(ConsentState.Timeout)]
    [InlineData(ConsentState.Ambiguous)]
    public void NonGrantedTerminalStates_ReturnDrop(ConsentState state)
    {
        var (mgr, gate) = Build();
        var entry = mgr.GetOrCreate("Speaker 1");
        entry.State = state;
        Assert.Equal(GateDecision.Drop, gate.Evaluate("Speaker 1"));
    }
}
```

- [ ] **Step 2: Run, verify fail**

- [ ] **Step 3: Create `IConsentGate.cs`**

```csharp
namespace Pia.Services.Consent;

public enum GateDecision { Drop, PassToConsentClassifier, PassToTranscript }

public interface IConsentGate
{
    GateDecision Evaluate(string speakerLabel);
}
```

- [ ] **Step 4: Implement `ConsentGate.cs`**

```csharp
using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

public sealed class ConsentGate : IConsentGate
{
    private readonly IConsentStateManager _mgr;
    private readonly ILogger<ConsentGate> _logger;

    public ConsentGate(IConsentStateManager mgr, ILogger<ConsentGate> logger)
    {
        _mgr = mgr;
        _logger = logger;
    }

    public GateDecision Evaluate(string speakerLabel)
    {
        var state = _mgr.CurrentState(speakerLabel);
        return state switch
        {
            ConsentState.Granted => GateDecision.PassToTranscript,
            ConsentState.Prompted => GateDecision.PassToConsentClassifier,
            _ => GateDecision.Drop
        };
    }
}
```

- [ ] **Step 5: Run tests, verify pass**

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/Consent/IConsentGate.cs src/Pia.Wpf/Services/Consent/ConsentGate.cs tests/Pia.Wpf.Tests/Consent/ConsentGateTests.cs
git commit -m "feat(consent): add Pre-STT consent gate"
```

### Task 8: ConsentPromptTemplates

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/ConsentPromptTemplates.cs`

- [ ] **Step 1: Create file**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Pia.Services.Consent;

public sealed record ConsentPrompt(string Id, string Language, string Text, string VersionHash);

public static class ConsentPromptTemplates
{
    public static readonly ConsentPrompt InitialConsentLocalOnlyDe = Build(
        "INITIAL_CONSENT_LOCAL_ONLY", "de",
        "Hallo, ich nutze ein Tool, das unser Gespräch lokal auf meinem Computer aufzeichnet "
        + "und für meine Notizen verarbeitet. Es werden keine Daten an externe Dienste gesendet. "
        + "Sind Sie damit einverstanden? Ein kurzes Ja oder Nein genügt.");

    public static readonly ConsentPrompt ClarificationAmbiguousDe = Build(
        "CLARIFICATION_AMBIGUOUS", "de",
        "Entschuldigung, ich habe Ihre Antwort nicht eindeutig verstanden. "
        + "Sind Sie mit der Aufzeichnung einverstanden – ja oder nein?");

    public static readonly ConsentPrompt RevocationConfirmDe = Build(
        "REVOCATION_CONFIRM", "de",
        "Verstanden, die Aufzeichnung wurde gestoppt und alle Notizen gelöscht.");

    private static ConsentPrompt Build(string id, string lang, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{id}|{lang}|{text}"));
        return new ConsentPrompt(id, lang, text, Convert.ToHexString(bytes)[..16]);
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build src/Pia.Wpf/Pia.Wpf.csproj
git add src/Pia.Wpf/Services/Consent/ConsentPromptTemplates.cs
git commit -m "feat(consent): add prompt templates with version hashes"
```

### Task 9: AuditEvent + JsonlConsentAuditLog (TDD)

**Files:**
- Create: `src/Pia.Wpf/Services/Consent/AuditEvent.cs`
- Create: `src/Pia.Wpf/Services/Consent/IConsentAuditLog.cs`
- Create: `src/Pia.Wpf/Services/Consent/JsonlConsentAuditLog.cs`
- Test: `tests/Pia.Wpf.Tests/Consent/JsonlConsentAuditLogTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class JsonlConsentAuditLogTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "pia-consent-tests-" + Guid.NewGuid("N"));

    public JsonlConsentAuditLogTests() { Directory.CreateDirectory(_tmpDir); }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    [Fact]
    public async Task Append_WritesOneLinePerEvent()
    {
        var path = Path.Combine(_tmpDir, "audit.jsonl");
        await using (var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "SPEAKER_JOINED", "Speaker 1", null));
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "CONSENT_GRANTED", "Speaker 1", null));
        }

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        foreach (var l in lines) JsonDocument.Parse(l).Dispose(); // assert valid JSON
    }

    [Fact]
    public async Task Append_NeverIncludesTranscriptContent()
    {
        var path = Path.Combine(_tmpDir, "audit.jsonl");
        await using (var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "DROPPED_TRANSCRIPT_NO_CONSENT", "Speaker 1",
                new Dictionary<string, object?> { ["reason"] = "post_stt_filter" }));
        }

        var content = await File.ReadAllTextAsync(path);
        // Sanity: details may include reason codes but never raw transcript text.
        Assert.DoesNotContain("transcript_text", content);
    }
}
```

- [ ] **Step 2: Run, verify fail**

- [ ] **Step 3: Create `AuditEvent.cs`**

```csharp
namespace Pia.Services.Consent;

public sealed record AuditEvent(
    Guid EventId,
    DateTimeOffset Timestamp,
    string EventType,
    string? SpeakerLabel,
    IReadOnlyDictionary<string, object?>? Details);
```

- [ ] **Step 4: Create `IConsentAuditLog.cs`**

```csharp
namespace Pia.Services.Consent;

public interface IConsentAuditLog : IAsyncDisposable
{
    void Append(AuditEvent evt);
}
```

- [ ] **Step 5: Implement `JsonlConsentAuditLog.cs`**

Implementation notes:
- Open file with `FileMode.Append`, `FileShare.Read`.
- Single background task draining a `Channel<AuditEvent>` (bounded, drop-newest-on-overflow with a logged warning — losing audit lines must be visible).
- `System.Text.Json` serialization, one line per event, ASCII-safe.
- `DisposeAsync` completes the channel writer and awaits flush.
- **Critical:** never serialize free-text transcript content. `Details` is opaque metadata only — document this on the type.

- [ ] **Step 6: Run tests, verify pass**

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/Consent/AuditEvent.cs src/Pia.Wpf/Services/Consent/IConsentAuditLog.cs src/Pia.Wpf/Services/Consent/JsonlConsentAuditLog.cs tests/Pia.Wpf.Tests/Consent/JsonlConsentAuditLogTests.cs
git commit -m "feat(consent): add append-only JSONL audit log"
```

---

## Chunk 4: Pipeline integration

### Task 10: Wire `IConsentGate` into `LiveTranscriptionEngineService`

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs`
- Test: `tests/Pia.Wpf.Tests/Consent/LiveTranscriptionEngineConsentIntegrationTests.cs`

- [ ] **Step 1: Write the failing integration test**

The existing engine wires `IAudioCaptureSource` → VAD → STT → sink. The test uses a fake `IAudioCaptureSource`, a fake `ITranscriptionEngine`, and a fake `IConsentGate` that returns `Drop` for `"Speaker 1"`. Assert: zero utterances reach the sink. Then flip the gate to `PassToTranscript` and assert: utterances flow.

```csharp
// File: tests/Pia.Wpf.Tests/Consent/LiveTranscriptionEngineConsentIntegrationTests.cs
// (sketch — see existing tests under tests/Pia.Wpf.Tests/ for fakes)
```

> The implementer must follow existing test patterns in `tests/Pia.Wpf.Tests/` for how engines are exercised — there is precedent in the live-transcription test suite. If no precedent exists, write a fake `IAudioCaptureSource` that emits a single 1.5 s sine wave + silence frame.

- [ ] **Step 2: Run, verify fail**

- [ ] **Step 3: Modify `LiveTranscriptionEngineService.cs`**

Changes:
1. Add constructor parameter `IConsentGate? consentGate = null` (nullable so non-loopback engines and existing callers still compile).
2. In `TranscribeSegmentAsync`, after determining `speakerLabel`:
   ```csharp
   if (_consentGate is not null && speakerLabel is not null)
   {
       var decision = _consentGate.Evaluate(speakerLabel);
       if (decision == GateDecision.Drop)
       {
           _logger.LogInformation("Consent gate dropped segment for {Label}", speakerLabel);
           return;
       }
       if (decision == GateDecision.PassToConsentClassifier)
       {
           // route to consent path: emit a TranscriptUtterance tagged as consent-classification.
           await _sink.WriteAsync(utt with { Channel = TranscriptChannel.ConsentClassification }, cancellationToken);
           return;
       }
   }
   ```
3. Extend `TranscriptUtterance` with a `TranscriptChannel Channel` field (`{ Regular, ConsentClassification }`), defaulting to `Regular` so existing callsites are unaffected.

- [ ] **Step 4: Run tests, verify pass**

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs src/Pia.Wpf/Models/TranscriptUtterance.cs tests/Pia.Wpf.Tests/Consent/LiveTranscriptionEngineConsentIntegrationTests.cs
git commit -m "feat(consent): gate STT pipeline on per-speaker consent state"
```

### Task 11: Post-STT defense filter in `LiveMeetingService`

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs`

- [ ] **Step 1: Modify `Utterances` channel reader path**

`LiveMeetingService` exposes `Utterances` directly. Wrap the channel: introduce a private intermediate channel; a forwarder task reads raw utterances, consults `IConsentStateManager`, and:
- `Channel == ConsentClassification`: invoke `IConsentClassifier`, then `mgr.RecordClassification(...)`. Do NOT forward to public `Utterances`. Append `CONSENT_GRANTED|CONSENT_DENIED|CONSENT_AMBIGUOUS` to audit log.
- `Channel == Regular` and `mgr.CurrentState(label) != Granted`: drop, append `DROPPED_TRANSCRIPT_NO_CONSENT` audit event, log warning (defense-in-depth — should be unreachable thanks to gate).
- Otherwise forward unchanged.

- [ ] **Step 2: Inject new dependencies via constructor**

`IConsentStateManager`, `IConsentClassifier`, `IConsentAuditLog`, `IConsentGate`, `ITtsService` (already DI-resolved elsewhere — confirm).

- [ ] **Step 3: On `NewSpeakerJoined` (detected by first-time speaker label from utterance forwarder):**

```
1. mgr.GetOrCreate(label)
2. audit.Append(SPEAKER_JOINED)
3. await _tts.SpeakAsync(InitialConsentLocalOnlyDe.Text)
4. mgr.MarkPrompted(label)
5. audit.Append(CONSENT_PROMPTED with prompt_hash=...)
```

- [ ] **Step 4: Periodic timeout sweep**

Start a `PeriodicTimer(TimeSpan.FromSeconds(2))` in `StartAsync` that calls `mgr.SweepTimeouts()`. On `Timeout` transition (subscribe to `mgr.StateChanged`): audit `CONSENT_TIMEOUT`.

- [ ] **Step 5: Build the project**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/LiveMeetingService.cs
git commit -m "feat(consent): wire consent state machine + audit log into live meeting"
```

### Task 12: DI registrations

**Files:**
- Modify: `src/Pia.Wpf/Bootstrapper.cs`

- [ ] **Step 1: Register the new services**

```csharp
services.AddSingleton<IConsentStateManager>(sp =>
    new ConsentStateManager(sp.GetRequiredService<ILogger<ConsentStateManager>>(), TimeProvider.System));
services.AddSingleton<IConsentClassifier, RuleBasedConsentClassifier>();
services.AddSingleton<IConsentGate, ConsentGate>();
services.AddSingleton<IConsentAuditLog>(sp =>
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "ConsentAudit");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, $"session_{Guid.NewGuid():N}.jsonl");
    return new JsonlConsentAuditLog(path, sp.GetRequiredService<ILogger<JsonlConsentAuditLog>>());
});
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build
git add src/Pia.Wpf/Bootstrapper.cs
git commit -m "feat(consent): register consent services in DI"
```

---

## Chunk 5: UI surface + verification

### Task 13: Surface `ConsentState` to view-model

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs`

- [ ] **Step 1: Inject `IConsentStateManager`**

- [ ] **Step 2: Add `[ObservableProperty] private string _consentBadge = ""` and subscribe to `mgr.StateChanged`**

Map `ConsentState` to a short German label: `Unknown="Warte auf Ansage"`, `Prompted="Frage läuft…"`, `Granted="Aufnahme freigegeben"`, `Denied="Aufnahme abgelehnt"`, `Timeout="Keine Antwort – Aufnahme gestoppt"`, `Ambiguous="Antwort unklar"`, `Revoked="Widerrufen"`.

- [ ] **Step 3: Marshal updates to UI thread via existing dispatcher pattern in this VM**

- [ ] **Step 4: Build + commit**

```bash
dotnet build
git add src/Pia.Wpf/ViewModels/LiveTranscriptionViewModel.cs
git commit -m "feat(consent): expose consent state to live-transcription VM"
```

### Task 14: Add badge to `LiveTranscriptionOverlay.xaml`

**Files:**
- Modify: `src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml`

- [ ] **Step 1: Add a `TextBlock` bound to `{Binding ConsentBadge}` near the existing speaker indicator**

Style: small pill, neutral colour. Visibility tied to non-empty value via existing `StringNotNullOrEmptyToBoolConverter`.

- [ ] **Step 2: Run app and visually verify**

```bash
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj
```

Manually walk through the golden path:
1. Start a meeting.
2. Speak from the loopback channel.
3. TTS plays `INITIAL_CONSENT_LOCAL_ONLY_DE`. Badge reads "Frage läuft…".
4. Reply "ja". Badge flips to "Aufnahme freigegeben". Transcript begins.
5. Stop. Inspect `%LOCALAPPDATA%\Pia\ConsentAudit\session_*.jsonl` — events present, no transcript text.

Edge cases to verify manually:
- Reply "vielleicht" → CLARIFICATION_AMBIGUOUS played, badge "Antwort unklar".
- Stay silent 16 s → badge "Keine Antwort – Aufnahme gestoppt", no transcript appears.
- Reply "nein" → badge "Aufnahme abgelehnt", subsequent speech is dropped.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Views/LiveTranscriptionOverlay.xaml
git commit -m "feat(consent): show consent state badge in overlay"
```

### Task 15: Final verification

- [ ] **Step 1: Run full test suite**

```bash
dotnet test
```
Expected: all green.

- [ ] **Step 2: Run end-to-end manual test in app per Task 14 checklist**

- [ ] **Step 3: Audit log inspection**

Open the latest `session_*.jsonl`. Verify:
- One `SPEAKER_JOINED` per detected speaker.
- One `CONSENT_PROMPTED` per prompt.
- Exactly one terminal event (`CONSENT_GRANTED`, `CONSENT_DENIED`, `CONSENT_TIMEOUT`, or `CONSENT_REVOKED`) per speaker.
- Zero occurrences of any raw transcript text — grep for utterance fragments.

- [ ] **Step 4: Tag the merge**

```bash
git tag consent-phase1-mvp
```

---

## Risks & open questions

1. **TTS playback path.** The spec (§3.8) requires TTS audio to reach the *outgoing* call — i.e., the other side hears the consent prompt, not just the local speaker. Phase 1 plays via existing local `ITtsService`. Routing to a virtual audio device is **out of scope** for Phase 1 and tracked for Phase 2. The prompt is therefore audible only locally; the user must hold a separate (manual) consent conversation. Document this clearly in the UI.
2. **Half-duplex protection.** While TTS plays, the loopback channel may capture the TTS itself and feed it back through diarization. Phase 1 mitigation: pause the loopback engine (`_loopbackEngine.PauseAsync()` — to add) while `_tts.IsPlaying`. If pause is too invasive, drop loopback segments whose timestamp overlaps `IsPlaying=true` windows.
3. **Speaker labels are non-unique across renames.** Existing `SpeakerIdentificationService.Rename` only swaps display labels. The consent state map is keyed by current label — renames must call `mgr.Rename(old,new)` (add to interface) to avoid losing state. Add to Task 4 if not already.

---

## Phase 1 follow-ups (next plans)

When Phase 1 is shipped, the next plan covers Phase 2:
- Multi-speaker Strategy B (`Selective Recording`).
- Hash-chained audit log.
- LLM classifier fallback for `Ambiguous` confidence band.
- Post-STT defense filter promoted to a first-class component (`PostSttDefenseFilter`).
- TTS routing to virtual audio device (Windows: VB-Cable / WASAPI loopback).

Subsequent plans (Phase 3, 4, 5) follow the spec's §8 schedule.
