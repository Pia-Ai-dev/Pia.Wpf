using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxContainerLeaseTests
{
    private static string Unique() => "pia-mcp-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void ANameCannotBeLeasedTwiceAtOnce()
    {
        var name = Unique();
        using var first = SandboxContainerLease.Acquire(name);

        var ex = Assert.Throws<SandboxStartException>(() => SandboxContainerLease.Acquire(name));

        Assert.Contains("already running", ex.Message);
    }

    [Fact]
    public void ANameCanBeLeasedAgainOnceReleased()
    {
        var name = Unique();
        SandboxContainerLease.Acquire(name).Dispose();

        using var again = SandboxContainerLease.Acquire(name);

        Assert.Equal(name, again.Name);
    }

    [Fact]
    public void NamesCompareWithoutRegardToCase()
    {
        var name = Unique();
        using var first = SandboxContainerLease.Acquire(name);

        Assert.Throws<SandboxStartException>(() => SandboxContainerLease.Acquire(name.ToUpperInvariant()));
    }

    [Fact]
    public void DifferentNamesDoNotCollide()
    {
        using var a = SandboxContainerLease.Acquire(Unique());
        using var b = SandboxContainerLease.Acquire(Unique());

        Assert.NotEqual(a.Name, b.Name);
    }

    [Fact]
    public void IsActive_FollowsTheLease()
    {
        var name = Unique();
        Assert.False(SandboxContainerLease.IsActive(name));

        var lease = SandboxContainerLease.Acquire(name);
        Assert.True(SandboxContainerLease.IsActive(name));
        Assert.True(SandboxContainerLease.IsActive(name.ToUpperInvariant()));

        lease.Dispose();
        Assert.False(SandboxContainerLease.IsActive(name));
    }

    [Fact]
    public void ReleasingTwice_DoesNotFreeSomeoneElsesLease()
    {
        var name = Unique();
        var first = SandboxContainerLease.Acquire(name);
        first.Dispose();
        using var second = SandboxContainerLease.Acquire(name);

        first.Dispose();

        Assert.Throws<SandboxStartException>(() => SandboxContainerLease.Acquire(name));
    }
}
