using System.IO;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxGrantVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-grant-" + Guid.NewGuid().ToString("N"));

    public SandboxGrantVerifierTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TempPath.Remove(_root);

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string File(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    private static SandboxLaunchSpec Spec(
        string? commandDirectory = null,
        IReadOnlyList<string>? rw = null,
        IReadOnlyList<string>? ro = null) =>
        new("pia-mcp-test", @"C:\x\x.exe", [], new Dictionary<string, string>(), @"C:\x",
            rw ?? [], ro ?? [], AllowNetwork: false, commandDirectory);

    [Fact]
    public void EverythingGrantable_IsOk()
    {
        var command = Dir("srv");
        File(command, "a.dll");

        var result = new SandboxGrantVerifier(_ => true).Verify(Spec(command));

        Assert.True(result.Ok);
        Assert.Equal(0, result.BlockedCount);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void FilesTheUserCannotGrant_AreCountedInTheCommandDirectoryTree()
    {
        var command = Dir("srv");
        var denied = new HashSet<string> { File(command, "a.dll"), File(Dir("srv", "deep", "deeper"), "b.dll") };
        File(command, "ok.dll");

        var result = new SandboxGrantVerifier(p => !denied.Contains(p)).Verify(Spec(command));

        Assert.False(result.Ok);
        Assert.Equal(2, result.BlockedCount);
        Assert.Contains("2 file(s)", result.Reason);
        Assert.Contains("administrator", result.Reason);
        Assert.DoesNotContain(_root, result.Reason);
    }

    [Fact]
    public void TheCommandDirectoryItself_IsChecked()
    {
        var command = Dir("srv");

        var result = new SandboxGrantVerifier(p => !string.Equals(p, command, StringComparison.OrdinalIgnoreCase))
            .Verify(Spec(command));

        Assert.Equal(1, result.BlockedCount);
    }

    [Fact]
    public void ACommandDirectoryThatNeedsNoGrant_IsNeverProbed()
    {
        var probed = new List<string>();

        var result = new SandboxGrantVerifier(p => { probed.Add(p); return false; }).Verify(Spec(commandDirectory: null));

        Assert.True(result.Ok);
        Assert.Empty(probed);
    }

    [Fact]
    public void PolicyRoots_AreCheckedShallow_TheRootAndItsDirectFiles()
    {
        var rw = Dir("rw");
        var direct = File(rw, "direct.txt");
        var deep = File(Dir("rw", "sub"), "deep.txt");
        var probed = new List<string>();

        var result = new SandboxGrantVerifier(p => { probed.Add(p); return p != direct; }).Verify(Spec(rw: [rw]));

        Assert.Equal(1, result.BlockedCount);
        Assert.Contains("item(s)", result.Reason);
        Assert.DoesNotContain(deep, probed);
    }

    [Fact]
    public void AReadonlyRoot_IsCheckedLikeAReadwriteRoot()
    {
        var ro = Dir("ro");

        var result = new SandboxGrantVerifier(p => !string.Equals(p, ro, StringComparison.OrdinalIgnoreCase))
            .Verify(Spec(ro: [ro]));

        Assert.False(result.Ok);
    }

    [Fact]
    public void AMissingPolicyPath_IsNotCountedAsBlocked()
    {
        var result = new SandboxGrantVerifier(_ => false).Verify(Spec(rw: [Path.Combine(_root, "missing")]));

        Assert.True(result.Ok);
    }

    [Fact]
    public void BothKindsOfProblem_AreNamedInOneReason()
    {
        var command = Dir("srv");
        var rw = Dir("rw");

        var result = new SandboxGrantVerifier(_ => false).Verify(Spec(command, rw: [rw]));

        Assert.Contains("file(s) in its folder", result.Reason);
        Assert.Contains("folders you chose", result.Reason);
    }

    [Fact]
    public void TheWalk_StopsAtTheCap_AndTrustsTheRest()
    {
        var command = Dir("srv");
        for (var i = 0; i < SandboxGrantVerifier.MaxEntries + 500; i++)
            System.IO.File.WriteAllBytes(Path.Combine(command, $"f{i}.bin"), []);
        var calls = 0;

        var result = new SandboxGrantVerifier(_ => { calls++; return true; }).Verify(Spec(command));

        Assert.True(result.Ok);
        Assert.InRange(calls, SandboxGrantVerifier.MaxEntries, SandboxGrantVerifier.MaxEntries + 2);
    }

    private static SandboxLaunchSpec RuntimeSpec(
        IReadOnlyList<string>? toolchain = null, string? state = null, string? support = null) =>
        Spec() with { ToolchainDirectories = toolchain, RuntimeStateDirectory = state, SupportDirectory = support };

    [Fact]
    public void AToolchain_IsWalkedPastTheCap_EveryEntryProbed()
    {
        var node = Dir("node");
        for (var i = 0; i < 3000; i++)
            System.IO.File.WriteAllBytes(Path.Combine(node, $"f{i}.js"), []);
        var deep = Dir("node", "node_modules", "npm");
        for (var i = 0; i < 3000; i++)
            System.IO.File.WriteAllBytes(Path.Combine(deep, $"g{i}.js"), []);
        var probed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new SandboxGrantVerifier(p => { probed.Add(p); return true; }).Verify(RuntimeSpec([node]));

        Assert.True(result.Ok);
        Assert.Equal(6003, probed.Count);
        Assert.Contains(node, probed);
        Assert.Contains(Path.Combine(deep, "g2999.js"), probed);
    }

    [Fact]
    public void ADeepBlockedFileInTheToolchain_Refuses_WithTheRuntimeClause()
    {
        var node = Dir("node");
        File(node, "node.exe");
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            File(Dir("node", "node_modules", "npm", "bin"), "npx-cli.js"),
            File(Dir("node", "node_modules", "npm", "lib", "deep"), "x.js"),
        };

        var result = new SandboxGrantVerifier(p => !denied.Contains(p)).Verify(RuntimeSpec([node]));

        Assert.False(result.Ok);
        Assert.Equal(2, result.BlockedCount);
        Assert.Equal(
            "Pia can't sandbox this server: 2 file(s) in the runtime folder belong to an administrator or another account. "
            + "Install the runtime for your user only; the server's settings show how.",
            result.Reason);
    }

    [Fact]
    public void ACleanToolchain_IsRemembered_ForTenMinutes()
    {
        var node = Dir("node");
        File(node, "node.exe");
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var probes = 0;
        var verifier = new SandboxGrantVerifier(_ => { probes++; return true; }, () => now);

        verifier.Verify(RuntimeSpec([node]));
        var firstWalk = probes;
        now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59);
        verifier.Verify(RuntimeSpec([node.ToUpperInvariant() + "\\"]));

        Assert.Equal(2, firstWalk);
        Assert.Equal(firstWalk, probes);

        now += TimeSpan.FromSeconds(2);
        verifier.Verify(RuntimeSpec([node]));

        Assert.Equal(2 * firstWalk, probes);
    }

    [Fact]
    public void ABlockedToolchain_IsNotRemembered()
    {
        var node = Dir("node");
        var blocked = File(node, "node.exe");
        var probes = 0;
        var verifier = new SandboxGrantVerifier(p => { probes++; return p != blocked; });

        Assert.False(verifier.Verify(RuntimeSpec([node])).Ok);
        Assert.False(verifier.Verify(RuntimeSpec([node])).Ok);

        Assert.Equal(4, probes);
    }

    [Fact]
    public void AMissingToolchain_IsNotRemembered()
    {
        var node = Path.Combine(_root, "node");
        var verifier = new SandboxGrantVerifier(_ => false);

        Assert.True(verifier.Verify(RuntimeSpec([node])).Ok);
        File(Dir("node"), "node.exe");

        Assert.Equal(2, verifier.Verify(RuntimeSpec([node])).BlockedCount);
    }

    [Fact]
    public void PiasOwnFolders_AreCheckedShallow_UnderTheirOwnClause()
    {
        var state = Dir("fs.runtime");
        var stateFile = File(state, "spec.json");
        var stateDeep = File(Dir("fs.runtime", "npm-cache"), "deep.bin");
        var support = Dir("support");
        var preload = File(support, "node-sandbox.cjs");
        var probed = new List<string>();
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stateFile, preload, stateDeep };

        var result = new SandboxGrantVerifier(p => { probed.Add(p); return !denied.Contains(p); })
            .Verify(RuntimeSpec(state: state, support: support));

        Assert.Equal(2, result.BlockedCount);
        Assert.Equal(
            "Pia can't sandbox this server: 2 file(s) in Pia's own folders for this server can't be shared. "
            + "Remove the server and add it again; Pia then starts with fresh folders.",
            result.Reason);
        Assert.Contains(state, probed);
        Assert.Contains(support, probed);
        Assert.DoesNotContain(stateDeep, probed);
    }

    [Fact]
    public void EveryClause_CountsTowardsTheBlockedTotal()
    {
        var command = Dir("srv");
        var node = Dir("node");
        var rw = Dir("rw");
        var state = Dir("fs.runtime");

        var result = new SandboxGrantVerifier(_ => false).Verify(Spec(command, rw: [rw]) with
        {
            ToolchainDirectories = [node],
            RuntimeStateDirectory = state
        });

        Assert.Equal(4, result.BlockedCount);
        Assert.Contains("file(s) in its folder", result.Reason);
        Assert.Contains("file(s) in the runtime folder", result.Reason);
        Assert.Contains("folders you chose", result.Reason);
        Assert.Contains("Pia's own folders", result.Reason);
        Assert.DoesNotContain(_root, result.Reason);
    }

    [Fact]
    public void TheRealProbe_AcceptsAFileTheUserOwns()
    {
        var path = File(Dir("own"), "mine.txt");

        Assert.True(Pia.Native.FileSecurityProbe.CanWriteDacl(path));
        Assert.True(Pia.Native.FileSecurityProbe.CanWriteDacl(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void TheRealProbe_RefusesAPathThatIsNotThere()
    {
        Assert.False(Pia.Native.FileSecurityProbe.CanWriteDacl(Path.Combine(_root, "nope.txt")));
    }
}
