namespace Pia.Tests.Services.E2EE;

using System.IO;
using Pia.Services.E2EE;
using Pia.Tests.TestInfrastructure;
using Xunit;

public sealed class ConfirmedApproverStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"pia-approvers-{Guid.NewGuid():N}", "approvers.json");

    public void Dispose() => TempPath.Remove(Path.GetDirectoryName(_file));

    [Fact]
    public async Task ARecordedApprover_IsConfirmedByALaterInstance()
    {
        await new ConfirmedApproverStore(_file).RecordAsync("dev-a", "AAAA-BBBB");

        var reloaded = new ConfirmedApproverStore(_file);

        Assert.True(reloaded.IsConfirmed("dev-a", "AAAA-BBBB"));
        Assert.True(reloaded.IsConfirmed("dev-a", "aaaa-bbbb"));
    }

    [Fact]
    public async Task AnotherFingerprintOrDevice_IsNotConfirmed()
    {
        var store = new ConfirmedApproverStore(_file);
        await store.RecordAsync("dev-a", "AAAA-BBBB");

        Assert.False(store.IsConfirmed("dev-a", "CCCC-DDDD"));
        Assert.False(store.IsConfirmed("dev-b", "AAAA-BBBB"));
    }

    [Fact]
    public async Task RecordingADeviceAgain_ReplacesItsFingerprint()
    {
        var store = new ConfirmedApproverStore(_file);
        await store.RecordAsync("dev-a", "AAAA-BBBB");
        await store.RecordAsync("dev-a", "CCCC-DDDD");

        var reloaded = new ConfirmedApproverStore(_file);

        Assert.False(reloaded.IsConfirmed("dev-a", "AAAA-BBBB"));
        Assert.True(reloaded.IsConfirmed("dev-a", "CCCC-DDDD"));
    }

    [Fact]
    public void AMissingOrUnreadableFile_ConfirmsNothing()
    {
        Assert.False(new ConfirmedApproverStore(_file).IsConfirmed("dev-a", "AAAA-BBBB"));

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ not json");

        Assert.False(new ConfirmedApproverStore(_file).IsConfirmed("dev-a", "AAAA-BBBB"));
    }
}
