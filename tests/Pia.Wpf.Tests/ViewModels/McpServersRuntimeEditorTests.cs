using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

// The runtime folder and the proposed folder resolve from process-wide PiaPaths, so the class runs serialised
// against a throwaway profile.
[Collection("PiaPathsStatic")]
public sealed class McpServersRuntimeEditorTests : IClassFixture<RedirectedProfileFixture>
{
    private static readonly Guid ServerId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private const string ExePath = @"C:\Tools\fs\fs.exe";
    private const string NpxArgs = "-y\n@modelcontextprotocol/server-everything";
    private const string AdminRoot = @"C:\Program Files\nodejs";

    private sealed record Fixture(
        McpServersSettingsViewModel Vm,
        IPluginService Plugins,
        ISandboxRuntimeService Runtime,
        IDialogService Dialogs,
        IClipboardService Clipboard,
        global::Wpf.Ui.ISnackbarService Snackbar,
        CapturingLogger<SettingsViewModel> Logger);

    private static Fixture Create(
        RuntimeDetection? detection = null,
        LocalMcpDefinition? stored = null,
        bool elevated = false)
    {
        var plugins = Substitute.For<IPluginService>();
        plugins.AreLocalMcpServersAllowed.Returns(true);
        plugins.GetLocalMcpPlugins().Returns(stored is null
            ? []
            : [new SyncPlugin
            {
                Id = ServerId,
                Kind = "mcp_server",
                Name = stored.Name,
                ConfigJson = """{"source":"local","transport":"stdio","command":"x"}""",
                UserEnabled = true
            }]);
        plugins.GetLocalMcpDefinition(ServerId).Returns(stored);
        plugins.GetLocalMcpStatus(ServerId).Returns(new LocalMcpStatus(false, [], [], null));

        // Echoing localizer: the assertions are about WHICH string a state picks, not its wording.
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(call => (string)call[0]);
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => $"{call[0]}:{string.Join(",", (object[])call[1])}");

        var runtime = Substitute.For<ISandboxRuntimeService>();
        runtime.CanGrantMachineWide.Returns(elevated);
        runtime.GuideFor(Arg.Any<RuntimeFamily>()).Returns(call => RuntimeInstallGuides.GuideFor(call.Arg<RuntimeFamily>()));
        runtime.GuideFor(Arg.Any<RuntimeResolution>()).Returns(call => RuntimeInstallGuides.GuideFor(call.Arg<RuntimeResolution>()));
        AnyDetect(runtime).Returns(Task.FromResult(detection ??Detection(RunnerKind.Npx, RuntimeOutcome.Ready, install: Node(readable: true))));

        var dialogs = Substitute.For<IDialogService>();
        var clipboard = Substitute.For<IClipboardService>();
        var snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();
        var logger = new CapturingLogger<SettingsViewModel>();

        var vm = new McpServersSettingsViewModel(
            plugins,
            dialogs,
            localization,
            snackbar,
            SettingsSubViewModels.Mxc(),
            Substitute.For<IFileDialogService>(),
            logger,
            runtime,
            clipboard)
        {
            SandboxGuardDebounceMs = 0,
            RuntimeDetectionDebounceMs = 0
        };

