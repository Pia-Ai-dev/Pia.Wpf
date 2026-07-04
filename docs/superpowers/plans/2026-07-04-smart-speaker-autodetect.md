# Smart Speaker Auto-Detect (Adaptive Diarization) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An optional (ON by default) smart auto-detect mode for meeting-attendee speaker diarization that continuously re-clusters all voice embeddings during the meeting and retroactively corrects the transcript, replacing the three manual tuning sliders.

**Architecture:** Keep the current instant per-segment labeling for latency; additionally journal every segment embedding and periodically re-run average-linkage agglomerative clustering (AHC) with a data-derived cut over the whole meeting. Changed assignments flow as `SpeakersReassigned` events from a new `AdaptiveSpeakerIdentificationService` through `MeetingAttendeeService` to the transcript ViewModel, which rebuilds its bubbles from a new per-utterance journal.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, sherpa-onnx `SpeakerEmbeddingExtractor`, xunit v3 (plain `Xunit.Assert`).

**Spec:** `docs/superpowers/specs/2026-07-04-smart-speaker-autodetect-design.md` — read it first; it defines every constant and contract used below.

---

## Global rules for this plan

- **Branch:** work on `feature/meeting_attendee` (current branch; the whole meeting attendee feature lives there, unmerged).
- **Line endings:** repo `.cs` files are CRLF. The Write tool emits LF. After creating each NEW file, convert it before committing:
  `pwsh: $p='<path>'; $c=[IO.File]::ReadAllText($p); [IO.File]::WriteAllText($p, ($c -replace "(?<!`r)`n", "`r`n"))`
- **Build gate:** `dotnet build` → 0 errors (pre-existing warnings like `xUnit1051`, `NU1903`, `MVVMTK0034` are OK).
- **Test gate:** `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` → 0 failures. (The excluded namespace holds ~18 known live-network failures; the runner is Microsoft.Testing.Platform, not VSTest.)
- **Namespaces:** production code uses `Pia.*` (NOT `Pia.Wpf.*`); tests use `Pia.Tests.*` (see `tests/Pia.Wpf.Tests/Services/LiveTranscription/LiveTranscriptionEngineDrainTests.cs` → `namespace Pia.Tests.Services.LiveTranscription;`).
- **Logging privacy:** speaker labels can become user-typed names after rename → log them only via `_logger.SensitiveInformation(...)` (from `Pia.Logging`). Counts, durations, cut distances are safe at `LogDebug`.
- **Commit style:** small commits per task, message prefix like existing history, trailer:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`

---

## Chunk 1: Contracts & plumbing

### Task 1: Interface additions + manual-service implementation

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/ISpeakerIdentificationService.cs`
- Modify: `src/Pia.Wpf/Services/LiveTranscription/SpeakerIdentificationService.cs`

