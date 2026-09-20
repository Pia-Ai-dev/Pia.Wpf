using Pia.Services.Help;
using Xunit;

namespace Pia.Tests.Services.Help;

/// <summary>
/// One index serves every window and every background run. These are smoke checks, not a reproduction:
/// a 250-row in-memory table answers too fast to lose the race reliably, and both tests pass with the
/// gate in HelpSearchService removed. The gate is there because a SqliteConnection does not support two
/// concurrent commands at all, not because this caught it.
/// </summary>
public sealed class HelpSearchConcurrencyTests
{
    [Fact]
    public void ConcurrentSearchesDoNotTripOverTheSharedConnection()
    {
        using var service = new HelpSearchService();

        var queries = new[]
        {
            "agent mode", "voice language", "persona output format", "file tools",
            "cloud sync encryption", "scheduled routine", "meeting transcription", "memory vault",
        };

        var results = new List<HelpHit>[32];
        Parallel.For(0, results.Length, i =>
        {
            results[i] = [.. service.Search(queries[i % queries.Length], 5)];
        });

        Assert.All(results, r => Assert.NotEmpty(r));

        // Same query, same answer, regardless of which thread got there first.
        for (var q = 0; q < queries.Length; q++)
        {
            var forThisQuery = results
                .Where((_, i) => i % queries.Length == q)
                .Select(r => string.Join(",", r.Select(h => h.Reference)))
                .Distinct()
                .ToList();

            Assert.True(forThisQuery.Count == 1,
                $"'{queries[q]}' returned {forThisQuery.Count} different result sets under load");
        }
    }

    [Fact]
    public void TheIndexIsBuiltOnceEvenWhenEveryoneAsksAtOnce()
    {
        using var service = new HelpSearchService();

        var pageCounts = new int[16];
        Parallel.For(0, pageCounts.Length, i => pageCounts[i] = service.PageCount);

        Assert.All(pageCounts, count => Assert.Equal(pageCounts[0], count));
        Assert.True(pageCounts[0] > 0);
    }
}
