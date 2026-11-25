using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Paths;
using Pia.Services.LocalDataReset;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LocalDataReset;

[Collection("PiaPathsStatic")]
public sealed class LocalDataResetStartupTests : IDisposable
{
    private readonly string _roaming = Path.Combine(Path.GetTempPath(), $"pia-startup-r-{Guid.NewGuid():N}");
    private readonly string _local = Path.Combine(Path.GetTempPath(), $"pia-startup-l-{Guid.NewGuid():N}");
    private readonly IDisposable _override;

    public LocalDataResetStartupTests() => _override = PiaPaths.OverrideForTests(_roaming, _local);

    public void Dispose()
    {
        _override.Dispose();
        TempPath.Remove(_roaming);
        TempPath.Remove(_local);
    }

    [Fact]
    public async Task RunIfPending_WithoutMarker_DoesNotWipe()
    {
        var calls = 0;

        var ran = await LocalDataResetStartup.RunIfPendingAsync(() => { calls++; return Task.CompletedTask; }, NullLogger.Instance);

        Assert.False(ran);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunIfPending_WithMarker_WipesThenClearsMarker()
    {
        LocalDataResetMarker.Write();
        var calls = 0;

        var ran = await LocalDataResetStartup.RunIfPendingAsync(() => { calls++; return Task.CompletedTask; }, NullLogger.Instance);

        Assert.True(ran);
        Assert.Equal(1, calls);
        Assert.False(LocalDataResetMarker.IsPending);
    }

    [Fact]
    public async Task RunIfPending_WhenWipeThrows_KeepsMarkerAndDoesNotThrow()
    {
        LocalDataResetMarker.Write();

        var ran = await LocalDataResetStartup.RunIfPendingAsync(() => throw new IOException("locked"), NullLogger.Instance);

        Assert.False(ran);
        Assert.True(LocalDataResetMarker.IsPending);
    }
}
