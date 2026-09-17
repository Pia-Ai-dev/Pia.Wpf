using System.Collections.Specialized;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Pia.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The tool list the editor shows comes from the RUNNING server, but the allowlist is stored on its own. A
/// stopped server therefore opens its editor with nothing to tick, and that must not read as "no restriction".
/// </summary>
public class McpServersSettingsViewModelTests
{
    private static readonly Guid ServerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed record Fixture(
        McpServersSettingsViewModel Vm,
        IPluginService Plugins,
        global::Wpf.Ui.ISnackbarService Snackbar);

    private static LocalMcpDefinition Stored(IReadOnlyList<string>? allowed) =>
        new("github", "npx", ["-y", "server"], new Dictionary<string, string>(),
            WorkingDirectory: null, "github", allowed);

    private static SyncPlugin Row(Guid id, string name) => new()
    {
        Id = id,
        Kind = "mcp_server",
        Name = name,
        ConfigJson = """{"source":"local","transport":"stdio","command":"npx"}""",
        UserEnabled = true
    };

    private static Fixture Create(
        IReadOnlyList<string>? allowed,
        LocalMcpStatus? status = null,
        LocalMcpDefinition? definition = null)
    {
        var plugins = Substitute.For<IPluginService>();
        plugins.GetLocalMcpPlugins().Returns([Row(ServerId, "github")]);
        plugins.GetLocalMcpDefinition(ServerId).Returns(definition ?? Stored(allowed));
        plugins.GetLocalMcpStatus(ServerId).Returns(status ?? new LocalMcpStatus(false, [], [], null));

        return Build(plugins);
    }

    private static Fixture CreateTwo()
    {
        var plugins = Substitute.For<IPluginService>();
        plugins.GetLocalMcpPlugins().Returns([Row(ServerId, "github"), Row(OtherId, "memory")]);
        plugins.GetLocalMcpDefinition(Arg.Any<Guid>()).Returns(Stored(null));
        plugins.GetLocalMcpStatus(Arg.Any<Guid>()).Returns(new LocalMcpStatus(true, [], [], null));

        return Build(plugins);
    }

    private static Fixture Build(IPluginService plugins)
    {
        // Echoing localizer: the assertions below are about WHICH string a state picks, not its wording.
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(call => (string)call[0]);
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => $"{call[0]}:{string.Join(",", (object[])call[1])}");

        var snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();

        var vm = new McpServersSettingsViewModel(
            plugins,
            Substitute.For<IDialogService>(),
            localization,
            snackbar,
            NullLogger<SettingsViewModel>.Instance);

        return new Fixture(vm, plugins, snackbar);
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
        var (vm, plugins, _) = Create(["create_issue"]);

