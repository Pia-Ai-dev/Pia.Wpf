using System.IO;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// Batch 04's two structural rules: the two gate files derive no autonomy decision of their own, and the three
/// PERSISTED gate enums keep their append-only shape.
/// </summary>
public class ToolAutonomyRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    /// <summary>
    /// The two files that decide whether a tool call runs. Scoped deliberately: <c>ActionCardBuilder.cs</c>
    /// legitimately calls <c>IsDeleteLike</c> for warning text and <c>ClassifyPresumedExternal</c> for its
    /// name-only guess, so a blanket ban on those tokens would be wrong.
    /// </summary>
    public static TheoryData<string> GateFiles =>
        new()
        {
            Path.Combine("ViewModels", "Models", "ChatSession.cs"),
            Path.Combine("Services", "BackgroundAssistantTurnRunner.cs"),
        };

    /// <summary>
    /// 04 D5.1: the floor is structural only while there is exactly ONE place that can reach an
    /// auto-approval. A gate that re-derives "destructive" or "external" for itself — or that reaches for the
    /// card's name-only guess — is a second decision, and a second decision is how a floor stops being one.
    /// </summary>
    [Theory]
    [MemberData(nameof(GateFiles))]
    public void TheTwoGateFilesDeriveNoAutonomyDecisionOfTheirOwn(string relativePath)
    {
        var path = Path.Combine(SourceDirectory, relativePath);
        Assert.True(File.Exists(path), $"gate file not found: {path}");
        var lines = File.ReadAllLines(path);

        // Exactly one Resolve call: the gate's single decision.
        Assert.Equal(1, lines.Count(l => l.Contains("ToolAutonomy.Resolve(", StringComparison.Ordinal)));

        // The floor's own predicate must not be re-derived at a gate at all — it lives inside Resolve.
        Assert.Empty(CodeLinesContaining(lines, "IsDeleteLike"));

        // Nor may a gate use the card's name-only guess: a renamed built-in must not become
        // grantable-as-external by name (ToolClassifier.ClassifyPresumedExternal's own doc comment).
        Assert.Empty(CodeLinesContaining(lines, "ClassifyPresumedExternal"));

        // At most one route lookup, and only inside the fail-closed IsExternalTool helper.
        var mcpLines = CodeLinesContaining(lines, "IsMcpTool");
        Assert.True(mcpLines.Count <= 1,
            $"{relativePath} derives MCP-ness more than once: {string.Join(" | ", mcpLines)}");
        foreach (var line in mcpLines)
            Assert.Equal("return _pluginService.IsMcpTool(toolName);", line);

        // At most one allowlist lookup, and it must be the hoisted local the resolver's input is built from.
        var allowlistLines = CodeLinesContaining(lines, "IsAutoApproveEligible");
        Assert.True(allowlistLines.Count <= 1,
            $"{relativePath} reads the allowlist more than once: {string.Join(" | ", allowlistLines)}");
        foreach (var line in allowlistLines)
            Assert.Contains("allowlisted", line, StringComparison.Ordinal);
    }

    /// <summary>Non-comment source lines mentioning <paramref name="token"/>, trimmed.</summary>
    private static List<string> CodeLinesContaining(string[] lines, string token) =>
        lines.Select(l => l.Trim())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal)
                        && !l.StartsWith("///", StringComparison.Ordinal)
                        && !l.StartsWith("*", StringComparison.Ordinal))
            .Where(l => l.Contains(token, StringComparison.Ordinal))
            .ToList();

    public static TheoryData<Type> PersistedGateEnums =>
        new() { typeof(ToolClass), typeof(ToolGateDecision), typeof(ToolGateSurface) };

    /// <summary>
    /// An ordinal a newer build writes (or an older DB carries) must render as <em>unknown</em> — never throw
    /// and never be re-mapped — which requires a zero member named <c>Unknown</c>. Duplicate values are the
    /// other half: two names sharing an ordinal is how a "rename" silently becomes a reuse.
    /// </summary>
    [Theory]
    [MemberData(nameof(PersistedGateEnums))]
    public void EveryPersistedGateEnumStartsAtUnknownZero(Type enumType)
    {
        var names = Enum.GetNames(enumType);
        Assert.Contains("Unknown", names);
        Assert.Equal(0, Convert.ToInt32(Enum.Parse(enumType, "Unknown")));

        var values = names.Select(n => Convert.ToInt32(Enum.Parse(enumType, n))).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    /// <summary>
    /// <see cref="ToolGateOutcome"/> is control flow, NOT persisted, so it deliberately does not carry the
    /// rule above. Asserted so nobody "fixes" it into the persisted set.
    /// </summary>
    [Fact]
    public void ToolGateOutcome_IsNotPartOfThePersistedVocabulary()
    {
        Assert.DoesNotContain("Unknown", Enum.GetNames<ToolGateOutcome>());
    }
}
