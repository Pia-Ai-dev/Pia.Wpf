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

public sealed class MxcRuntimeProvisionerTests : IDisposable
{
    private const string NativeEntry = "runtimes/win-x64/native/mxc_ffi.dll";
    private static readonly byte[] GoodDll = Encoding.UTF8.GetBytes("signed-by-microsoft");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-mxc-prov-" + Guid.NewGuid().ToString("N"));
    private readonly RuntimeAsset _asset = new("runtimes/microsoft.mxc.sdk.9.9.9.nupkg", "https://example.invalid/mxc.nupkg");

    public MxcRuntimeProvisionerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TempPath.Remove(_root);

    private string RuntimeDirectory => Path.Combine(_root, "Mxc", "9.9.9");

    private string ArchivePath => Path.Combine(_root, "Mxc", "microsoft.mxc.sdk.9.9.9.nupkg");

    private static byte[] Package(byte[]? dll = null, bool withNative = true)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "lib/net8.0/Microsoft.Mxc.Sdk.dll", "managed"u8.ToArray());
            Add(zip, "runtimes/win-x64/native/plm.exe", "plm"u8.ToArray());
            if (withNative)
                Add(zip, NativeEntry, dll ?? GoodDll);
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        // A fixed timestamp: the pinned hash is taken from one build of the package and checked against another.
        var created = zip.CreateEntry(name);
        created.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var entry = created.Open();
        entry.Write(content);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string? SignedByMicrosoft(string path) =>
        File.ReadAllBytes(path).AsSpan().SequenceEqual(GoodDll) ? "Microsoft Corporation" : null;

    private MxcRuntimeProvisioner Provisioner(FakeDownloader downloader, byte[]? pinned = null, Func<string, string?>? publisherOf = null) =>
        new(downloader, NullLogger<MxcRuntimeProvisioner>.Instance, RuntimeDirectory, ArchivePath, _asset,
            Sha(pinned ?? Package()), publisherOf ?? SignedByMicrosoft);

    private sealed class FakeDownloader(Func<byte[]> content) : IAssetDownloader
    {
        public int Calls;
        public Exception? Fail { get; init; }
        public RuntimeAsset? LastAsset { get; private set; }

        public Task<long> DownloadAsync(RuntimeAsset asset, string destinationPath, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            LastAsset = asset;
            if (Fail is not null) throw Fail;
            var bytes = content();
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, bytes);
            progress?.Report(new ModelDownloadProgress(50, bytes.Length));
            return Task.FromResult((long)bytes.Length);
        }
    }

    private IEnumerable<string> LeftOvers() =>
        Directory.Exists(Path.Combine(_root, "Mxc"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(_root, "Mxc")).Where(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            : [];

    [Fact]
    public async Task AGoodPackage_InstallsOnlyTheNativeFile_AndCleansUp()
    {
        var downloader = new FakeDownloader(() => Package());

        var result = await Provisioner(downloader).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(["mxc_ffi.dll"], Directory.GetFileSystemEntries(RuntimeDirectory).Select(Path.GetFileName));
        Assert.Equal(GoodDll, await File.ReadAllBytesAsync(Path.Combine(RuntimeDirectory, "mxc_ffi.dll"), TestContext.Current.CancellationToken));
        Assert.Empty(LeftOvers());
        Assert.False(File.Exists(ArchivePath));
        Assert.Equal(_asset, downloader.LastAsset);
    }

    [Fact]
    public async Task AWrongChecksum_DeletesTheDownload_AndLeavesNothingBehind()
    {
        var downloader = new FakeDownloader(() => Package(dll: "tampered"u8.ToArray()));

        var result = await Provisioner(downloader, pinned: Package()).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("checksum", result.Reason);
        Assert.False(Directory.Exists(RuntimeDirectory));
        Assert.Empty(LeftOvers());
        Assert.False(File.Exists(ArchivePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Evil Corp")]
    public async Task AFileNotSignedByMicrosoft_IsRefused_AndNothingIsLeftBehind(string? publisher)
    {
        var downloader = new FakeDownloader(() => Package());

        var result = await Provisioner(downloader, publisherOf: _ => publisher).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("not signed by Microsoft", result.Reason);
        Assert.False(Directory.Exists(RuntimeDirectory));
        Assert.Empty(LeftOvers());
    }

    [Fact]
    public async Task AnInterruptedExtract_LeavesOnlyTmp_AndTheNextRunDiscardsIt()
    {
        Directory.CreateDirectory(RuntimeDirectory + ".tmp");
        await File.WriteAllTextAsync(Path.Combine(RuntimeDirectory + ".tmp", "half.bin"), "partial", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(ArchivePath + ".tmp", "partial download", TestContext.Current.CancellationToken);

        var result = await Provisioner(new FakeDownloader(() => Package())).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.Empty(LeftOvers());
        Assert.False(File.Exists(Path.Combine(RuntimeDirectory, "half.bin")));
    }

    [Fact]
    public async Task AnInstalledAndVerifiedRuntime_IsNotDownloadedAgain()
    {
        var downloader = new FakeDownloader(() => Package());
        var provisioner = Provisioner(downloader);
        await provisioner.InstallAsync(ct: TestContext.Current.CancellationToken);

        var again = await provisioner.InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(again.Installed);
        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task AnInstalledRuntimeThatNoLongerVerifies_IsReplaced()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        await File.WriteAllTextAsync(Path.Combine(RuntimeDirectory, "mxc_ffi.dll"), "swapped", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(RuntimeDirectory, "stray.txt"), "x", TestContext.Current.CancellationToken);
        var downloader = new FakeDownloader(() => Package());

        var result = await Provisioner(downloader).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(1, downloader.Calls);
        Assert.Equal(GoodDll, await File.ReadAllBytesAsync(Path.Combine(RuntimeDirectory, "mxc_ffi.dll"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(RuntimeDirectory, "stray.txt")));
    }

    [Fact]
    public async Task ADownloadFailure_IsReportedWithItsReason_AndLeavesNothing()
    {
        var downloader = new FakeDownloader(() => []) { Fail = new HttpRequestException("503 from the mirror") };

        var result = await Provisioner(downloader).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("could not be downloaded", result.Reason);
        Assert.Contains("503 from the mirror", result.Reason);
        Assert.Empty(LeftOvers());
        Assert.False(Directory.Exists(RuntimeDirectory));
    }

    [Fact]
    public async Task APackageWithoutTheNativeFile_Fails()
    {
        var bytes = Package(withNative: false);

        var result = await Provisioner(new FakeDownloader(() => bytes), pinned: bytes).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.Contains("does not contain", result.Reason);
        Assert.False(Directory.Exists(RuntimeDirectory));
        Assert.Empty(LeftOvers());
    }

    [Fact]
    public async Task APrestagedArchive_IsUsedWithoutDownloading_AndKept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
        await File.WriteAllBytesAsync(ArchivePath, Package(), TestContext.Current.CancellationToken);
        var downloader = new FakeDownloader(() => Package());

        var result = await Provisioner(downloader).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(0, downloader.Calls);
        Assert.True(File.Exists(ArchivePath));
    }

    /// <summary>An air-gapped machine has nowhere else to get the package from.</summary>
    [Fact]
    public async Task APrestagedArchive_ReinstallsTheRuntime_WhenTheInstalledFileLaterFailsVerification()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
        await File.WriteAllBytesAsync(ArchivePath, Package(), TestContext.Current.CancellationToken);
        var downloader = new FakeDownloader(() => Package());
        var provisioner = Provisioner(downloader);
        await provisioner.InstallAsync(ct: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(RuntimeDirectory, "mxc_ffi.dll"), "corrupted", TestContext.Current.CancellationToken);

        var again = await provisioner.InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(again.Installed, again.Reason);
        Assert.Equal(0, downloader.Calls);
        Assert.Equal(GoodDll, await File.ReadAllBytesAsync(Path.Combine(RuntimeDirectory, "mxc_ffi.dll"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APrestagedArchiveThatDoesNotMatch_IsDiscarded_AndTheRealOneDownloaded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
        await File.WriteAllTextAsync(ArchivePath, "a truncated copy", TestContext.Current.CancellationToken);
        var downloader = new FakeDownloader(() => Package());

        var result = await Provisioner(downloader).InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task Cancelling_Propagates_AndLeavesNothing()
    {
        var downloader = new FakeDownloader(() => []) { Fail = new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Provisioner(downloader).InstallAsync(ct: TestContext.Current.CancellationToken));

        Assert.Empty(LeftOvers());
    }

    [Fact]
    public async Task Progress_ShowsTheDownload_ThenExtracting_ThenCompleted()
    {
        var seen = new InlineProgress();

        await Provisioner(new FakeDownloader(() => Package()))
            .InstallAsync(seen, TestContext.Current.CancellationToken);

        var phases = seen.Reports.Select(p => p.Phase).Distinct().ToList();
        Assert.Equal([ModelDownloadPhase.Downloading, ModelDownloadPhase.Extracting, ModelDownloadPhase.Completed], phases);
    }

    private sealed class InlineProgress : IProgress<ModelDownloadProgress>
    {
        public List<ModelDownloadProgress> Reports { get; } = [];

        public void Report(ModelDownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task ConcurrentInstalls_DownloadOnce()
    {
        var downloader = new FakeDownloader(() => Package());
        var provisioner = Provisioner(downloader);

        var results = await Task.WhenAll(
            provisioner.InstallAsync(ct: TestContext.Current.CancellationToken),
            provisioner.InstallAsync(ct: TestContext.Current.CancellationToken));

        Assert.All(results, r => Assert.True(r.Installed, r.Reason));
        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task IsInstalled_IsTrueOnlyOnceTheNativeFileIsThere()
    {
        var provisioner = Provisioner(new FakeDownloader(() => Package()));
        Assert.False(provisioner.IsInstalled);

        await provisioner.InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(provisioner.IsInstalled);
    }
}
