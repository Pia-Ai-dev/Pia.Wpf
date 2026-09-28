using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>A plugin row shows the switch the plugin service acts on, so a server that waits for the user reads as off.</summary>
public class PluginItemViewModelTests
{
    private const string DistributedServer = """{"transport":"stdio","command":"node"}""";

    [Fact]
    public void ADistributedServerWithoutAnAdminDefault_ShowsOff()
    {
        var row = Row(new SyncPlugin { Kind = "mcp_server", Name = "Echo", ConfigJson = DistributedServer });

        Assert.False(row.IsEnabled);
        Assert.Equal("Inactive", row.StatusText);
    }

    [Fact]
    public void ADistributedServerTheAdminSwitchedOn_ShowsOn()
    {
        var row = Row(new SyncPlugin
        {
            Kind = "mcp_server",
            Name = "Echo",
            ConfigJson = """{"transport":"stdio","command":"node","defaultEnabled":true}""",
        });

        Assert.True(row.IsEnabled);
        Assert.Equal("Active", row.StatusText);
    }

    [Fact]
    public void TheUsersChoice_WinsOverTheWait()
    {
        var row = Row(new SyncPlugin { Kind = "mcp_server", Name = "Echo", ConfigJson = DistributedServer, UserEnabled = true });

        Assert.True(row.IsEnabled);
    }

    [Fact]
    public void AServerTheAdminDeactivated_ShowsOff()
    {
        var row = Row(new SyncPlugin
        {
            Kind = "mcp_server",
            Name = "Echo",
            ConfigJson = """{"transport":"stdio","command":"node","defaultEnabled":true}""",
            IsActive = false,
        });

        Assert.False(row.IsEnabled);
        Assert.Equal("Inactive", row.StatusText);
    }

    [Theory]
    [InlineData("builtin_tool_pack", true, """{"handlerId":"memory"}""")]
    [InlineData("mcp_server", false, """{"source":"local","transport":"stdio","command":"node"}""")]
    public void ABuiltInOrALocalServer_StaysOnByDefault(string kind, bool preloaded, string configJson)
    {
        var row = Row(new SyncPlugin { Kind = kind, Name = "Pack", IsPreloaded = preloaded, ConfigJson = configJson });

        Assert.True(row.IsEnabled);
        Assert.Equal("Active", row.StatusText);
    }

    private static PluginItemViewModel Row(SyncPlugin plugin)
    {
        var row = new PluginItemViewModel(Substitute.For<IPluginIconLoader>());
        row.Initialize(plugin, serverUrl: null);
        return row;
    }
}
