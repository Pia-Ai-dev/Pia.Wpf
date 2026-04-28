using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent.Biometric;

/// <summary>
/// Brute-force cosine-similarity matcher across the persisted store. Per spec §3.1
/// the latency budget allows linear scan up to a few thousand entries.
/// </summary>
public sealed class CosineSimilarityBiometricMatcher : IBiometricMatcher
{
    private readonly IBiometricConsentStore _store;
    private readonly IConsentAuditLog _auditLog;
    private readonly TimeProvider _clock;
    private readonly ILogger<CosineSimilarityBiometricMatcher> _logger;

    public CosineSimilarityBiometricMatcher(
        IBiometricConsentStore store,
        IConsentAuditLog auditLog,
        TimeProvider clock,
        ILogger<CosineSimilarityBiometricMatcher> logger)
    {
        _store = store;
        _auditLog = auditLog;
        _clock = clock;
        _logger = logger;
    }

    public async Task<BiometricMatchResult?> MatchAsync(
        float[] embedding, float threshold = 0.85f, CancellationToken ct = default)
    {
        if (embedding is null || embedding.Length == 0) return null;
        var rows = await _store.GetAllWithEmbeddingsAsync(ct).ConfigureAwait(false);

        BiometricConsentEntry? best = null;
        float bestScore = float.NegativeInfinity;

        foreach (var (entry, candidate) in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (candidate is null)
            {
                _logger.LogWarning("Skipping corrupted biometric entry {Id}", entry.Id);
                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), _clock.GetUtcNow(), "BIOMETRIC_STORE_CORRUPTION_DETECTED", null,
                    new Dictionary<string, object?> { ["entryId"] = entry.Id }));
                continue;
            }
            if (candidate.Length != embedding.Length) continue;
            var sim = CosineSimilarity(embedding, candidate);
            if (sim > bestScore) { bestScore = sim; best = entry; }
        }

        if (best is not null && bestScore >= threshold)
            return new BiometricMatchResult(best, bestScore);
        return null;
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Embedding length mismatch.");
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        if (denom == 0) return 0f;
        return (float)(dot / denom);
    }
}
