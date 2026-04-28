using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Pia.Services.Consent.Biometric;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Biometric;

public sealed class CosineSimilarityBiometricMatcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly FakeAuditLog _audit = new();

    public CosineSimilarityBiometricMatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PiaMatcher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "store.bin");
    }

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private (EncryptedFileBiometricConsentStore store, CosineSimilarityBiometricMatcher matcher) Make()
    {
        var store = new EncryptedFileBiometricConsentStore(
            _filePath, NullLogger<EncryptedFileBiometricConsentStore>.Instance);
        var matcher = new CosineSimilarityBiometricMatcher(
            store, _audit, TimeProvider.System,
            NullLogger<CosineSimilarityBiometricMatcher>.Instance);
        return (store, matcher);
    }

    private static float[] Norm(params float[] v)
    {
        var len = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / len).ToArray();
    }

    [Fact]
    public void Cosine_ExactMatch_Is_One()
    {
        var v = Norm(1, 2, 3, 4);
        Assert.Equal(1f, CosineSimilarityBiometricMatcher.CosineSimilarity(v, v), 5);
    }

    [Fact]
    public void Cosine_Orthogonal_Is_Zero()
    {
        var a = new[] { 1f, 0f, 0f };
        var b = new[] { 0f, 1f, 0f };
        Assert.Equal(0f, CosineSimilarityBiometricMatcher.CosineSimilarity(a, b), 5);
    }

    [Fact]
    public async Task Match_ReturnsEntry_OnExactMatch()
    {
        var (store, matcher) = Make();
        var emb = Norm(0.1f, 0.2f, 0.3f, 0.4f);
        var entry = await store.AddAsync("Alice", emb, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMonths(12), "ev", "h");

        var match = await matcher.MatchAsync(emb, 0.85f);
        Assert.NotNull(match);
        Assert.Equal(entry.Id, match!.Entry.Id);
        Assert.True(match.Similarity > 0.99f);
    }

    [Fact]
    public async Task Match_ReturnsNull_OnFarMiss()
    {
        var (store, matcher) = Make();
        var stored = Norm(1, 0, 0, 0);
        await store.AddAsync("Alice", stored, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMonths(12), "ev", "h");

        var probe = Norm(0, 1, 0, 0); // orthogonal
        var match = await matcher.MatchAsync(probe, 0.85f);
        Assert.Null(match);
    }

    [Fact]
    public async Task Match_NearMiss_BelowThreshold_ReturnsNull()
    {
        var (store, matcher) = Make();
        var stored = Norm(1, 0.1f, 0.1f, 0.1f);
        await store.AddAsync("Alice", stored, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMonths(12), "ev", "h");

        // Construct a probe at ~0.7 cosine similarity
        var probe = Norm(0.5f, 0.5f, 0.5f, 0.5f);
        var match = await matcher.MatchAsync(probe, 0.95f);
        Assert.Null(match);
    }

    [Fact]
    public async Task Match_PicksHighest_AboveThreshold()
    {
        var (store, matcher) = Make();
        var alice = Norm(1, 0, 0, 0);
        var bob = Norm(0.95f, 0.1f, 0.1f, 0.1f);
        await store.AddAsync("Alice", alice, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(12), "ev", "h");
        var bobEntry = await store.AddAsync("Bob", bob, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(12), "ev", "h");

        // Probe closer to Bob.
        var probe = Norm(0.96f, 0.1f, 0.1f, 0.1f);
        var match = await matcher.MatchAsync(probe, 0.9f);
        Assert.NotNull(match);
        Assert.Equal(bobEntry.Id, match!.Entry.Id);
    }

    [Fact]
    public async Task Performance_1000Entries_UnderBudget()
    {
        var (store, matcher) = Make();
        var rng = new Random(42);
        const int n = 1000;
        const int dim = 192; // typical speaker-embedding dimension
        for (int i = 0; i < n; i++)
        {
            var v = new float[dim];
            for (int j = 0; j < dim; j++) v[j] = (float)(rng.NextDouble() - 0.5);
            // Normalize
            var len = MathF.Sqrt(v.Sum(x => x * x));
            for (int j = 0; j < dim; j++) v[j] /= len;
            await store.AddAsync($"S{i}", v, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(12), "ev", "h");
        }

        var probe = new float[dim];
        for (int j = 0; j < dim; j++) probe[j] = (float)(rng.NextDouble() - 0.5);
        var plen = MathF.Sqrt(probe.Sum(x => x * x));
        for (int j = 0; j < dim; j++) probe[j] /= plen;

        var sw = Stopwatch.StartNew();
        await matcher.MatchAsync(probe, 0.85f);
        sw.Stop();

        // Spec budget: 200 ms. CI machines vary; we set a generous 2000 ms ceiling
        // to catch order-of-magnitude regressions without being flaky.
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Matcher took {sw.ElapsedMilliseconds} ms for {n} entries (budget 2000 ms)");
    }
}
