using System;
using System.IO;
using Pia.Paths;
using Pia.Services.LocalDataReset;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LocalDataReset;

[Collection("PiaPathsStatic")]
public sealed class LocalDataResetMarkerTests : IDisposable
{
    private readonly string _roaming = Path.Combine(Path.GetTempPath(), $"pia-marker-r-{Guid.NewGuid():N}");
    private readonly string _local = Path.Combine(Path.GetTempPath(), $"pia-marker-l-{Guid.NewGuid():N}");
    private readonly IDisposable _override;

    public LocalDataResetMarkerTests() => _override = PiaPaths.OverrideForTests(_roaming, _local);

    public void Dispose()
    {
        _override.Dispose();
        TempPath.Remove(_roaming);
        TempPath.Remove(_local);
    }

    [Fact]
    public void IsPending_WithNoMarker_IsFalse() => Assert.False(LocalDataResetMarker.IsPending);

    [Fact]
    public void Write_ThenIsPending_IsTrue_AndLeavesNoTempFile()
    {
        LocalDataResetMarker.Write();

        Assert.True(LocalDataResetMarker.IsPending);
        Assert.Equal(
            [PiaPaths.LocalDataResetMarkerPath],
            Directory.GetFiles(PiaPaths.RoamingDataDirectory, "pending-local-data-reset*"));
    }

    [Fact]
    public void Write_Twice_IsHarmless()
    {
        LocalDataResetMarker.Write();
        LocalDataResetMarker.Write();

        Assert.True(LocalDataResetMarker.IsPending);
    }

    [Fact]
    public void Clear_RemovesTheMarker_AndIsSafeWhenAbsent()
    {
        LocalDataResetMarker.Clear();
        LocalDataResetMarker.Write();
        LocalDataResetMarker.Clear();

        Assert.False(LocalDataResetMarker.IsPending);
    }
}
