using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

// PiaPaths resolves the sandbox root from process-wide environment variables, so the class runs in the
// serialised collection with a throwaway profile: the expected default folder, and the one test that creates
// it, never touch the developer's real data.
[Collection("PiaPathsStatic")]
public sealed class McpServersSandboxEditorTests : IClassFixture<RedirectedProfileFixture>
{
    private static readonly Guid ServerId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string ExePath = @"C:\Tools\fs\fs.exe";

    private sealed record Fixture(
        McpServersSettingsViewModel Vm,
        IPluginService Plugins,
        IMxcAvailability Mxc,
        IFileDialogService Dialogs);

    private static McpSandboxPolicy Policy(bool network = false) =>
        new([@"C:\Data\work"], [@"C:\Data\reference"], network);

    private static LocalMcpDefinition Stored(McpSandboxPolicy? sandbox, string command = ExePath) =>
        new("files", command, ["--stdio"], new Dictionary<string, string>(),
            WorkingDirectory: null, "files", AllowedTools: null, Sandbox: sandbox);

    private static Fixture Create(
        LocalMcpDefinition? stored = null,
        MxcRuntimeState state = MxcRuntimeState.Ready,
        string? reason = null,
        LocalMcpStatus? status = null)
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
        plugins.GetLocalMcpStatus(ServerId).Returns(status ?? new LocalMcpStatus(false, [], [], null));

