using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Paths;
using Pia.Services.Assets;
using Pia.Services.Interfaces;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Updates;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>The pinned package and the real signed binary, checked offline against what NuGet restored.</summary>
[Collection("MxcRuntimeEnvironment")]
public sealed class MxcRuntimeAssetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-mxc-asset-" + Guid.NewGuid().ToString("N"));
    private readonly string? _originalVariable = Environment.GetEnvironmentVariable("MXC_FFI_DIR");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MXC_FFI_DIR", _originalVariable);
        TempPath.Remove(_root);
    }

    private static string? RestoredPackage()
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(PiaPaths.UserProfileRoot, ".nuget", "packages");
        var path = Path.Combine(packages, "microsoft.mxc.sdk", PiaPaths.MxcRuntimeVersion,
            $"microsoft.mxc.sdk.{PiaPaths.MxcRuntimeVersion}.nupkg");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void TheAsset_UsesTheMirrorKeyAndTheNugetOrgUpstream()
    {
        Assert.Equal("runtimes/microsoft.mxc.sdk.1.0.1.nupkg", RuntimeAssetCatalog.MxcRuntime.MirrorKey);
        Assert.Equal(
            "https://api.nuget.org/v3-flatcontainer/microsoft.mxc.sdk/1.0.1/microsoft.mxc.sdk.1.0.1.nupkg",
            RuntimeAssetCatalog.MxcRuntime.UpstreamUrl);
        Assert.Matches("^[A-Za-z0-9._/-]+$", RuntimeAssetCatalog.MxcRuntime.MirrorKey);
    }

    private static readonly string Catalogue = File.ReadAllText(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        "scripts", "RuntimeAssetCatalogue.ps1"));

    [Fact]
    public void TheCatalogue_AppearsInTheMirrorSet()
    {
        Assert.Contains(RuntimeAssetCatalog.MxcRuntime, RuntimeAssetCatalog.All);
    }

    /// <summary>The publishing script checks a staged package against this before uploading it, so a mirror
    /// can never be fed bytes the client would refuse.</summary>
    [Fact]
    public void TheScriptsPin_IsTheOneTheClientChecks()
    {
        var pin = Regex.Match(Catalogue, @"Sha256\s*=\s*'([0-9A-Fa-f]{64})'");

        Assert.True(pin.Success, "the Mxc group has no Sha256");
        Assert.Equal(RuntimeAssetCatalog.MxcRuntimeSha256, pin.Groups[1].Value, ignoreCase: true);
    }

    /// <summary>Pre-fetching on an air-gapped machine only works if the package lands where the provisioner looks.</summary>
    [Fact]
    public void ThePrefetchTarget_IsWhereTheClientLooksForAStagedPackage()
    {
        var group = Catalogue[Catalogue.IndexOf("Mxc = @(", StringComparison.Ordinal)..];
        var target = Regex.Match(group, @"Target\s*=\s*(Join-Path[^
]+)").Groups[1].Value;

        Assert.Contains("'Runtimes'", target);
        Assert.Contains("'Mxc'", target);
        Assert.Contains($"'{Path.GetFileName(PiaPaths.MxcRuntimeArchivePath)}'", target);
    }

    [Fact]
    public void ThePin_IsTheSha256OfThePackageNuGetRestored()
    {
        var package = RestoredPackage();
        Assert.SkipWhen(package is null, "the Microsoft.Mxc.Sdk package is not in the NuGet cache");

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package!)));

        Assert.Equal(RuntimeAssetCatalog.MxcRuntimeSha256, actual, ignoreCase: true);
    }

    [Fact]
    public void TheRealNativeFile_IsSignedByMicrosoft()
    {
        var file = Path.Combine(MxcTestHost.NativeDirectory, "mxc_ffi.dll");
        Assert.SkipUnless(File.Exists(file), "the native copy is not in the test output");

        Assert.Equal("Microsoft Corporation", UpdatePackageVerifier.PublisherOf(file));
    }

    [Fact]
    public async Task TheRealPackage_InstallsEndToEnd_AndLoads()
    {
        var package = RestoredPackage();
        Assert.SkipWhen(package is null, "the Microsoft.Mxc.Sdk package is not in the NuGet cache");
        var downloader = new CopyingDownloader(package!);
        var directory = Path.Combine(_root, "Mxc", PiaPaths.MxcRuntimeVersion);
        var provisioner = new MxcRuntimeProvisioner(
            downloader, NullLogger<MxcRuntimeProvisioner>.Instance, directory,
            Path.Combine(_root, "Mxc", "pkg.nupkg"), RuntimeAssetCatalog.MxcRuntime,
            RuntimeAssetCatalog.MxcRuntimeSha256, UpdatePackageVerifier.PublisherOf);

        var result = await provisioner.InstallAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(["mxc_ffi.dll"], Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
        Assert.True(provisioner.TryLoad(out var reason), reason);
        Assert.Equal(directory, Environment.GetEnvironmentVariable("MXC_FFI_DIR"));
    }

    private sealed class CopyingDownloader(string source) : IAssetDownloader
    {
        public Task<long> DownloadAsync(RuntimeAsset asset, string destinationPath, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(source, destinationPath, overwrite: true);
            return Task.FromResult(new FileInfo(destinationPath).Length);
        }
    }
}
