using Pia.Native;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class PiaContainerSidResolverTests
{
    private static PiaContainerSidResolver Resolver(
        IEnumerable<(string Sid, string Moniker)> mappings, Func<string, string?>? derive = null) =>
        new(() => mappings, derive ?? (_ => null));

    [Fact]
    public void OnlyContainersWhoseNameStartsWithOurPrefix_AreOurs()
    {
        var resolver = Resolver([
            ("S-1-15-2-101", "pia-mcp-fs"),
            ("S-1-15-2-102", "Microsoft.WindowsStore_8wekyb3d8bbwe"),
            ("S-1-15-2-103", "pia-mcp-fs-probe"),
            ("S-1-15-2-104", "spike-hold")]);

        var sids = resolver.Resolve([]);

        Assert.Equal(["S-1-15-2-101", "S-1-15-2-103"], sids.Order());
    }

    [Fact]
    public void TheSidsOfTheNamesInUse_AreDerived_EvenWithoutAMapping()
    {
        var resolver = Resolver([], name => name == "pia-mcp-fs" ? "S-1-15-2-derived" : null);

        var sids = resolver.Resolve(["pia-mcp-fs", "pia-mcp-other"]);

        Assert.Equal(["S-1-15-2-derived"], sids);
    }

    [Fact]
    public void TheSameSid_FromBothSources_IsOnlyListedOnce()
    {
        var resolver = Resolver([("S-1-15-2-101", "pia-mcp-fs")], _ => "S-1-15-2-101");

        Assert.Single(resolver.Resolve(["pia-mcp-fs"]));
    }

    [Fact]
    public void AContainerRunningInThisProcess_IsNotSwept_FromItsMapping()
    {
        var running = "pia-mcp-running-" + Guid.NewGuid().ToString("N");
        var stopped = "pia-mcp-stopped-" + Guid.NewGuid().ToString("N");
        var resolver = Resolver([("S-1-15-2-201", running), ("S-1-15-2-202", stopped)]);
        using var lease = SandboxContainerLease.Acquire(running);

        var sids = resolver.Resolve([]);

        Assert.Equal(["S-1-15-2-202"], sids);
    }

    [Fact]
    public void ANameInUse_WhoseContainerIsRunning_ContributesNoSid()
    {
        var running = "pia-mcp-files-probe-" + Guid.NewGuid().ToString("N");
        var stopped = "pia-mcp-files-" + Guid.NewGuid().ToString("N");
        var resolver = Resolver([], name => name == running ? "S-1-15-2-running" : "S-1-15-2-stopped");
        using var lease = SandboxContainerLease.Acquire(running);

        var sids = resolver.Resolve([stopped, running]);

        Assert.Equal(["S-1-15-2-stopped"], sids);
    }

    [Fact]
    public void AContainerWhoseLeaseIsReleased_IsSweptAgain()
    {
        var name = "pia-mcp-released-" + Guid.NewGuid().ToString("N");
        var resolver = Resolver([("S-1-15-2-203", name)]);
        SandboxContainerLease.Acquire(name).Dispose();

        Assert.Equal(["S-1-15-2-203"], resolver.Resolve([]));
    }

    [Fact]
    public void TheWellKnownPackageSids_AreNeverOurs()
    {
        var resolver = Resolver([("S-1-15-2-1", "pia-mcp-x"), ("S-1-15-2-2", "pia-mcp-y")], _ => "S-1-15-2-1");

        Assert.Empty(resolver.Resolve(["pia-mcp-x"]));
    }

    [Fact]
    public void ABrokenRegistryRead_FallsBackToTheDerivedSids()
    {
        var resolver = new PiaContainerSidResolver(() => throw new System.Security.SecurityException("denied"), name => "S-1-15-2-derived");

        Assert.Equal(["S-1-15-2-derived"], resolver.Resolve(["pia-mcp-fs"]));
    }

    [Fact]
    public void TheNativeDerivation_IsDeterministic_AndDiffersPerName()
    {
        var a = AppContainerNative.DeriveSid("pia-mcp-derive-test-a");
        var again = AppContainerNative.DeriveSid("pia-mcp-derive-test-a");
        var b = AppContainerNative.DeriveSid("pia-mcp-derive-test-b");

        Assert.StartsWith("S-1-15-2-", a);
        Assert.Equal(a, again);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TheRealRegistryRead_DoesNotThrow()
    {
        var resolver = new PiaContainerSidResolver();

        var sids = resolver.Resolve(["pia-mcp-registry-read-test"]);

        Assert.Contains(AppContainerNative.DeriveSid("pia-mcp-registry-read-test")!, sids);
    }
}
