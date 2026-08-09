using Pia.Services.Search;
using Xunit;

namespace Pia.Tests.Services;

public class VectorSearchHelperTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var a = new float[] { 1f, 2f, 3f };
        var b = new float[] { 1f, 2f, 3f };
        var score = VectorSearchHelper.CosineSimilarity(a, b);
        Assert.True(Math.Abs(score - 1f) < 1e-5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };
        var score = VectorSearchHelper.CosineSimilarity(a, b);
        Assert.True(Math.Abs(score) < 1e-5);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsMinusOne()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { -1f, 0f };
        var score = VectorSearchHelper.CosineSimilarity(a, b);
        Assert.True(Math.Abs(score + 1f) < 1e-5);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        var a = new float[] { 1f, 2f };
        var b = new float[] { 1f, 2f, 3f };
        var score = VectorSearchHelper.CosineSimilarity(a, b);
        Assert.Equal(0f, score);
    }

    [Fact]
    public void RankByCosine_SortsAndFiltersAndLimits()
    {
        var query = new float[] { 1f, 0f };
        var items = new[]
        {
            ("near", new float[] { 0.9f, 0.1f }),
            ("far", new float[] { -1f, 0f }),
            ("perp", new float[] { 0f, 1f }),
            ("exact", new float[] { 1f, 0f })
        };

        var ranked = VectorSearchHelper.RankByCosine(
            items,
            getEmbedding: x => x.Item2,
            query,
            topK: 2,
            threshold: 0.5f).ToList();

        Assert.Equal(2, ranked.Count);
        Assert.Equal("exact", ranked[0].Item1);
        Assert.Equal("near", ranked[1].Item1);
    }

    [Fact]
    public void RankByCosine_NullEmbeddings_AreSkipped()
    {
        var query = new float[] { 1f, 0f };
        var items = new (string, float[]?)[]
        {
            ("hit", new float[] { 1f, 0f }),
            ("missing", null)
        };

        var ranked = VectorSearchHelper.RankByCosine(
            items,
            getEmbedding: x => x.Item2,
            query,
            topK: 5,
            threshold: 0f).ToList();

        Assert.Single(ranked);
        Assert.Equal("hit", ranked[0].Item1);
    }
}