        vm.EditSelectedCommand.Execute(null);
        Assert.Empty(vm.Tools);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["create_issue"], (await SavedDefinition(plugins)).AllowedTools);
    }

    [Fact]
    public async Task EditingARunningServer_SavesWhateverIsTicked()
    {
        var (vm, plugins, _) = Create(["create_issue"], new LocalMcpStatus(
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
        var (vm, plugins, _) = Create(["create_issue"]);

        vm.AddServerCommand.Execute(null);
        vm.EditName = "new one";
        vm.EditCommand = "npx";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null((await SavedDefinition(plugins)).AllowedTools);
    }

    [Fact]
    public async Task TestingAStoppedServer_RestoresTheStoredTicks()
    {
        var (vm, plugins, _) = Create(["create_issue"]);
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

    [Fact]
    public void TheDetailPane_ListsARunningServersTools_AndMarksTheWithheldOnes()
    {
        var (vm, _, _) = Create(["create_issue"], new LocalMcpStatus(
            true,
            [new McpProbeTool("create_issue", "Opens an issue", false), new McpProbeTool("delete_issue", null, true)],
            ["github__create_issue"],
            null));

        Assert.Equal(["create_issue", "delete_issue"], vm.SelectedTools.Select(t => t.Name));
        Assert.Equal([false, true], vm.SelectedTools.Select(t => t.IsWithheld));
        Assert.True(vm.HasSelectedTools);
        Assert.Equal("McpServers_Detail_ToolsSummary:1,2", vm.SelectedToolsSummary);
        Assert.Null(vm.SelectedToolsHint);
    }

    [Fact]
    public void TheDetailPane_FallsBackToTheSavedAllowlist_WhenTheServerIsStopped()
    {
        var (vm, _, _) = Create(["create_issue"]);

        Assert.Equal(["create_issue"], vm.SelectedTools.Select(t => t.Name));
        Assert.Equal("McpServers_Detail_ToolsStopped", vm.SelectedToolsHint);
    }

    [Fact]
    public void TheDetailPane_SaysWhatToDo_WhenNoToolListIsKnownAtAll()
    {
        var (vm, _, _) = Create(null);

        Assert.Empty(vm.SelectedTools);
        Assert.False(vm.HasSelectedTools);
        Assert.Equal("McpServers_Detail_ToolsUnknown", vm.SelectedToolsHint);
    }

    [Fact]
    public void TheDetailPane_NamesEnvironmentVariablesWithoutTheirValues()
    {
        var (vm, _, _) = Create(null, definition: new LocalMcpDefinition(
            "github", "npx", ["-y", "server"],
            new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_secret", ["LOG_LEVEL"] = "debug" },
            WorkingDirectory: @"C:\work", "github", null));

        Assert.Equal("GITHUB_TOKEN, LOG_LEVEL", vm.SelectedEnvironmentKeys);
        Assert.DoesNotContain("ghp_secret", vm.SelectedEnvironmentKeys);
        Assert.Equal(@"C:\work", vm.SelectedWorkingDirectory);
    }

    [Fact]
    public void AFailedServer_PutsItsReasonInTheDetailPane()
    {
        var (vm, _, _) = Create(null, new LocalMcpStatus(false, [], [], "no such file"));

        Assert.True(vm.SelectedServer!.IsFailed);
        Assert.Equal("no such file", vm.SelectedError);
    }

    [Fact]
    public async Task TestingFromTheDetailPane_ListsTheProbedTools_UnderTheSavedAllowlist()
    {
        var (vm, plugins, _) = Create(["create_issue"]);
        plugins.ProbeLocalMcpAsync(Arg.Any<LocalMcpDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpProbeResult(true,
                [new McpProbeTool("create_issue", null, false), new McpProbeTool("delete_issue", null, true)],
                null));

        await vm.TestSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["create_issue", "delete_issue"], vm.SelectedTools.Select(t => t.Name));
        Assert.Equal([false, true], vm.SelectedTools.Select(t => t.IsWithheld));
        Assert.Equal("McpServers_TestSucceeded:2", vm.DetailMessage);
        Assert.False(vm.DetailMessageIsError);
        Assert.False(vm.IsTestingSelected);
    }

    [Fact]
    public async Task AFailedDetailProbe_SaysWhy_AndLeavesTheToolListAlone()
    {
        var (vm, plugins, _) = Create(["create_issue"]);
        plugins.ProbeLocalMcpAsync(Arg.Any<LocalMcpDefinition>(), Arg.Any<CancellationToken>())
            .Returns(McpProbeResult.Failed("no such file"));

        await vm.TestSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["create_issue"], vm.SelectedTools.Select(t => t.Name));
        Assert.Equal("McpServers_TestFailed:no such file", vm.DetailMessage);
        Assert.True(vm.DetailMessageIsError);
    }

    [Fact]
    public void SelectingAnotherServer_DropsTheLastProbeMessage()
    {
        var (vm, plugins, _) = CreateTwo();
        vm.DetailMessage = "stale";

        vm.SelectedServer = vm.Servers.Single(s => s.Id == OtherId);

        Assert.Null(vm.DetailMessage);
    }

    [Fact]
    public void AServerThatComesUpAfterTheViewIsBuilt_StopsSayingNotRunning()
    {
        // Startup activates servers one at a time and Settings can be built in the middle of that, so the
        // rows have to follow PluginsChanged rather than trust what was true when they were created.
        var plugins = Substitute.For<IPluginService>();
        plugins.GetLocalMcpPlugins().Returns([Row(ServerId, "github")]);
        plugins.GetLocalMcpDefinition(ServerId).Returns(Stored(null));
        plugins.GetLocalMcpStatus(ServerId).Returns(new LocalMcpStatus(false, [], [], null));

        var (vm, _, _) = Build(plugins);
        Assert.True(vm.Servers.Single().IsFailed);

        plugins.GetLocalMcpStatus(ServerId).Returns(new LocalMcpStatus(
            true, [new McpProbeTool("create_issue", null, false)], ["github__create_issue"], null));
        plugins.PluginsChanged += Raise.Event<EventHandler>(plugins, EventArgs.Empty);

        Assert.True(vm.Servers.Single().IsRunning);
        Assert.False(vm.Servers.Single().IsFailed);
        Assert.Equal(["create_issue"], vm.SelectedTools.Select(t => t.Name));
    }

    [Fact]
    public void AReloadWhileTheEditorIsOpen_DoesNotDiscardWhatIsTyped()
    {
        var (vm, plugins, _) = Create(null);

        // What the ListBox does: a collection reset clears its selection and writes null back through the
        // two-way binding. Without reproducing that, this test passes for the wrong reason.
        vm.Servers.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                vm.SelectedServer = null;
        };

        vm.AddServerCommand.Execute(null);
        vm.EditName = "half typed";

        plugins.PluginsChanged += Raise.Event<EventHandler>(plugins, EventArgs.Empty);

        Assert.True(vm.IsEditorOpen);
        Assert.Equal("half typed", vm.EditName);
    }

    [Fact]
    public void PickingADifferentServer_StillClosesTheEditor()
    {
        var (vm, _, _) = CreateTwo();
        vm.EditSelectedCommand.Execute(null);
        Assert.True(vm.IsEditorOpen);

        vm.SelectedServer = vm.Servers.Single(s => s.Id == OtherId);

        Assert.False(vm.IsEditorOpen);
    }

    [Fact]
    public async Task TogglingOneServer_LeavesEveryOtherRowClickable()
    {
        var (vm, plugins, _) = CreateTwo();
        var gate = new TaskCompletionSource();
        plugins.SetPluginEnabledAsync(ServerId, false).Returns(gate.Task);

        var row = vm.Servers.Single(s => s.Id == ServerId);
        row.IsEnabled = false;
        var toggle = vm.ToggleServerCommand.ExecuteAsync(row);

        var moving = vm.Servers.Single(s => s.Id == ServerId);
        var other = vm.Servers.Single(s => s.Id == OtherId);
        Assert.True(moving.IsToggling);
        Assert.Equal("McpServers_Status_Stopping", moving.StatusText);
        Assert.False(other.IsToggling);

        // The regression this guards: one command instance serves every row, so serialising it reports
        // CanExecute false and WPF greys out every other switch with nothing to say why.
        Assert.True(vm.ToggleServerCommand.CanExecute(other));

        gate.SetResult();
        await toggle;

        Assert.False(vm.Servers.Single(s => s.Id == ServerId).IsToggling);
    }

    [Fact]
    public async Task AFailedToggle_IsReported_AndTheRowStopsSpinning()
    {
        var (vm, plugins, snackbar) = Create(null);
        plugins.SetPluginEnabledAsync(ServerId, Arg.Any<bool>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        var row = vm.Servers.Single();
        row.IsEnabled = false;
        await vm.ToggleServerCommand.ExecuteAsync(row);

        Assert.False(vm.Servers.Single().IsToggling);
        snackbar.Received(1).Show(
            Arg.Any<string>(), "boom", ControlAppearance.Danger, Arg.Any<IconElement?>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task SavingAServer_ReportsItself_WhileTheSubprocessRestarts()
    {
        var (vm, plugins, _) = Create(["create_issue"]);
        var gate = new TaskCompletionSource<Guid>();
        plugins.SaveLocalMcpAsync(Arg.Any<Guid?>(), Arg.Any<LocalMcpDefinition>(), Arg.Any<CancellationToken>()).Returns(gate.Task);

        vm.EditSelectedCommand.Execute(null);
        var save = vm.SaveCommand.ExecuteAsync(null);

        // The regression this guards: the save restarts the server, so the button greys out for seconds
        // with nothing on screen saying why.
        Assert.True(vm.IsSaving);
        Assert.False(vm.CancelEditCommand.CanExecute(null));

        gate.SetResult(ServerId);
        await save;

        Assert.False(vm.IsSaving);
        Assert.True(vm.CancelEditCommand.CanExecute(null));
        Assert.False(vm.IsEditorOpen);
    }

    [Fact]
    public async Task AFailedSave_StopsTheIndicator_AndLeavesTheEditorOpen()
    {
        var (vm, plugins, _) = Create(["create_issue"]);
        plugins.SaveLocalMcpAsync(Arg.Any<Guid?>(), Arg.Any<LocalMcpDefinition>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Guid>(new InvalidOperationException("boom")));

        vm.EditSelectedCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsSaving);
        Assert.True(vm.IsEditorOpen);
        Assert.Equal("boom", vm.EditorMessage);
        Assert.True(vm.EditorMessageIsError);
    }
}
