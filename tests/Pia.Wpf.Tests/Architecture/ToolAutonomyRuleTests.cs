using System.IO;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// Batch 04's two structural rules: the THREE gate files derive no autonomy decision of their own, and the
/// three PERSISTED gate enums keep their append-only shape.
/// </summary>
public class ToolAutonomyRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    /// <summary>
    /// Every file that decides whether a tool call runs, with the EXACT number of times each gate token may
    /// appear in it. Exact rather than a ceiling: <c>Assert.True(count &lt;= 1)</c> plus a <c>foreach</c> over the
    /// matches asserts NOTHING on a zero match, so the rule used to pass just as happily on a gate whose
    /// allowlist read had been deleted and replaced with a hardcoded <c>false</c> — a silent removal of
    /// authority, which is the same class of defect as a silent addition.
    /// <para>
    /// Scoped deliberately to the gates: <c>ActionCardBuilder.cs</c> legitimately calls <c>IsDeleteLike</c> for
    /// warning text and <c>ClassifyPresumedExternal</c> for its name-only guess, so a blanket ban would be wrong.
    /// </para>
    /// <para>
    /// <b>If this goes red after an unrelated change, fix the count, do not delete the rule.</b>
    /// <c>AssistantViewModel.cs</c> is a large ViewModel and the bans below apply to the WHOLE file, so an
    /// addition anywhere in it can land here. That is the cost of the guarantee: a red line in this file means
    /// somebody added or removed a second autonomy derivation, and the reviewer wants to know either way.
    /// </para>
    /// </summary>
    /// <remarks>Columns: file, <c>ToolAutonomy.Resolve</c>, <c>IsMcpTool</c>, <c>IsAutoApproveEligible</c>.</remarks>
    public static TheoryData<string, int, int, int> GateFiles =>
        new()
        {
            // The interactive gate: reads the allowlist for the offerable/standing-grant question.
            { Path.Combine("ViewModels", "Models", "ChatSession.cs"), 1, 1, 1 },
            // The unattended gate: NO allowlist read at all — IToolPermissionService is injected nowhere in the
            // headless files, so it passes IsAllowlisted: false (§13.3). A 1 here would be a real widening:
            // four tools would become free on every scheduled job.
            { Path.Combine("Services", "BackgroundAssistantTurnRunner.cs"), 1, 1, 0 },
            // The voice gate (D13). A full gate since Batch 04 — its own Resolve call, its own fail-closed
            // IsExternalTool and its own allowlist read — so a voice-only shortcut past the floor is exactly the
            // "second decision" this rule forbids and must be caught here too.
            { Path.Combine("ViewModels", "AssistantViewModel.cs"), 1, 1, 1 },
        };

    /// <summary>
    /// 04 D5.1: the floor is structural only while there is exactly ONE place that can reach an
    /// auto-approval. A gate that re-derives "destructive" or "external" for itself — or that reaches for the
    /// card's name-only guess — is a second decision, and a second decision is how a floor stops being one.
    /// </summary>
    [Theory]
    [MemberData(nameof(GateFiles))]
    public void EveryGateFileDerivesNoAutonomyDecisionOfItsOwn(
        string relativePath, int expectedResolveCalls, int expectedMcpLookups, int expectedAllowlistReads)
    {
        var path = Path.Combine(SourceDirectory, relativePath);
        Assert.True(File.Exists(path), $"gate file not found: {path}");
        var lines = File.ReadAllLines(path);

        // Exactly one Resolve call: the gate's single decision. Not "at most" — a gate with zero is a gate that
        // has stopped consulting the resolver.
        Assert.Equal(expectedResolveCalls, lines.Count(l => l.Contains("ToolAutonomy.Resolve(", StringComparison.Ordinal)));

        // The floor's own predicate must not be re-derived at a gate at all — it lives inside Resolve.
        Assert.Empty(CodeLinesContaining(lines, "IsDeleteLike"));

        // Nor may a gate use the card's name-only guess: a renamed built-in must not become
        // grantable-as-external by name (ToolClassifier.ClassifyPresumedExternal's own doc comment).
        Assert.Empty(CodeLinesContaining(lines, "ClassifyPresumedExternal"));

        // The route lookup, exactly as often as expected, and only ever inside the IsExternalTool helper.
        var mcpLines = CodeLinesContaining(lines, "IsMcpTool");
        Assert.Equal(expectedMcpLookups, mcpLines.Count);
        foreach (var line in mcpLines)
            Assert.Equal("return _pluginService.IsMcpTool(toolName);", line);

        // The allowlist read, exactly as often as expected, and only as the hoisted local the resolver's input
        // is built from. Case-insensitive: ChatSession assigns `var allowlisted = …` while the voice gate names
        // the record parameter `IsAllowlisted:` on the same line.
        var allowlistLines = CodeLinesContaining(lines, "IsAutoApproveEligible");
        Assert.Equal(expectedAllowlistReads, allowlistLines.Count);
        foreach (var line in allowlistLines)
            Assert.Contains("allowlisted", line, StringComparison.OrdinalIgnoreCase);
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
