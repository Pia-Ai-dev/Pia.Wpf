using System.IO;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class RuntimeDetectorTests
{
    private static readonly string ProgramFiles = Path.Combine(@"C:\", "Program Files");
    private static readonly string UserProfile = Path.Combine(@"C:\", "Users", "u");
    private static readonly string LocalAppData = Path.Combine(UserProfile, "AppData", "Local");
    private static readonly string RoamingAppData = Path.Combine(UserProfile, "AppData", "Roaming");
    private static readonly string ProgramFilesNode = Path.Combine(ProgramFiles, "nodejs");
    private static readonly string ProgramFilesDotnet = Path.Combine(ProgramFiles, "dotnet");
    private static readonly string UserNode = Path.Combine(LocalAppData, "Programs", "nodejs");
    private static readonly string UserDotnet = Path.Combine(LocalAppData, "Microsoft", "dotnet");
    private static readonly string ToolsNode = Path.Combine(@"D:\", "tools", "node");

    private readonly FakeRuntimeHost _host = new();
    private readonly FakeRootVerifier _roots = new();

    private RuntimeResolution Detect(
        RunnerKind kind,
        string? hint = null,
        string? scriptOrDll = null,
        CancellationToken? cancellationToken = null)
    {
        var uvx = kind == RunnerKind.Uvx ? new UvxSpec("mcp-server-time", "mcp-server-time", [], null) : null;
        var runner = new RunnerRequest(kind, hint, [], scriptOrDll, [], uvx);
        return new RuntimeDetector(_host, _roots).Detect(runner, cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private void AddNodeWithoutCliScripts(string root)
    {
        _host.WithFile(Path.Combine(root, "node.exe"));
        _host.WithFile(Path.Combine(root, "node_modules", "npm", "package.json"), """{"name": "npm", "version": "10.9.8"}""");
        _host.DaclWritable.Add(root);
    }

    private string AddScript(string path)
    {
        _host.WithFile(path);
        return path;
    }

    private string AddDll(string path, string? runtimeConfig)
    {
        _host.WithFile(path);
        if (runtimeConfig is not null)
            _host.WithFile(Path.ChangeExtension(path, ".runtimeconfig.json"), runtimeConfig);
        return path;
    }

    private static string RuntimeConfig(params (string Name, string Version)[] frameworks)
    {
        var entries = frameworks.Select(f => $$"""{"name": "{{f.Name}}", "version": "{{f.Version}}"}""");
        return """{"runtimeOptions": {"frameworks": [""" + string.Join(", ", entries) + "]}}";
    }

    private static void AssertReady(RuntimeResolution resolution, string root, params string[] toolchainRoots)
    {
        Assert.Equal(RuntimeOutcome.Ready, resolution.Outcome);
        Assert.Equal(RuntimeIssue.None, resolution.Issue);
        Assert.NotNull(resolution.Install);
        Assert.Equal(root, resolution.Install.Root);
        Assert.Equal(toolchainRoots, resolution.ToolchainRoots);
    }

    [Fact]
    public void PathOrder_TheFirstValidEntryWins_WithVersionsFromMetadata()
    {
        var second = Path.Combine(@"D:\", "second", "node");
        _host.WithNode(ToolsNode, version: "22.23.2", npmVersion: "10.9.8").WithNode(second);
        _host.PathEntries.AddRange([ToolsNode, second]);

        var resolution = Detect(RunnerKind.Npx);

        AssertReady(resolution, ToolsNode, ToolsNode);
        var install = resolution.Install!;
        Assert.Equal(RuntimeFamily.Node, install.Family);
        Assert.Equal(Path.Combine(ToolsNode, "node.exe"), install.Executable);
        Assert.Equal("22.23.2", install.Version);
        Assert.Equal("10.9.8", install.NpmVersion);
        Assert.Equal(Path.Combine(ToolsNode, "node_modules", "npm", "bin", "npx-cli.js"), install.EntryScript);
        Assert.False(install.ReadableAlready);
        Assert.False(install.AdminOwned);
        Assert.Null(resolution.ProgramFolder);
        Assert.Equal([(ToolsNode, false)], _roots.Calls);
    }

    [Fact]
    public void AdminOwnedProgramFilesNode_ThenUserNode_PicksTheUserNode()
    {
        _host.WithNode(ProgramFilesNode, grantable: false).WithNode(UserNode);
        _host.PathEntries.Add(ProgramFilesNode);

        AssertReady(Detect(RunnerKind.Npx), UserNode, UserNode);
    }

    [Fact]
    public void OnlyAnAdminOwnedProgramFilesNode_NeedsAdminFix_EvenWhenItsDaclLooksWritable()
    {
        _host.WithNode(ProgramFilesNode, grantable: true);
        _host.PathEntries.Add(ProgramFilesNode);

        var resolution = Detect(RunnerKind.Npx);

        Assert.Equal(RuntimeOutcome.NeedsAdminFix, resolution.Outcome);
        Assert.Equal(RuntimeIssue.NodeAdminOwned, resolution.Issue);
        Assert.Equal(ProgramFilesNode, resolution.Install!.Root);
        Assert.True(resolution.Install.AdminOwned);
        Assert.Empty(resolution.ToolchainRoots);
        Assert.Empty(_roots.Calls);
    }

    [Fact]
    public void AFolderTheUserCannotReDacl_IsAdminOwned()
    {
        _host.WithNode(ToolsNode, grantable: false);
        _host.PathEntries.Add(ToolsNode);

        var resolution = Detect(RunnerKind.NodeScript, scriptOrDll: AddScript(Path.Combine(@"D:\", "mcp", "server.js")));

        Assert.Equal(RuntimeOutcome.NeedsAdminFix, resolution.Outcome);
        Assert.Equal(RuntimeIssue.NodeAdminOwned, resolution.Issue);
        Assert.True(resolution.Install!.AdminOwned);
    }

    [Fact]
    public void TheFirstAdminOwnedInstallWinsOverALaterRefusedOne()
    {
        _host.WithNode(ProgramFilesNode).WithNode(UserProfile);
        _host.PathEntries.AddRange([ProgramFilesNode, UserProfile]);
        _roots.Issues[UserProfile] = RuntimeIssue.ToolchainTooWide;

        var resolution = Detect(RunnerKind.Npx);

        Assert.Equal(RuntimeOutcome.NeedsAdminFix, resolution.Outcome);
        Assert.Equal(ProgramFilesNode, resolution.Install!.Root);
    }

    [Fact]
    public void ReadableProgramFilesNode_IsReadyWithoutRoots()
    {
        _host.WithNode(ProgramFilesNode, readable: true, grantable: false);
        _host.PathEntries.Add(ProgramFilesNode);

        var resolution = Detect(RunnerKind.NpmExec);

        AssertReady(resolution, ProgramFilesNode);
        Assert.True(resolution.Install!.ReadableAlready);
        Assert.Equal(Path.Combine(ProgramFilesNode, "node_modules", "npm", "bin", "npm-cli.js"), resolution.Install.EntryScript);
        Assert.Empty(_roots.Calls);
    }

    [Fact]
    public void ARootThatIsReadableButNotItsEntryScript_IsNotReadableAlready()
    {
        _host.WithNode(ToolsNode);
        _host.Readable.UnionWith([ToolsNode, Path.Combine(ToolsNode, "node.exe")]);
        _host.PathEntries.Add(ToolsNode);

        var resolution = Detect(RunnerKind.Npx);

        AssertReady(resolution, ToolsNode, ToolsNode);
        Assert.False(resolution.Install!.ReadableAlready);
    }

    [Fact]
    public void NodeScript_NeedsOnlyNodeAndNpmsPackageJson()
    {
        AddNodeWithoutCliScripts(ToolsNode);
        _host.PathEntries.Add(ToolsNode);
        var script = AddScript(Path.Combine(@"D:\", "mcp", "server.js"));

        var resolution = Detect(RunnerKind.NodeScript, scriptOrDll: script);

        AssertReady(resolution, ToolsNode, ToolsNode);
        Assert.Null(resolution.Install!.EntryScript);
        Assert.Equal(RuntimeIssue.NodeNotFound, Detect(RunnerKind.Npx).Issue);
    }

    [Fact]
    public void ANodeWithoutNpm_AScoopStyleShimsFolder_IsSkippedForTheNextCandidate()
    {
        var shims = Path.Combine(UserProfile, "scoop", "shims");
        _host.WithNode(shims, withNpm: false).WithNode(ToolsNode);
        _host.PathEntries.AddRange([shims, ToolsNode]);

        AssertReady(Detect(RunnerKind.Npx), ToolsNode, ToolsNode);
        Assert.Equal([(ToolsNode, false)], _roots.Calls);
    }

    [Fact]
    public void NpmExec_RequiresNpmCli_AndNpx_RequiresNpxCli()
    {
        AddNodeWithoutCliScripts(ToolsNode);
        _host.WithFile(Path.Combine(ToolsNode, "node_modules", "npm", "bin", "npx-cli.js"));
        _host.PathEntries.Add(ToolsNode);

        Assert.Equal(RuntimeOutcome.Ready, Detect(RunnerKind.Npx).Outcome);
        var npmExec = Detect(RunnerKind.NpmExec);
        Assert.Equal(RuntimeOutcome.NotFound, npmExec.Outcome);
        Assert.Equal(RuntimeIssue.NodeNotFound, npmExec.Issue);
    }

    [Fact]
    public void ReadableProgramFilesDotnet_WithSdk10_IsReadyWithoutRoots()
    {
        _host.WithDotnet(ProgramFilesDotnet, ["10.0.402"], ["10.0.3"], readable: true, grantable: false);
        _host.PathEntries.Add(ProgramFilesDotnet);

        var resolution = Detect(RunnerKind.Dnx);

        AssertReady(resolution, ProgramFilesDotnet);
        var install = resolution.Install!;
        Assert.Equal(RuntimeFamily.Dotnet, install.Family);
        Assert.Equal("10.0.402", install.Version);
        Assert.True(install.ReadableAlready);
        Assert.Equal(Path.Combine(ProgramFilesDotnet, "dotnet.exe"), install.Executable);
        Assert.Equal(Path.Combine(ProgramFilesDotnet, "sdk", "10.0.402", "dotnet.dll"), install.EntryScript);
        Assert.Null(install.NpmVersion);
    }

    [Fact]
    public void Sdk9OnlyUnderProgramFiles_ThenAUserSdk10_PicksTheUserRoot()
    {
        _host.WithDotnet(ProgramFilesDotnet, ["9.0.300"], ["9.0.6"], readable: true, grantable: false);
        _host.WithDotnet(UserDotnet, ["10.0.100"], ["10.0.0"]);
        _host.PathEntries.Add(ProgramFilesDotnet);

        var resolution = Detect(RunnerKind.Dnx);

        AssertReady(resolution, UserDotnet, UserDotnet);
        Assert.Equal("10.0.100", resolution.Install!.Version);
    }

    [Fact]
    public void Sdk11Prerelease_BeatsSdk10()
    {
        _host.WithDotnet(UserDotnet, ["10.0.402", "11.0.100-rc.2", "9.0.300"], ["10.0.3"]);

        var resolution = Detect(RunnerKind.Dnx);

        Assert.Equal("11.0.100-rc.2", resolution.Install!.Version);
        Assert.Equal(Path.Combine(UserDotnet, "sdk", "11.0.100-rc.2", "dotnet.dll"), resolution.Install.EntryScript);
    }

    [Fact]
    public void AnSdkFolderWithoutDotnetDll_IsNotPicked()
    {
        _host.WithDotnet(UserDotnet, ["10.0.402"], ["10.0.3"]);
        _host.WithDirectory(Path.Combine(UserDotnet, "sdk", "11.0.100"));

        Assert.Equal("10.0.402", Detect(RunnerKind.Dnx).Install!.Version);
    }

    [Fact]
    public void DnxOnARootWithOnlySdk9_IsDotnetSdkMissing()
    {
        _host.WithDotnet(UserDotnet, ["9.0.300", "8.0.400"], ["9.0.6"]);

        var resolution = Detect(RunnerKind.Dnx);

        Assert.Equal(RuntimeOutcome.NotFound, resolution.Outcome);
        Assert.Equal(RuntimeIssue.DotnetSdkMissing, resolution.Issue);
        Assert.Null(resolution.Install);
    }

    [Fact]
    public void ADllAskingForNet10_OnARootWithOnly8_IsDotnetRuntimeMissing()
    {
        _host.WithDotnet(UserDotnet, [], ["8.0.11"]);
        var dll = AddDll(Path.Combine(@"D:\", "app", "server.dll"), RuntimeConfig(("Microsoft.NETCore.App", "10.0.0")));

        var resolution = Detect(RunnerKind.DotnetDll, scriptOrDll: dll);

        Assert.Equal(RuntimeOutcome.NotFound, resolution.Outcome);
        Assert.Equal(RuntimeIssue.DotnetRuntimeMissing, resolution.Issue);
        Assert.Null(resolution.Install);
    }

    [Fact]
    public void ADllAskingForNet10_TakesALaterRuntimeOfTheSameMajor_AndReportsTheHighestRuntime()
    {
        _host.WithDotnet(UserDotnet, [], ["8.0.11", "10.0.3", "10.0.12"]);
        var dll = AddDll(Path.Combine(@"D:\", "app", "server.dll"), RuntimeConfig(("Microsoft.NETCore.App", "10.0.1")));

        var resolution = Detect(RunnerKind.DotnetDll, scriptOrDll: dll);

        AssertReady(resolution, UserDotnet, UserDotnet);
        Assert.Equal("10.0.12", resolution.Install!.Version);
        Assert.Null(resolution.Install.EntryScript);
        Assert.Equal(Path.Combine(@"D:\", "app"), resolution.ProgramFolder);
        Assert.Contains((Path.Combine(@"D:\", "app"), true), _roots.Calls);
    }

    [Fact]
    public void ADllNeedingAspNetCore_SkipsARootWithoutIt()
    {
        var other = Path.Combine(@"D:\", "dotnet");
        _host.WithDotnet(other, [], ["10.0.3"]);
        _host.WithDotnet(UserDotnet, [], ["10.0.3"]);
        _host.WithDirectory(Path.Combine(UserDotnet, "shared", "Microsoft.AspNetCore.App", "10.0.3"));
        _host.PathEntries.Add(other);
        var dll = AddDll(
            Path.Combine(@"D:\", "app", "web.dll"),
            RuntimeConfig(("Microsoft.NETCore.App", "10.0.0"), ("Microsoft.AspNetCore.App", "10.0.0")));

        AssertReady(Detect(RunnerKind.DotnetDll, scriptOrDll: dll), UserDotnet, UserDotnet);
    }

    [Fact]
    public void ADllWithoutRuntimeConfig_TakesAnyNetCoreRuntime()
    {
        _host.WithDotnet(UserDotnet, [], ["8.0.11"]);
        var dll = AddDll(Path.Combine(@"D:\", "app", "server.dll"), runtimeConfig: null);

        var resolution = Detect(RunnerKind.DotnetDll, scriptOrDll: dll);

        AssertReady(resolution, UserDotnet, UserDotnet);
        Assert.Equal("8.0.11", resolution.Install!.Version);
    }

    [Fact]
    public void ADotnetWithoutAnyRuntime_IsSkippedForADll()
    {
        _host.WithDotnet(UserDotnet, ["10.0.100"], []);
        var dll = AddDll(Path.Combine(@"D:\", "app", "server.dll"), runtimeConfig: null);

        Assert.Equal(RuntimeIssue.DotnetRuntimeMissing, Detect(RunnerKind.DotnetDll, scriptOrDll: dll).Issue);
    }

    [Fact]
    public void Uvx_IsReadyWithoutRoots_EvenUnderProgramFiles()
    {
        var uvFolder = Path.Combine(ProgramFiles, "uv");
        _host.WithUv(uvFolder, version: "0.13.1");
        _host.PathEntries.Add(uvFolder);

        var resolution = Detect(RunnerKind.Uvx);

        AssertReady(resolution, uvFolder);
        var install = resolution.Install!;
        Assert.Equal(RuntimeFamily.Uv, install.Family);
        Assert.Equal(Path.Combine(uvFolder, "uv.exe"), install.Executable);
        Assert.Equal("0.13.1", install.Version);
        Assert.Null(install.EntryScript);
        Assert.False(install.AdminOwned);
        Assert.Empty(_roots.Calls);
    }

    [Fact]
    public void AWinGetLinksUv_ResolvesToTheLinkTarget()
    {
        var links = Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links");
        var package = Path.Combine(LocalAppData, "Microsoft", "WinGet", "Packages", "astral-sh.uv_Microsoft.Winget.Source_8wekyb3d8bbwe");
        _host.WithDirectory(links).WithUv(package);
        _host.FinalPaths[Path.Combine(links, "uv.exe")] = Path.Combine(package, "uv.exe");

        var resolution = Detect(RunnerKind.Uvx);

        AssertReady(resolution, package);
        Assert.Equal(Path.Combine(package, "uv.exe"), resolution.Install!.Executable);
    }

    [Fact]
    public void ScoopsUvShim_PointsAtTheUvFolder()
    {
        var uvFolder = Path.Combine(UserProfile, "scoop", "apps", "uv", "current");
        _host.WithUv(uvFolder);
        _host.WithFile(
            Path.Combine(UserProfile, "scoop", "shims", "uv.shim"),
            "path = \"" + Path.Combine(uvFolder, "uv.exe") + "\"\r\nargs = \r\n");

        AssertReady(Detect(RunnerKind.Uvx), uvFolder);
    }

    [Fact]
    public void TheOverride_ReplacesPathAndTheFixedLocations()
    {
        var overridden = Path.Combine(@"D:\", "override", "node");
        _host.WithNode(ToolsNode).WithNode(UserNode).WithNode(overridden);
        _host.PathEntries.Add(ToolsNode);
        _host.Override = [overridden];

        AssertReady(Detect(RunnerKind.Npx), overridden, overridden);

        _host.Override = [Path.Combine(@"D:\", "empty")];
        Assert.Equal(RuntimeOutcome.NotFound, Detect(RunnerKind.Npx).Outcome);
    }

    [Fact]
    public void AHint_IsTriedFirst()
    {
        _host.WithNode(ProgramFilesNode, readable: true, grantable: false).WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);

        var resolution = Detect(RunnerKind.Npx, hint: Path.Combine(ProgramFilesNode, "npx.cmd"));

        AssertReady(resolution, ProgramFilesNode);
    }

    [Fact]
    public void AHint_IsTriedBeforeTheOverride()
    {
        var overridden = Path.Combine(@"D:\", "override", "node");
        _host.WithNode(ToolsNode).WithNode(overridden);
        _host.Override = [overridden];

        AssertReady(Detect(RunnerKind.Npx, hint: Path.Combine(ToolsNode, "npx.cmd")), ToolsNode, ToolsNode);
    }

    [Fact]
    public void AnUnusableHint_FallsBackToTheUsualOrder()
    {
        var hintFolder = Path.Combine(@"D:\", "hint");
        _host.WithNode(hintFolder, withNpm: false).WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);

        AssertReady(Detect(RunnerKind.Npx, hint: Path.Combine(hintFolder, "npx.cmd")), ToolsNode, ToolsNode);
        AssertReady(Detect(RunnerKind.Npx, hint: Path.Combine(@"D:\", "missing", "npx.cmd")), ToolsNode, ToolsNode);
    }

    [Fact]
    public void ANodeAtTheProfileRoot_IsRefusedAsTooWide_AndTheNextCandidateWins()
    {
        _host.WithNode(UserProfile).WithNode(ToolsNode);
        _host.PathEntries.AddRange([UserProfile, ToolsNode]);
        _roots.Issues[UserProfile] = RuntimeIssue.ToolchainTooWide;

        AssertReady(Detect(RunnerKind.Npx), ToolsNode, ToolsNode);
        Assert.Equal([(UserProfile, false), (ToolsNode, false)], _roots.Calls);
    }

    [Fact]
    public void OnlyARefusedNode_IsRefusedWithItsIssue()
    {
        _host.WithNode(UserProfile);
        _host.PathEntries.Add(UserProfile);
        _roots.Issues[UserProfile] = RuntimeIssue.ToolchainTooWide;

        var resolution = Detect(RunnerKind.Npx);

        Assert.Equal(RuntimeOutcome.Refused, resolution.Outcome);
        Assert.Equal(RuntimeIssue.ToolchainTooWide, resolution.Issue);
        Assert.Equal(UserProfile, resolution.Install!.Root);
        Assert.False(resolution.Install.AdminOwned);
        Assert.Empty(resolution.ToolchainRoots);
    }

    [Theory]
    [InlineData(RunnerKind.Npx, RuntimeIssue.NodeNotFound)]
    [InlineData(RunnerKind.NpmExec, RuntimeIssue.NodeNotFound)]
    [InlineData(RunnerKind.NodeScript, RuntimeIssue.NodeNotFound)]
    [InlineData(RunnerKind.Dnx, RuntimeIssue.DotnetSdkMissing)]
    [InlineData(RunnerKind.DotnetDll, RuntimeIssue.DotnetRuntimeMissing)]
    [InlineData(RunnerKind.Uvx, RuntimeIssue.UvNotFound)]
    public void NothingFound_GivesTheNotFoundIssueOfTheKind(RunnerKind kind, RuntimeIssue issue)
    {
        var program = kind switch
        {
            RunnerKind.NodeScript => AddScript(Path.Combine(@"D:\", "mcp", "server.js")),
            RunnerKind.DotnetDll => AddDll(Path.Combine(@"D:\", "app", "server.dll"), runtimeConfig: null),
            _ => null
        };

        var resolution = Detect(kind, scriptOrDll: program);

        Assert.Equal(RuntimeOutcome.NotFound, resolution.Outcome);
        Assert.Equal(issue, resolution.Issue);
        Assert.Null(resolution.Install);
        Assert.Empty(resolution.ToolchainRoots);
    }

    [Fact]
    public void WindowsAppsAliases_AreSkipped()
    {
        var windowsApps = Path.Combine(LocalAppData, "Microsoft", "WindowsApps");
        _host.WithNode(windowsApps).WithNode(ToolsNode);
        _host.PathEntries.AddRange([windowsApps, ToolsNode]);

        AssertReady(Detect(RunnerKind.Npx), ToolsNode, ToolsNode);
    }

    [Fact]
    public void VoltaShimFolders_AreSkipped()
    {
        var voltaBin = Path.Combine(@"D:\", "volta", "bin");
        _host.WithNode(voltaBin).WithFile(Path.Combine(voltaBin, "volta-shim.exe")).WithNode(ToolsNode);
        _host.PathEntries.AddRange([voltaBin, ToolsNode]);

        AssertReady(Detect(RunnerKind.Npx), ToolsNode, ToolsNode);
    }

    [Fact]
    public void ARootUnderTheWindowsFolder_IsSkippedAndNotRemembered()
    {
        var windowsNode = Path.Combine(@"C:\", "Windows", "node");
        _host.WithNode(windowsNode, grantable: false);
        _host.PathEntries.Add(windowsNode);

        Assert.Equal(RuntimeOutcome.NotFound, Detect(RunnerKind.Npx).Outcome);
    }

    [Fact]
    public void TheSameFolderTwice_IsProbedOnce()
    {
        var link = Path.Combine(@"D:\", "link");
        _host.WithNode(UserProfile);
        _host.FinalPaths[link] = UserProfile;
        _host.PathEntries.AddRange([UserProfile, UserProfile + "\\", link, UserProfile.ToUpperInvariant()]);
        _roots.Issues[UserProfile] = RuntimeIssue.ToolchainTooWide;

        Detect(RunnerKind.Npx);

        Assert.Single(_roots.Calls);
    }

    [Fact]
    public void ALinkedNodeExe_RootsAtTheFolderOfItsTarget()
    {
        var linkFolder = Path.Combine(@"D:\", "bin");
        var target = Path.Combine(@"D:\", "node-versions", "v22.1.0");
        _host.WithDirectory(linkFolder).WithNode(target);
        _host.FinalPaths[Path.Combine(linkFolder, "node.exe")] = Path.Combine(target, "node.exe");
        _host.PathEntries.Add(linkFolder);

        var resolution = Detect(RunnerKind.Npx);

        AssertReady(resolution, target, target);
        Assert.Equal(Path.Combine(target, "node.exe"), resolution.Install!.Executable);
    }

    [Fact]
    public void NvmSymlink_RootsAtTheVersionFolder()
    {
        var symlink = Path.Combine(@"C:\", "nvm4w", "nodejs");
        var version = Path.Combine(LocalAppData, "nvm", "v22.1.0");
        _host.RegistryEnvironment["NVM_SYMLINK"] = symlink;
        _host.FinalPaths[symlink] = version;
        _host.WithNode(version);

        AssertReady(Detect(RunnerKind.Npx), version, version);
    }

    [Fact]
    public void FnmDefaultAlias_RootsAtTheInstallationFolder()
    {
        var alias = Path.Combine(RoamingAppData, "fnm", "aliases", "default");
        var installation = Path.Combine(RoamingAppData, "fnm", "node-versions", "v22.1.0", "installation");
        _host.FinalPaths[alias] = installation;
        _host.WithNode(installation);

        AssertReady(Detect(RunnerKind.Npx), installation, installation);
    }

    [Fact]
    public void FnmDir_ReplacesTheDefaultFnmFolder()
    {
        var fnmDir = Path.Combine(@"D:\", "fnm");
        var installation = Path.Combine(fnmDir, "node-versions", "v20.0.0", "installation");
        _host.RegistryEnvironment["FNM_DIR"] = fnmDir;
        _host.FinalPaths[Path.Combine(fnmDir, "aliases", "default")] = installation;
        _host.WithNode(installation);

        AssertReady(Detect(RunnerKind.Npx), installation, installation);
    }

    [Fact]
    public void Volta_RootsAtTheImageOfTheDefaultNode()
    {
        var volta = Path.Combine(LocalAppData, "Volta");
        var image = Path.Combine(volta, "tools", "image", "node", "20.11.0");
        _host.WithFile(Path.Combine(volta, "tools", "user", "platform.json"), """{"node": {"runtime": "20.11.0", "npm": null}, "pnpm": null}""");
        _host.WithNode(image);

        AssertReady(Detect(RunnerKind.Npx), image, image);
    }

    [Fact]
    public void Volta_WithAPathInsteadOfAVersion_IsIgnored()
    {
        var volta = Path.Combine(LocalAppData, "Volta");
        _host.WithFile(Path.Combine(volta, "tools", "user", "platform.json"), """{"node": {"runtime": "D:\\tools\\node"}}""");
        _host.WithNode(ToolsNode);

        Assert.Equal(RuntimeOutcome.NotFound, Detect(RunnerKind.Npx).Outcome);
    }

    [Fact]
    public void ScoopNodeLts_RootsAtTheCurrentVersion()
    {
        var current = Path.Combine(UserProfile, "scoop", "apps", "nodejs-lts", "current");
        var version = Path.Combine(UserProfile, "scoop", "apps", "nodejs-lts", "22.1.0");
        _host.FinalPaths[current] = version;
        _host.WithNode(version);

        AssertReady(Detect(RunnerKind.Npx), version, version);
    }

    [Fact]
    public void WinGetNode_TakesTheNewestVersionFolder()
    {
        var packages = Path.Combine(LocalAppData, "Microsoft", "WinGet", "Packages");
        var lts = Path.Combine(packages, "OpenJS.NodeJS.LTS_Microsoft.Winget.Source_8wekyb3d8bbwe");
        var current = Path.Combine(packages, "OpenJS.NodeJS_Microsoft.Winget.Source_8wekyb3d8bbwe");
        var newest = Path.Combine(lts, "node-v22.11.0-win-x64");
        _host.WithNode(Path.Combine(lts, "node-v20.11.0-win-x64"))
            .WithNode(newest)
            .WithNode(Path.Combine(current, "node-v9.11.0-win-x64"));

        AssertReady(Detect(RunnerKind.Npx), newest, newest);
    }

    [Fact]
    public void DotnetRoot_IsTriedBeforeTheUserDotnet()
    {
        var dotnetRoot = Path.Combine(@"D:\", "dotnet");
        _host.RegistryEnvironment["DOTNET_ROOT"] = dotnetRoot;
        _host.WithDotnet(dotnetRoot, ["10.0.100"], ["10.0.0"]).WithDotnet(UserDotnet, ["10.0.402"], ["10.0.3"]);

        AssertReady(Detect(RunnerKind.Dnx), dotnetRoot, dotnetRoot);
    }

    [Fact]
    public void ProgramFilesDotnet_IsTheLastFixedLocation()
    {
        _host.WithDotnet(ProgramFilesDotnet, ["10.0.100"], ["10.0.0"], readable: true, grantable: false);

        AssertReady(Detect(RunnerKind.Dnx), ProgramFilesDotnet);
    }

    [Fact]
    public void AScriptsPackageRoot_IsFoundThroughPackageJson()
    {
        _host.WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);
        var project = Path.Combine(@"D:\", "proj");
        _host.WithFile(Path.Combine(project, "package.json"));
        var script = AddScript(Path.Combine(project, "dist", "server.js"));

        var resolution = Detect(RunnerKind.NodeScript, scriptOrDll: script);

        AssertReady(resolution, ToolsNode, ToolsNode);
        Assert.Equal(project, resolution.ProgramFolder);
        Assert.Equal([(ToolsNode, false), (project, true)], _roots.Calls);
    }

    [Fact]
    public void APackageJsonAtTheProfileRoot_IsNotAPackageRoot()
    {
        _host.WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);
        _host.WithFile(Path.Combine(UserProfile, "package.json"));
        var script = AddScript(Path.Combine(UserProfile, "mcp", "server.js"));

        Assert.Equal(Path.Combine(UserProfile, "mcp"), Detect(RunnerKind.NodeScript, scriptOrDll: script).ProgramFolder);
    }

    [Fact]
    public void APackageJsonAtTheDriveRoot_IsNotAPackageRoot()
    {
        _host.WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);
        _host.WithFile(Path.Combine(@"D:\", "package.json"));
        var script = AddScript(Path.Combine(@"D:\", "mcp", "server.js"));

        Assert.Equal(Path.Combine(@"D:\", "mcp"), Detect(RunnerKind.NodeScript, scriptOrDll: script).ProgramFolder);
    }

    [Fact]
    public void ALinkedScript_GivesItsFinalFolder()
    {
        _host.WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);
        var linked = Path.Combine(@"D:\", "linked");
        var real = Path.Combine(@"E:\", "real");
        _host.FinalPaths[linked] = real;
        AddScript(Path.Combine(real, "server.js"));

        Assert.Equal(real, Detect(RunnerKind.NodeScript, scriptOrDll: Path.Combine(linked, "server.js")).ProgramFolder);
    }

    [Fact]
    public void AMissingScript_IsScriptMissing_WithTheInstallKept()
    {
        _host.WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);

        var resolution = Detect(RunnerKind.NodeScript, scriptOrDll: Path.Combine(@"D:\", "mcp", "server.js"));

        Assert.Equal(RuntimeOutcome.Refused, resolution.Outcome);
        Assert.Equal(RuntimeIssue.ScriptMissing, resolution.Issue);
        Assert.Equal(ToolsNode, resolution.Install!.Root);
        Assert.Empty(resolution.ToolchainRoots);
        Assert.Null(resolution.ProgramFolder);
    }

    [Fact]
    public void AMissingScript_WinsOverAMissingRuntime()
    {
        var resolution = Detect(RunnerKind.NodeScript, scriptOrDll: Path.Combine(@"D:\", "mcp", "server.js"));

        Assert.Equal(RuntimeIssue.ScriptMissing, resolution.Issue);
        Assert.Null(resolution.Install);
    }

    [Fact]
    public void AMissingDll_IsDllMissing()
    {
        _host.WithDotnet(UserDotnet, [], ["10.0.3"]);

        var resolution = Detect(RunnerKind.DotnetDll, scriptOrDll: Path.Combine(@"D:\", "app", "server.dll"));

        Assert.Equal(RuntimeOutcome.Refused, resolution.Outcome);
        Assert.Equal(RuntimeIssue.DllMissing, resolution.Issue);
        Assert.Equal(UserDotnet, resolution.Install!.Root);
    }

    [Fact]
    public void ATooWideProgramFolder_IsRefused()
    {
        _host.WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);
        var script = AddScript(Path.Combine(UserProfile, "server.js"));
        _roots.Issues[UserProfile] = RuntimeIssue.ProgramFolderTooWide;

        var resolution = Detect(RunnerKind.NodeScript, scriptOrDll: script);

        Assert.Equal(RuntimeOutcome.Refused, resolution.Outcome);
        Assert.Equal(RuntimeIssue.ProgramFolderTooWide, resolution.Issue);
        Assert.Equal(ToolsNode, resolution.Install!.Root);
        Assert.Null(resolution.ProgramFolder);
        Assert.Contains((UserProfile, true), _roots.Calls);
    }

    [Theory]
    [InlineData(@"C:\Program Files\mcp\server.js")]
    [InlineData(@"C:\Windows\mcp\server.js")]
    public void AProgramFolderUnderProgramFilesOrWindows_IsNotGranted(string script)
    {
        _host.WithNode(ToolsNode);
        _host.PathEntries.Add(ToolsNode);
        AddScript(script);

        var resolution = Detect(RunnerKind.NodeScript, scriptOrDll: script);

        AssertReady(resolution, ToolsNode, ToolsNode);
        Assert.Null(resolution.ProgramFolder);
        Assert.DoesNotContain(_roots.Calls, call => call.IsProgramFolder);
    }

    [Fact]
    public void MissingMetadata_GivesNullVersions()
    {
        _host.WithNode(ToolsNode);
        _host.Versions.Remove(Path.Combine(ToolsNode, "node.exe"));
        _host.Texts[Path.Combine(ToolsNode, "node_modules", "npm", "package.json")] = "not json";
        _host.PathEntries.Add(ToolsNode);

        var install = Detect(RunnerKind.Npx).Install!;

        Assert.Null(install.Version);
        Assert.Null(install.NpmVersion);

        _host.Texts.Remove(Path.Combine(ToolsNode, "node_modules", "npm", "package.json"));
        Assert.Null(Detect(RunnerKind.Npx).Install!.NpmVersion);

        _host.Texts[Path.Combine(ToolsNode, "node_modules", "npm", "package.json")] = """{"name": "npm"}""";
        Assert.Null(Detect(RunnerKind.Npx).Install!.NpmVersion);
    }

    [Fact]
    public void AUvWithoutAFileVersion_HasANullVersion()
    {
        var uvFolder = Path.Combine(UserProfile, ".local", "bin");
        _host.WithUv(uvFolder);
        _host.Versions.Remove(Path.Combine(uvFolder, "uv.exe"));

        Assert.Null(Detect(RunnerKind.Uvx).Install!.Version);
    }

    [Fact]
    public void ACancelledToken_StopsBeforeTheSecondCandidate()
    {
        using var cancellation = new CancellationTokenSource();
        _host.WithNode(UserProfile).WithNode(ToolsNode);
        _host.PathEntries.AddRange([UserProfile, ToolsNode]);
        _roots.Issues[UserProfile] = RuntimeIssue.ToolchainTooWide;
        _roots.OnCheck = _ => cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Detect(RunnerKind.Npx, cancellationToken: cancellation.Token));
        Assert.Equal([(UserProfile, false)], _roots.Calls);
    }

    [Fact]
    public void KindNone_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Detect(RunnerKind.None));
    }

    private sealed class FakeRootVerifier : IRuntimeRootVerifier
    {
        public Dictionary<string, RuntimeIssue> Issues { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Root, bool IsProgramFolder)> Calls { get; } = [];

        public Action<string>? OnCheck { get; set; }

        public RuntimeIssue? Check(string root, bool isProgramFolder)
        {
            Calls.Add((root, isProgramFolder));
            OnCheck?.Invoke(root);
            return Issues.TryGetValue(root, out var issue) ? issue : null;
        }
    }
}
