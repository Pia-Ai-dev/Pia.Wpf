using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Assets;
using Pia.Services.Interfaces;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>The SDK probes MXC_FFI_DIR before anything else, so an inherited value would load unverified native
/// code; Pia owns the variable and sets it only to a directory it has verified in this process.</summary>
[Collection("MxcRuntimeEnvironment")]
public sealed class MxcRuntimeEnvironmentTests : IDisposable
{
    private const string Variable = "MXC_FFI_DIR";

    private readonly string? _original = Environment.GetEnvironmentVariable(Variable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-mxc-env-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, _original);
        TempPath.Remove(_root);
    }

    private string RuntimeDirectory => Path.Combine(_root, "Mxc", "9.9.9");

    private MxcRuntimeProvisioner Provisioner(Func<string, string?> publisherOf) =>
        new(Substitute.For<IAssetDownloader>(), NullLogger<MxcRuntimeProvisioner>.Instance, RuntimeDirectory,
            Path.Combine(_root, "Mxc", "x.nupkg"), new RuntimeAsset("runtimes/x.nupkg", "https://example.invalid/x"),
            new string('0', 64), publisherOf);

    private void InstallFile(string content = "dll")
    {
        Directory.CreateDirectory(RuntimeDirectory);
        File.WriteAllText(Path.Combine(RuntimeDirectory, "mxc_ffi.dll"), content);
    }

    [Fact]
    public void ConstructingTheProvisioner_RemovesAnInheritedValue()
    {
        Environment.SetEnvironmentVariable(Variable, @"C:\evil");

        _ = Provisioner(_ => "Microsoft Corporation");

        Assert.Null(Environment.GetEnvironmentVariable(Variable));
    }

    [Fact]
    public void AVerifiedRuntime_PointsTheProcessAtItsDirectory()
    {
        InstallFile();
        var provisioner = Provisioner(_ => "Microsoft Corporation");

        var loaded = provisioner.TryLoad(out var reason);

        Assert.True(loaded, reason);
        Assert.Equal(RuntimeDirectory, Environment.GetEnvironmentVariable(Variable));
    }

    [Fact]
    public void ARuntimeThatIsNotInstalled_LeavesTheVariableUnset_AndSaysWhy()
    {
        var provisioner = Provisioner(_ => "Microsoft Corporation");
        Environment.SetEnvironmentVariable(Variable, @"C:\evil");

        var loaded = provisioner.TryLoad(out var reason);

        Assert.False(loaded);
        Assert.Contains("not installed", reason);
        Assert.Null(Environment.GetEnvironmentVariable(Variable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Evil Corp")]
    public void ARuntimeThatFailsItsSignatureCheck_IsNeverLoaded(string? publisher)
    {
        InstallFile();
        var provisioner = Provisioner(_ => publisher);
        Environment.SetEnvironmentVariable(Variable, @"C:\evil");

        var loaded = provisioner.TryLoad(out var reason);

        Assert.False(loaded);
        Assert.Contains("signature", reason);
        Assert.Null(Environment.GetEnvironmentVariable(Variable));
    }

    [Fact]
    public void TheSignatureIsCheckedOncePerProcess_NotOnEveryLoad()
    {
        InstallFile();
        var checks = 0;
        var provisioner = Provisioner(_ => { checks++; return "Microsoft Corporation"; });

        provisioner.TryLoad(out _);
        provisioner.TryLoad(out _);
        provisioner.TryLoad(out _);

        Assert.Equal(1, checks);
    }

    [Fact]
    public void ANewProvisioner_ChecksTheSignatureAgain()
    {
        InstallFile();
        var first = Provisioner(_ => "Microsoft Corporation");
        Assert.True(first.TryLoad(out _));

        var secondProcess = Provisioner(_ => null);

        Assert.False(secondProcess.TryLoad(out _));
        Assert.Null(Environment.GetEnvironmentVariable(Variable));
    }
}
