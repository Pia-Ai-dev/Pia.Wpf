using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Tier 3 adds the container's SID to every policy path and a hard kill leaves it there. The sweep removes
/// Pia's own container SIDs and no other: a user folder may carry other packaged apps' ACEs.</summary>
public sealed class AceSweepServiceTests : IDisposable
{
    private static readonly SecurityIdentifier Ours = new("S-1-15-2-1111111111-2222222222-3333333333-4444444444-5555555555-6666666666-7777777777");
    private static readonly SecurityIdentifier Foreign = new("S-1-15-2-9999999999-8888888888-7777777777-6666666666-5555555555-4444444444-3333333333");
    private static readonly SecurityIdentifier Running = new("S-1-15-2-1212121212-3434343434-1515151515-2626262626-3737373737-4848484848-1919191919");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-ace-" + Guid.NewGuid().ToString("N"));

    public AceSweepServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TempPath.Remove(_root);

    private AceSweepService Service(Action<string>? beforeEntry = null)
    {
        var sids = Substitute.For<IPiaContainerSidResolver>();
        sids.Resolve(Arg.Any<IEnumerable<string>>()).Returns(new HashSet<string> { Ours.Value });
        return new AceSweepService(sids, NullLogger<AceSweepService>.Instance, beforeEntry);
    }

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

