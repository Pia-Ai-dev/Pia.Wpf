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
