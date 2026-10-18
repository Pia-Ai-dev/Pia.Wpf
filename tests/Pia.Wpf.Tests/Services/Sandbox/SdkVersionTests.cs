using System.IO;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SdkVersionTests
{
    private static readonly string Root = Path.Combine(@"C:\", "Program Files", "dotnet");

    [Theory]
    [InlineData("10.0.402", 10, 0, 402, null)]
    [InlineData("11.0.100-rc.2", 11, 0, 100, "rc.2")]
    [InlineData("10.0.0-rc.2.25502.107", 10, 0, 0, "rc.2.25502.107")]
    [InlineData("0.0.0", 0, 0, 0, null)]
    public void TryParse_ReadsMajorMinorPatchAndPrerelease(string text, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(SdkVersion.TryParse(text, out var parsedMajor, out var parsedMinor, out var parsedPatch, out var parsedPrerelease));
        Assert.Equal((major, minor, patch, prerelease), (parsedMajor, parsedMinor, parsedPatch, parsedPrerelease));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0")]
    [InlineData("10.0.0.0")]
    [InlineData("v10.0.0")]
    [InlineData("10.0.0-")]
    [InlineData("10.0.0+build")]
    [InlineData("10.0.0\n")]
    [InlineData("\u0661\u0660.0.0")]
    [InlineData("99999999999.0.0")]
    [InlineData("NuGetFallbackFolder")]
    public void TryParse_RefusesAnythingElse(string? text)
    {
        Assert.False(SdkVersion.TryParse(text, out _, out _, out _, out _));
        Assert.Null(SdkVersion.MajorOf(text));
    }

    [Theory]
    [InlineData("10.0.402", "10.0.100")]
    [InlineData("10.0.10", "10.0.9")]
    [InlineData("10.0.0", "9.0.0")]
    [InlineData("10.1.0", "10.0.999")]
    [InlineData("11.0.100-rc.2", "10.0.402")]
    [InlineData("10.0.100", "10.0.100-rc.2")]
    [InlineData("10.0.100-rc.10", "10.0.100-rc.2")]
    [InlineData("10.0.100-rc.1", "10.0.100-preview.7")]
    [InlineData("10.0.100-rc.1.25", "10.0.100-rc.1")]
    [InlineData("10.0.100-alpha", "10.0.100-1")]
    [InlineData("1.0.0", "garbage")]
    [InlineData("1.0.0", null)]
    public void Compare_OrdersTheFirstAboveTheSecond(string higher, string? lower)
    {
        Assert.True(SdkVersion.Compare(higher, lower) > 0);
        Assert.True(SdkVersion.Compare(lower, higher) < 0);
    }

    [Theory]
    [InlineData("10.0.402", "10.0.402")]
    [InlineData("10.0.100-rc.2", "10.0.100-rc.2")]
    [InlineData("10.0.100-rc.02", "10.0.100-rc.2")]
    public void Compare_TreatsEqualVersionsAsEqual(string left, string right)
    {
        Assert.Equal(0, SdkVersion.Compare(left, right));
    }

    [Fact]
    public void MajorOf_ReadsTheMajor()
    {
        Assert.Equal(11, SdkVersion.MajorOf("11.0.100-rc.2"));
    }

    [Fact]
    public void HighestUsableSdk_PicksTheHighestFolderHoldingDotnetDll()
    {
        var host = new FakeRuntimeHost()
            .WithFile(Path.Combine(Root, "sdk", "9.0.300", "dotnet.dll"))
            .WithFile(Path.Combine(Root, "sdk", "10.0.402", "dotnet.dll"))
            .WithFile(Path.Combine(Root, "sdk", "10.0.100-rc.2", "dotnet.dll"))
            .WithDirectory(Path.Combine(Root, "sdk", "11.0.100"))
            .WithDirectory(Path.Combine(Root, "sdk", "NuGetFallbackFolder"));

        Assert.Equal(Path.Combine(Root, "sdk", "10.0.402"), SdkVersion.HighestUsableSdk(host, Root));
    }

    [Fact]
    public void HighestUsableSdk_TakesAPrereleaseOfAHigherMajor()
    {
        var host = new FakeRuntimeHost()
            .WithFile(Path.Combine(Root, "sdk", "10.0.402", "dotnet.dll"))
            .WithFile(Path.Combine(Root, "sdk", "11.0.100-rc.2", "dotnet.dll"));

        Assert.Equal(Path.Combine(Root, "sdk", "11.0.100-rc.2"), SdkVersion.HighestUsableSdk(host, Root));
    }

    [Fact]
    public void HighestUsableSdk_IsNullWithoutAnUsableSdk()
    {
        var withoutSdkFolder = new FakeRuntimeHost().WithFile(Path.Combine(Root, "dotnet.exe"));
        var withoutDll = new FakeRuntimeHost().WithDirectory(Path.Combine(Root, "sdk", "10.0.402"));

        Assert.Null(SdkVersion.HighestUsableSdk(withoutSdkFolder, Root));
        Assert.Null(SdkVersion.HighestUsableSdk(withoutDll, Root));
    }
}
