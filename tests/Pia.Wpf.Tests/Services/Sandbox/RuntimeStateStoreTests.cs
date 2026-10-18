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
    public void AGrantRecord_IsReplacedByTheNextOne_ExceptForRootsStillOnDisk()
    {
        var prefix = NewPrefix();
        _store.WriteGrantRecord($"pia-mcp-{prefix}", [Path.Combine(_outside, "gone")], stateReadOnly: true);

        _store.WriteGrantRecord($"pia-mcp-{prefix}", [@"C:\new"]);

        Assert.Equal([@"C:\new"], _store.SweepRoots(prefix));
        Assert.False(_store.StateGrantedReadOnly($"pia-mcp-{prefix}"));
        Assert.Single(Directory.EnumerateFiles(PiaPaths.McpSandboxGrantsDirectory, $"*{prefix}*"));
    }

    [Fact]
    public void AGrantRecord_KeepsAnEarlierRootWhileItIsOnDisk()
    {
        var prefix = NewPrefix();
        var container = $"pia-mcp-{prefix}";
        var earlier = Directory.CreateDirectory(Path.Combine(_outside, "node20")).FullName;
        var later = Directory.CreateDirectory(Path.Combine(_outside, "node22")).FullName;
        var shared = Directory.CreateDirectory(Path.Combine(_outside, "support")).FullName;
        _store.WriteGrantRecord(container, [earlier, shared]);

        _store.WriteGrantRecord(container, [later, shared.ToUpperInvariant()]);

        Assert.Equal([later, shared.ToUpperInvariant(), earlier], _store.SweepRoots(prefix));

        Directory.Delete(earlier);
        _store.WriteGrantRecord(container, [later, shared]);

        Assert.Equal([later, shared], _store.SweepRoots(prefix));
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

    // The traversals land on <profile>\sentinel.runtime and, for the records, <profile>\sentinel.json.
    [Theory]
    [InlineData(@"x\..\..\..\sentinel")]
    [InlineData("x/../../../sentinel")]
    [InlineData(@"..\..\sentinel")]
    [InlineData(@"{root}\sentinel")]
    [InlineData(@"D:\data\proj")]
    public void APrefixThatIsNotAPlainName_IsNeitherSweptNorDeleted(string prefix)
    {
        prefix = prefix.Replace("{root}", _profile.Root);
        var sentinelState = Directory.CreateDirectory(Path.Combine(_profile.Root, "sentinel" + PiaPaths.McpRuntimeStateSuffix)).FullName;
        var sentinel = Path.Combine(sentinelState, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        string[] sentinelRecords = [Path.Combine(_profile.Root, "sentinel.json"), Path.Combine(_profile.Root, "sentinel-probe.json")];
        foreach (var record in sentinelRecords)
            File.WriteAllText(record, "{\"roots\":[]}");
        var called = false;
        try
        {
            Assert.Empty(_store.SweepRoots(prefix));
            Assert.False(_store.TryDelete(prefix, () => called = true));

            Assert.False(called);
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.All(sentinelRecords, record => Assert.True(File.Exists(record)));
            Assert.Equal("outside", File.ReadAllText(_outsideFile));
            Assert.False(Directory.Exists(PiaPaths.McpSandboxGrantsDirectory)
                && Directory.EnumerateFileSystemEntries(PiaPaths.McpSandboxGrantsDirectory).Any());
        }
        finally
        {
            TempPath.Remove(sentinelState);
            foreach (var record in sentinelRecords)
                File.Delete(record);
        }
    }

    [Fact]
    public void OrphanPrefixes_FindsStateFoldersAndRecords_NotKnownOnes()
    {
        StateOf("orphanfolder");
        StateOf("known");
        Directory.CreateDirectory(Path.Combine(PiaPaths.McpSandboxRoot, "plainserver"));
        _store.WriteGrantRecord(SandboxLaunchSpecBuilder.ContainerName("orphanrecord", isProbe: false), []);
        _store.WriteGrantRecord(SandboxLaunchSpecBuilder.ContainerName("orphanprobe", isProbe: true), []);
        _store.WriteGrantRecord(SandboxLaunchSpecBuilder.ContainerName("known", isProbe: true), []);
        _store.WriteGrantRecord(SandboxLaunchSpecBuilder.ContainerName("orphanfolder", isProbe: false), []);
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
