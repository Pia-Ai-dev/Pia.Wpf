using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

// Drives the real ONNX model, a ~460 MB download that is not in the repo — hence the self-skip.
public class EmbeddingServiceSemanticTests
{
    private static EmbeddingService CreateOrSkip()
    {
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance, new StubAssetDownloader());
        if (!svc.IsModelAvailable)
            Assert.Skip("embedding model not downloaded on this machine");
        return svc;
    }

    private static float Cosine(float[] a, float[] b)
    {
        // Embeddings are L2-normalized, so cosine similarity is just the dot product.
        float dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ProducesFiniteNormalizedVector()
    {
        var svc = CreateOrSkip();

        var v = await svc.GenerateEmbeddingAsync("This is a business plan.", TestContext.Current.CancellationToken);

        Assert.Equal(384, v.Length);
        Assert.All(v, x => Assert.True(float.IsFinite(x), "embedding component must be finite"));

        float norm = 0;
        foreach (var x in v) norm += x * x;
        norm = MathF.Sqrt(norm);
        Assert.InRange(norm, 0.99f, 1.01f); // L2-normalized

        // Not the degenerate all-zeros / all-equal vector the broken tokenizer would have implied.
        Assert.Contains(v, x => MathF.Abs(x) > 1e-4f);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_SimilarTextRanksAboveUnrelated()
    {
        var svc = CreateOrSkip();

        var anchor = await svc.GenerateEmbeddingAsync("The cat sat on the mat.", TestContext.Current.CancellationToken);
        var similar = await svc.GenerateEmbeddingAsync("A cat is sitting on the rug.", TestContext.Current.CancellationToken);
        var unrelated = await svc.GenerateEmbeddingAsync("Quarterly revenue projections for the fiscal year.", TestContext.Current.CancellationToken);

        var simScore = Cosine(anchor, similar);
        var unrelScore = Cosine(anchor, unrelated);

        Assert.True(simScore > unrelScore,
            $"expected paraphrase to score higher than unrelated text, but sim={simScore:F3} unrel={unrelScore:F3}");
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_IsCrossLingual()
    {
        var svc = CreateOrSkip();

        // The whole point of the multilingual model: a German translation must be closer to the English
        // sentence than an unrelated English sentence is. A WordPiece-shaped tokenizer against this XLM-R
        // model produced garbage that could never satisfy this.
        var english = await svc.GenerateEmbeddingAsync("The cat sat on the mat.", TestContext.Current.CancellationToken);
        var german = await svc.GenerateEmbeddingAsync("Die Katze saß auf der Matte.", TestContext.Current.CancellationToken);
        var unrelated = await svc.GenerateEmbeddingAsync("Quarterly revenue projections for the fiscal year.", TestContext.Current.CancellationToken);

        var translationScore = Cosine(english, german);
        var unrelScore = Cosine(english, unrelated);

        Assert.True(translationScore > unrelScore,
            $"expected cross-lingual translation to score higher than unrelated text, but de={translationScore:F3} unrel={unrelScore:F3}");
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ConcurrentFirstUse_DoesNotCorruptVocabulary()
    {
        var svc = CreateOrSkip();

        // The vault watcher fires one callback per changed file, so a multi-file drop hits a FRESH
        // service concurrently. Before EnsureModelLoaded was locked, two threads populated the same
        // _vocabulary Dictionary and corrupted it permanently ("Operations that change non-concurrent
        // collections must have exclusive access"), failing here and on every call after.
        var tasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => svc.GenerateEmbeddingAsync($"Concurrent first-use document {i}.")));
        var vectors = await Task.WhenAll(tasks);

        Assert.All(vectors, v => Assert.Equal(384, v.Length));

        // The corruption outlived the racing calls — a later, uncontended call must also still work.
        var after = await svc.GenerateEmbeddingAsync("The cat sat on the mat.", TestContext.Current.CancellationToken);
        Assert.Equal(384, after.Length);
    }
}
