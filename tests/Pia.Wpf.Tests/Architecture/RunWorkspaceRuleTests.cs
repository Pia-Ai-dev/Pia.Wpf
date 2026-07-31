using System.IO;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// Batch 06's two structural rules: every workspace removal in the launcher goes through the provisioner (a
/// worktree torn down with <c>rmdir</c> leaves a stale registration in the user's repository forever), and the
/// one new persisted-by-NAME enum keeps its append-only shape.
/// </summary>
public class RunWorkspaceRuleTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    /// <summary>
    /// T-G4-21, <b>GUARD</b>. The launcher has exactly ONE workspace-removal path and it delegates to the
    /// provisioner; the single remaining <c>Directory.Delete</c> is the documented no-provisioner fallback
    /// inside <c>TryDeleteDirectory</c>. A second inline delete elsewhere in this file would work fine in copy
    /// mode and silently leak a <c>.git/worktrees/&lt;id&gt;</c> entry in worktree mode — the failure this batch
    /// most needs to prevent, and one no unit test can observe without a real repository.
    /// <para>
    /// Non-vacuity is explicit: the file must have been read, the delegation must appear at least as many times
    /// as there are removal sites, and there must be at least one call to the removal path. Without those, a
    /// rename or a moved file turns this rule green by finding nothing.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-G4-22, <b>GUARD</b>. <see cref="RunWorkspaceMode"/> is serialized by NAME into the workspace metadata
    /// document, which is read by builds older and newer than the one that wrote it. So it is append-only, and
    /// the unknown/absent case must resolve to the RESTRICTIVE member: <c>None = 0</c> means "no isolation",
    /// and a name a build does not recognise reads back as exactly that.
    /// </summary>
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
