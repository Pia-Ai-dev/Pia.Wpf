using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The tool list the editor shows comes from the RUNNING server, but the allowlist is stored on its own. A
/// stopped server therefore opens its editor with nothing to tick, and that must not read as "no restriction".
/// </summary>
public class McpServersSettingsViewModelTests
{
    private static readonly Guid ServerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static LocalMcpDefinition Stored(IReadOnlyList<string>? allowed) =>
        new("github", "npx", ["-y", "server"], new Dictionary<string, string>(),
            WorkingDirectory: null, "github", allowed);

    private static (McpServersSettingsViewModel Vm, IPluginService Plugins) Create(
        IReadOnlyList<string>? allowed,
        LocalMcpStatus? status = null)
    {
        var plugins = Substitute.For<IPluginService>();
        plugins.GetLocalMcpPlugins().Returns([new SyncPlugin
        {
            Id = ServerId,
            Kind = "mcp_server",
            Name = "github",
            ConfigJson = """{"source":"local","transport":"stdio","command":"npx"}""",
            UserEnabled = true
        }]);
        plugins.GetLocalMcpDefinition(ServerId).Returns(Stored(allowed));
        plugins.GetLocalMcpStatus(ServerId).Returns(status ?? new LocalMcpStatus(false, [], [], null));

        var vm = new McpServersSettingsViewModel(
            plugins,
            Substitute.For<IDialogService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            NullLogger<SettingsViewModel>.Instance);

        return (vm, plugins);
    }

    private static async Task<LocalMcpDefinition> SavedDefinition(IPluginService plugins)
    {
        var call = plugins.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IPluginService.SaveLocalMcpAsync));
        await Task.CompletedTask;
        return (LocalMcpDefinition)call.GetArguments()[1]!;
    }

    [Fact]
    public async Task EditingAStoppedServer_KeepsItsAllowlist()
    {
        var (vm, plugins) = Create(["create_issue"]);

        vm.EditSelectedCommand.Execute(null);
        Assert.Empty(vm.Tools);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["create_issue"], (await SavedDefinition(plugins)).AllowedTools);
    }

    [Fact]
    public async Task EditingARunningServer_SavesWhateverIsTicked()
    {
        var (vm, plugins) = Create(["create_issue"], new LocalMcpStatus(
            true,
            [new McpProbeTool("create_issue", null, false), new McpProbeTool("delete_issue", null, true)],
            ["github__create_issue"],
            null));

        vm.EditSelectedCommand.Execute(null);
        Assert.Equal([true, false], vm.Tools.Select(t => t.IsAllowed));

        vm.Tools[1].IsAllowed = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["create_issue", "delete_issue"], (await SavedDefinition(plugins)).AllowedTools);
    }

    [Fact]
    public async Task ANewServerThatWasNeverTested_OffersEveryTool()
    {
        var (vm, plugins) = Create(["create_issue"]);

        vm.AddServerCommand.Execute(null);
        vm.EditName = "new one";
        vm.EditCommand = "npx";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null((await SavedDefinition(plugins)).AllowedTools);
    }

    [Fact]
    public async Task TestingAStoppedServer_RestoresTheStoredTicks()
    {
        var (vm, plugins) = Create(["create_issue"]);
        plugins.ProbeLocalMcpAsync(Arg.Any<LocalMcpDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpProbeResult(true,
                [new McpProbeTool("create_issue", null, false), new McpProbeTool("delete_issue", null, true)],
                null));

        vm.EditSelectedCommand.Execute(null);
        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal([true, false], vm.Tools.Select(t => t.IsAllowed));
    }

    [Fact]
    public void ARunningServersRow_SaysSo_AndAFailedOneDoesNot()
    {
        Assert.True(Create(null, new LocalMcpStatus(true, [], ["github__x"], null)).Vm.Servers.Single().IsRunning);
        Assert.False(Create(null, new LocalMcpStatus(false, [], [], "no such file")).Vm.Servers.Single().IsRunning);
    }
}
