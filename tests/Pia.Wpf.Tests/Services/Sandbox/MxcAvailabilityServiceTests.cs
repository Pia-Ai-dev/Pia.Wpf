using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Assets;
using Pia.Services.Interfaces;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>The SDK throws on every call until the native runtime is installed and verified, so the platform check
/// must wait for that, and the state has to say so.</summary>
[Collection("MxcRuntimeEnvironment")]
public sealed class MxcAvailabilityServiceTests : IDisposable
{
    private static readonly byte[] GoodDll = Encoding.UTF8.GetBytes("signed-by-microsoft");

    private readonly string? _originalVariable = Environment.GetEnvironmentVariable("MXC_FFI_DIR");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-mxc-avail-" + Guid.NewGuid().ToString("N"));
    private readonly RuntimeAsset _asset = new("runtimes/x.nupkg", "https://example.invalid/x");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MXC_FFI_DIR", _originalVariable);
        TempPath.Remove(_root);
    }

    private string RuntimeDirectory => Path.Combine(_root, "Mxc", "9.9.9");

    private static byte[] Package()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var created = zip.CreateEntry("runtimes/win-x64/native/mxc_ffi.dll");
            created.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var entry = created.Open();
            entry.Write(GoodDll);
        }
        return stream.ToArray();
    }

    private sealed class SlowDownloader : IAssetDownloader
    {
        public int Calls;
        public Exception? Fail { get; init; }
        public TaskCompletionSource? Gate { get; init; }

        public async Task<long> DownloadAsync(RuntimeAsset asset, string destinationPath, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is not null) await Gate.Task;
            if (Fail is not null) throw Fail;
            var bytes = Package();
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
            return bytes.Length;
        }
    }

    private (MxcAvailabilityService Service, SlowDownloader Downloader, Counter Platform) Create(
        SlowDownloader? downloader = null, bool supported = true, Func<string, string?>? publisherOf = null)
    {
        downloader ??= new SlowDownloader();
        var provisioner = new MxcRuntimeProvisioner(
            downloader, NullLogger<MxcRuntimeProvisioner>.Instance, RuntimeDirectory,
            Path.Combine(_root, "Mxc", "x.nupkg"), _asset, Convert.ToHexString(SHA256.HashData(Package())),
            publisherOf ?? (path => File.ReadAllBytes(path).AsSpan().SequenceEqual(GoodDll) ? "Microsoft Corporation" : null));
        var platform = new Counter();
        var service = new MxcAvailabilityService(provisioner, NullLogger<MxcAvailabilityService>.Instance, () =>
        {
            platform.Calls++;
            return (supported, supported ? null : "This edition of Windows cannot create sandboxes.");
        });
        return (service, downloader, platform);
    }

    private sealed class Counter
    {
        public int Calls;
    }

    private void PreInstall(string content = "signed-by-microsoft")
    {
        Directory.CreateDirectory(RuntimeDirectory);
        File.WriteAllText(Path.Combine(RuntimeDirectory, "mxc_ffi.dll"), content);
    }

    [Fact]
    public void WithoutTheRuntime_TheStateIsNotInstalled_AndTheSdkIsNeverTouched()
    {
        var (service, _, platform) = Create();

        Assert.Equal(MxcRuntimeState.NotInstalled, service.Status.State);
        Assert.False(service.Status.IsReady);
        Assert.Equal(0, platform.Calls);
    }

    [Fact]
    public void AnInstalledRuntimeOnASupportedHost_IsReady()
    {
        PreInstall();
        var (service, _, platform) = Create();

        Assert.Equal(MxcRuntimeState.Ready, service.Status.State);
        Assert.Equal(1, platform.Calls);
        Assert.Equal(RuntimeDirectory, Environment.GetEnvironmentVariable("MXC_FFI_DIR"));
    }

    [Fact]
    public void AnInstalledRuntimeOnAnUnsupportedHost_IsUnsupported_WithTheReason()
    {
        PreInstall();
        var (service, _, _) = Create(supported: false);

        Assert.Equal(MxcRuntimeState.Unsupported, service.Status.State);
        Assert.Equal("This edition of Windows cannot create sandboxes.", service.Status.Reason);
    }

    [Fact]
    public void AnInstalledRuntimeThatFailsItsSignatureCheck_IsNotInstalled_AndTheSdkIsNeverTouched()
    {
        PreInstall("swapped");
        var (service, _, platform) = Create();

        Assert.Equal(MxcRuntimeState.NotInstalled, service.Status.State);
        Assert.Contains("signature", service.Status.Reason);
        Assert.Equal(0, platform.Calls);
    }

    [Fact]
    public async Task Installing_ReportsDownloading_ThenReady()
    {
        var (service, downloader, _) = Create();
        var states = new List<MxcRuntimeState>();
        service.StatusChanged += (_, status) => states.Add(status.State);

        var result = await service.InstallRuntimeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MxcRuntimeState.Ready, result.State);
        Assert.Equal([MxcRuntimeState.Downloading, MxcRuntimeState.Ready], states);
        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task Installing_OnAnUnsupportedHost_EndsUnsupported()
    {
        var (service, _, _) = Create(supported: false);

        var result = await service.InstallRuntimeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MxcRuntimeState.Unsupported, result.State);
        Assert.Contains("cannot create sandboxes", result.Reason);
    }

    [Fact]
    public async Task AFailedInstall_LeavesNotInstalled_WithTheReason()
    {
        var (service, _, _) = Create(new SlowDownloader { Fail = new HttpRequestException("offline") });
        var states = new List<MxcRuntimeState>();
        service.StatusChanged += (_, status) => states.Add(status.State);

        var result = await service.InstallRuntimeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MxcRuntimeState.NotInstalled, result.State);
        Assert.Contains("offline", result.Reason);
        Assert.Equal([MxcRuntimeState.Downloading, MxcRuntimeState.NotInstalled], states);
        Assert.Equal(result, service.Status);
    }

    [Fact]
    public async Task Installing_ReplacesARuntimeThatFailedVerification()
    {
        PreInstall("swapped");
        var (service, downloader, _) = Create();

        var result = await service.InstallRuntimeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MxcRuntimeState.Ready, result.State);
        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task InstallingWhenAlreadyReady_DoesNothing()
    {
        PreInstall();
        var (service, downloader, _) = Create();
        _ = service.Status;

        var result = await service.InstallRuntimeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MxcRuntimeState.Ready, result.State);
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task ConcurrentInstalls_ShareOneDownload_AndTheStatusSaysDownloadingMeanwhile()
    {
        var gate = new TaskCompletionSource();
        var (service, downloader, _) = Create(new SlowDownloader { Gate = gate });

        var first = service.InstallRuntimeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var second = service.InstallRuntimeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(MxcRuntimeState.Downloading, service.Status.State);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(MxcRuntimeState.Ready, r.State));
        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task Refreshing_PicksUpAChangeOnDisk_AndSaysSo()
    {
        PreInstall();
        var (service, _, _) = Create();
        Assert.Equal(MxcRuntimeState.Ready, service.Status.State);
        var changes = new List<MxcRuntimeState>();
        service.StatusChanged += (_, status) => changes.Add(status.State);

        File.Delete(Path.Combine(RuntimeDirectory, "mxc_ffi.dll"));
        var refreshed = await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MxcRuntimeState.NotInstalled, refreshed.State);
        Assert.Equal([MxcRuntimeState.NotInstalled], changes);
    }

    [Fact]
    public async Task Refreshing_WithNothingChanged_RaisesNothing()
    {
        var (service, _, _) = Create();
        _ = service.Status;
        var raised = 0;
        service.StatusChanged += (_, _) => raised++;

        await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, raised);
    }
}