        return new Fixture(vm, plugins, runtime, dialogs, clipboard, snackbar, logger);
    }

    private static RuntimeDetection Detection(
        RunnerKind kind,
        RuntimeOutcome outcome,
        RuntimeIssue issue = RuntimeIssue.None,
        RuntimeInstall? install = null,
        IReadOnlyList<string>? roots = null,
        string? programFolder = null,
        IReadOnlyList<DotnetRuntimeConfig.Framework>? frameworks = null) =>
        new(new RunnerParseResult(kind, null, RunnerRefusal.None, null, "npx", []),
            new RuntimeResolution(kind, outcome, issue, install, roots ?? [], programFolder, frameworks),
            @"C:\state",
            kind == RunnerKind.Uvx);

    private static RuntimeInstall Node(string root = @"C:\Users\Jane\node", bool readable = false, bool adminOwned = false) =>
        new(RuntimeFamily.Node, root, Path.Combine(root, "node.exe"), "22.23.2", "10.9.8", null, readable, adminOwned);

    private static RuntimeInstall Install(RuntimeFamily family, string? version) =>
        new(family, @"C:\Users\Jane\rt", @"C:\Users\Jane\rt\rt.exe", version, null, null, false, false);

    private static LocalMcpDefinition Stored(McpSandboxPolicy? sandbox, string command = "npx", IReadOnlyList<string>? args = null) =>
        new("files", command, args ?? ["-y", "@modelcontextprotocol/server-filesystem"], new Dictionary<string, string>(),
            WorkingDirectory: null, "files", AllowedTools: null, Sandbox: sandbox);

    private static void NewServer(McpServersSettingsViewModel vm, string command, string arguments = "", string name = "GitHub")
    {
        vm.AddServerCommand.Execute(null);
        vm.EditName = name;
        vm.EditCommand = command;
        vm.EditArguments = arguments;
    }

    private static void WaitFor(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)));

    private static Task<RuntimeDetection> AnyDetect(ISandboxRuntimeService runtime) =>
        runtime.DetectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());

    private static string StateFolder(string prefix) => PiaPaths.McpRuntimeStateDirectory(prefix);

    // ---- the network default ----------------------------------------------------------------------------

    [Fact]
    public void ANewRunnerServer_DefaultsToNetworkAccess()
    {
        var (vm, _, _, _, _, _, _) = Create();

        NewServer(vm, "npx", NpxArgs);

        Assert.True(vm.EditSandboxEnabled);
        Assert.True(vm.EditSandboxAllowNetwork);
        Assert.False(vm.ShowRunnerDenyHint);
    }

    /// <summary>A local script or dll fetches nothing at its first start, so it keeps the no-network default and gets no hint.</summary>
    [Theory]
    [InlineData("node", @"C:/Servers/weather/index.js", RunnerKind.NodeScript)]
    [InlineData("dotnet", @"C:/Servers/app/app.dll", RunnerKind.DotnetDll)]
    public void ANewLocalScriptOrDllServer_DefaultsToNoNetwork_WithoutTheDenyHint(string command, string arguments, RunnerKind kind)
    {
        var (vm, _, _, _, _, _, _) = Create(Detection(kind, RuntimeOutcome.Ready, install: Node(readable: true)));

        NewServer(vm, command, arguments);

        Assert.True(vm.IsRunnerCommand);
        Assert.True(vm.ShowRuntimePanel);
        Assert.False(vm.EditSandboxAllowNetwork);
        Assert.False(vm.ShowRunnerDenyHint);

        vm.EditSandboxAllowNetwork = true;
        vm.EditSandboxAllowNetwork = false;

        Assert.False(vm.ShowRunnerDenyHint);
    }

    [Fact]
    public void ANewProgramServer_DefaultsToNoNetwork_AndLeavingARunnerTakesTheDefaultAway()
    {
        var (vm, _, _, _, _, _, _) = Create();

        NewServer(vm, ExePath);
        Assert.False(vm.EditSandboxAllowNetwork);

        vm.EditCommand = "npx";
        Assert.True(vm.EditSandboxAllowNetwork);

        vm.EditCommand = ExePath;
        Assert.False(vm.EditSandboxAllowNetwork);
    }

    [Fact]
    public void AFolderAddedBeforeTheCommand_StillGetsTheRunnerDefault()
    {
        var (vm, _, _, _, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditName = "GitHub";
        vm.NewReadwritePath = @"C:\Mine";
        vm.AddReadwritePathCommand.Execute(null);

        vm.EditCommand = "npx";

        Assert.True(vm.EditSandboxAllowNetwork);
        Assert.Contains(@"C:\Mine", vm.EditReadwritePaths);
    }

    [Fact]
    public void ANetworkSwitchTheUserMoved_IsKeptThroughLaterEdits()
    {
        var (vm, _, _, _, _, _, _) = Create();
        NewServer(vm, "npx");

        vm.EditSandboxAllowNetwork = false;
        vm.EditArguments = NpxArgs;
        vm.EditCommand = ExePath;
        vm.EditCommand = "npx";

        Assert.False(vm.EditSandboxAllowNetwork);
        Assert.True(vm.ShowRunnerDenyHint);
    }

    [Fact]
    public void ANewEditor_ForgetsTheSwitchTheLastOneMoved()
    {
        var (vm, _, _, _, _, _, _) = Create();
        NewServer(vm, "npx");
        vm.EditSandboxAllowNetwork = false;
        vm.CancelEditCommand.Execute(null);

        NewServer(vm, "npx");

        Assert.True(vm.EditSandboxAllowNetwork);
    }

    [Fact]
    public void ASavedServersNetworkSwitch_IsNeverChangedByTheCommand()
    {
        var (deny, _, _, _, _, _, _) = Create(stored: Stored(new McpSandboxPolicy([], [], false), command: ExePath, args: []));
        deny.EditSelectedCommand.Execute(null);
        deny.EditCommand = "npx";
        Assert.False(deny.EditSandboxAllowNetwork);

        var (allow, _, _, _, _, _, _) = Create(stored: Stored(new McpSandboxPolicy([], [], true)));
        allow.EditSelectedCommand.Execute(null);
        allow.EditCommand = ExePath;
        Assert.True(allow.EditSandboxAllowNetwork);
    }

    // ---- what counts as a runner ------------------------------------------------------------------------

    [Fact]
    public void CmdSlashCWithARunner_BecomesARunnerThroughTheArguments()
    {
        var (vm, _, runtime, _, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditName = "Everything";
        vm.EditCommand = "cmd";

        Assert.False(vm.IsRunnerCommand);
        Assert.False(vm.EditSandboxEnabled);
        Assert.Equal("McpServers_Sandbox_Reason_CmdWithoutC", vm.SandboxDisabledReason);

        vm.EditArguments = "/c\nnpx\n-y\n@modelcontextprotocol/server-everything";

        Assert.True(vm.IsRunnerCommand);
        Assert.True(vm.EditSandboxEnabled);
        Assert.True(vm.ShowRuntimePanel);
        Assert.True(vm.EditSandboxAllowNetwork);
        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
        _ = runtime.Received().DetectAsync(
            "cmd",
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "/c", "npx", "-y", "@modelcontextprotocol/server-everything" })),
            null,
            "everything",
            Arg.Any<CancellationToken>());
    }

    // ---- when detection runs ----------------------------------------------------------------------------

    [Fact]
    public void Detection_FollowsTheCommandTheArgumentsAndTheWorkingDirectory()
    {
        var (vm, _, runtime, _, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditName = "Everything";

        vm.EditCommand = "npx";
        _ = AnyDetect(runtime.Received(1));

        vm.EditArguments = NpxArgs;
        _ = runtime.Received(1).DetectAsync(
            "npx", Arg.Is<IReadOnlyList<string>>(a => a.Count == 2), null, "everything", Arg.Any<CancellationToken>());

        vm.EditWorkingDirectory = @" C:\Work ";
        _ = runtime.Received(1).DetectAsync(
            "npx", Arg.Any<IReadOnlyList<string>>(), @"C:\Work", "everything", Arg.Any<CancellationToken>());

        _ = AnyDetect(runtime.Received(3));
        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
    }

    [Fact]
    public void ANewerDetection_WinsOverAnOlderOneThatFinishesLater()
    {
        var (vm, _, runtime, _, _, _, _) = Create();
        var first = new TaskCompletionSource<RuntimeDetection>();
        var tokens = new List<CancellationToken>();
        AnyDetect(runtime).Returns(call =>
        {
            tokens.Add(call.Arg<CancellationToken>());
            return tokens.Count == 1
                ? first.Task
                : Task.FromResult(Detection(RunnerKind.Npx, RuntimeOutcome.Ready, install: Node(readable: true)));
        });

        vm.AddServerCommand.Execute(null);
        vm.EditName = "Everything";
        vm.EditCommand = "npx";
        WaitFor(() => vm.IsDetectingRuntime);

        vm.EditArguments = NpxArgs;
        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
        Assert.True(tokens[0].IsCancellationRequested);

        first.SetResult(Detection(RunnerKind.Npx, RuntimeOutcome.NotFound, RuntimeIssue.NodeNotFound));

        Assert.Equal(RuntimeOutcome.Ready, vm.RuntimeOutcomeValue);
        Assert.False(vm.IsDetectingRuntime);
    }

    [Fact]
    public void ANameChange_RedrawsOnlyTheStateFolder_WithoutDetectingAgain()
    {
        var (vm, _, runtime, _, _, _, _) = Create();
        NewServer(vm, "npx");
        _ = AnyDetect(runtime.Received(1));
        Assert.Equal($"McpServers_Sandbox_Runtime_StateFolder:{StateFolder("github")}", vm.RuntimeStateFolderText);
        runtime.ClearReceivedCalls();

        vm.EditName = "Docs";

        _ = AnyDetect(runtime.DidNotReceive());
        Assert.Equal($"McpServers_Sandbox_Runtime_StateFolder:{StateFolder("docs")}", vm.RuntimeStateFolderText);
    }

    [Fact]
    public void NothingIsDetected_WhileTheSandboxIsOff_AndTurningItOnDetects()
    {
        var (vm, _, runtime, _, _, _, _) = Create();
        NewServer(vm, ExePath);
        vm.EditSandboxEnabled = false;

        vm.EditCommand = "npx";
        vm.EditArguments = NpxArgs;

        _ = AnyDetect(runtime.DidNotReceive());
        Assert.False(vm.ShowRuntimePanel);
        Assert.False(vm.DetectRuntimesCommand.CanExecute(null));

        vm.EditSandboxEnabled = true;

        _ = AnyDetect(runtime.Received(1));
        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
        Assert.True(vm.DetectRuntimesCommand.CanExecute(null));

        vm.EditSandboxEnabled = false;

        Assert.Null(vm.RuntimeOutcomeValue);
        Assert.Null(vm.RuntimeSummary);
    }

    [Fact]
    public void TheDetectButton_DetectsAtOnce()
    {
        var (vm, _, runtime, _, _, _, _) = Create();
        vm.RuntimeDetectionDebounceMs = 60_000;
        NewServer(vm, "npx");
        _ = AnyDetect(runtime.DidNotReceive());

        vm.DetectRuntimesCommand.Execute(null);

        _ = AnyDetect(runtime.Received(1));
        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
    }

    [Fact]
    public void OpeningASavedSandboxedRunner_DetectsItsRuntime_ButTheDetailPaneDoesNot()
    {
        var (vm, _, runtime, _, _, _, _) = Create(stored: Stored(new McpSandboxPolicy([], [], true)));
        _ = AnyDetect(runtime.DidNotReceive());

        vm.EditSelectedCommand.Execute(null);

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
        _ = runtime.Received().DetectAsync("npx", Arg.Any<IReadOnlyList<string>>(), null, "files", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AFailedDetection_ShowsTheGenericReason_StopsTheSpinner_AndLogsNoPath()
    {
        var (vm, _, runtime, _, _, _, logger) = Create();
        AnyDetect(runtime).Returns(Task.FromException<RuntimeDetection>(new UnauthorizedAccessException(@"Access to C:\Users\Jane\node is denied.")));

        NewServer(vm, "npx");

        WaitFor(() => vm.RuntimeReason is not null);
        Assert.Equal("McpServers_Sandbox_Runtime_CheckFailed", vm.RuntimeReason);
        Assert.False(vm.IsDetectingRuntime);
        Assert.Null(vm.RuntimeOutcomeValue);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains(@"C:\Users"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(UnauthorizedAccessException)));
    }

    // ---- the panel per outcome --------------------------------------------------------------------------

    [Fact]
    public void AReadyRuntimeSandboxedAppsCanRead_SharesNothingExtra()
    {
        var (vm, _, _, _, _, _, _) = Create(Detection(RunnerKind.Npx, RuntimeOutcome.Ready, install: Node(readable: true)));

        NewServer(vm, "npx", NpxArgs);

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
        Assert.Equal("McpServers_Sandbox_Runtime_Found:Node.js 22.23.2, npm 10.9.8", vm.RuntimeSummary);
        Assert.True(vm.RuntimeReadableAlready);
        Assert.False(vm.ShowRuntimeFolders);
        Assert.Null(vm.RuntimeFolders);
        Assert.Null(vm.RuntimeReason);
        Assert.False(vm.ShowRuntimeFix);
        Assert.False(vm.ShowRuntimeGuide);
        Assert.Null(vm.RuntimeGuideCommand);
    }

    [Fact]
    public void AReadyRuntime_ListsTheFoldersSharedAtEachStart()
    {
        var detection = Detection(RunnerKind.NodeScript, RuntimeOutcome.Ready, install: Node(),
            roots: [@"C:\Users\Jane\node"], programFolder: @"C:\Servers\weather");
        var (vm, _, _, _, _, _, _) = Create(detection);

        NewServer(vm, "node", @"C:\Servers\weather\index.js");

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
        Assert.True(vm.ShowRuntimeFolders);
        Assert.Equal($@"C:\Users\Jane\node{Environment.NewLine}C:\Servers\weather", vm.RuntimeFolders);
        Assert.False(vm.RuntimeReadableAlready);
    }

    [Theory]
    [InlineData(RunnerKind.Dnx, "dnx", "Contoso.Mcp", RuntimeFamily.Dotnet, "10.0.402", ".NET SDK 10.0.402")]
    [InlineData(RunnerKind.DotnetDll, "dotnet", @"C:\Servers\app\app.dll", RuntimeFamily.Dotnet, "10.0.1", ".NET 10.0.1")]
    [InlineData(RunnerKind.Uvx, "uvx", "mcp-server-time", RuntimeFamily.Uv, "0.13.1", "uv 0.13.1")]
    [InlineData(RunnerKind.Uvx, "uvx", "mcp-server-time", RuntimeFamily.Uv, null, "uv")]
    public void TheSummary_NamesTheProductAndItsVersion(
        RunnerKind kind, string command, string arguments, RuntimeFamily family, string? version, string product)
    {
        var (vm, _, _, _, _, _, _) = Create(Detection(kind, RuntimeOutcome.Ready, install: Install(family, version)));

        NewServer(vm, command, arguments);

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Ready);
        Assert.Equal($"McpServers_Sandbox_Runtime_Found:{product}", vm.RuntimeSummary);
    }

    [Fact]
    public void AnAdminOwnedRuntime_OffersTheFix_WhenPiaRunsElevated()
    {
        var detection = Detection(RunnerKind.Npx, RuntimeOutcome.NeedsAdminFix, RuntimeIssue.NodeAdminOwned,
            Node(AdminRoot, adminOwned: true));
        var (vm, _, _, _, _, _, _) = Create(detection, elevated: true);

        NewServer(vm, "npx", NpxArgs);

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.NeedsAdminFix);
        Assert.True(vm.ShowRuntimeFix);
        Assert.Equal($"McpServers_Sandbox_Runtime_FixText:{AdminRoot}", vm.RuntimeFixText);
        Assert.True(vm.GrantRuntimeReadCommand.CanExecute(null));
        Assert.False(vm.ShowRuntimeGuide);
        Assert.Equal("McpServers_Sandbox_Runtime_Issue_NodeAdminOwned_Elevated", vm.RuntimeReason);
        Assert.NotNull(vm.RuntimeSummary);
    }

    [Fact]
    public void AnAdminOwnedRuntime_OffersTheGuide_WhenPiaIsNotElevated()
    {
        var detection = Detection(RunnerKind.Npx, RuntimeOutcome.NeedsAdminFix, RuntimeIssue.NodeAdminOwned,
            Node(AdminRoot, adminOwned: true));
        var (vm, _, _, _, _, _, _) = Create(detection, elevated: false);

        NewServer(vm, "npx", NpxArgs);

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.NeedsAdminFix);
        Assert.False(vm.ShowRuntimeFix);
        Assert.Null(vm.RuntimeFixText);
        Assert.False(vm.GrantRuntimeReadCommand.CanExecute(null));
        Assert.True(vm.ShowRuntimeGuide);
        Assert.Equal(RuntimeInstallGuides.GuideFor(RuntimeFamily.Node).Command, vm.RuntimeGuideCommand);
        Assert.Equal("McpServers_Sandbox_Runtime_Issue_NodeAdminOwned", vm.RuntimeReason);
    }

    [Theory]
    [InlineData(RuntimeIssue.NodeAdminOwned, true)]
    [InlineData(RuntimeIssue.NodeAdminOwned, false)]
    [InlineData(RuntimeIssue.DotnetAdminOwned, true)]
    [InlineData(RuntimeIssue.DotnetAdminOwned, false)]
    public void AnAdminOwnedRuntimesReason_AsksForElevation_OnlyWhenPiaIsNotElevated(RuntimeIssue issue, bool elevated)
    {
        var (kind, command, arguments, install) = issue == RuntimeIssue.NodeAdminOwned
            ? (RunnerKind.Npx, "npx", NpxArgs, Node(AdminRoot, adminOwned: true))
            : (RunnerKind.Dnx, "dnx", "Contoso.Mcp", new RuntimeInstall(RuntimeFamily.Dotnet, @"C:\Program Files\dotnet",
                @"C:\Program Files\dotnet\dotnet.exe", "10.0.100", null, null, ReadableAlready: false, AdminOwned: true));
        var (vm, _, _, _, _, _, _) = Create(Detection(kind, RuntimeOutcome.NeedsAdminFix, issue, install), elevated: elevated);

        NewServer(vm, command, arguments);

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.NeedsAdminFix);
        Assert.Equal(elevated, vm.ShowRuntimeFix);
        Assert.Equal(
            elevated ? $"McpServers_Sandbox_Runtime_Issue_{issue}_Elevated" : $"McpServers_Sandbox_Runtime_Issue_{issue}",
            vm.RuntimeReason);
    }

    [Theory]
    [InlineData(RunnerKind.Npx, "npx", NpxArgs, RuntimeIssue.NodeNotFound, RuntimeFamily.Node,
        "McpServers_Sandbox_Runtime_Guide_Node", "McpServers_Sandbox_Runtime_Guide_NodeAlt")]
    [InlineData(RunnerKind.Uvx, "uvx", "mcp-server-time", RuntimeIssue.UvNotFound, RuntimeFamily.Uv,
        "McpServers_Sandbox_Runtime_Guide_Uv", "McpServers_Sandbox_Runtime_Guide_UvAlt")]
    [InlineData(RunnerKind.Dnx, "dnx", "Contoso.Mcp", RuntimeIssue.DotnetSdkMissing, RuntimeFamily.Dotnet,
        "McpServers_Sandbox_Runtime_Guide_Dotnet", null)]
    [InlineData(RunnerKind.DotnetDll, "dotnet", @"C:\Servers\app\app.dll", RuntimeIssue.DotnetRuntimeMissing, RuntimeFamily.Dotnet,
        "McpServers_Sandbox_Runtime_Guide_DotnetRuntime:.NET 8.0", null,
        "Microsoft.NETCore.App", "8.0.0", "McpServers_Sandbox_Runtime_Issue_DotnetRuntimeMissing:.NET 8.0")]
    [InlineData(RunnerKind.DotnetDll, "dotnet", @"C:\Servers\app\app.dll", RuntimeIssue.DotnetRuntimeMissing, RuntimeFamily.Dotnet,
        "McpServers_Sandbox_Runtime_Guide_DotnetRuntime:ASP.NET Core 8.0", null,
        "Microsoft.AspNetCore.App", "8.0.0", "McpServers_Sandbox_Runtime_Issue_DotnetRuntimeMissing:ASP.NET Core 8.0")]
    [InlineData(RunnerKind.DotnetDll, "dotnet", @"C:\Servers\app\app.dll", RuntimeIssue.DotnetRuntimeMissing, RuntimeFamily.Dotnet,
        "McpServers_Sandbox_Runtime_Guide_Dotnet", null,
        null, null, "McpServers_Sandbox_Runtime_Issue_DotnetRuntimeMissing:.NET")]
    public void AMissingRuntime_ShowsItsFamilysGuide(
        RunnerKind kind, string command, string arguments, RuntimeIssue issue, RuntimeFamily family, string guideKey, string? altKey,
        string? framework = null, string? frameworkVersion = null, string? reason = null)
    {
        IReadOnlyList<DotnetRuntimeConfig.Framework>? frameworks = framework is null
            ? null
            : [new DotnetRuntimeConfig.Framework(framework, frameworkVersion!)];
        var detection = Detection(kind, RuntimeOutcome.NotFound, issue, frameworks: frameworks);
        var (vm, _, _, _, _, _, _) = Create(detection);

        NewServer(vm, command, arguments);

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.NotFound);
        var guide = RuntimeInstallGuides.GuideFor(detection.Resolution!);
        Assert.Equal(family, guide.Family);
        Assert.True(vm.ShowRuntimeGuide);
        Assert.Null(vm.RuntimeSummary);
        Assert.Equal(reason ?? $"McpServers_Sandbox_Runtime_Issue_{issue}", vm.RuntimeReason);
        Assert.Equal(guideKey, vm.RuntimeGuideText);
        Assert.Equal(guide.Command, vm.RuntimeGuideCommand);
        Assert.Equal(guide.AlternativeCommand, vm.RuntimeGuideAltCommand);
        Assert.Equal(altKey is not null, vm.ShowRuntimeGuideAlt);
        Assert.Equal(altKey, vm.RuntimeGuideAltText);
        Assert.False(vm.ShowRuntimeFix);
    }

    [Theory]
    [InlineData(RuntimeIssue.ToolchainTooLarge)]
    [InlineData(RuntimeIssue.ToolchainTooWide)]
    [InlineData(RuntimeIssue.ToolchainPathUnsupported)]
    public void AToolchainPiaWillNotShare_ShowsTheGuide(RuntimeIssue issue)
    {
        var (vm, _, _, _, _, _, _) = Create(Detection(RunnerKind.Npx, RuntimeOutcome.Refused, issue, Node()));

        NewServer(vm, "npx", NpxArgs);

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Refused);
        Assert.Equal($"McpServers_Sandbox_Runtime_Issue_{issue}", vm.RuntimeReason);
        Assert.True(vm.ShowRuntimeGuide);
        Assert.Equal(RuntimeInstallGuides.GuideFor(RuntimeFamily.Node).Command, vm.RuntimeGuideCommand);
    }

    /// <summary>Detection calls a drive or share root too wide as well, so the editor's text must say so in every language.</summary>
    [Theory]
    [InlineData("en", "drive", "network share")]
    [InlineData("de", "Laufwerk", "Netzwerkfreigabe")]
    [InlineData("fr", "lecteur", "partage réseau")]
    public void TheTooWideReason_CoversADriveAndAShare_InEveryLanguage(string culture, string drive, string share)
    {
        var text = Pia.Resources.Strings.ViewStrings.ResourceManager.GetString(
            "McpServers_Sandbox_Runtime_Issue_ToolchainTooWide", System.Globalization.CultureInfo.GetCultureInfo(culture));

        Assert.NotNull(text);
        Assert.Contains(drive, text);
        Assert.Contains(share, text);
        Assert.DoesNotMatch(@"[A-Za-z]:[\\/]|\\\\", text);
    }

    [Fact]
    public void AUvInAWritableFolder_ShowsTheUvGuide()
    {
        var uv = new RuntimeInstall(RuntimeFamily.Uv, @"C:\Work\box", @"C:\Work\box\uv.exe", "0.13.1", null, null, false, false);
        var (vm, _, _, _, _, _, _) = Create(Detection(RunnerKind.Uvx, RuntimeOutcome.Refused, RuntimeIssue.UvInWritableFolder, uv));

        NewServer(vm, "uvx", "mcp-server-time");

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Refused);
        Assert.Equal("McpServers_Sandbox_Runtime_Issue_UvInWritableFolder", vm.RuntimeReason);
        Assert.True(vm.ShowRuntimeGuide);
        Assert.Equal("McpServers_Sandbox_Runtime_Guide_Uv", vm.RuntimeGuideText);
        Assert.Equal(RuntimeInstallGuides.GuideFor(RuntimeFamily.Uv).Command, vm.RuntimeGuideCommand);
        Assert.Equal(RuntimeInstallGuides.GuideFor(RuntimeFamily.Uv).AlternativeCommand, vm.RuntimeGuideAltCommand);
    }

    [Theory]
    [InlineData(RuntimeIssue.ScriptMissing)]
    [InlineData(RuntimeIssue.ProgramFolderTooWide)]
    public void AProgramProblem_ShowsTheReasonWithoutAGuide(RuntimeIssue issue)
    {
        var (vm, _, _, _, _, _, _) = Create(Detection(RunnerKind.NodeScript, RuntimeOutcome.Refused, issue, Node()));

        NewServer(vm, "node", @"C:\Servers\weather\index.js");

        WaitFor(() => vm.RuntimeOutcomeValue == RuntimeOutcome.Refused);
        Assert.Equal($"McpServers_Sandbox_Runtime_Issue_{issue}", vm.RuntimeReason);
        Assert.False(vm.ShowRuntimeGuide);
        Assert.Null(vm.RuntimeGuideCommand);
        Assert.False(vm.ShowRuntimeFolders);
    }

    [Fact]
    public void TheStateFolder_FollowsThePrefix_IsReadOnlyForUvx_AndIsAbsentForAProgram()
    {
        var (vm, _, _, _, _, _, _) = Create();

        NewServer(vm, "npx");
        Assert.Equal($"McpServers_Sandbox_Runtime_StateFolder:{StateFolder("github")}", vm.RuntimeStateFolderText);

        vm.EditCommand = "uvx";
        vm.EditArguments = "mcp-server-time";
        Assert.Equal($"McpServers_Sandbox_Runtime_StateFolderReadOnly:{StateFolder("github")}", vm.RuntimeStateFolderText);

        vm.EditArguments = string.Empty;
        vm.EditCommand = ExePath;
        Assert.Null(vm.RuntimeStateFolderText);
    }

    // ---- the machine-wide grant ----

    private static MachineWideGrantPreview RealPreview(string root) =>
        new SandboxRuntimeService(
                Substitute.For<IRuntimeDetector>(),
                Substitute.For<IToolchainAclService>(),
                Substitute.For<IElevationService>(),
                Substitute.For<IRuntimeHostService>(),
                Substitute.For<IRuntimeRootVerifier>(),
                NullLogger<SandboxRuntimeService>.Instance)
            .PreviewMachineWideGrant(root);

    private static Fixture CreateAdminFix()
    {
        var fixture = Create(
            Detection(RunnerKind.Npx, RuntimeOutcome.NeedsAdminFix, RuntimeIssue.NodeAdminOwned, Node(AdminRoot, adminOwned: true)),
            elevated: true);
        NewServer(fixture.Vm, "npx", NpxArgs);
        WaitFor(() => fixture.Vm.ShowRuntimeFix);
        fixture.Runtime.ClearReceivedCalls();
        return fixture;
    }

    [Fact]
    public async Task TheFix_AsksWithBothCommands_Grants_AndDetectsAgain()
    {
        var (vm, _, runtime, dialogs, _, snackbar, _) = CreateAdminFix();
        var preview = RealPreview(AdminRoot);
        runtime.PreviewMachineWideGrant(AdminRoot).Returns(preview);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        runtime.GrantMachineWideReadAsync(AdminRoot, Arg.Any<CancellationToken>())
            .Returns(new MachineWideGrantResult(MachineWideGrantFailure.None));

        await vm.GrantRuntimeReadCommand.ExecuteAsync(null);

        Assert.Contains("/grant *S-1-15-2-1:(OI)(CI)(RX)", preview.GrantCommand);
        Assert.Contains("/remove:g *S-1-15-2-1", preview.RemoveCommand);
        await dialogs.Received(1).ShowConfirmationDialogAsync(
            "McpServers_Sandbox_Runtime_FixConfirmTitle",
            $"McpServers_Sandbox_Runtime_FixConfirmMessage:{AdminRoot},{preview.GrantCommand},{preview.RemoveCommand}");
        await runtime.Received(1).GrantMachineWideReadAsync(AdminRoot, Arg.Any<CancellationToken>());
        snackbar.Received(1).Show(
            "McpServers_Title", "McpServers_Sandbox_Runtime_FixDone", global::Wpf.Ui.Controls.ControlAppearance.Success, null,
            Arg.Any<TimeSpan>());
        _ = AnyDetect(runtime.Received(1));
        Assert.Null(vm.EditorMessage);
    }

    [Fact]
    public async Task AFolderThatAlreadyCarriesTheEntry_IsRefusedWithoutADialog()
    {
        var (vm, _, runtime, dialogs, _, _, _) = CreateAdminFix();
        var preview = RealPreview(AdminRoot) with { HasExistingAce = true };
        runtime.PreviewMachineWideGrant(AdminRoot).Returns(preview);

        await vm.GrantRuntimeReadCommand.ExecuteAsync(null);

        Assert.Equal("McpServers_Sandbox_Runtime_FixExistingAce", vm.EditorMessage);
        Assert.True(vm.EditorMessageIsError);
        await dialogs.DidNotReceiveWithAnyArgs().ShowConfirmationDialogAsync(default!, default!);
        await runtime.DidNotReceive().GrantMachineWideReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADeclinedConfirmation_ChangesNothing()
    {
        var (vm, _, runtime, dialogs, _, _, _) = CreateAdminFix();
        var preview = RealPreview(AdminRoot);
        runtime.PreviewMachineWideGrant(AdminRoot).Returns(preview);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await vm.GrantRuntimeReadCommand.ExecuteAsync(null);

        await runtime.DidNotReceive().GrantMachineWideReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Null(vm.EditorMessage);
    }

    [Theory]
    [InlineData(MachineWideGrantFailure.NotElevated)]
    [InlineData(MachineWideGrantFailure.WindowsFolder)]
    [InlineData(MachineWideGrantFailure.TooWide)]
    [InlineData(MachineWideGrantFailure.ExistingAce)]
    [InlineData(MachineWideGrantFailure.Unexpected)]
    public async Task AFailedGrant_ShowsItsLocalizedReason(MachineWideGrantFailure failure)
    {
        var (vm, _, runtime, dialogs, _, snackbar, _) = CreateAdminFix();
        var preview = RealPreview(AdminRoot);
        runtime.PreviewMachineWideGrant(AdminRoot).Returns(preview);
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        runtime.GrantMachineWideReadAsync(AdminRoot, Arg.Any<CancellationToken>()).Returns(new MachineWideGrantResult(failure));

        await vm.GrantRuntimeReadCommand.ExecuteAsync(null);

        Assert.Equal($"McpServers_Sandbox_Runtime_FixFailed:McpServers_Sandbox_Runtime_FixFailed_{failure}", vm.EditorMessage);
        Assert.True(vm.EditorMessageIsError);
        snackbar.DidNotReceiveWithAnyArgs().Show(default!, default!, default, default, default);
    }

    /// <summary>The exception's text is English and may name the folder, so only the generic reason is shown.</summary>
    [Fact]
    public async Task AnUnreadablePermissionList_ShowsTheGenericReasonWithoutADialog()
    {
        var (vm, _, runtime, dialogs, _, _, _) = CreateAdminFix();
        runtime.PreviewMachineWideGrant(AdminRoot).Returns(_ => throw new UnauthorizedAccessException($"Access to the path '{AdminRoot}' is denied."));

        await vm.GrantRuntimeReadCommand.ExecuteAsync(null);

        Assert.Equal("McpServers_Sandbox_Runtime_FixFailed:McpServers_Sandbox_Runtime_FixFailed_Unexpected", vm.EditorMessage);
        Assert.True(vm.EditorMessageIsError);
        await dialogs.DidNotReceiveWithAnyArgs().ShowConfirmationDialogAsync(default!, default!);
    }

    // ---- copying an install command ----

    [Fact]
    public void Copy_PutsTheChosenCommandOnTheClipboard()
    {
        var (vm, _, _, _, clipboard, snackbar, _) = Create(Detection(RunnerKind.Npx, RuntimeOutcome.NotFound, RuntimeIssue.NodeNotFound));
        NewServer(vm, "npx", NpxArgs);
        WaitFor(() => vm.ShowRuntimeGuide);
        var guide = RuntimeInstallGuides.GuideFor(RuntimeFamily.Node);

        vm.CopyRuntimeGuideCommand.Execute("primary");
        vm.CopyRuntimeGuideCommand.Execute("alt");

        Received.InOrder(() =>
        {
            clipboard.SetText(guide.Command);
            clipboard.SetText(guide.AlternativeCommand!);
        });
        snackbar.Received(2).Show(
            "McpServers_Title", "McpServers_Sandbox_Runtime_Copied", global::Wpf.Ui.Controls.ControlAppearance.Success, null,
            Arg.Any<TimeSpan>());
    }

    [Fact]
    public void ABusyClipboard_BecomesAnErrorMessage()
    {
        var (vm, _, _, _, clipboard, snackbar, _) = Create(Detection(RunnerKind.Npx, RuntimeOutcome.NotFound, RuntimeIssue.NodeNotFound));
        clipboard.When(c => c.SetText(Arg.Any<string>())).Do(_ => throw new COMException("OpenClipboard failed"));
        NewServer(vm, "npx", NpxArgs);
        WaitFor(() => vm.ShowRuntimeGuide);

        vm.CopyRuntimeGuideCommand.Execute("primary");

        Assert.Equal("OpenClipboard failed", vm.EditorMessage);
        Assert.True(vm.EditorMessageIsError);
        snackbar.DidNotReceiveWithAnyArgs().Show(default!, default!, default, default, default);
    }

    // ---- the preview prefix -------------------------------------------------------------------------------

    [Fact]
    public async Task TestConnection_TheProposedFolder_AndDetection_UseThePreviewPrefix()
    {
        var (vm, plugins, runtime, _, _, _, _) = Create();
        plugins.PreviewLocalMcpToolPrefix(Arg.Any<Guid?>(), Arg.Any<string>()).Returns("github_2");
        plugins.ProbeLocalMcpAsync(Arg.Any<LocalMcpDefinition>(), Arg.Any<CancellationToken>())
            .Returns(McpProbeResult.Failed("stub"));

        NewServer(vm, "npx", NpxArgs);

        Assert.Equal([Path.Combine(PiaPaths.McpSandboxRoot, "github_2")], vm.EditReadwritePaths);
        Assert.Equal($"McpServers_Sandbox_Runtime_StateFolder:{StateFolder("github_2")}", vm.RuntimeStateFolderText);
        _ = runtime.Received().DetectAsync("npx", Arg.Any<IReadOnlyList<string>>(), null, "github_2", Arg.Any<CancellationToken>());
        plugins.Received().PreviewLocalMcpToolPrefix(null, "GitHub");

        await vm.TestConnectionCommand.ExecuteAsync(null);

        var probed = (LocalMcpDefinition)plugins.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPluginService.ProbeLocalMcpAsync))
            .GetArguments()[0]!;
        Assert.Equal("github_2", probed.ToolPrefix);
    }

    // ---- guard parity with the start --------------------------------------------------------------------------

    // Inside Pia's own data folder the guard asks for a folder of its own; the check resolves the file on disk.
    private static string MakeProgramInPiaData(string name)
    {
        var folder = Path.Combine(PiaPaths.LocalDataDirectory, "guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var program = Path.Combine(folder, name);
        File.WriteAllBytes(program, []);
        return program;
    }

    [Fact]
    public void TheGuard_JudgesTheProgramBehindCmdSlashC()
    {
        var program = MakeProgramInPiaData("server.exe");
        try
        {
            var (vm, _, runtime, _, _, _, _) = Create();
            vm.AddServerCommand.Execute(null);
            vm.EditName = "Typed";
            vm.EditCommand = "cmd";

            vm.EditArguments = $"/c\n{program}";

            Assert.False(vm.IsRunnerCommand);
            WaitFor(() => vm.SandboxGuardMessage is not null);
            Assert.Contains("its own folder", vm.SandboxGuardMessage);
            _ = AnyDetect(runtime.DidNotReceive());
        }
        finally
        {
            TempPath.Remove(Path.GetDirectoryName(program)!);
        }
    }

    [Fact]
    public void TheGuard_LeavesARunnersFoldersToTheDetector()
    {
        var node = MakeProgramInPiaData("node.exe");
        try
        {
            var (vm, _, _, _, _, _, _) = Create();
            NewServer(vm, node, @"C:\Servers\weather\index.js", name: "Weather");

            // A switch the user moves re-checks at once, so no debounced result is raced.
            vm.EditSandboxEnabled = false;
            vm.EditSandboxEnabled = true;

            Assert.True(vm.IsRunnerCommand);
            Assert.Null(vm.SandboxGuardMessage);

            var program = Path.Combine(Path.GetDirectoryName(node)!, "server.exe");
            File.WriteAllBytes(program, []);
            vm.EditArguments = string.Empty;
            vm.EditCommand = program;
            vm.EditSandboxEnabled = false;
            vm.EditSandboxEnabled = true;

            Assert.False(vm.IsRunnerCommand);
            Assert.Contains("its own folder", vm.SandboxGuardMessage);
        }
        finally
        {
            TempPath.Remove(Path.GetDirectoryName(node)!);
        }
    }

    // ---- the detail pane ------------------------------------------------------------------------------------

    [Fact]
    public void TheDetailPane_NamesASavedRunnersKindAndRuntimeFolder_WithoutDetecting()
    {
        var (vm, _, runtime, _, _, _, _) = Create(stored: Stored(new McpSandboxPolicy([], [], true)));

        Assert.Equal(
            $"McpServers_Detail_SandboxRuntime:McpServers_Sandbox_Runner_Npx,{StateFolder("files")}",
            vm.SelectedSandboxRuntime);
        _ = AnyDetect(runtime.DidNotReceive());
    }

    [Fact]
    public void TheDetailPane_HasNoRuntimeLine_ForAProgramOrAnUncontainedRunner()
    {
        var (program, _, _, _, _, _, _) = Create(stored: Stored(new McpSandboxPolicy([], [], false), command: ExePath, args: []));
        var (open, _, _, _, _, _, _) = Create(stored: Stored(sandbox: null));

        Assert.Null(program.SelectedSandboxRuntime);
        Assert.Null(open.SelectedSandboxRuntime);
    }

    [Fact]
    public void TheDetailPane_NamesTheProgramFolderBehindCmdSlashC()
    {
        var root = Directory.CreateTempSubdirectory("pia-guard-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "srv"));
            Directory.CreateDirectory(Path.Combine(root, "work"));
            var exe = Path.Combine(root, "srv", "server.exe");
            File.WriteAllBytes(exe, []);
            var policy = new McpSandboxPolicy([Path.Combine(root, "work")], [], false);

            var (vm, _, _, _, _, _, _) = Create(stored: Stored(policy, command: "cmd", args: ["/c", exe]));

            Assert.Equal(Path.Combine(root, "srv"), vm.SelectedSandboxAlsoReadable, ignoreCase: true);
            Assert.Null(vm.SelectedSandboxRuntime);
        }
        finally
        {
            TempPath.Remove(root);
        }
    }
}
