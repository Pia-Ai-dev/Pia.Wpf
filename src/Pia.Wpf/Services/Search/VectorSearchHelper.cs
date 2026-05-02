namespace Pia.Services.Search;

public static class VectorSearchHelper
{
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0f;
        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    public static IEnumerable<T> RankByCosine<T>(
        IEnumerable<T> items,
        Func<T, float[]?> getEmbedding,
        float[] query,
        int topK,
        float threshold)
    {
        return items
            .Select(item => (Item: item, Embedding: getEmbedding(item)))
            .Where(x => x.Embedding is not null)
            .Select(x => (x.Item, Score: CosineSimilarity(query, x.Embedding!)))
            .Where(x => x.Score >= threshold)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Item);
    }
}
