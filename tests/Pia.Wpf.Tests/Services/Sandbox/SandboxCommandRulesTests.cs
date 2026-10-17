using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxCommandRulesTests
{
    [Theory]
    [InlineData("npx")]
    [InlineData("npm")]
    [InlineData("pnpm")]
    [InlineData("pnpx")]
    [InlineData("yarn")]
    [InlineData("uvx")]
    [InlineData("bunx")]
    [InlineData("pipx")]
    [InlineData("NPX")]
    [InlineData("npx.cmd")]
    [InlineData("uvx.exe")]
    [InlineData(@"C:\Users\me\.local\bin\uvx.exe")]
    [InlineData(@"""C:\Program Files\nodejs\npx.cmd""")]
    [InlineData(@"C:\Tools\run.cmd")]
    [InlineData(@"C:\Tools\run.BAT")]
    [InlineData(@"C:\Tools\run.ps1")]
    public void ShimsAndScriptLaunchers_AreRecognised(string command)
    {
        Assert.True(SandboxCommandRules.IsShimCommand(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("node")]
    [InlineData("node.exe")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Tools\fs\fs.exe")]
    [InlineData("python")]
    [InlineData(@"C:\Tools\npx-helper\server.exe")]
    public void RealPrograms_AreNotShims(string? command)
    {
        Assert.False(SandboxCommandRules.IsShimCommand(command));
    }
}
