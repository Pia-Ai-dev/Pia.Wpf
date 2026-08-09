using System.IO;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Architecture;

public class ToolAutonomyRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    /// <summary>Columns: file, <c>ToolAutonomy.Resolve</c>, <c>IsMcpTool</c>, <c>IsAutoApproveEligible</c>. Counts are exact,
    /// not ceilings, because a ceiling asserts nothing on a zero match.</summary>
    public static TheoryData<string, int, int, int> GateFiles =>
        new()
        {
            { Path.Combine("ViewModels", "Models", "ChatSession.cs"), 1, 1, 1 },
            // No IToolPermissionService is injected in the headless files, so this gate reads no allowlist.
            { Path.Combine("Services", "BackgroundAssistantTurnRunner.cs"), 1, 1, 0 },
            { Path.Combine("ViewModels", "AssistantViewModel.cs"), 1, 1, 1 },
        };

    /// <summary>The floor is structural only while exactly one place can reach an auto-approval.</summary>
    [Theory]
    [MemberData(nameof(GateFiles))]
    public void EveryGateFileDerivesNoAutonomyDecisionOfItsOwn(
        string relativePath, int expectedResolveCalls, int expectedMcpLookups, int expectedAllowlistReads)
    {
        var path = Path.Combine(SourceDirectory, relativePath);
        Assert.True(File.Exists(path), $"gate file not found: {path}");
        var lines = File.ReadAllLines(path);

        Assert.Equal(expectedResolveCalls, lines.Count(l => l.Contains("ToolAutonomy.Resolve(", StringComparison.Ordinal)));

        // The floor's own predicate lives inside Resolve and must not be re-derived at a gate.
        Assert.Empty(CodeLinesContaining(lines, "IsDeleteLike"));

        // A gate must not use the card's name-only guess: a renamed built-in would become grantable-as-external.
        Assert.Empty(CodeLinesContaining(lines, "ClassifyPresumedExternal"));

        var mcpLines = CodeLinesContaining(lines, "IsMcpTool");
        Assert.Equal(expectedMcpLookups, mcpLines.Count);
        foreach (var line in mcpLines)
            Assert.Equal("return _pluginService.IsMcpTool(toolName);", line);

        // Case-insensitive: one gate assigns `var allowlisted = …`, the other names `IsAllowlisted:`.
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

    /// <summary>An ordinal this build does not know must read as <c>Unknown</c> rather than throw or be re-mapped.</summary>
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

    /// <summary><see cref="ToolGateOutcome"/> is control flow, not persisted, so the rule above deliberately does not apply.</summary>
    [Fact]
    public void ToolGateOutcome_IsNotPartOfThePersistedVocabulary()
    {
        Assert.DoesNotContain("Unknown", Enum.GetNames<ToolGateOutcome>());
    }
}
