using System.IO;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// One structural rule, for one line: the single production construction of <c>LiveTurnExecutor</c> in
/// <c>ChatSessionManager</c> must keep passing every optional argument an interactive
/// <c>RunShape.Planned</c> run depends on.
/// <para>
/// <b>Why a source scan rather than a behavioural test.</b> Every parameter that line supplies past
/// <c>tokenizationEnabled</c> is trailing and defaulted — by design, because the executor is hand-constructed
/// positionally in production and in tests. So DELETING any one of them compiles, the whole suite stays green,
/// and the corresponding feature silently stops existing on the interactive path only: no per-run autonomy
/// policy (Batch 04), no audit trail (03), no workspace isolation (06), or no per-step persona (07). The
/// end-to-end fixtures in <c>LiveTurnExecutorPlannedRunTests</c> cannot see it, because they construct the
/// executor themselves and therefore supply the arguments themselves — which is exactly how this seam stayed
/// uncovered through three batches that each added an argument to it.
/// </para>
/// <para>
/// Precedent for the shape: <see cref="ToolAutonomyRuleTests"/> pins gate-token counts by reading the source.
/// <b>If this goes red after a deliberate rename, update the expected token — do not delete the rule.</b>
/// </para>
/// </summary>
public class PlannedRunWiringRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    private const string ConstructionMarker = "new LiveTurnExecutor(";

    /// <summary>
    /// Each argument the one production call site must still pass, with the batch that added it, so a red line
    /// says which feature is about to be dropped rather than just "a string is missing".
    /// </summary>
    public static TheoryData<string, string> RequiredArguments =>
        new()
        {
            { "policy", "Batch 04 — the run's autonomy policy; without it every write cards again" },
            { "_agentTimelineService", "Batch 03 — the audit timeline; without it an interactive run records nothing" },
            { "workspaceRoot", "Batch 06 — workspace isolation; without it steps write into the user's files folder" },
            // Both Batch 07 arguments as ONE token, deliberately. A bare "persona" row would be VACUOUS: the
            // same call already passes PersonaAttribution.From(persona), so the substring survives even when
            // the run-persona argument itself is deleted (measured — that row stayed green under the mutant).
            // The pair pins their presence AND their order.
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

    /// <summary>
    /// Non-vacuity, as its own fact: there is EXACTLY ONE production construction and the argument text this
    /// rule reads is real. Without it, a renamed type or a moved call site would make every theory row above
    /// pass against an empty string.
    /// </summary>
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