        // Echoing localizer: the assertions are about WHICH string a state picks, not its wording.
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(call => (string)call[0]);
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => $"{call[0]}:{string.Join(",", (object[])call[1])}");

        var mxc = SettingsSubViewModels.Mxc(state, reason);
        var dialogs = Substitute.For<IFileDialogService>();

        var vm = new McpServersSettingsViewModel(
            plugins,
            Substitute.For<IDialogService>(),
            localization,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            mxc,
            dialogs,
            NullLogger<SettingsViewModel>.Instance);

        return new Fixture(vm, plugins, mxc, dialogs);
    }

    private static LocalMcpDefinition SavedDefinition(IPluginService plugins)
    {
        var call = plugins.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IPluginService.SaveLocalMcpAsync));
        return (LocalMcpDefinition)call.GetArguments()[1]!;
    }

    private static string DefaultFolder(string slug) => Path.Combine(PiaPaths.McpSandboxRoot, slug);

    private static void AssertSamePolicy(McpSandboxPolicy expected, McpSandboxPolicy? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ReadwritePaths, actual!.ReadwritePaths);
        Assert.Equal(expected.ReadonlyPaths, actual.ReadonlyPaths);
        Assert.Equal(expected.AllowNetwork, actual.AllowNetwork);
    }

    // ---- defaults for a new server -------------------------------------------------------------------

    [Fact]
    public async Task ANewServerWithASandboxableCommand_DefaultsToSandboxedWithItsOwnFolder()
    {
        var (vm, plugins, _, _) = Create();

        vm.AddServerCommand.Execute(null);
        vm.EditName = "Files";
        vm.EditCommand = ExePath;

        Assert.True(vm.EditSandboxEnabled);
        Assert.Equal([DefaultFolder("files")], vm.EditReadwritePaths);
        Assert.Empty(vm.EditReadonlyPaths);
        Assert.False(vm.EditSandboxAllowNetwork);

        await vm.SaveCommand.ExecuteAsync(null);

        AssertSamePolicy(new McpSandboxPolicy([DefaultFolder("files")], [], false), SavedDefinition(plugins).Sandbox);
    }

    [Fact]
    public void ANewServerWithoutANameYet_ProposesNoFolder_NotTheSandboxRootItself()
    {
        var (vm, _, _, _) = Create();

        vm.AddServerCommand.Execute(null);
        vm.EditCommand = ExePath;

        Assert.True(vm.EditSandboxEnabled);
        Assert.Empty(vm.EditReadwritePaths);

        vm.EditName = "Files";
        Assert.Equal([DefaultFolder("files")], vm.EditReadwritePaths);
    }

    [Fact]
    public void RenamingANewServer_MovesTheProposedFolder_WhileTheListIsUntouched()
    {
        var (vm, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditCommand = ExePath;
        vm.EditName = "Files";

        vm.EditName = "Docs";

        Assert.Equal([DefaultFolder("docs")], vm.EditReadwritePaths);
    }

    [Fact]
    public void OnceTheUserEditsTheFolders_RenamingTheServerLeavesThemAlone()
    {
        var (vm, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditCommand = ExePath;
        vm.EditName = "Files";
        vm.NewReadwritePath = @"C:\Mine";
        vm.AddReadwritePathCommand.Execute(null);

        vm.EditName = "Docs";

        Assert.Equal([DefaultFolder("files"), @"C:\Mine"], vm.EditReadwritePaths);
    }

    [Fact]
    public void ANewServerWithAShimCommand_DefaultsOff_AndTheToggleIsDisabledWithTheReason()
    {
        var (vm, _, _, _) = Create();

        vm.AddServerCommand.Execute(null);
        vm.EditName = "GitHub";
        vm.EditCommand = "npx";

        Assert.False(vm.EditSandboxEnabled);
        Assert.False(vm.IsSandboxToggleEnabled);
        Assert.Equal("McpServers_Sandbox_Reason_Shim", vm.SandboxDisabledReason);
        Assert.Empty(vm.EditReadwritePaths);

        vm.EditCommand = ExePath;

        Assert.True(vm.EditSandboxEnabled);
        Assert.True(vm.IsSandboxToggleEnabled);
        Assert.Null(vm.SandboxDisabledReason);
        Assert.Equal([DefaultFolder("github")], vm.EditReadwritePaths);
    }

    [Fact]
    public void OnAHostThatCannotSandbox_ANewServerDefaultsOff_AndTheReasonIsShown()
    {
        var (vm, _, _, _) = Create(state: MxcRuntimeState.Unsupported, reason: "No AppContainer here.");

        vm.AddServerCommand.Execute(null);
        vm.EditName = "Files";
        vm.EditCommand = ExePath;

        Assert.False(vm.EditSandboxEnabled);
        Assert.False(vm.IsSandboxToggleEnabled);
        Assert.Equal("No AppContainer here.", vm.SandboxDisabledReason);
    }

    [Fact]
    public void AUserChoice_StopsTheDefaultsFromRewritingTheToggle()
    {
        var (vm, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditName = "Files";
        vm.EditCommand = ExePath;

        vm.EditSandboxEnabled = false;
        vm.EditCommand = @"C:\Tools\other\other.exe";

        Assert.False(vm.EditSandboxEnabled);
    }

    // ---- an existing sandboxed server must stay sandboxed through any edit ---------------------------

    [Fact]
    public async Task RenamingASandboxedServer_AndSaving_KeepsItsPolicyExactly()
    {
        var policy = Policy(network: true);
        var (vm, plugins, _, _) = Create(Stored(policy));

        vm.EditSelectedCommand.Execute(null);
        vm.EditName = "files-renamed";
        await vm.SaveCommand.ExecuteAsync(null);

        AssertSamePolicy(policy, SavedDefinition(plugins).Sandbox);
    }

    [Fact]
    public async Task ChangingTheArgumentsOfASandboxedServer_AndSaving_KeepsItsPolicyExactly()
    {
        var policy = Policy();
        var (vm, plugins, _, _) = Create(Stored(policy));

        vm.EditSelectedCommand.Execute(null);
        vm.EditArguments = "--stdio\n--verbose";
        await vm.SaveCommand.ExecuteAsync(null);

        AssertSamePolicy(policy, SavedDefinition(plugins).Sandbox);
    }

    [Fact]
    public async Task AnUncontainedServer_StaysUncontained_WhenEdited()
    {
        var (vm, plugins, _, _) = Create(Stored(sandbox: null));

        vm.EditSelectedCommand.Execute(null);
        vm.EditName = "files-renamed";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(SavedDefinition(plugins).Sandbox);
    }

    [Fact]
    public async Task SwitchingTheSandboxOff_AndSaving_SavesNoPolicy()
    {
        var (vm, plugins, _, _) = Create(Stored(Policy()));

        vm.EditSelectedCommand.Execute(null);
        vm.EditSandboxEnabled = false;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(SavedDefinition(plugins).Sandbox);
    }

    [Fact]
    public async Task SwitchingTheSandboxOnForAnUncontainedServer_ProposesItsFolder_AndSavesThePolicy()
    {
        var (vm, plugins, _, _) = Create(Stored(sandbox: null));

        vm.EditSelectedCommand.Execute(null);
        vm.EditSandboxEnabled = true;
        await vm.SaveCommand.ExecuteAsync(null);

        AssertSamePolicy(new McpSandboxPolicy([DefaultFolder("files")], [], false), SavedDefinition(plugins).Sandbox);
    }

    [Fact]
    public async Task ASandboxedServer_OnAHostThatCannotSandbox_StaysOn_KeepsItsPolicy_AndCanStillBeSwitchedOff()
    {
        var policy = Policy();
        var (vm, plugins, _, _) = Create(Stored(policy), MxcRuntimeState.Unsupported, "Not here.");

        vm.EditSelectedCommand.Execute(null);

        Assert.True(vm.EditSandboxEnabled);
        Assert.False(vm.CanEnableSandbox);
        Assert.True(vm.IsSandboxToggleEnabled);
        Assert.Equal("Not here.", vm.SandboxDisabledReason);

        await vm.SaveCommand.ExecuteAsync(null);
        AssertSamePolicy(policy, SavedDefinition(plugins).Sandbox);
    }

    [Fact]
    public void AnUncontainedServer_OnAHostThatCannotSandbox_HasADisabledToggle()
    {
        var (vm, _, _, _) = Create(Stored(sandbox: null), MxcRuntimeState.Unsupported, "Not here.");

        vm.EditSelectedCommand.Execute(null);

        Assert.False(vm.EditSandboxEnabled);
        Assert.False(vm.IsSandboxToggleEnabled);
    }

    [Fact]
    public async Task ChangingASandboxedServersCommandToAShim_KeepsThePolicy_AndShowsTheReason()
    {
        var policy = Policy();
        var (vm, plugins, _, _) = Create(Stored(policy));

        vm.EditSelectedCommand.Execute(null);
        vm.EditCommand = "npx";

        Assert.True(vm.EditSandboxEnabled);
        Assert.False(vm.CanEnableSandbox);
        Assert.True(vm.IsSandboxToggleEnabled);
        Assert.Equal("McpServers_Sandbox_Reason_Shim", vm.SandboxDisabledReason);

        await vm.SaveCommand.ExecuteAsync(null);
        AssertSamePolicy(policy, SavedDefinition(plugins).Sandbox);
    }

    [Fact]
    public async Task TestConnection_ProbesWithTheSandboxPolicy()
    {
        var policy = Policy();
        var (vm, plugins, _, _) = Create(Stored(policy));
        plugins.ProbeLocalMcpAsync(Arg.Any<LocalMcpDefinition>(), Arg.Any<CancellationToken>())
            .Returns(McpProbeResult.Failed("stub"));

        vm.EditSelectedCommand.Execute(null);
        await vm.TestConnectionCommand.ExecuteAsync(null);

        var probed = (LocalMcpDefinition)plugins.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPluginService.ProbeLocalMcpAsync))
            .GetArguments()[0]!;
        AssertSamePolicy(policy, probed.Sandbox);
    }

    [Fact]
    public void ClosingTheEditor_ClearsTheSandboxState()
    {
        var (vm, _, _, _) = Create(Stored(Policy(network: true)));
        vm.EditSelectedCommand.Execute(null);

        vm.CancelEditCommand.Execute(null);

        Assert.False(vm.EditSandboxEnabled);
        Assert.False(vm.EditSandboxAllowNetwork);
        Assert.Empty(vm.EditReadwritePaths);
        Assert.Empty(vm.EditReadonlyPaths);
    }

    // ---- folder lists ---------------------------------------------------------------------------------

    [Fact]
    public void AddingAndRemovingFolders_EditsTheRightList_AndIgnoresDuplicates()
    {
        var (vm, _, _, _) = Create(Stored(Policy()));
        vm.EditSelectedCommand.Execute(null);

        vm.NewReadwritePath = @"  ""C:\Data\more""  ";
        vm.AddReadwritePathCommand.Execute(null);
        vm.NewReadwritePath = @"c:\data\MORE";
        vm.AddReadwritePathCommand.Execute(null);
        vm.NewReadonlyPath = @"C:\Data\docs";
        vm.AddReadonlyPathCommand.Execute(null);
        vm.RemoveReadwritePathCommand.Execute(@"C:\Data\work");

        Assert.Equal([@"C:\Data\more"], vm.EditReadwritePaths);
        Assert.Equal([@"C:\Data\reference", @"C:\Data\docs"], vm.EditReadonlyPaths);
        Assert.Equal(string.Empty, vm.NewReadwritePath);
        Assert.False(vm.AddReadwritePathCommand.CanExecute(null));
    }

    [Fact]
    public void BrowsingForAFolder_AddsThePickedPath_AndACancelledPickerAddsNothing()
    {
        var (vm, _, _, dialogs) = Create(Stored(Policy()));
        dialogs.PromptSelectFolder(Arg.Any<string>(), Arg.Any<string?>()).Returns(@"C:\Picked", (string?)null);
        vm.EditSelectedCommand.Execute(null);

        vm.BrowseReadonlyFolderCommand.Execute(null);
        vm.BrowseReadonlyFolderCommand.Execute(null);

        Assert.Equal([@"C:\Data\reference", @"C:\Picked"], vm.EditReadonlyPaths);
    }

    [Fact]
    public void CreatingTheServerFolder_MakesItUnderTheSandboxRoot_AndAddsItToTheReadWriteList()
    {
        var (vm, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditName = "Files";
        vm.EditCommand = ExePath;
        vm.RemoveReadwritePathCommand.Execute(DefaultFolder("files"));
        Assert.False(Directory.Exists(DefaultFolder("files")));

        vm.CreateDefaultFolderCommand.Execute(null);

        Assert.True(Directory.Exists(DefaultFolder("files")));
        Assert.Equal([DefaultFolder("files")], vm.EditReadwritePaths);
    }

    [Fact]
    public async Task SavingADefaultedServer_CreatesNoFolder()
    {
        var (vm, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditName = "Quiet";
        vm.EditCommand = ExePath;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(DefaultFolder("quiet")));
    }

    [Fact]
    public void CreatingTheServerFolder_NeedsAName()
    {
        var (vm, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);

        Assert.False(vm.CreateDefaultFolderCommand.CanExecute(null));
        vm.EditName = "Files";
        Assert.True(vm.CreateDefaultFolderCommand.CanExecute(null));
    }

    // ---- the runtime download -------------------------------------------------------------------------

    [Fact]
    public void WhenTheRuntimeIsNotInstalled_ASandboxedEditorOffersTheDownload()
    {
        var (vm, _, _, _) = Create(Stored(Policy()), MxcRuntimeState.NotInstalled);

        vm.EditSelectedCommand.Execute(null);

        Assert.True(vm.ShowSandboxDownloadPrompt);
        Assert.False(vm.ShowSandboxDownloadProgress);
        Assert.Equal("McpServers_Sandbox_NotInstalled", vm.SandboxDownloadMessage);
        Assert.True(vm.DownloadSandboxRuntimeCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADownloadThatSucceeds_ClearsThePrompt()
    {
        var (vm, _, mxc, _) = Create(Stored(Policy()), MxcRuntimeState.NotInstalled);
        mxc.InstallRuntimeAsync(Arg.Any<IProgress<ModelDownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new MxcAvailabilityStatus(MxcRuntimeState.Ready));
        vm.EditSelectedCommand.Execute(null);

        await vm.DownloadSandboxRuntimeCommand.ExecuteAsync(null);

        Assert.Equal(MxcRuntimeState.Ready, vm.SandboxStatus.State);
        Assert.False(vm.ShowSandboxDownloadPrompt);
        Assert.False(vm.IsSandboxDownloading);
        Assert.Null(vm.SandboxDownloadError);
        Assert.True(vm.EditSandboxEnabled);
    }

    [Fact]
    public async Task ADownloadThatFails_ShowsTheReason_KeepsTheToggle_AndCanBeRetried()
    {
        var (vm, _, mxc, _) = Create(Stored(Policy()), MxcRuntimeState.NotInstalled);
        mxc.InstallRuntimeAsync(Arg.Any<IProgress<ModelDownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new MxcAvailabilityStatus(MxcRuntimeState.NotInstalled, "Hash mismatch."));
        vm.EditSelectedCommand.Execute(null);

        await vm.DownloadSandboxRuntimeCommand.ExecuteAsync(null);

        Assert.Equal("McpServers_Sandbox_DownloadFailedWithReason:Hash mismatch.", vm.SandboxDownloadError);
        Assert.True(vm.EditSandboxEnabled);
        Assert.True(vm.ShowSandboxDownloadPrompt);
        Assert.True(vm.DownloadSandboxRuntimeCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADownloadThatThrows_ShowsTheMessage()
    {
        var (vm, _, mxc, _) = Create(Stored(Policy()), MxcRuntimeState.NotInstalled);
        mxc.InstallRuntimeAsync(Arg.Any<IProgress<ModelDownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<MxcAvailabilityStatus>>(_ => throw new HttpRequestException("offline"));
        vm.EditSelectedCommand.Execute(null);

        await vm.DownloadSandboxRuntimeCommand.ExecuteAsync(null);

        Assert.Equal("McpServers_Sandbox_DownloadFailedWithReason:offline", vm.SandboxDownloadError);
        Assert.False(vm.IsSandboxDownloading);
    }

    [Fact]
    public void WhileTheRuntimeDownloadsElsewhere_TheEditorShowsProgress_AndOffersNoSecondDownload()
    {
        var (vm, _, _, _) = Create(Stored(Policy()), MxcRuntimeState.Downloading);

        vm.EditSelectedCommand.Execute(null);

        Assert.True(vm.ShowSandboxDownloadProgress);
        Assert.False(vm.ShowSandboxDownloadPrompt);
        Assert.False(vm.DownloadSandboxRuntimeCommand.CanExecute(null));
    }

    [Fact]
    public void AStatusChangeFromTheService_UpdatesTheEditor()
    {
        var (vm, _, mxc, _) = Create(Stored(sandbox: null));
        vm.EditSelectedCommand.Execute(null);
        Assert.True(vm.IsSandboxToggleEnabled);

        mxc.StatusChanged += Raise.Event<EventHandler<MxcAvailabilityStatus>>(
            mxc, new MxcAvailabilityStatus(MxcRuntimeState.Unsupported, "Gone."));

        Assert.False(vm.IsSandboxToggleEnabled);
        Assert.Equal("Gone.", vm.SandboxDisabledReason);
    }

    // ---- list row and detail pane ----------------------------------------------------------------------

    [Fact]
    public void ASandboxedServersRow_SaysSandboxedWithItsFolderCounts()
    {
        var (vm, _, _, _) = Create(Stored(Policy()));

        Assert.Equal("McpServers_Sandbox_Summary_NetworkOff:1,1", vm.Servers.Single().SandboxText);
    }

    [Fact]
    public void ASandboxedServersRow_SaysWhenTheNetworkIsOn()
    {
        var (vm, _, _, _) = Create(Stored(Policy(network: true)));

        Assert.Equal("McpServers_Sandbox_Summary_NetworkOn:1,1", vm.Servers.Single().SandboxText);
    }

    [Fact]
    public void AnUncontainedServersRow_SaysNotSandboxed()
    {
        var (vm, _, _, _) = Create(Stored(sandbox: null));

        Assert.Equal("McpServers_Sandbox_NotSandboxed", vm.Servers.Single().SandboxText);
    }

    [Fact]
    public void TheDetailPaneListsTheGrantedFolders_OfTheSelectedSandboxedServer()
    {
        var (vm, _, _, _) = Create(Stored(new McpSandboxPolicy([@"C:\a", @"C:\b"], [@"C:\c"], false)));

        Assert.True(vm.HasSelectedSandbox);
        Assert.Equal("McpServers_Sandbox_Summary_NetworkOff:2,1", vm.SelectedSandboxSummary);
        Assert.Equal($@"C:\a{Environment.NewLine}C:\b", vm.SelectedSandboxReadwrite);
        Assert.Equal(@"C:\c", vm.SelectedSandboxReadonly);
    }

    [Fact]
    public void TheDetailPane_ShowsNoFolders_ForAZeroFolderPolicyAndForAnUncontainedServer()
    {
        var (zero, _, _, _) = Create(Stored(McpSandboxPolicy.Empty));
        var (open, _, _, _) = Create(Stored(sandbox: null));

        Assert.True(zero.HasSelectedSandbox);
        Assert.Null(zero.SelectedSandboxReadwrite);
        Assert.Null(zero.SelectedSandboxReadonly);
        Assert.False(open.HasSelectedSandbox);
        Assert.Equal("McpServers_Sandbox_NotSandboxed", open.SelectedSandboxSummary);
        Assert.Null(open.SelectedSandboxAlsoReadable);
    }

    [Fact]
    public void ADisabledSandboxedServer_StillShowsItsSandbox()
    {
        var (vm, plugins, _, _) = Create(Stored(Policy()));
        plugins.GetLocalMcpPlugins().Returns([new SyncPlugin
        {
            Id = ServerId, Kind = "mcp_server", Name = "files", UserEnabled = false,
            ConfigJson = """{"source":"local","transport":"stdio","command":"x"}"""
        }]);

        plugins.PluginsChanged += Raise.Event<EventHandler>(plugins, EventArgs.Empty);

        Assert.Equal("McpServers_Sandbox_Summary_NetworkOff:1,1", vm.Servers.Single().SandboxText);
    }

    [Fact]
    public void AFailClosedRefusal_ShowsItsReasonVerbatimInTheDetailNotice()
    {
        const string reason = "Pia can't sandbox this server: 3 file(s) in its folder belong to an administrator.";
        var (vm, _, _, _) = Create(Stored(Policy()), status: new LocalMcpStatus(false, [], [], reason));

        Assert.True(vm.Servers.Single().IsFailed);
        Assert.Equal(reason, vm.SelectedError);
        Assert.Contains(reason, vm.Servers.Single().StatusText);
    }

    // ---- the "created when the server starts" hint ----------------------------------------------------

    [Fact]
    public void AMissingFolderUnderTheSandboxRoot_ShowsTheCreatedAtStartHint_UntilItIsCreated()
    {
        var (vm, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditName = "Hinted";
        vm.EditCommand = ExePath;

        Assert.True(vm.ShowSandboxFolderCreatedHint);

        vm.CreateDefaultFolderCommand.Execute(null);

        Assert.False(vm.ShowSandboxFolderCreatedHint);
    }

    [Fact]
    public void AMissingFolderElsewhere_IsNotCoveredByTheHint()
    {
        var (vm, _, _, _) = Create(Stored(Policy()));

        vm.EditSelectedCommand.Execute(null);

        Assert.False(vm.ShowSandboxFolderCreatedHint);
    }

    // ---- the path guard, live ---------------------------------------------------------------------------

    private static string MakeFolders(params string[] relative)
    {
        var root = Directory.CreateTempSubdirectory("pia-guard-").FullName;
        foreach (var path in relative)
            Directory.CreateDirectory(Path.Combine(root, path));
        return root;
    }

    [Fact]
    public void AReadOnlyFolderInsideAReadWriteFolder_IsFlaggedAsItIsAdded_AndClearsWhenRemoved()
    {
        var root = MakeFolders(@"work\inner");
        try
        {
            var work = Path.Combine(root, "work");
            var inner = Path.Combine(work, "inner");
            var (vm, _, _, _) = Create(Stored(new McpSandboxPolicy([work], [], false)));
            vm.EditSelectedCommand.Execute(null);
            Assert.Null(vm.SandboxGuardMessage);

            vm.NewReadonlyPath = inner;
            vm.AddReadonlyPathCommand.Execute(null);

            Assert.NotNull(vm.SandboxGuardMessage);
            Assert.Contains(inner, vm.SandboxGuardMessage);

            vm.RemoveReadonlyPathCommand.Execute(inner);

            Assert.Null(vm.SandboxGuardMessage);
        }
        finally
        {
            TempPath.Remove(root);
        }
    }

    [Fact]
    public void AMissingReadOnlyFolder_IsFlaggedWithItsPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pia-missing-" + Guid.NewGuid().ToString("N"));
        var (vm, _, _, _) = Create(Stored(new McpSandboxPolicy([], [missing], false)));

        vm.EditSelectedCommand.Execute(null);

        Assert.NotNull(vm.SandboxGuardMessage);
        Assert.Contains(missing, vm.SandboxGuardMessage);
    }

    [Fact]
    public void ANotRootedFolder_IsFlagged_OnlyWhileTheSandboxIsOn()
    {
        var (vm, _, _, _) = Create(Stored(sandbox: null));
        vm.EditSelectedCommand.Execute(null);

        vm.NewReadonlyPath = @"relative\folder";
        vm.AddReadonlyPathCommand.Execute(null);
        Assert.Null(vm.SandboxGuardMessage);

        vm.EditSandboxEnabled = true;
        Assert.NotNull(vm.SandboxGuardMessage);
        Assert.Contains(@"relative\folder", vm.SandboxGuardMessage);

        vm.EditSandboxEnabled = false;
        Assert.Null(vm.SandboxGuardMessage);
    }

    [Fact]
    public void AZeroFolderPolicy_IsAcceptable()
    {
        var (vm, _, _, _) = Create(Stored(McpSandboxPolicy.Empty));

        vm.EditSelectedCommand.Execute(null);

        Assert.True(vm.EditSandboxEnabled);
        Assert.Null(vm.SandboxGuardMessage);
    }

    [Fact]
    public void ANewServersMissingDefaultFolder_IsAcceptable_BecausePiaCreatesIt()
    {
        var (vm, _, _, _) = Create();
        vm.AddServerCommand.Execute(null);
        vm.EditName = "Fresh";
        vm.EditCommand = ExePath;

        Assert.True(vm.ShowSandboxFolderCreatedHint);
        Assert.Null(vm.SandboxGuardMessage);
    }

    [Fact]
    public async Task SavingIsNotBlockedByAGuardIssue()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pia-missing-" + Guid.NewGuid().ToString("N"));
        var (vm, plugins, _, _) = Create(Stored(new McpSandboxPolicy([], [missing], false)));
        vm.EditSelectedCommand.Execute(null);
        Assert.NotNull(vm.SandboxGuardMessage);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal([missing], SavedDefinition(plugins).Sandbox!.ReadonlyPaths);
    }
}
