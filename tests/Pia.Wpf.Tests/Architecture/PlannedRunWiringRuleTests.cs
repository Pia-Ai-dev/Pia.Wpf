using System.IO;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// A source scan, not a behavioural test: every argument the one production <c>LiveTurnExecutor</c> construction
/// supplies past <c>tokenizationEnabled</c> is trailing and defaulted, so dropping one compiles and stays green.
/// </summary>
public class PlannedRunWiringRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    private const string ConstructionMarker = "new LiveTurnExecutor(";

    public static TheoryData<string, string> RequiredArguments =>
        new()
        {
            { "policy", "Batch 04 — the run's autonomy policy; without it every write cards again" },
            { "_agentTimelineService", "Batch 03 — the audit timeline; without it an interactive run records nothing" },
            { "workspaceRoot", "Batch 06 — workspace isolation; without it steps write into the user's files folder" },
            // Both persona arguments as ONE token, deliberately: a bare "persona" row is vacuous, because the same
            // call already passes PersonaAttribution.From(persona). The pair pins their presence AND their order.
            { "_stepPersonas?.Invoke(), persona", "Batch 07 — the per-run resolver plus the run persona the resolver falls back to" },
        };

    [Theory]
    [MemberData(nameof(RequiredArguments))]
    public void TheProductionLiveExecutorConstructionKeepsPassing(string argument, string because)
    {
        var arguments = ProductionConstructionArguments();
        Assert.Contains(argument, arguments, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(because)); // the table's second column is documentation, not data
    }

    // Without this, a renamed type or moved call site would make every theory row above pass against an empty string.
    [Fact]
    public void ThereIsExactlyOneProductionLiveExecutorConstruction()
    {
        var source = ReadManagerSource();
        var first = source.IndexOf(ConstructionMarker, StringComparison.Ordinal);
        Assert.True(first >= 0, $"'{ConstructionMarker}' is gone from ChatSessionManager.cs — the rule below reads nothing");
        Assert.Equal(-1, source.IndexOf(ConstructionMarker, first + ConstructionMarker.Length, StringComparison.Ordinal));
        Assert.True(ProductionConstructionArguments().Length > 40, "the extracted argument list is implausibly short");
    }

    private static string ReadManagerSource()
    {
        var path = Path.Combine(SourceDirectory, "ViewModels", "Models", "ChatSessionManager.cs");
        Assert.True(File.Exists(path), $"ChatSessionManager.cs not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The argument text of the one construction: from the opening paren to the first <c>);</c>.</summary>
    private static string ProductionConstructionArguments()
    {
        var source = ReadManagerSource();
        var start = source.IndexOf(ConstructionMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{ConstructionMarker}' not found in ChatSessionManager.cs");
        start += ConstructionMarker.Length;
        var end = source.IndexOf(");", start, StringComparison.Ordinal);
        Assert.True(end > start, "the LiveTurnExecutor construction has no closing ');'");
        return source[start..end];
    }
}
