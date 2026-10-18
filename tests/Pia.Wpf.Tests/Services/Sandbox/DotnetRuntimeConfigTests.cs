using System.IO;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class DotnetRuntimeConfigTests
{
    private static readonly string Dll = Path.Combine(@"D:\", "app", "my.server.dll");
    private static readonly string ConfigPath = Path.Combine(@"D:\", "app", "my.server.runtimeconfig.json");

    private static IReadOnlyList<DotnetRuntimeConfig.Framework>? Read(string? text)
    {
        var host = new FakeRuntimeHost();
        if (text is not null)
            host.WithFile(ConfigPath, text);
        return DotnetRuntimeConfig.Read(host, Dll);
    }

    [Fact]
    public void ASingleFramework_IsRead()
    {
        var frameworks = Read("""
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
              }
            }
            """);

        Assert.Equal([new DotnetRuntimeConfig.Framework("Microsoft.NETCore.App", "10.0.0")], frameworks);
    }

    [Fact]
    public void AFrameworkList_IsRead()
    {
        var frameworks = Read("""
            {"runtimeOptions": {"frameworks": [
              {"name": "Microsoft.NETCore.App", "version": "10.0.0"},
              {"name": "Microsoft.AspNetCore.App", "version": "10.0.1"}
            ]}}
            """);

        Assert.Equal(
            [
                new DotnetRuntimeConfig.Framework("Microsoft.NETCore.App", "10.0.0"),
                new DotnetRuntimeConfig.Framework("Microsoft.AspNetCore.App", "10.0.1")
            ],
            frameworks);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAccepted()
    {
        var frameworks = Read("""
            {
              // written by hand
              "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0", }, },
            }
            """);

        Assert.Equal([new DotnetRuntimeConfig.Framework("Microsoft.NETCore.App", "8.0.0")], frameworks);
    }

    [Fact]
    public void AMissingFile_IsNull()
    {
        Assert.Null(Read(null));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"runtimeOptions\": ")]
    public void AnUnreadableFile_IsNull(string text)
    {
        Assert.Null(Read(text));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"runtimeOptions\": {\"tfm\": \"net10.0\"}}")]
    [InlineData("{\"runtimeOptions\": \"x\"}")]
    public void AFileWithoutFrameworks_IsEmpty(string text)
    {
        var frameworks = Read(text);

        Assert.NotNull(frameworks);
        Assert.Empty(frameworks);
    }

    [Fact]
    public void EntriesWithoutANameOrVersion_AreSkipped()
    {
        var frameworks = Read("""
            {"runtimeOptions": {"frameworks": [
              {"name": "Microsoft.NETCore.App"},
              {"version": "10.0.0"},
              {"name": "", "version": "10.0.0"},
              {"name": "Microsoft.AspNetCore.App", "version": 10},
              "Microsoft.NETCore.App",
              {"name": "Microsoft.WindowsDesktop.App", "version": "10.0.0"}
            ]}}
            """);

        Assert.Equal([new DotnetRuntimeConfig.Framework("Microsoft.WindowsDesktop.App", "10.0.0")], frameworks);
    }
}
