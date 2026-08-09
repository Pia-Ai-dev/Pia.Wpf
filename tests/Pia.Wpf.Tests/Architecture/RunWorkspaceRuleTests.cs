using System.IO;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>A worktree torn down with <c>rmdir</c> leaves a stale registration in the user's repository forever.</summary>
public class RunWorkspaceRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    /// <summary>A second inline delete works fine in copy mode but silently leaks a <c>.git/worktrees</c> entry in worktree mode.</summary>
    [Fact]
    public void TheLauncherTearsDownThroughTheWorkspaceService()
    {
        var path = Path.Combine(SourceDirectory, "Services", "HeadlessRunLauncher.cs");
        Assert.True(File.Exists(path), $"launcher not found: {path}");
        var source = File.ReadAllText(path);
        Assert.NotEmpty(source);

        // The delegation exists, and the ONE path is really called from more than one site (the startup sweep
        // and the chat-deleted handler today).
        Assert.Contains("_workspaces.TearDownAsync", source);
        var teardownPathCalls = Count(source, "TearDownWorkspaceAsync");
        Assert.True(teardownPathCalls >= 3,
            $"expected the single teardown path to be declared and called at least twice; found {teardownPathCalls} mentions");

        // Exactly one Directory.Delete CALL in the whole file (the trailing paren keeps the doc comment that
        // explains the rule from counting as a violation of it), and it sits in the documented fallback.
        Assert.Equal(1, Count(source, "Directory.Delete("));
        var fallback = source[source.IndexOf("private void TryDeleteDirectory", StringComparison.Ordinal)..];
        Assert.Contains("Directory.Delete(", fallback);
    }

    /// <summary>Persisted by name and read by older builds, so it is append-only and an unrecognised name must read back as <c>None = 0</c>.</summary>
    [Fact]
    public void RunWorkspaceModeStartsAtNoneZero()
    {
        var values = Enum.GetValues<RunWorkspaceMode>();
        Assert.NotEmpty(values); // a rule over an empty type set passes on nothing

        Assert.Equal(0, (int)RunWorkspaceMode.None);
        Assert.Contains("None", Enum.GetNames<RunWorkspaceMode>());
        Assert.Equal(values.Length, values.Distinct().Count()); // no two members share an ordinal
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