    private static void Leak(string path, SecurityIdentifier sid)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
    }

    private static List<string> ExplicitSids(string path)
    {
        FileSystemSecurity acl = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        return [.. acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value)];
    }

    [Fact]
    public void OurLeakedAce_IsRemovedFromARoot_AndEveryoneElsesIsLeftAlone()
    {
        var root = Dir("root");
        Leak(root, Ours);
        Leak(root, Foreign);
        var userAcesBefore = ExplicitSids(root).Count(s => s != Ours.Value);

        var result = Service().Sweep([root], ["pia-mcp-fs"]);

        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Failed);
        Assert.DoesNotContain(Ours.Value, ExplicitSids(root));
        Assert.Contains(Foreign.Value, ExplicitSids(root));
        Assert.Equal(userAcesBefore, ExplicitSids(root).Count);
    }

    [Fact]
    public void ARootThatCarriesOurAce_IsWalked_AndEveryLeakBelowIsRemoved()
    {
        var root = Dir("root");
        var childDir = Dir("root", "child");
        var deepFile = File(Dir("root", "child", "deep"), "d.txt");
        var topFile = File(root, "t.txt");
        foreach (var path in new[] { root, childDir, deepFile, topFile })
            Leak(path, Ours);

        var result = Service().Sweep([root], ["pia-mcp-fs"]);

        Assert.Equal(4, result.Removed);
        Assert.All(new[] { root, childDir, deepFile, topFile }, p => Assert.DoesNotContain(Ours.Value, ExplicitSids(p)));
    }

    /// <summary>Shallow first: the cost of a start is one ACL read per root, not a walk of a big tree.</summary>
    [Fact]
    public void ACleanRoot_IsNotWalked()
    {
        var root = Dir("clean");
        var child = File(root, "c.txt");
        Leak(child, Ours);
        var visited = new List<string>();

        var result = Service(visited.Add).Sweep([root], ["pia-mcp-fs"]);

        Assert.Equal(0, result.Removed);
        Assert.Empty(visited);
        Assert.Contains(Ours.Value, ExplicitSids(child));
    }

    /// <summary>The root carries the marker that says "there may be more below", so it goes last.</summary>
    [Fact]
    public void Children_AreCleanedBeforeTheirFolders_AndTheRootIsLast()
    {
        var root = Dir("root");
        var sub = Dir("root", "sub");
        var file = File(sub, "f.txt");
        foreach (var path in new[] { root, sub, file })
            Leak(path, Ours);
        var order = new List<string>();

        Service(order.Add).Sweep([root], ["pia-mcp-fs"]);

        Assert.Equal(root, order[^1]);
        Assert.True(order.IndexOf(file) < order.IndexOf(sub));
        Assert.True(order.IndexOf(sub) < order.IndexOf(root));
    }

    [Fact]
    public void ASweepThatFailsMidway_LeavesTheRootAce_SoTheNextSweepFinishesTheJob()
    {
        var root = Dir("root");
        var first = File(root, "a.txt");
        var second = File(root, "b.txt");
        var third = File(root, "c.txt");
        foreach (var path in new[] { root, first, second, third })
            Leak(path, Ours);
        var seen = 0;

        var interrupted = Service(_ => { if (++seen == 2) throw new IOException("sweep interrupted"); }).Sweep([root], ["pia-mcp-fs"]);

        Assert.True(interrupted.Failed >= 1);
        Assert.Contains(Ours.Value, ExplicitSids(root));

        var finished = Service().Sweep([root], ["pia-mcp-fs"]);

        Assert.Equal(0, finished.Failed);
        Assert.All(new[] { root, first, second, third }, p => Assert.DoesNotContain(Ours.Value, ExplicitSids(p)));
    }

    [Fact]
    public void SystemFolders_AreRefused_AndNeverTouched()
    {
        var touched = new List<string>();

        var result = Service(touched.Add).Sweep(
            [Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)],
            ["pia-mcp-fs"]);

        Assert.Equal(0, result.Roots);
        Assert.Empty(touched);
    }

    [Fact]
    public void AMissingRoot_IsSkipped()
    {
        var result = Service().Sweep([Path.Combine(_root, "gone")], ["pia-mcp-fs"]);

        Assert.Equal(0, result.Roots);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void ADirectoryLink_IsNotFollowed()
    {
        var root = Dir("root");
        var outside = Dir("outside");
        var outsideFile = File(outside, "o.txt");
        Leak(root, Ours);
        Leak(outsideFile, Ours);
        var link = Path.Combine(root, "link");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            mklink.WaitForExit();
        }
        Assert.SkipUnless(Directory.Exists(link), "a junction could not be created here");

        Service().Sweep([root], ["pia-mcp-fs"]);

        Assert.Contains(Ours.Value, ExplicitSids(outsideFile));
    }

    [Fact]
    public void WithNoContainerSids_NothingIsRemoved()
    {
        var root = Dir("root");
        Leak(root, Ours);
        var sids = Substitute.For<IPiaContainerSidResolver>();
        sids.Resolve(Arg.Any<IEnumerable<string>>()).Returns(new HashSet<string>());

        var result = new AceSweepService(sids, NullLogger<AceSweepService>.Instance).Sweep([root], ["pia-mcp-fs"]);

        Assert.Equal(0, result.Removed);
        Assert.Contains(Ours.Value, ExplicitSids(root));
    }

    /// <summary>A shared toolchain carries the ACE of every container that uses it; removing one server must not
    /// take the grant away from another that is still running.</summary>
    [Fact]
    public void TheAceOfAnotherContainerStillRunning_SurvivesASweepOfASharedRoot()
    {
        var shared = Dir("toolchain");
        var removed = "pia-mcp-removed-" + Guid.NewGuid().ToString("N");
        var running = "pia-mcp-running-" + Guid.NewGuid().ToString("N");
        Leak(shared, Ours);
        Leak(shared, Running);
        var resolver = new PiaContainerSidResolver(() => [(Ours.Value, removed), (Running.Value, running)], _ => null);
        using var lease = SandboxContainerLease.Acquire(running);

        var result = new AceSweepService(resolver, NullLogger<AceSweepService>.Instance).Sweep([shared], [removed]);

        Assert.Equal(1, result.Removed);
        Assert.DoesNotContain(Ours.Value, ExplicitSids(shared));
        Assert.Contains(Running.Value, ExplicitSids(shared));
    }

    [Fact]
    public void TheSameRootTwice_IsSweptOnce()
    {
        var root = Dir("root");
        Leak(root, Ours);

        var result = Service().Sweep([root, root + Path.DirectorySeparatorChar, root.ToUpperInvariant()], ["pia-mcp-fs"]);

        Assert.Equal(1, result.Roots);
        Assert.Equal(1, result.Removed);
    }
}
