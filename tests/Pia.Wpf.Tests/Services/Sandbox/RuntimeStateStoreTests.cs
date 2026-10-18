using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Paths;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

[Collection("PiaPathsStatic")]
public sealed class RuntimeStateStoreTests : IClassFixture<RedirectedProfileFixture>, IDisposable
{
    private readonly RedirectedProfileFixture _profile;
    private readonly RuntimeStateStore _store = new(NullLogger<RuntimeStateStore>.Instance);
    private readonly string _outside;
    private readonly string _outsideFile;

    public RuntimeStateStoreTests(RedirectedProfileFixture profile)
    {
        _profile = profile;
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxRoot);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxGrantsDirectory);
        _outside = Directory.CreateDirectory(Path.Combine(profile.Root, "outside-" + Guid.NewGuid().ToString("N"))).FullName;
        _outsideFile = Path.Combine(_outside, "keep.txt");
        File.WriteAllText(_outsideFile, "outside");
    }

    public void Dispose()
    {
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxRoot);
        TempPath.Remove(_outside);
    }

    private static string NewPrefix() => "p" + Guid.NewGuid().ToString("N")[..10];

    private static string StateOf(string prefix) => Directory.CreateDirectory(PiaPaths.McpRuntimeStateDirectory(prefix)).FullName;

    private static string Record(string containerName) =>
        Path.Combine(PiaPaths.McpSandboxGrantsDirectory, containerName + ".json");

    [Fact]
    public void GrantRecords_AreRoutedToThePiaDataFolder()
    {
        _store.WriteGrantRecord("pia-mcp-routed", [@"C:\Tools\node"]);

        Assert.StartsWith(_profile.Root, PiaPaths.McpSandboxGrantsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Record("pia-mcp-routed")));
        Assert.Equal(Path.Combine(PiaPaths.McpSandboxRoot, "routed.runtime"), PiaPaths.McpRuntimeStateDirectory("routed"));
    }

    [Fact]
    public void AGrantRecord_RoundTrips_WithContainerRootsAndTime()
    {
        var prefix = NewPrefix();
        var before = DateTime.UtcNow.AddSeconds(-1);

        _store.WriteGrantRecord($"pia-mcp-{prefix}", [@"C:\Tools\node", @"C:\Users\Jane Doe\app"]);

        using var json = JsonDocument.Parse(File.ReadAllText(Record($"pia-mcp-{prefix}")));
        Assert.Equal($"pia-mcp-{prefix}", json.RootElement.GetProperty("container").GetString());
        Assert.Equal([@"C:\Tools\node", @"C:\Users\Jane Doe\app"],
            json.RootElement.GetProperty("roots").EnumerateArray().Select(r => r.GetString()));
        Assert.True(json.RootElement.GetProperty("writtenUtc").GetDateTime() >= before);
        Assert.Equal([@"C:\Tools\node", @"C:\Users\Jane Doe\app"], _store.SweepRoots(prefix));
    }

    [Fact]
    public void AGrantRecord_IsReplacedByTheNextOne()
    {
        var prefix = NewPrefix();
        _store.WriteGrantRecord($"pia-mcp-{prefix}", [@"C:\old"]);

        _store.WriteGrantRecord($"pia-mcp-{prefix}", [@"C:\new"]);

        Assert.Equal([@"C:\new"], _store.SweepRoots(prefix));
        Assert.Single(Directory.EnumerateFiles(PiaPaths.McpSandboxGrantsDirectory, $"*{prefix}*"));
    }

    [Fact]
    public void AContainerNameThatIsNotAFileName_IsRefused() =>
        Assert.Throws<ArgumentException>(() => _store.WriteGrantRecord(@"..\pia-mcp-x", []));

    [Fact]
    public void TheStateAccess_RoundTrips_AndOnlyAnExplicitReadOnlyCounts()
    {
        var prefix = NewPrefix();
        Assert.Null(_store.StateGrantedReadOnly($"pia-mcp-{prefix}"));

        _store.WriteGrantRecord($"pia-mcp-{prefix}", [@"C:\state"], stateReadOnly: true);
        Assert.True(_store.StateGrantedReadOnly($"pia-mcp-{prefix}"));
        using (var json = JsonDocument.Parse(File.ReadAllText(Record($"pia-mcp-{prefix}"))))
            Assert.True(json.RootElement.GetProperty("stateReadOnly").GetBoolean());

        _store.WriteGrantRecord($"pia-mcp-{prefix}", [@"C:\state"]);
        Assert.False(_store.StateGrantedReadOnly($"pia-mcp-{prefix}"));
    }

    [Theory]
    [InlineData("{\"container\":\"x\",\"roots\":[],\"writtenUtc\":\"2026-10-18T00:00:00Z\"}")]
    [InlineData("{ not json")]
    public void ARecordWithoutTheStateAccess_OrDamaged_CountsAsReadWrite(string content)
    {
        var container = $"pia-mcp-{NewPrefix()}-probe";
        Directory.CreateDirectory(PiaPaths.McpSandboxGrantsDirectory);
        File.WriteAllText(Record(container), content);

        Assert.False(_store.StateGrantedReadOnly(container));
    }

    [Fact]
    public void SweepRoots_WithNothingOnDisk_IsEmpty() => Assert.Empty(_store.SweepRoots(NewPrefix()));

    [Fact]
    public void SweepRoots_HoldsTheStateFolder_WhenItExists()
    {
        var prefix = NewPrefix();
        var state = StateOf(prefix);

        Assert.Equal([state], _store.SweepRoots(prefix));
    }

    [Fact]
    public void SweepRoots_JoinsTheStateFolderAndBothRecords_WithoutDuplicates()
    {
        var prefix = NewPrefix();
        var state = StateOf(prefix);
        _store.WriteGrantRecord($"pia-mcp-{prefix}", [@"C:\Tools\node", state]);
        _store.WriteGrantRecord($"pia-mcp-{prefix}-probe", [@"C:\TOOLS\NODE", @"C:\Tools\support"]);

        Assert.Equal([state, @"C:\Tools\node", @"C:\Tools\support"], _store.SweepRoots(prefix));
    }

    [Fact]
    public void SweepRoots_IgnoresADamagedRecord()
    {
        var prefix = NewPrefix();
        _store.WriteGrantRecord($"pia-mcp-{prefix}", [@"C:\Tools\node"]);
        File.WriteAllText(Record($"pia-mcp-{prefix}-probe"), "{ not json");

        Assert.Equal([@"C:\Tools\node"], _store.SweepRoots(prefix));
    }

    [Fact]
    public void TryDelete_RemovesADeepTreeWithReadOnlyFilesAndAJunction_AndBothRecords()
    {
        var prefix = NewPrefix();
        var state = StateOf(prefix);
        var deep = Directory.CreateDirectory(Path.Combine(state, "tools", "pkg", "Lib", "site-packages", "a", "b")).FullName;
        var readOnly = Path.Combine(deep, "ro.py");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        StateFolderSafetyTests.Junction(Path.Combine(state, "home", "link"), _outside);
        _store.WriteGrantRecord($"pia-mcp-{prefix}", [state]);
        _store.WriteGrantRecord($"pia-mcp-{prefix}-probe", [state]);
        var called = false;

        Assert.True(_store.TryDelete(prefix, () => called = true));

        Assert.True(called);
        Assert.False(Directory.Exists(state));
        Assert.False(File.Exists(Record($"pia-mcp-{prefix}")));
        Assert.False(File.Exists(Record($"pia-mcp-{prefix}-probe")));
        Assert.Equal("outside", File.ReadAllText(_outsideFile));
    }

    [Fact]
    public void TryDelete_OfAPrefixWithNothingOnDisk_StillRunsBeforeDelete()
    {
        var called = false;

        Assert.True(_store.TryDelete(NewPrefix(), () => called = true));

        Assert.True(called);
    }

    [Fact]
    public void TryDelete_NeverThrows_WhenBeforeDeleteFails()
    {
        var prefix = NewPrefix();
        var state = StateOf(prefix);

        Assert.True(_store.TryDelete(prefix, () => throw new IOException("busy")));

        Assert.True(Directory.Exists(state));
    }

    [Fact]
    public void TryDelete_WhileTheStateFolderLockIsHeld_TouchesNothing()
    {
        var prefix = NewPrefix();
        var state = StateOf(prefix);
        _store.WriteGrantRecord($"pia-mcp-{prefix}", [state]);
        var called = false;

        using (RuntimeStateLocks.TryAcquire(state))
            Assert.False(_store.TryDelete(prefix, () => called = true));

        Assert.False(called);
        Assert.True(Directory.Exists(state));
        Assert.True(File.Exists(Record($"pia-mcp-{prefix}")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryDelete_WhileEitherContainerHoldsALease_TouchesNothing_AndFreesTheLock(bool probe)
    {
        var prefix = NewPrefix();
        var state = StateOf(prefix);
        var called = false;

        using (SandboxContainerLease.Acquire(SandboxLaunchSpecBuilder.ContainerName(prefix, probe)))
            Assert.False(_store.TryDelete(prefix, () => called = true));

        Assert.False(called);
        Assert.True(Directory.Exists(state));
        using var free = RuntimeStateLocks.TryAcquire(state);
        Assert.NotNull(free);
    }

    [Fact]
    public void OrphanPrefixes_FindsStateFoldersAndRecords_NotKnownOnes()
    {
        StateOf("orphanfolder");
        StateOf("known");
        Directory.CreateDirectory(Path.Combine(PiaPaths.McpSandboxRoot, "plainserver"));
        _store.WriteGrantRecord("pia-mcp-orphanrecord", []);
        _store.WriteGrantRecord("pia-mcp-orphanprobe-probe", []);
        _store.WriteGrantRecord("pia-mcp-known-probe", []);
        _store.WriteGrantRecord("pia-mcp-orphanfolder", []);
        File.WriteAllText(Path.Combine(PiaPaths.McpSandboxGrantsDirectory, "notes.json"), "{}");

        var orphans = _store.OrphanPrefixes(new HashSet<string> { "known" });

        Assert.Equal(["orphanfolder", "orphanprobe", "orphanrecord"], orphans.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OrphanPrefixes_WithNoFolders_IsEmpty()
    {
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxGrantsDirectory);

        Assert.Empty(_store.OrphanPrefixes(new HashSet<string>()));
    }
}