No new behavior to TDD here (the manual service's new method is a thin wrapper whose ctor needs a native ONNX model — sanctioned no-unit-test, same precedent as `WipeBiometricStateUnderLock`). Compile + existing suite is the gate.

- [ ] **Step 1: Add the records and new members to the interface**

Append to `ISpeakerIdentificationService.cs` (inside the namespace, after the interface):

```csharp
/// <summary>Identify-or-register result carrying the journal id for the segment's embedding.</summary>
public readonly record struct SpeakerSegmentResult(long SegmentId, string Label);

/// <summary>One retroactive label correction produced by an adaptive re-cluster pass.</summary>
public readonly record struct SpeakerReassignment(long SegmentId, string NewLabel);
```

Add to the `ISpeakerIdentificationService` interface body:

```csharp
    /// <summary>
    /// Like <see cref="IdentifyOrRegister"/> but also returns the segment id under which the
    /// (adaptive) implementation journals this segment's embedding, so later
    /// <see cref="SpeakersReassigned"/> events can retarget the utterance. The manual
    /// implementation hands out monotonically increasing ids too — they are simply never
    /// reassigned.
    /// </summary>
    SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate);

    /// <summary>
    /// Raised after a re-cluster pass changed the label of already-emitted segments. Carries only
    /// the changed (SegmentId → new Label) pairs. Never raised by the manual implementation.
    /// Fires on the calling thread, outside the diarization lock.
    /// </summary>
    event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;
```

- [ ] **Step 2: Implement trivially in the manual `SpeakerIdentificationService`**

Add a field next to `_counter`:

```csharp
    private long _nextSegmentId;
```

Add members (near `IdentifyOrRegister`):

```csharp
    public SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate)
    {
        var label = IdentifyOrRegister(segmentSamples, sampleRate);
        var id = Interlocked.Increment(ref _nextSegmentId) - 1;
        return new SpeakerSegmentResult(id, label);
    }

    // Manual mode never revisits a decision, so the event can never fire. Explicit empty
    // accessors instead of a field avoid the CS0067 unused-event warning.
    public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned { add { } remove { } }
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: 0 errors. (The `MeetingAttendeeServiceStateTests` stubs pass `null` for the SpeakerId tuple element, so no test-side changes are needed.)

- [ ] **Step 4: Run the test gate**

Run: `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: 0 failures.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/ISpeakerIdentificationService.cs src/Pia.Wpf/Services/LiveTranscription/SpeakerIdentificationService.cs
git commit -m "Add segment-id identify + reassignment event to speaker identification contract"
```

### Task 2: `TranscriptUtterance.SegmentId` + engine stamping

**Files:**
- Modify: `src/Pia.Wpf/Models/TranscriptUtterance.cs`
- Modify: `src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs` (method `TranscribeSegmentAsync`, ~line 149)

- [ ] **Step 1: Add the additive record parameter**

```csharp
public sealed record TranscriptUtterance(
    TranscriptSpeaker Speaker,
    string Text,
    DateTimeOffset Timestamp,
    string? SpeakerLabel = null,
    long? SegmentId = null);
```

- [ ] **Step 2: Stamp it in the engine**

In `TranscribeSegmentAsync`, replace the identification block:

```csharp
            string? speakerLabel = null;
            long? segmentId = null;
            if (_speakerId is not null && samples.Length >= _minDiarizationSamples)
            {
                try
                {
                    var seg = _speakerId.IdentifyOrRegisterSegment(samples, 16000);
                    speakerLabel = seg.Label;
                    segmentId = seg.SegmentId;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Speaker identification failed for {Speaker}", _speaker); }
            }
```

and the utterance construction:

```csharp
            var utt = new TranscriptUtterance(_speaker, text, DateTimeOffset.Now, speakerLabel, segmentId);
```

(Sub-minimum segments keep `SpeakerLabel = null, SegmentId = null` — the pinned null-split behavior is untouched.)

- [ ] **Step 3: Build + test gate**

Run: `dotnet build` then `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: 0 errors, 0 failures. Note: there is no engine-level runtime test for the stamping (constructing the engine needs a real Silero model — sanctioned skip, consistent with `LiveTranscriptionEngineDrainTests` which deliberately bypasses the real engine). The id flow is covered end-to-end by the service tests (Chunk 3) and VM tests (Chunk 4).

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Models/TranscriptUtterance.cs src/Pia.Wpf/Services/LiveTranscription/LiveTranscriptionEngineService.cs
git commit -m "Stamp diarization segment id onto transcript utterances"
```

---

## Chunk 2: SpeakerClusterer (pure clustering logic)

### Task 3: `ChooseCut` — data-derived cut selection (TDD)

**Files:**
- Create: `src/Pia.Wpf/Services/LiveTranscription/SpeakerClusterer.cs`
- Create: `tests/Pia.Wpf.Tests/Services/LiveTranscription/SpeakerClustererTests.cs`

`ChooseCut` is a pure static function over the sorted merge-distance sequence — test it with hand-written arrays before any geometry.

- [ ] **Step 1: Write the failing tests**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/SpeakerClustererTests.cs`:

```csharp
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class SpeakerClustererTests
{
    // ---- ChooseCut (pure cut selection over sorted merge distances) ----------------------------

    [Fact]
    public void ChooseCut_AllMergesBelowBand_ReturnsCutMin_SingleCluster()
    {
        // Everything merges tightly → one speaker; cut reported as CutMin keeps the derived
        // instant-match threshold strict (sim ≥ 0.70), not degenerate.
        var cut = SpeakerClusterer.ChooseCut(new[] { 0.01f, 0.02f, 0.03f }, previousClusterCount: 0);
        Assert.Equal(SpeakerClusterer.CutMin, cut);
    }

    [Fact]
    public void ChooseCut_ClearGapIntoBand_CutsInsideGap()
    {
        // Within-speaker merges ~0.03, one between-speaker merge at 0.50 → cut lands strictly
        // between 0.04 and 0.50 (midpoint clamped up to CutMin).
        var cut = SpeakerClusterer.ChooseCut(new[] { 0.02f, 0.04f, 0.50f }, previousClusterCount: 0);
        Assert.InRange(cut, 0.04f + 0.001f, 0.50f - 0.001f);
        Assert.True(cut >= SpeakerClusterer.CutMin);
    }

    [Fact]
    public void ChooseCut_NoMergeInBand_FallsBackToDefault()
    {
        // Upper edges 0.90/0.95 are above CutMax → no candidate → today's default 0.50.
        var cut = SpeakerClusterer.ChooseCut(new[] { 0.02f, 0.90f, 0.95f }, previousClusterCount: 0);
        Assert.Equal(SpeakerClusterer.FallbackCut, cut);
    }

    [Fact]
    public void ChooseCut_AmbiguousGaps_PrefersPreviousClusterCount()
    {
        // Candidates: i=0 (upper 0.33, gap 0.31 → 4 clusters) and i=2 (upper 0.64, gap 0.29
        // → 2 clusters). Gap difference 0.02 < HysteresisGapDelta → with previousClusterCount=2
        // the 2-cluster cut wins; without a previous count the larger gap wins.
        var seq = new[] { 0.02f, 0.33f, 0.35f, 0.64f };

        var sticky = SpeakerClusterer.ChooseCut(seq, previousClusterCount: 2);
        Assert.InRange(sticky, 0.36f, 0.63f);   // between 0.35 and 0.64 → yields 2 clusters

        var fresh = SpeakerClusterer.ChooseCut(seq, previousClusterCount: 0);
        Assert.InRange(fresh, SpeakerClusterer.CutMin, 0.32f); // below 0.33 → yields 4 clusters
    }
}
```

- [ ] **Step 2: Create a stub so the test compiles, run tests, verify they fail**

Create `src/Pia.Wpf/Services/LiveTranscription/SpeakerClusterer.cs` with the class skeleton and constants but `ChooseCut` returning `0f`:

```csharp
namespace Pia.Services.LiveTranscription;

/// <summary>
/// Result of one re-clustering pass over all journaled embeddings.
/// <see cref="AssignmentPerSegment"/>[i] is the cluster index (0..ClusterCount-1, numbered in
/// first-appearance order) of embeddings[i]. <see cref="CutDistance"/> is the cosine-DISTANCE cut
/// the pass used, clamped to the guardrail band — consumers derive the instant-match similarity
/// threshold as 1 − CutDistance.
/// </summary>
public sealed record ClusterResult(int[] AssignmentPerSegment, int ClusterCount, float CutDistance);

/// <summary>
/// Average-linkage agglomerative clustering (AHC) over L2-NORMALIZED speaker embeddings with a
/// data-derived cut: instead of a user-tuned similarity threshold, the cut falls into the largest
/// gap of the dendrogram's merge-distance sequence — the natural boundary between within-speaker
/// and between-speaker distance for THESE voices. Pure logic: no I/O, no native deps,
/// deterministic. O(n²) time (Lance–Williams + nearest-neighbor cache), O(n²) memory.
/// </summary>
public sealed class SpeakerClusterer
{
    // Guardrail band for the cut (cosine distance = 1 − cosine similarity). A cut outside this
    // band would mean an implausible speaker geometry — likely a degenerate gap — so we never cut
    // there. 0.50 distance == today's default manual threshold (sim 0.50).
    internal const float CutMin = 0.30f;
    internal const float CutMax = 0.70f;
    internal const float FallbackCut = 0.50f;
    // Two nearly-equal gaps (< this delta apart) are treated as ambiguous → prefer the cut that
    // keeps the previous pass's cluster count (label-churn dampening).
    internal const float HysteresisGapDelta = 0.03f;
    // Over-segmentation guard; matches the manual mode's max cap (12).
    internal const int MaxClusters = 12;

    internal static float ChooseCut(float[] sortedMergeDistances, int previousClusterCount)
    {
        return 0f; // stub
    }
}
```

Convert the two new files to CRLF (see global rules).

Run: `dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.SpeakerClustererTests"`
Expected: 4 FAILED (assert mismatches).
(If `--filter-class` is not accepted by the MTP runner in this repo, fall back to the full gate command — the 4 new tests must be the only failures.)

- [ ] **Step 3: Implement `ChooseCut`**

Replace the stub:

```csharp
    /// <summary>
    /// Chooses the cut distance from the SORTED merge-distance sequence of a dendrogram with
    /// n = sortedMergeDistances.Length + 1 leaves. Candidate cuts sit between consecutive merges
    /// whose upper edge falls inside the guardrail band; the largest gap wins, with hysteresis
    /// toward <paramref name="previousClusterCount"/> on near-ties. Accepting all merges strictly
    /// below the returned cut yields the clustering.
    /// </summary>
    internal static float ChooseCut(float[] sortedMergeDistances, int previousClusterCount)
    {
        var seq = sortedMergeDistances;
        if (seq.Length == 0) return CutMin;
        if (seq[^1] < CutMin) return CutMin;            // everything is one speaker

        // Candidate i = cut between seq[i] and seq[i+1]. Accepting merges 0..i leaves
        // (seq.Length - i) clusters. Only consider candidates whose upper edge is in the band.
        List<(float Gap, float Cut, int ClusterCount)> candidates = new();
        for (int i = 0; i + 1 < seq.Length; i++)
        {
            var upper = seq[i + 1];
            if (upper < CutMin || upper > CutMax) continue;
            var cut = Math.Clamp((seq[i] + upper) / 2f, CutMin, CutMax);
            candidates.Add((upper - seq[i], cut, seq.Length - i));
        }
        if (candidates.Count == 0) return FallbackCut;

        candidates.Sort((x, y) => y.Gap.CompareTo(x.Gap));
        var best = candidates[0];
        if (previousClusterCount > 0)
        {
            foreach (var c in candidates)
            {
                if (best.Gap - c.Gap >= HysteresisGapDelta) break;   // sorted → rest are worse
                if (c.ClusterCount == previousClusterCount) return c.Cut;
            }
        }
        return best.Cut;
    }
```

- [ ] **Step 4: Run the tests, verify they pass**

Run: `dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.SpeakerClustererTests"`
Expected: 4 PASSED.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/SpeakerClusterer.cs tests/Pia.Wpf.Tests/Services/LiveTranscription/SpeakerClustererTests.cs
git commit -m "Add SpeakerClusterer cut selection with guardrail band and hysteresis"
```

### Task 4: `Cluster` — AHC end-to-end (TDD)

**Files:**
- Modify: `src/Pia.Wpf/Services/LiveTranscription/SpeakerClusterer.cs`
- Modify: `tests/Pia.Wpf.Tests/Services/LiveTranscription/SpeakerClustererTests.cs`

Geometric tests use 2-D unit vectors at controlled angles: cosine distance between directions δ° apart is `1 − cos δ`. Within-speaker jitter ±2° ≈ distance ≤ 0.003; speakers 55–65° apart land in the guardrail band (0.43–0.57).

- [ ] **Step 1: Write the failing tests**

Append to `SpeakerClustererTests.cs`:

```csharp
    // ---- Cluster (geometric end-to-end) ---------------------------------------------------------

    private static float[] Vec(double degrees)
    {
        var r = Math.PI * degrees / 180.0;
        return new[] { (float)Math.Cos(r), (float)Math.Sin(r) };
    }

    [Fact]
    public void Cluster_TwoSpeakersSixtyDegreesApart_TwoClusters()
    {
        var e = new[] { Vec(0), Vec(2), Vec(4), Vec(60), Vec(62), Vec(64) };
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(2, r.ClusterCount);
        Assert.Equal(r.AssignmentPerSegment[0], r.AssignmentPerSegment[1]);
        Assert.Equal(r.AssignmentPerSegment[0], r.AssignmentPerSegment[2]);
        Assert.Equal(r.AssignmentPerSegment[3], r.AssignmentPerSegment[4]);
        Assert.Equal(r.AssignmentPerSegment[3], r.AssignmentPerSegment[5]);
        Assert.NotEqual(r.AssignmentPerSegment[0], r.AssignmentPerSegment[3]);
        // First-appearance numbering: segment 0's cluster is 0.
        Assert.Equal(0, r.AssignmentPerSegment[0]);
    }

    [Fact]
    public void Cluster_SingleSpeaker_OneCluster_ReportsCutMin()
    {
        var e = new[] { Vec(0), Vec(1), Vec(2), Vec(3) };
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(1, r.ClusterCount);
        Assert.All(r.AssignmentPerSegment, a => Assert.Equal(0, a));
        Assert.Equal(SpeakerClusterer.CutMin, r.CutDistance);
    }

    [Fact]
    public void Cluster_ThreeSpeakers_ThreeClusters()
    {
        var e = new[] { Vec(0), Vec(2), Vec(55), Vec(57), Vec(115), Vec(117) };
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(3, r.ClusterCount);
        Assert.Equal(r.AssignmentPerSegment[0], r.AssignmentPerSegment[1]);
        Assert.Equal(r.AssignmentPerSegment[2], r.AssignmentPerSegment[3]);
        Assert.Equal(r.AssignmentPerSegment[4], r.AssignmentPerSegment[5]);
        Assert.Equal(3, r.AssignmentPerSegment.Distinct().Count());
    }

    [Fact]
    public void Cluster_OutlierFirstSegment_StillJoinsItsSpeaker()
    {
        // The "poisoned first impression": segment 0 is off-center for speaker A but far from B —
        // a full re-cluster puts it with A. This is the self-healing property the feature promises.
        var e = new[] { Vec(10), Vec(0), Vec(2), Vec(4), Vec(60), Vec(62) };
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(2, r.ClusterCount);
        Assert.Equal(r.AssignmentPerSegment[1], r.AssignmentPerSegment[0]);
    }

    [Fact]
    public void Cluster_MoreClustersThanCap_MergedDownToTwelve()
    {
        // 14 mutually-orthogonal one-hot embeddings: every merge distance is 1.0 (out of band)
        // → fallback cut accepts none → 14 singletons → the cap merges down to 12.
        var e = Enumerable.Range(0, 14).Select(i =>
        {
            var v = new float[14];
            v[i] = 1f;
            return v;
        }).ToArray();
        var r = new SpeakerClusterer().Cluster(e);

        Assert.Equal(SpeakerClusterer.MaxClusters, r.ClusterCount);
        Assert.True(r.CutDistance <= SpeakerClusterer.CutMax); // reported cut stays in band
    }

    [Fact]
    public void Cluster_EdgeCases_EmptyAndSingle()
    {
        var empty = new SpeakerClusterer().Cluster(Array.Empty<float[]>());
        Assert.Equal(0, empty.ClusterCount);
        Assert.Empty(empty.AssignmentPerSegment);

        var one = new SpeakerClusterer().Cluster(new[] { Vec(0) });
        Assert.Equal(1, one.ClusterCount);
        Assert.Equal(new[] { 0 }, one.AssignmentPerSegment);
    }
```

- [ ] **Step 2: Run to verify the new tests fail**

Run: `dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.SpeakerClustererTests"`
Expected: the 6 new tests FAIL (no `Cluster` method yet → compile error first; add the method stub `public ClusterResult Cluster(IReadOnlyList<float[]> embeddings, int previousClusterCount = 0) => new(Array.Empty<int>(), 0, CutMin);` if needed to get red-not-broken).

- [ ] **Step 3: Implement `Cluster` + dendrogram**

Add to `SpeakerClusterer`:

```csharp
    /// <summary>
    /// Clusters L2-normalized embeddings (caller's contract) by average-linkage AHC and the
    /// <see cref="ChooseCut"/> policy. <paramref name="previousClusterCount"/> (0 = none) feeds
    /// the hysteresis. Assignments are numbered 0..k-1 in first-appearance order so callers get
    /// stable, comparable indexes for the same input.
    /// </summary>
    public ClusterResult Cluster(IReadOnlyList<float[]> embeddings, int previousClusterCount = 0)
    {
        int n = embeddings.Count;
        if (n == 0) return new ClusterResult(Array.Empty<int>(), 0, CutMin);
        if (n == 1) return new ClusterResult(new[] { 0 }, 1, CutMin);

        var merges = BuildDendrogram(embeddings);

        // Average linkage is monotonic (reducible), so merge order == sorted order; sort
        // defensively anyway so ChooseCut's contract is honored under float noise.
        var sorted = new float[merges.Count];
        for (int i = 0; i < merges.Count; i++) sorted[i] = merges[i].Distance;
        Array.Sort(sorted);

        var cut = ChooseCut(sorted, previousClusterCount);

        // Accept merges strictly below the cut via union-find over representative indexes.
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        int clusters = n;
        foreach (var m in merges)
        {
            if (m.Distance >= cut) continue;
            var (ra, rb) = (Find(m.A), Find(m.B));
            if (ra == rb) continue;
            parent[rb] = ra;
            clusters--;
        }

        // Over-segmentation guard: keep merging (cheapest remaining first — merges are already
        // in ascending order) until the cap is met; the reported cut follows the last merge.
        if (clusters > MaxClusters)
        {
            foreach (var m in merges)
            {
                if (clusters <= MaxClusters) break;
                var (ra, rb) = (Find(m.A), Find(m.B));
                if (ra == rb) continue;
                parent[rb] = ra;
                clusters--;
                cut = Math.Max(cut, m.Distance);
            }
        }

        // Root → 0..k-1 in first-appearance order.
        var assignment = new int[n];
        var indexByRoot = new Dictionary<int, int>(clusters);
        for (int i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!indexByRoot.TryGetValue(root, out var idx))
            {
                idx = indexByRoot.Count;
                indexByRoot[root] = idx;
            }
            assignment[i] = idx;
        }

        return new ClusterResult(assignment, indexByRoot.Count, Math.Clamp(cut, CutMin, CutMax));
    }

    /// <summary>
    /// Average-linkage dendrogram via Lance–Williams updates with a per-row nearest-neighbor
    /// cache (O(n²) average). Returns the n−1 merges in merge order; A/B are representative
    /// ORIGINAL segment indexes (B folds into A).
    /// </summary>
    private static List<(int A, int B, float Distance)> BuildDendrogram(IReadOnlyList<float[]> embeddings)
    {
        int n = embeddings.Count;
        var dist = new float[n][];
        for (int i = 0; i < n; i++) dist[i] = new float[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                var d = 1f - Dot(embeddings[i], embeddings[j]);
                dist[i][j] = d;
                dist[j][i] = d;
            }
        }

        var active = new bool[n];
        Array.Fill(active, true);
        var size = new int[n];
        Array.Fill(size, 1);
        var nn = new int[n];
        var nnd = new float[n];

        void Refresh(int i)
        {
            var best = float.PositiveInfinity;
            var bi = -1;
            var row = dist[i];
            for (int j = 0; j < n; j++)
            {
                if (j == i || !active[j]) continue;
                if (row[j] < best) { best = row[j]; bi = j; }
            }
            nn[i] = bi;
            nnd[i] = best;
        }
        for (int i = 0; i < n; i++) Refresh(i);

        var merges = new List<(int A, int B, float Distance)>(n - 1);
        for (int step = 0; step < n - 1; step++)
        {
            var best = float.PositiveInfinity;
            int a = -1;
            for (int i = 0; i < n; i++)
            {
                if (active[i] && nnd[i] < best) { best = nnd[i]; a = i; }
            }
            var b = nn[a];
            merges.Add((a, b, best));

            // Lance–Williams average linkage: d(k, a∪b) = (|a|·d(k,a) + |b|·d(k,b)) / (|a|+|b|).
            var (sa, sb) = (size[a], size[b]);
            for (int k = 0; k < n; k++)
            {
                if (!active[k] || k == a || k == b) continue;
                var d = (sa * dist[a][k] + sb * dist[b][k]) / (sa + sb);
                dist[a][k] = d;
                dist[k][a] = d;
            }
            size[a] += size[b];
            active[b] = false;

            // The merged row changed and rows pointing at a or b went stale; averages can only
            // grow past cached minima, so other caches stay valid.
            Refresh(a);
            for (int k = 0; k < n; k++)
            {
                if (active[k] && k != a && (nn[k] == a || nn[k] == b)) Refresh(k);
            }
        }
        return merges;
    }

    private static float Dot(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
```

- [ ] **Step 4: Run all clusterer tests, verify they pass**

Run: `dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.SpeakerClustererTests"`
Expected: 10 PASSED.

- [ ] **Step 5: Run the full test gate (regression check)**

Run: `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/LiveTranscription/SpeakerClusterer.cs tests/Pia.Wpf.Tests/Services/LiveTranscription/SpeakerClustererTests.cs
git commit -m "Add average-linkage AHC clustering with data-derived cut"
```

---

## Chunk 3: Extractor seam + adaptive service

### Task 5: `IEmbeddingExtractor` seam

**Files:**
- Create: `src/Pia.Wpf/Services/LiveTranscription/IEmbeddingExtractor.cs`
- Create: `src/Pia.Wpf/Services/LiveTranscription/SherpaEmbeddingExtractor.cs`

Wrapper-only task (native ctor → sanctioned no-unit-test); the seam exists so Task 6 is fully testable.

- [ ] **Step 1: Create the interface**

`src/Pia.Wpf/Services/LiveTranscription/IEmbeddingExtractor.cs`:

```csharp
namespace Pia.Services.LiveTranscription;

/// <summary>
/// Seam over the native speaker-embedding extractor so the adaptive diarizer can be unit-tested
/// without an ONNX model. Production implementation: <see cref="SherpaEmbeddingExtractor"/>.
/// </summary>
public interface IEmbeddingExtractor : IDisposable
{
    /// <summary>Embedding dimensionality.</summary>
    int Dim { get; }

    /// <summary>Computes the voice embedding for a 16 kHz mono float32 segment.</summary>
    float[] Compute(float[] samples, int sampleRate);
}
```

- [ ] **Step 2: Create the sherpa-onnx implementation**

`src/Pia.Wpf/Services/LiveTranscription/SherpaEmbeddingExtractor.cs` (mirrors the construction in `SpeakerIdentificationService`'s ctor and its `ComputeEmbedding`):

```csharp
using SherpaOnnx;

namespace Pia.Services.LiveTranscription;

/// <summary>Production <see cref="IEmbeddingExtractor"/> over sherpa-onnx.</summary>
public sealed class SherpaEmbeddingExtractor : IEmbeddingExtractor
{
    private readonly SpeakerEmbeddingExtractor _extractor;

    public SherpaEmbeddingExtractor(string modelPath)
    {
        var config = new SpeakerEmbeddingExtractorConfig();
        config.Model = modelPath;
        config.NumThreads = 1;
        config.Provider = "cpu";
        config.Debug = 0;
        _extractor = new SpeakerEmbeddingExtractor(config);
    }

    public int Dim => _extractor.Dim;

    public float[] Compute(float[] samples, int sampleRate)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        stream.InputFinished();
        return _extractor.Compute(stream);
    }

    public void Dispose() => _extractor.Dispose();
}
```

Convert both files to CRLF.

- [ ] **Step 3: Build + commit**

Run: `dotnet build` → 0 errors.

```bash
git add src/Pia.Wpf/Services/LiveTranscription/IEmbeddingExtractor.cs src/Pia.Wpf/Services/LiveTranscription/SherpaEmbeddingExtractor.cs
git commit -m "Add embedding-extractor seam over sherpa-onnx"
```

### Task 6: `AdaptiveSpeakerIdentificationService` (TDD)

**Files:**
- Create: `src/Pia.Wpf/Services/LiveTranscription/AdaptiveSpeakerIdentificationService.cs`
- Create: `tests/Pia.Wpf.Tests/Services/LiveTranscription/AdaptiveSpeakerIdentificationServiceTests.cs`

The fake extractor maps `samples[0]` (an angle in degrees) to a 2-D unit vector — tests control voice geometry exactly like the clusterer tests.

- [ ] **Step 1: Write the failing tests**

Create `tests/Pia.Wpf.Tests/Services/LiveTranscription/AdaptiveSpeakerIdentificationServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class AdaptiveSpeakerIdentificationServiceTests
{
    private sealed class FakeExtractor : IEmbeddingExtractor
    {
        public int Dim => 2;
        public bool Disposed;
        public float[] Compute(float[] samples, int sampleRate)
        {
            var r = Math.PI * samples[0] / 180.0;
            return new[] { (float)Math.Cos(r), (float)Math.Sin(r) };
        }
        public void Dispose() => Disposed = true;
    }

    /// <summary>A "segment" whose first sample encodes the voice direction in degrees.</summary>
    private static float[] Seg(double degrees) => new[] { (float)degrees };

    private static AdaptiveSpeakerIdentificationService Create(
        FakeExtractor? extractor = null, Func<DateTimeOffset>? now = null)
        => new(extractor ?? new FakeExtractor(), NullLogger<AdaptiveSpeakerIdentificationService>.Instance, now);

    [Fact]
    public void FirstSegment_RegistersSpeaker1_AndRaisesSpeakerRegistered()
    {
        using var svc = Create();
        var registered = new List<string>();
        svc.SpeakerRegistered += (_, label) => registered.Add(label);

        var r = svc.IdentifyOrRegisterSegment(Seg(0), 16000);

        Assert.Equal("Speaker 1", r.Label);
        Assert.Equal(0, r.SegmentId);
        Assert.Equal(new[] { "Speaker 1" }, registered);
    }

    [Fact]
    public void CloseSegments_ShareTheLabel_DistantSegmentGetsANewOne()
    {
        using var svc = Create();

        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(0), 16000).Label);
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(3), 16000).Label);
        // 80° apart → sim cos80 ≈ 0.17 < 0.50 initial threshold → new speaker.
        Assert.Equal("Speaker 2", svc.IdentifyOrRegisterSegment(Seg(80), 16000).Label);
    }

    [Fact]
    public void ReclusterPass_SplitsAProvisionallyMergedVoice_AndEmitsOnlyChangedSegments()
    {
        using var svc = Create();
        var events = new List<IReadOnlyList<SpeakerReassignment>>();
        svc.SpeakersReassigned += (_, e) => events.Add(e);

        // Speaker A: 3 segments around 0°. Speaker B: around 55° — cos55 ≈ 0.574 ≥ 0.50, so the
        // instant path wrongly merges B into "Speaker 1" (the exact first-impression failure).
        foreach (var deg in new[] { 0.0, 2, 4 })
            Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(deg), 16000).Label);
        foreach (var deg in new[] { 55.0, 57 })
            Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(deg), 16000).Label);

        // 6th segment reaches warm-up; the pass re-clusters and splits B out retroactively.
        var sixth = svc.IdentifyOrRegisterSegment(Seg(59), 16000);

        var change = Assert.Single(events);
        Assert.All(change, c => Assert.Equal("Speaker 2", c.NewLabel));
        Assert.Equal(new long[] { 3, 4, 5 }, change.Select(c => c.SegmentId).OrderBy(x => x).ToArray());
        // Earliest-segment tie-break keeps "Speaker 1" on the earlier voice.
        Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(1), 16000).Label);
    }

    [Fact]
    public void ElapsedTime_TriggersAPass_EvenBelowTheSegmentStride()
    {
        var clock = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        using var svc = Create(now: () => clock);
        var events = new List<IReadOnlyList<SpeakerReassignment>>();
        svc.SpeakersReassigned += (_, e) => events.Add(e);

        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000); // pass at #6, clean
        svc.IdentifyOrRegisterSegment(Seg(55), 16000);                            // wrongly merged
        Assert.Empty(events);                                                     // stride not reached

        clock += TimeSpan.FromSeconds(31);
        svc.IdentifyOrRegisterSegment(Seg(1), 16000);                             // latency trigger

        var change = Assert.Single(events);
        var single = Assert.Single(change);
        Assert.Equal(6, single.SegmentId);
        Assert.Equal("Speaker 2", single.NewLabel);
    }

    [Fact]
    public void Rename_SurvivesReclusterPasses()
    {
        using var svc = Create();
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        Assert.True(svc.Rename("Speaker 1", "Alice"));

        // New distinct voice + enough segments for another pass.
        for (var i = 0; i < 5; i++) svc.IdentifyOrRegisterSegment(Seg(80 + i), 16000);
        Assert.Equal("Alice", svc.IdentifyOrRegisterSegment(Seg(2), 16000).Label);
    }

    [Fact]
    public void Reset_RestartsNumbering_AndStopsReassignments()
    {
        using var svc = Create();
        for (var i = 0; i < 6; i++) svc.IdentifyOrRegisterSegment(Seg(i), 16000);

        svc.Reset();

        var r = svc.IdentifyOrRegisterSegment(Seg(0), 16000);
        Assert.Equal("Speaker 1", r.Label);
        // Segment ids stay monotonic across Reset so stale UI reassignments can never collide.
        Assert.Equal(6, r.SegmentId);
    }

    [Fact]
    public void Dispose_DisposesTheExtractor()
    {
        var extractor = new FakeExtractor();
        var svc = Create(extractor);
        svc.IdentifyOrRegisterSegment(Seg(0), 16000);
        svc.Dispose();
        Assert.True(extractor.Disposed);
    }

    [Fact]
    public void IdentifyOrRegisterWithEmbedding_ReturnsAUnitEmbedding()
    {
        using var svc = Create();
        var (label, embedding) = svc.IdentifyOrRegisterWithEmbedding(Seg(0), 16000);
        Assert.Equal("Speaker 1", label);
        Assert.Equal(1f, embedding[0] * embedding[0] + embedding[1] * embedding[1], 3);
    }

    [Fact]
    public void JournalCap_DropsOldest_WithoutBreakingLabeling()
    {
        using var svc = new AdaptiveSpeakerIdentificationService(
            new FakeExtractor(), NullLogger<AdaptiveSpeakerIdentificationService>.Instance,
            now: null, maxJournaledSegments: 8);

        for (var i = 0; i < 20; i++)
            Assert.Equal("Speaker 1", svc.IdentifyOrRegisterSegment(Seg(i % 4), 16000).Label);
    }
}
```

- [ ] **Step 2: Create the service stub, run tests, verify they fail**

Create `src/Pia.Wpf/Services/LiveTranscription/AdaptiveSpeakerIdentificationService.cs` with the class implementing `ISpeakerIdentificationService`, the two ctors, and `NotImplementedException` bodies. Convert to CRLF. Run the new test class; expected: all FAIL.

- [ ] **Step 3: Implement the service**

Full implementation:

```csharp
using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Adaptive ("smart auto-detect") within-meeting speaker identification. Serves every segment an
/// INSTANT provisional label (nearest cluster centroid — same latency as the manual service) and
/// additionally journals every embedding; periodically it re-clusters the whole meeting with
/// <see cref="SpeakerClusterer"/> so early mistakes self-heal, emitting
/// <see cref="SpeakersReassigned"/> for retro-corrections. No user tuning: the cut (and thus the
/// instant-match threshold) is derived from the data each pass.
///
/// Privacy: journaled embeddings are biometric data — in-memory only, per meeting, actively
/// zeroed on <see cref="Reset"/>/<see cref="Dispose"/> (same discipline as the manual service's
/// WipeBiometricStateUnderLock).
/// </summary>
public sealed class AdaptiveSpeakerIdentificationService : ISpeakerIdentificationService
{
    // A pass needs enough evidence to beat the provisional path; below this we stay provisional.
    internal const int WarmupSegments = 6;
    // Pass cadence: every N new segments, or after this latency once at least one new segment
    // arrived. Passes are cheap (O(n²) Lance–Williams) but not free; the stride bounds churn.
    internal const int PassSegmentStride = 5;
    internal static readonly TimeSpan PassMaxLatency = TimeSpan.FromSeconds(30);
    internal const int DefaultMaxJournaledSegments = 2000;
    internal const float InitialMatchSimilarity = 0.50f;

    private readonly IEmbeddingExtractor _extractor;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _maxJournaledSegments;
    private readonly SpeakerClusterer _clusterer = new();

    private readonly object _lock = new();
    private readonly List<(long SegmentId, float[] Embedding)> _segments = new(); // oldest first
    private readonly Dictionary<long, int> _clusterBySegment = new();
    private readonly Dictionary<int, string> _labelByCluster = new();
    private readonly Dictionary<int, RunningCentroid> _centroidByCluster = new();
    private readonly HashSet<int> _renamedClusters = new();
    private long _nextSegmentId;
    private int _nextClusterId;
    private int _speakerCounter;
    private float _matchSimilarity = InitialMatchSimilarity;
    private int _segmentsSinceLastPass;
    private DateTimeOffset _lastPassAt;
    private bool _disposed;

    public event EventHandler<string>? SpeakerRegistered;
    public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;

    public AdaptiveSpeakerIdentificationService(
        IEmbeddingExtractor extractor, ILogger logger, Func<DateTimeOffset>? now = null)
        : this(extractor, logger, now, DefaultMaxJournaledSegments)
    {
    }

    /// <summary>Test ctor: caps sized down so cap behavior is exercisable.</summary>
    internal AdaptiveSpeakerIdentificationService(
        IEmbeddingExtractor extractor, ILogger logger, Func<DateTimeOffset>? now,
        int maxJournaledSegments)
    {
        _extractor = extractor;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _maxJournaledSegments = maxJournaledSegments;
        _lastPassAt = _now();
        _logger.LogInformation(
            "Adaptive speaker identification active. dim={Dim} warmup={Warmup} stride={Stride} maxJournal={MaxJournal}",
            extractor.Dim, WarmupSegments, PassSegmentStride, _maxJournaledSegments);
    }

    public string IdentifyOrRegister(float[] segmentSamples, int sampleRate)
        => IdentifyOrRegisterSegment(segmentSamples, sampleRate).Label;

    public (string Label, float[] Embedding) IdentifyOrRegisterWithEmbedding(float[] segmentSamples, int sampleRate)
    {
        var embedding = Normalize(_extractor.Compute(segmentSamples, sampleRate));
        var result = ProcessEmbedding(embedding);
        // The journal owns its copy; hand the caller an independent one so the biometric wipe
        // cannot zero a buffer the caller still holds (and vice versa).
        return (result.Label, (float[])embedding.Clone());
    }

    public SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate)
    {
        var embedding = Normalize(_extractor.Compute(segmentSamples, sampleRate));
        return ProcessEmbedding(embedding);
    }

    private SpeakerSegmentResult ProcessEmbedding(float[] embedding)
    {
        string? newLabel = null;
        List<SpeakerReassignment>? reassignments = null;
        List<string>? passLabels = null;
        SpeakerSegmentResult result;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var segId = _nextSegmentId++;
            _segments.Add((segId, embedding));
            if (_segments.Count > _maxJournaledSegments)
            {
                // Oldest falls off: zero the biometric vector; its assignment stays frozen (the
                // VM's own journal is far smaller, so no rebuildable utterance references it).
                Array.Clear(_segments[0].Embedding);
                _clusterBySegment.Remove(_segments[0].SegmentId);
                _segments.RemoveAt(0);
            }

            // Instant provisional label: nearest centroid at the adaptive similarity threshold.
            var (bestCluster, bestSim) = BestClusterUnderLock(embedding);
            int cluster;
            if (bestCluster < 0 || bestSim < _matchSimilarity)
            {
                cluster = _nextClusterId++;
                var label = $"Speaker {++_speakerCounter}";
                _labelByCluster[cluster] = label;
                _centroidByCluster[cluster] = new RunningCentroid(embedding);
                newLabel = label;
            }
            else
            {
                cluster = bestCluster;
                _centroidByCluster[cluster].Add(embedding);
            }
            _clusterBySegment[segId] = cluster;
            _segmentsSinceLastPass++;
            result = new SpeakerSegmentResult(segId, _labelByCluster[cluster]);

            var due = _segmentsSinceLastPass >= PassSegmentStride
                      || (_segmentsSinceLastPass >= 1 && _now() - _lastPassAt >= PassMaxLatency);
            if (due && _segments.Count >= WarmupSegments)
            {
                try
                {
                    (reassignments, passLabels) = RunPassUnderLock();
                }
                catch (Exception ex)
                {
                    // A clustering bug must never take down transcription; keep the previous
                    // assignment and try again next time.
                    _logger.LogWarning(ex, "Adaptive re-cluster pass failed; keeping previous assignment");
                }
                _segmentsSinceLastPass = 0;
                _lastPassAt = _now();
            }
        }

        // Events outside the lock (same rationale as the manual service).
        if (newLabel is not null) RaiseSpeakerRegistered(newLabel);
        if (passLabels is not null)
            foreach (var label in passLabels) RaiseSpeakerRegistered(label);
        if (reassignments is { Count: > 0 })
        {
            try { SpeakersReassigned?.Invoke(this, reassignments); }
            catch (Exception ex) { _logger.LogError(ex, "SpeakersReassigned subscriber threw"); }
        }

        return result;
    }

    /// <summary>
    /// Re-clusters ALL journaled embeddings and maps the resulting clusters onto the existing
    /// stable cluster ids by greedy segment-overlap matching (ties: user-renamed label first,
    /// then earliest member segment — so "Speaker 1"/"Alice" stays on the earlier voice).
    /// Returns changed (segment → label) pairs and any labels newly created by the pass.
    /// </summary>
    private (List<SpeakerReassignment> Reassignments, List<string> NewLabels) RunPassUnderLock()
    {
        var embeddings = new float[_segments.Count][];
        for (int i = 0; i < _segments.Count; i++) embeddings[i] = _segments[i].Embedding;

        var previousCount = _labelByCluster.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cr = _clusterer.Cluster(embeddings, previousCount);
        sw.Stop();
        _matchSimilarity = 1f - cr.CutDistance;

        // Members per new cluster index (in journal order → element 0 is the earliest segment).
        var members = new List<long>[cr.ClusterCount];
        for (int c = 0; c < cr.ClusterCount; c++) members[c] = new List<long>();
        for (int i = 0; i < _segments.Count; i++)
            members[cr.AssignmentPerSegment[i]].Add(_segments[i].SegmentId);

        // Greedy overlap matching new-cluster ↔ previous stable cluster id.
        var candidates = new List<(int NewCluster, int PrevCluster, int Overlap, bool Renamed, long EarliestSeg)>();
        foreach (var (newCluster, segIds) in members.Index())
        {
            var overlapByPrev = new Dictionary<int, int>();
            foreach (var segId in segIds)
            {
                if (_clusterBySegment.TryGetValue(segId, out var prev))
                    overlapByPrev[prev] = overlapByPrev.GetValueOrDefault(prev) + 1;
            }
            foreach (var (prev, overlap) in overlapByPrev)
                candidates.Add((newCluster, prev, overlap, _renamedClusters.Contains(prev), segIds[0]));
        }
        candidates.Sort((x, y) =>
        {
            var byOverlap = y.Overlap.CompareTo(x.Overlap);
            if (byOverlap != 0) return byOverlap;
            var byRenamed = y.Renamed.CompareTo(x.Renamed);
            if (byRenamed != 0) return byRenamed;
            return x.EarliestSeg.CompareTo(y.EarliestSeg);
        });

        var stableByNew = new int[cr.ClusterCount];
        Array.Fill(stableByNew, -1);
        var takenPrev = new HashSet<int>();
        foreach (var c in candidates)
        {
            if (stableByNew[c.NewCluster] != -1 || takenPrev.Contains(c.PrevCluster)) continue;
            stableByNew[c.NewCluster] = c.PrevCluster;
            takenPrev.Add(c.PrevCluster);
        }

        // Unmatched new clusters get fresh stable ids + "Speaker N" labels.
        var newLabels = new List<string>();
        var newLabelByCluster = new Dictionary<int, string>();
        var newCentroidByCluster = new Dictionary<int, RunningCentroid>();
        var newRenamed = new HashSet<int>();
        for (int c = 0; c < cr.ClusterCount; c++)
        {
            if (stableByNew[c] == -1)
            {
                stableByNew[c] = _nextClusterId++;
                var label = $"Speaker {++_speakerCounter}";
                newLabelByCluster[stableByNew[c]] = label;
                newLabels.Add(label);
            }
            else
            {
                newLabelByCluster[stableByNew[c]] = _labelByCluster[stableByNew[c]];
                if (_renamedClusters.Contains(stableByNew[c])) newRenamed.Add(stableByNew[c]);
            }
        }

        // Apply: new assignment + per-cluster mean centroids; diff labels for the event.
        var reassignments = new List<SpeakerReassignment>();
        for (int i = 0; i < _segments.Count; i++)
        {
            var segId = _segments[i].SegmentId;
            var stable = stableByNew[cr.AssignmentPerSegment[i]];
            var oldLabel = _clusterBySegment.TryGetValue(segId, out var oldCluster)
                ? _labelByCluster.GetValueOrDefault(oldCluster)
                : null;
            _clusterBySegment[segId] = stable;

            var label = newLabelByCluster[stable];
            if (!string.Equals(oldLabel, label, StringComparison.Ordinal))
                reassignments.Add(new SpeakerReassignment(segId, label));

            if (!newCentroidByCluster.TryGetValue(stable, out var centroid))
                newCentroidByCluster[stable] = centroid = new RunningCentroid(_segments[i].Embedding);
            else
                centroid.Add(_segments[i].Embedding);
        }

        // Old centroids are biometric state too — zero them before swapping in the new set.
        foreach (var old in _centroidByCluster.Values) old.Wipe();
        _centroidByCluster.Clear();
        foreach (var (k, v) in newCentroidByCluster) _centroidByCluster[k] = v;
        _labelByCluster.Clear();
        foreach (var (k, v) in newLabelByCluster) _labelByCluster[k] = v;
        _renamedClusters.Clear();
        foreach (var k in newRenamed) _renamedClusters.Add(k);

        _logger.LogDebug(
            "Adaptive pass: {Segments} segments → {Clusters} clusters cut={Cut:F2} changed={Changed} ({Ms}ms)",
            _segments.Count, cr.ClusterCount, cr.CutDistance, reassignments.Count, sw.ElapsedMilliseconds);
        // Labels can carry user-typed names after a rename → DEBUG-only.
        _logger.SensitiveInformation("Adaptive pass labels: [{Labels}]",
            string.Join(", ", _labelByCluster.Values));

        return (reassignments, newLabels);
    }

    private (int Cluster, float Similarity) BestClusterUnderLock(float[] embedding)
    {
        var best = float.NegativeInfinity;
        var bestCluster = -1;
        foreach (var (cluster, centroid) in _centroidByCluster)
        {
            var sim = centroid.Similarity(embedding);
            if (sim > best) { best = sim; bestCluster = cluster; }
        }
        return (bestCluster, best);
    }

    public bool Rename(string oldLabel, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(newLabel)) return false;
        lock (_lock)
        {
            foreach (var (cluster, label) in _labelByCluster)
            {
                if (label != oldLabel) continue;
                _labelByCluster[cluster] = newLabel;
                _renamedClusters.Add(cluster);
                _logger.SensitiveInformation("Speaker renamed: '{Old}' → '{New}' (cluster={Cluster})",
                    oldLabel, newLabel, cluster);
                return true;
            }
            return false;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            WipeBiometricStateUnderLock();
            _logger.LogInformation("Adaptive speaker identification state reset");
        }
    }

    /// <summary>
    /// Actively erase all in-memory biometric state: zero every journaled embedding and every
    /// centroid vector before dropping references. Segment ids stay monotonic across Reset so a
    /// stale reassignment held by the UI can never collide with a new segment.
    /// </summary>
    private void WipeBiometricStateUnderLock()
    {
        foreach (var (_, embedding) in _segments) Array.Clear(embedding);
        _segments.Clear();
        foreach (var centroid in _centroidByCluster.Values) centroid.Wipe();
        _centroidByCluster.Clear();
        _clusterBySegment.Clear();
        _labelByCluster.Clear();
        _renamedClusters.Clear();
        _nextClusterId = 0;
        _speakerCounter = 0;
        _matchSimilarity = InitialMatchSimilarity;
        _segmentsSinceLastPass = 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            WipeBiometricStateUnderLock();
            _extractor.Dispose();
        }
    }

    private void RaiseSpeakerRegistered(string label)
    {
        try { SpeakerRegistered?.Invoke(this, label); }
        catch (Exception ex) { _logger.LogError(ex, "SpeakerRegistered subscriber threw for {Label}", label); }
    }

    private static float[] Normalize(float[] v)
    {
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        var norm = (float)Math.Sqrt(sumSq);
        if (norm > 1e-12f)
            for (int i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    /// <summary>Running mean of unit vectors, renormalized for cosine matching by dot product.</summary>
    private sealed class RunningCentroid
    {
        private readonly float[] _sum;
        private int _count;

        public RunningCentroid(float[] first)
        {
            _sum = (float[])first.Clone();
            _count = 1;
        }

        public void Add(float[] embedding)
        {
            for (int i = 0; i < _sum.Length; i++) _sum[i] += embedding[i];
            _count++;
        }

        public float Similarity(float[] embedding)
        {
            if (embedding.Length != _sum.Length) return 0f;
            float dot = 0, norm = 0;
            for (int i = 0; i < _sum.Length; i++)
            {
                dot += _sum[i] * embedding[i];
                norm += _sum[i] * _sum[i];
            }
            var denom = MathF.Sqrt(norm);
            return denom <= 1e-12f ? 0f : dot / denom;   // embedding is already unit-norm
        }

        public void Wipe() => Array.Clear(_sum);
    }
}
```

Note: `members.Index()` is .NET 10's `Enumerable.Index()`; if it trips analyzers, use a plain `for` loop over `members`.

- [ ] **Step 4: Run the new tests, verify they pass**

Run: `dotnet test --filter-class "Pia.Tests.Services.LiveTranscription.AdaptiveSpeakerIdentificationServiceTests"`
Expected: 9 PASSED. Walk through any failure carefully — the pass-trigger arithmetic (`WarmupSegments`/stride interplay in `ReclusterPass_Splits…`) is the most likely off-by-one: the stride pass attempt at segment #5 must be SKIPPED for warm-up **without** resetting `_segmentsSinceLastPass` (the implementation above only resets when the pass was due AND ran or failed — check that a warm-up skip leaves the counter intact; the `due && _segments.Count >= WarmupSegments` guard does this correctly because the reset lines sit inside the `if`).

- [ ] **Step 5: Full test gate + commit**

Run: `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: 0 failures.

```bash
git add src/Pia.Wpf/Services/LiveTranscription/AdaptiveSpeakerIdentificationService.cs tests/Pia.Wpf.Tests/Services/LiveTranscription/AdaptiveSpeakerIdentificationServiceTests.cs
git commit -m "Add adaptive speaker identification with periodic re-clustering"
```

---

## Chunk 4: Transcript ViewModel — utterance journal + retro rebuild

### Task 7: Journal, `ApplyReassignments`, rebuild (TDD)

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs`
- Modify: `src/Pia.Wpf/ViewModels/MeetingAttendeeViewModel.cs` (two `Bubbles.Clear()` sites, lines ~141 and ~175)
- Modify: `tests/Pia.Wpf.Tests/ViewModels/MeetingAttendeeViewModelTests.cs`

Existing tests drive the VM through the internal `AddUtterance` seam with a fake `IMeetingAttendeeService` — mirror that fixture (read the top of `MeetingAttendeeViewModelTests.cs` first and reuse its helpers for constructing the VM). `DispatchToUi` runs synchronously in tests (no WPF `Application.Current`), so no dispatcher pumping is needed.

- [ ] **Step 1: Write the failing tests**

Add to `MeetingAttendeeViewModelTests.cs` (using the file's existing VM-construction helper; `t0` below means the same fixed base `DateTimeOffset` the file already uses, seconds offsets added explicitly):

```csharp
    [Fact]
    public void ApplyReassignments_MergesBubbles_WhenTwoLabelsCollapse()
    {
        var vm = CreateVm(); // the file's existing helper

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", T0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "world", T0.AddSeconds(5), "Speaker 2", SegmentId: 1));
        Assert.Equal(2, vm.Bubbles.Count);

        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 1") });

        var bubble = Assert.Single(vm.Bubbles);
        Assert.Equal("Speaker 1", bubble.SpeakerLabel);
        Assert.Contains("hello", bubble.Text);
        Assert.Contains("world", bubble.Text);
    }

    [Fact]
    public void ApplyReassignments_SplitsABubble_WhenOneUtteranceMovesAway()
    {
        var vm = CreateVm();

        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", T0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "world", T0.AddSeconds(5), "Speaker 1", SegmentId: 1));
        Assert.Single(vm.Bubbles);

        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 2") });

        Assert.Equal(2, vm.Bubbles.Count);
        Assert.Equal("Speaker 1", vm.Bubbles[0].SpeakerLabel);
        Assert.Equal("Speaker 2", vm.Bubbles[1].SpeakerLabel);
    }

    [Fact]
    public void ApplyReassignments_UnknownOrUnchangedSegments_LeaveBubblesUntouched()
    {
        var vm = CreateVm();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", T0, "Speaker 1", SegmentId: 0));
        var before = Assert.Single(vm.Bubbles);

        vm.ApplyReassignments(new[]
        {
            new SpeakerReassignment(0, "Speaker 1"),   // unchanged
            new SpeakerReassignment(99, "Speaker 3"),  // unknown id
        });

        Assert.Same(before, Assert.Single(vm.Bubbles)); // no rebuild happened
    }

    [Fact]
    public void ApplyReassignments_AfterRename_KeepsTheRenamedLabel()
    {
        var vm = CreateVm();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "hello", T0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "later", T0.AddSeconds(60), "Speaker 2", SegmentId: 1));

        vm.RelabelSpeakerForTest("Speaker 1", "Alice"); // see step 2: expose via existing pattern

        // An unrelated reassignment triggers a rebuild — the rename must survive it.
        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 3") });

        Assert.Equal("Alice", vm.Bubbles[0].SpeakerLabel);
        Assert.Equal("Speaker 3", vm.Bubbles[1].SpeakerLabel);
    }

    [Fact]
    public void ApplyReassignments_ColorStaysWithTheSpeaker_AcrossRebuild()
    {
        var vm = CreateVm();
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "a", T0, "Speaker 1", SegmentId: 0));
        vm.AddUtterance(new TranscriptUtterance(TranscriptSpeaker.Them, "b", T0.AddSeconds(60), "Speaker 2", SegmentId: 1));
        var color1 = vm.Bubbles[0].ColorIndex;
        var color2 = vm.Bubbles[1].ColorIndex;

        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 1") });
        vm.ApplyReassignments(new[] { new SpeakerReassignment(1, "Speaker 2") }); // move it back

        Assert.Equal(color1, vm.Bubbles[0].ColorIndex);
        Assert.Equal(color2, vm.Bubbles[1].ColorIndex);
    }
```

Notes for the implementer:
- `T0` / `CreateVm()` — reuse whatever base-time constant and construction helper the test file already defines (rename in the snippets accordingly).
- `RelabelSpeakerForTest`: `RelabelSpeaker` is `protected` on the base VM. Expose it the way the file already exposes internals if a pattern exists; otherwise add to `MeetingAttendeeViewModel`: `internal void RelabelSpeakerForTest(string o, string n) => RelabelSpeaker(o, n);` (tests see internals via the existing `InternalsVisibleTo`).

- [ ] **Step 2: Run to verify the new tests fail**

Run: `dotnet test --filter-class "<full name of MeetingAttendeeViewModelTests>"`
Expected: compile error (`ApplyReassignments` missing) → add stubs, then FAIL.

- [ ] **Step 3: Implement in `TranscriptOverlayViewModel`**

Add near the top (with the other consts/fields):

```csharp
    // Per-utterance retention so adaptive reassignments can rebuild bubbles retroactively.
    // Comfortably above MaxBubbles; the rebuild trims to MaxBubbles at the end.
    private const int JournalCap = 1000;
    private readonly List<UtteranceEntry> _journal = [];

    /// <summary>One journaled utterance; Label is mutable (reassignments and renames retarget it).</summary>
    private sealed class UtteranceEntry
    {
        public required TranscriptSpeaker Speaker { get; init; }
        public required string Text { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public required string? Label { get; set; }
        public required long? SegmentId { get; init; }
    }
```

Change `AddUtterance` to journal first (inside the existing `DispatchToUi` body, before `GetOrCreateBubble`):

```csharp
                _journal.Add(new UtteranceEntry
                {
                    Speaker = utterance.Speaker,
                    Text = utterance.Text,
                    Timestamp = utterance.Timestamp,
                    Label = utterance.SpeakerLabel,
                    SegmentId = utterance.SegmentId,
                });
                if (_journal.Count > JournalCap) _journal.RemoveAt(0);
```

Add the new members:

```csharp
    /// <summary>
    /// Applies a batch of adaptive-diarization label corrections: updates the utterance journal
    /// (keyed by segment id) and, if anything actually changed, rebuilds the bubble collection
    /// from the journal so merges/splits/relabels all render correctly. Journal and bubbles are
    /// UI-thread state; the whole batch runs as one dispatcher action.
    /// </summary>
    internal void ApplyReassignments(IReadOnlyList<SpeakerReassignment> changes)
    {
        if (changes.Count == 0) return;
        DispatchToUi(() =>
        {
            try
            {
                var labelBySegment = new Dictionary<long, string>(changes.Count);
                foreach (var c in changes) labelBySegment[c.SegmentId] = c.NewLabel;

                var any = false;
                foreach (var entry in _journal)
                {
                    if (entry.SegmentId is not long id) continue;
                    if (!labelBySegment.TryGetValue(id, out var newLabel)) continue;
                    if (string.Equals(entry.Label, newLabel, StringComparison.Ordinal)) continue;
                    entry.Label = newLabel;
                    any = true;
                }
                if (any) RebuildBubblesFromJournal();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply speaker reassignments");
            }
        });
    }

    /// <summary>
    /// Replays the journal through the SAME incremental path (<see cref="GetOrCreateBubble"/> +
    /// Append), so rebuild-vs-incremental equivalence holds by construction. The palette map is
    /// deliberately NOT reset — speakers keep their colors across rebuilds. Trims in a loop
    /// (TrimIfNeeded removes at most one batch per call).
    /// </summary>
    private void RebuildBubblesFromJournal()
    {
        Bubbles.Clear();
        foreach (var entry in _journal)
        {
            var bubble = GetOrCreateBubble(entry.Speaker, entry.Timestamp, entry.Label, createIfMissing: true);
            bubble!.Append(entry.Text, entry.Timestamp);
        }
        while (Bubbles.Count > MaxBubbles) Bubbles.RemoveAt(0);
    }

    /// <summary>Clears the visible transcript AND its journal — they must never diverge.</summary>
    protected void ClearTranscript()
    {
        Bubbles.Clear();
        _journal.Clear();
    }
```

Extend `RelabelSpeaker` — inside its existing `DispatchToUi` action, after the bubble walk:

```csharp
            foreach (var entry in _journal)
            {
                if (entry.Label == oldLabel)
                    entry.Label = newLabel;
            }
```

- [ ] **Step 4: Route the existing `Bubbles.Clear()` call sites through `ClearTranscript()`**

In `MeetingAttendeeViewModel.cs`, replace both `Bubbles.Clear();` occurrences (in `PrepareForDisplayAsync`, ~line 141, and in the start path, ~line 175) with `ClearTranscript();`. A cleared transcript with a stale journal would resurrect old bubbles on the next reassignment.

- [ ] **Step 5: Run the tests, verify they pass**

Run the test class from Step 2 → new tests PASS, and the existing bubble tests (`Utterances_DifferentSpeakerLabelWithinWindow_SplitIntoTwoBubbles`, `Utterances_NullLabelSegmentMidRun_SplitsTheColoredRun`) stay green.

- [ ] **Step 6: Full test gate + commit**

Run: `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: 0 failures.

```bash
git add src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs src/Pia.Wpf/ViewModels/MeetingAttendeeViewModel.cs tests/Pia.Wpf.Tests/ViewModels/MeetingAttendeeViewModelTests.cs
git commit -m "Add utterance journal and retroactive bubble rebuild for speaker reassignments"
```

---

## Chunk 5: Wiring, settings, UI, final gates

### Task 8: Service selection + event forwarding

**Files:**
- Modify: `src/Pia.Wpf/Models/AppSettings.cs`
- Modify: `src/Pia.Wpf/Services/MeetingAttendee/IMeetingAttendeeService.cs`
- Modify: `src/Pia.Wpf/Services/MeetingAttendee/MeetingAttendeeService.cs`
- Modify: `src/Pia.Wpf/ViewModels/MeetingAttendeeViewModel.cs`

- [ ] **Step 1: Add the setting**

In `AppSettings.cs`, directly under `EnableMeetingDiarization` (~line 129):

```csharp
    // Smart auto-detect: continuously re-cluster all voice embeddings during the meeting and
    // retro-correct earlier speaker assignments. ON by default; when on, the manual tuning knobs
    // below (threshold / max speakers / min speech) are ignored and hidden in the settings UI.
    // Local-only (no SyncSettings mirror).
    public bool MeetingSmartSpeakerDetection { get; set; } = true;
```

- [ ] **Step 2: Interface event**

In `IMeetingAttendeeService.cs`, after `StateChanged`:

```csharp
    /// <summary>
    /// Forwarded from the per-session adaptive diarizer: retroactive speaker-label corrections
    /// for already-emitted utterances (by <see cref="TranscriptUtterance.SegmentId"/>). Never
    /// raised in manual-diarization mode. May fire on a background thread.
    /// </summary>
    event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;
```

(`SpeakerReassignment` lives in `Pia.Services.LiveTranscription` — the file already imports it via the `Pia.Services.LiveTranscription` types used in the tuple seam; add the `using` if missing.)

- [ ] **Step 3: Orchestrator — select the adaptive service and forward the event**

In `MeetingAttendeeService.cs`:

a) Public event + forwarding handler (near `StateChanged`):

```csharp
    public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;

    private void OnSpeakersReassigned(object? sender, IReadOnlyList<SpeakerReassignment> changes)
        => SpeakersReassigned?.Invoke(this, changes);
```

b) In `StartAsync`, right after `_speakerId = speakerId;` (~line 249):

```csharp
            if (_speakerId is not null)
                _speakerId.SpeakersReassigned += OnSpeakersReassigned;
```

c) In `DisposeAllAsync`, where `_speakerId` is disposed/nulled (find the `_speakerId` teardown after the engine drain), unsubscribe first:

```csharp
                _speakerId.SpeakersReassigned -= OnSpeakersReassigned;
```

d) In `TryCreateSpeakerIdentificationAsync` (~line 732), replace the construction with the mode branch:

```csharp
            if (settings.MeetingSmartSpeakerDetection)
            {
                return new AdaptiveSpeakerIdentificationService(
                    new SherpaEmbeddingExtractor(speakerModelPath),
                    loggerFactory.CreateLogger<AdaptiveSpeakerIdentificationService>());
            }
            return new SpeakerIdentificationService(
                speakerModelPath,
                settings.SpeakerEmbeddingThreshold,
                settings.MeetingMaxSpeakers,
                loggerFactory.CreateLogger<SpeakerIdentificationService>());
```

e) In `StartAsync` (~line 294), auto mode pins the embed minimum to the 1.5 s default:

```csharp
            var minSpeechSeconds = settings.MeetingSmartSpeakerDetection ? 1.5f : settings.MeetingMinSpeechSeconds;
            var minDiarizationSamples = (int)System.Math.Round(minSpeechSeconds * 16000);
```

- [ ] **Step 4: ViewModel subscription**

In `MeetingAttendeeViewModel.cs` ctor, next to `_service.StateChanged += OnServiceStateChanged;`:

```csharp
        _service.SpeakersReassigned += OnSpeakersReassigned;
```

Handler (near `OnServiceStateChanged`):

```csharp
    private void OnSpeakersReassigned(object? sender, IReadOnlyList<SpeakerReassignment> changes)
        => ApplyReassignments(changes);
```

If the VM unsubscribes `StateChanged` anywhere (check `Dispose`), unsubscribe `SpeakersReassigned` in the same place; if it never unsubscribes (singleton-lifetime pairing), match that existing behavior — do not invent a new disposal pattern here.

- [ ] **Step 5: Fix test fakes**

Any test fake implementing `IMeetingAttendeeService` (grep `: IMeetingAttendeeService` under `tests/`) needs the event member:

```csharp
    public event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned { add { } remove { } }
```

(Use the empty-accessor form to avoid CS0067; add `using Pia.Services.LiveTranscription;` where needed.)

- [ ] **Step 6: Build + full test gate**

Run: `dotnet build` then `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: 0 errors, 0 failures. (The `TryCreateSpeakerIdentificationAsync` degrade-to-null tests must still pass — the mode branch sits inside the existing try/catch, so an extractor construction failure still degrades to null in BOTH modes.)

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Models/AppSettings.cs src/Pia.Wpf/Services/MeetingAttendee/IMeetingAttendeeService.cs src/Pia.Wpf/Services/MeetingAttendee/MeetingAttendeeService.cs src/Pia.Wpf/ViewModels/MeetingAttendeeViewModel.cs tests/
git commit -m "Wire adaptive diarization selection and reassignment forwarding"
```

### Task 9: Settings ViewModel (TDD)

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/MeetingSettingsViewModel.cs`
- Modify: `tests/Pia.Wpf.Tests/ViewModels/MeetingSettingsViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `MeetingSettingsViewModelTests.cs`, following the file's existing load/persist test pattern (reuse its fake `ISettingsService` fixture):

```csharp
    [Fact]
    public async Task Initialize_LoadsSmartSpeakerDetection()
    {
        var settings = new AppSettings { MeetingSmartSpeakerDetection = false };
        var vm = CreateVm(settings); // the file's existing helper
        await vm.InitializeAsync();

        Assert.False(vm.MeetingSmartSpeakerDetection);
        Assert.True(vm.ShowManualTuning);
    }

    [Fact]
    public async Task TogglingSmartSpeakerDetection_PersistsAndFlipsManualTuningVisibility()
    {
        var settings = new AppSettings { MeetingSmartSpeakerDetection = true };
        var vm = CreateVm(settings);
        await vm.InitializeAsync();
        Assert.False(vm.ShowManualTuning);

        vm.MeetingSmartSpeakerDetection = false;

        Assert.True(vm.ShowManualTuning);
        Assert.False(settings.MeetingSmartSpeakerDetection); // saved through the fake service
    }
```

(Adapt helper names/`await`-drain idioms to the file's existing conventions — the save is fire-and-forget via `SafeFireAndForget`, and the existing tests already know how to observe it; mirror them.)

- [ ] **Step 2: Run to verify they fail**

Expected: compile error → property missing.

- [ ] **Step 3: Implement**

In `MeetingSettingsViewModel.cs`:

```csharp
    // Smart auto-detect replaces all manual diarization tuning while ON.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManualTuning))]
    private bool _meetingSmartSpeakerDetection = true;

    /// <summary>The manual tuning sliders are only shown while smart auto-detect is OFF.</summary>
    public bool ShowManualTuning => !MeetingSmartSpeakerDetection;

    partial void OnMeetingSmartSpeakerDetectionChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }
```

In `InitializeAsync`, with the other loads: `MeetingSmartSpeakerDetection = settings.MeetingSmartSpeakerDetection;`
In `SaveSettingsAsync`, with the other saves: `settings.MeetingSmartSpeakerDetection = MeetingSmartSpeakerDetection;`

- [ ] **Step 4: Run tests → PASS, full gate → 0 failures, commit**

```bash
git add src/Pia.Wpf/ViewModels/MeetingSettingsViewModel.cs tests/Pia.Wpf.Tests/ViewModels/MeetingSettingsViewModelTests.cs
git commit -m "Add smart speaker detection toggle to meeting settings"
```

### Task 10: Settings XAML + localization

**Files:**
- Modify: `src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml` (Meeting tab, ~lines 217–287)
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `ViewStrings.de.resx`, `ViewStrings.fr.resx`

- [ ] **Step 1: Add the resource strings**

Add to `ViewStrings.resx`:

```xml
  <data name="Settings_Diarization_SmartAuto" xml:space="preserve">
    <value>Smart speaker detection (automatic)</value>
  </data>
  <data name="Settings_Diarization_SmartAuto_Description" xml:space="preserve">
    <value>Pia re-evaluates all voices as the meeting progresses and automatically corrects earlier speaker assignments. Manual tuning is hidden while this is on.</value>
  </data>
```

`ViewStrings.de.resx`:

```xml
  <data name="Settings_Diarization_SmartAuto" xml:space="preserve">
    <value>Intelligente Sprechererkennung (automatisch)</value>
  </data>
  <data name="Settings_Diarization_SmartAuto_Description" xml:space="preserve">
    <value>Pia bewertet alle Stimmen im Verlauf des Meetings laufend neu und korrigiert frühere Sprecherzuordnungen automatisch. Die manuelle Feinabstimmung ist ausgeblendet, solange diese Option aktiv ist.</value>
  </data>
```

`ViewStrings.fr.resx`:

```xml
  <data name="Settings_Diarization_SmartAuto" xml:space="preserve">
    <value>Détection intelligente des intervenants (automatique)</value>
  </data>
  <data name="Settings_Diarization_SmartAuto_Description" xml:space="preserve">
    <value>Pia réévalue toutes les voix au fil de la réunion et corrige automatiquement les attributions précédentes. Les réglages manuels sont masqués tant que cette option est active.</value>
  </data>
```

- [ ] **Step 2: Add the toggle and gate the three tuning panels**

In `AssistantView.xaml`, inside the diarization StackPanel (after the `Settings_Diarization_Enable_Description` TextBlock, before its closing `</StackPanel>` ~line 227), add:

```xml
              <CheckBox Content="{loc:Str Settings_Diarization_SmartAuto}"
                        IsChecked="{Binding MeetingSmartSpeakerDetection}"
                        IsEnabled="{Binding EnableMeetingDiarization}"
                        Margin="0,12,0,0"/>
              <TextBlock Text="{loc:Str Settings_Diarization_SmartAuto_Description}"
                         Style="{StaticResource PiaSettingsDescriptionStyle}"
                         Margin="22,4,0,0"/>
```

Then add to EACH of the three tuning StackPanels (threshold ~line 230, max speakers ~line 250, min speech ~line 270), alongside their existing `IsEnabled="{Binding EnableMeetingDiarization}"`:

```xml
                        Visibility="{Binding ShowManualTuning, Converter={StaticResource BooleanToVisibilityConverter}}"
```

Check `App.xaml` (~line 35 area) declares `BooleanToVisibilityConverter` as a resource key; the codebase has `Pia.Converters.BooleanToVisibilityConverter` and registers converters there — if only the Inverse variant is registered, register the plain one the same way:

```xml
      <converters:BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
```

- [ ] **Step 3: Build + full gate**

Run: `dotnet build` then `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: 0 errors, 0 failures (XAML compiles; localization tests, if any assert key parity, must see all three languages — they do).

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml src/Pia.Wpf/Resources/Strings/ src/Pia.Wpf/App.xaml
git commit -m "Add smart speaker detection settings UI (en/de/fr)"
```

### Task 11: Final verification

- [ ] **Step 1: Full build (Release too)**

Run: `dotnet build` and `dotnet build -c Release`
Expected: 0 errors each. Release matters: `SensitiveInformation` calls must compile out cleanly.

- [ ] **Step 2: Full test gate**

Run: `dotnet test --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
Expected: 0 failures, total count ≥ 923 + the ~20 new tests.

- [ ] **Step 3: Manual smoke checklist (report as NOT DONE if not executed)**

The only validation that closes the feature's actual promise needs a real multi-speaker Teams meeting (cannot be automated here — same caveat as `docs/superpowers/handover/2026-06-24-meeting-per-speaker-bubbles-open-questions.md`):

1. Join a 2+ speaker meeting with smart detection ON (default). Watch: initial fragmentation heals within ~30 s (bubbles merge/relabel); two people converge to exactly two stable speakers with NO slider tuning.
2. Rename a speaker mid-meeting → the name sticks across later corrections.
3. Toggle smart detection OFF in Settings → Assistant → Meeting: the three sliders reappear; next meeting uses the manual path (old behavior).
4. Transcript export after the meeting carries the corrected labels.

- [ ] **Step 4: Update the handover doc pointer (optional but recommended)**

Append a short section to `docs/superpowers/handover/2026-06-24-meeting-per-speaker-bubbles-open-questions.md` noting that the "central caveat" now has a structural mitigation (adaptive re-clustering, spec/plan links) and that threshold tuning (next-step #2) is superseded by auto mode.

- [ ] **Step 5: Final commit if anything changed in steps 3–4**

```bash
git add -A
git commit -m "Finalize smart speaker auto-detect: docs and verification notes"
```
