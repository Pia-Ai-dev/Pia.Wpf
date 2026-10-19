using System.IO;
using System.Text.RegularExpressions;
using NSubstitute;
using Pia.Paths;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxPathGuardTests
{
    private static readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\gone",
        @"C:\Users\me\AppData\Local\Pia\McpSandbox\fresh",
        @"C:\Users\me\AppData\Local\Pia\McpSandbox\ro-fresh",
        @"C:\Users\me\AppData\Local\Pia\McpSandbox\gone.runtime",
    };

    private static readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Work\huge"] = 50_000,
        [@"C:\Work\exactly"] = SandboxPathGuard.MaxEntriesPerRoot,
        [@"C:\Work\one-over"] = SandboxPathGuard.MaxEntriesPerRoot + 1,
        [@"C:\Users\me\AppData\Local\Pia\McpSandbox\big"] = 90_000,
        [@"C:\Tools\venv"] = 30_000,
    };

    private static readonly SandboxGuardEnvironment Fake = new(
        WindowsDirectory: @"C:\Windows",
        ProgramFilesDirectories: [@"C:\Program Files", @"C:\Program Files (x86)"],
        ProfileRoot: @"C:\Users\me",
        PiaDataRoots: [@"C:\Users\me\AppData\Roaming\Pia", @"C:\Users\me\AppData\Local\Pia"],
        SandboxRoot: @"C:\Users\me\AppData\Local\Pia\McpSandbox",
        WideFolders: [@"C:\Users\me\Desktop", @"C:\Users\me\Documents", @"C:\Users\me\Downloads", @"C:\Users\me\OneDrive - Contoso"],
        PathExists: path => !Missing.Contains(path),
        CountEntries: (path, limit) => Math.Min(Counts.GetValueOrDefault(path, 10), limit));

    private static IReadOnlyList<SandboxPathIssue> Validate(
        IReadOnlyList<string> rw, IReadOnlyList<string> ro, string? command = null) =>
        SandboxPathGuard.Validate(new McpSandboxPolicy(rw, ro, false), command, Fake, fileExists: _ => true);

    private static SandboxPathIssue Single(IReadOnlyList<SandboxPathIssue> issues) => Assert.Single(issues);

    [Fact]
    public void AnOrdinaryPolicy_HasNoIssues()
    {
        Assert.Empty(Validate([@"C:\Work\box"], [@"C:\Work\reference"]));
    }

    /// <summary>The most restrictive policy: a server that needs no files, such as a time server.</summary>
    [Fact]
    public void APolicyWithNoFolders_IsValid()
    {
        Assert.Empty(Validate([], []));
    }

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData("folder")]
    public void ARelativePath_IsRefused(string path)
    {
        var issue = Single(Validate([path], []));

        Assert.Equal(SandboxPathIssueKind.NotRooted, issue.Kind);
        Assert.Equal(path, issue.Path);
    }

    [Theory]
    [InlineData(@"\\server\share\folder")]
    [InlineData(@"//server/share")]
    public void ANetworkPath_IsRefused(string path)
    {
        Assert.Equal(SandboxPathIssueKind.UncPath, Single(Validate([], [path])).Kind);
    }

    [Fact]
    public void AMissingFolder_IsRefused_ForBothKinds()
    {
        Assert.Equal(SandboxPathIssueKind.Missing, Single(Validate([@"C:\gone"], [])).Kind);
        Assert.Equal(SandboxPathIssueKind.Missing, Single(Validate([], [@"C:\gone"])).Kind);
    }

    /// <summary>Pia owns the sandbox root and creates a read-write folder there when the server starts.</summary>
    [Fact]
    public void AMissingReadwriteFolder_UnderTheSandboxRoot_IsFine()
    {
        Assert.Empty(Validate([@"C:\Users\me\AppData\Local\Pia\McpSandbox\fresh"], []));
    }

    [Fact]
    public void AMissingReadonlyFolder_UnderTheSandboxRoot_IsStillRefused()
    {
        Assert.Equal(
            SandboxPathIssueKind.Missing,
            Single(Validate([], [@"C:\Users\me\AppData\Local\Pia\McpSandbox\ro-fresh"])).Kind);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\dotnet")]
    [InlineData(@"C:\Program Files (x86)\Tool\data")]
    public void WindowsAndProgramFiles_AreRefused(string path)
    {
        Assert.Equal(SandboxPathIssueKind.SystemFolder, Single(Validate([path], [])).Kind);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"C:\Users\me\")]
    [InlineData(@"c:\users\ME")]
    public void TheProfileRoot_AndEverythingAboveIt_IsRefused(string path)
    {
        Assert.Equal(SandboxPathIssueKind.ProfileOrAncestor, Single(Validate([], [path])).Kind);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents\Reference")]
    [InlineData(@"C:\Users\me\Downloads\server")]
    [InlineData(@"C:\Users\me\OneDrive - Contoso\Reference")]
    public void AFolderInsideTheProfile_IsTheUsersChoice(string path)
    {
        Assert.Empty(Validate([], [path]));
    }

    /// <summary>Granting a folder changes the permissions of every file below it, on every start.</summary>
    [Theory]
    [InlineData(@"C:\Users\me\Documents")]
    [InlineData(@"C:\Users\me\Desktop\")]
    [InlineData(@"c:\users\ME\downloads")]
    [InlineData(@"C:\Users\me\OneDrive - Contoso")]
    public void ADocumentsStyleRoot_IsRefusedAsAPolicyPath_ForBothKinds(string path)
    {
        var readwrite = Single(Validate([path], []));
        var readOnly = Single(Validate([], [path]));

        Assert.Equal(SandboxPathIssueKind.WideFolder, readwrite.Kind);
        Assert.Equal(SandboxPathIssueKind.WideFolder, readOnly.Kind);
        Assert.Equal("Share a subfolder instead: Pia would have to change the permissions of everything in this folder.", readwrite.Message);
        Assert.Equal(readwrite.Message, readOnly.Message);
    }

    [Fact]
    public void TheCurrentEnvironment_KnowsTheOneDriveRoots_WhenTheyAreSet()
    {
        var original = Environment.GetEnvironmentVariable("OneDriveCommercial");
        try
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", @"C:\Users\me\OneDrive - Fabrikam");

            Assert.Contains(@"C:\Users\me\OneDrive - Fabrikam", SandboxGuardEnvironment.Current().WideFolders);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", original);
        }
    }

    private const string PathFolderWithoutUv = @"D:\tools\bin";

    private static readonly SandboxGuardEnvironment WithHostPrograms = Fake with
    {
        HostProgramFolders = () => [@"C:\Users\me\.local\bin", @"C:\Users\me\.cargo\bin", @"C:\Tools\uv\bin", PathFolderWithoutUv]
    };

    private static IReadOnlyList<SandboxPathIssue> ValidateWithHostPrograms(IReadOnlyList<string> rw, IReadOnlyList<string> ro) =>
        SandboxPathGuard.Validate(new McpSandboxPolicy(rw, ro, false), null, WithHostPrograms, fileExists: _ => true);

    /// <summary>The user and Pia run programs from these folders outside the sandbox, so a server that could plant one
    /// there would run its own program uncontained, whether or not a program is there yet.</summary>
    [Theory]
    [InlineData(@"C:\Users\me\.local\bin")]
    [InlineData(@"c:\users\ME\.LOCAL\bin\")]
    [InlineData(@"C:\Users\me\.local")]
    [InlineData(@"C:\Users\me\.cargo\bin")]
    [InlineData(@"C:\Tools\uv\bin")]
    [InlineData(@"C:\Tools\uv")]
    [InlineData(@"C:\Tools")]
    [InlineData(PathFolderWithoutUv)]
    [InlineData(@"d:\TOOLS\bin\")]
    [InlineData(@"D:\tools")]
    [InlineData(@"D:\")]
    public void AReadwriteFolderThatIsOrHoldsAHostProgramFolder_IsRefused(string path)
    {
        var issue = Single(ValidateWithHostPrograms([path], []));

        Assert.Equal(SandboxPathIssueKind.HostProgramFolder, issue.Kind);
        Assert.Equal(path, issue.Path);
        Assert.Equal(
            "This folder holds programs that run outside the sandbox (a folder on your PATH, or one Pia runs uv from), so a sandboxed server can't be allowed to change it. Share it read-only or choose a different folder.",
            issue.Message);
        PathFreeAssert.IsPathFree(new SandboxStartException(issue.Message));
        Assert.DoesNotMatch(@"(?i)\b(me|users|tools|local|cargo|bin)\b", issue.Message);
    }

    [Theory]
    [InlineData(@"C:\Users\me\.local\bin\cache")]
    [InlineData(@"C:\Users\me\.local2")]
    [InlineData(@"C:\Tools\uvx")]
    [InlineData(@"D:\tools\bin\data")]
    [InlineData(@"D:\tools2")]
    public void AReadwriteFolderBesideOrInsideAHostProgramFolder_IsFine(string path)
    {
        Assert.Empty(ValidateWithHostPrograms([path], []));
    }

    [Fact]
    public void AHostProgramFolder_MayStillBeSharedReadOnly()
    {
        Assert.Empty(ValidateWithHostPrograms([], [@"C:\Users\me\.local\bin", @"C:\Tools\uv", PathFolderWithoutUv, @"D:\tools"]));
    }

    [Fact]
    public void TheHostProgramFolders_AreAskedOnlyForAReadwriteFolder()
    {
        var asked = 0;
        var resolved = new List<string>();
        var environment = Fake with
        {
            HostProgramFolders = () => { asked++; return [PathFolderWithoutUv]; },
            FinalPath = path => { resolved.Add(path); return null; }
        };

        Assert.Null(SandboxPathGuard.JudgeRuntimeRoot(@"C:\Users\me\tools\node", isProgramFolder: false, environment));
        Assert.Null(SandboxPathGuard.JudgeRuntimeRoot(@"C:\Users\me\tools\node", isProgramFolder: true, environment));
        Assert.Equal(@"C:\Tools\fs", SandboxPathGuard.GrantedCommandFolder(
            new McpSandboxPolicy([], [], false), @"C:\Tools\fs\fs.exe", environment, _ => true));
        Assert.Empty(SandboxPathGuard.Validate(new McpSandboxPolicy([], [@"C:\Work\reference"], false), @"C:\Tools\fs\fs.exe",
            environment, fileExists: _ => true));
        Assert.Equal(0, asked);
        Assert.Empty(resolved);

        Assert.Empty(SandboxPathGuard.Validate(new McpSandboxPolicy([@"C:\Work\box"], [], false), null, environment, fileExists: _ => true));
        Assert.Equal(1, asked);
        Assert.Equal([@"C:\Work\box"], resolved);
    }

    private const string NodeOnPath = @"C:\Program Files\nodejs";
    private const string NvmVersion = @"C:\Users\me\AppData\Roaming\nvm\v20.11.0";
    private const string LinkToPathFolder = @"C:\Work\tools-link";
    private const string LinkAbovePathFolder = @"C:\Work\drive-link";

    /// <summary>nvm-windows puts a link on PATH; Pia runs what sits in its target, so that target is a PATH folder too.</summary>
    private static (SandboxGuardEnvironment Environment, FakeRuntimeHost Host) LinkedPathFolders()
    {
        var host = new FakeRuntimeHost();
        host.PathEntries.AddRange([NodeOnPath, PathFolderWithoutUv]);
        host.WithDirectory(NvmVersion).WithDirectory(PathFolderWithoutUv);
        host.FinalPaths[NodeOnPath] = NvmVersion;
        host.FinalPaths[LinkToPathFolder] = PathFolderWithoutUv;
        host.FinalPaths[LinkAbovePathFolder] = @"D:\tools";

        var environment = Fake with
        {
            HostProgramFolders = () => RuntimeCandidates.HostProgramFolders(host, _ => true),
            FinalPath = host.ResolveFinalPath
        };
        return (environment, host);
    }

    [Theory]
    [InlineData(NvmVersion)]
    [InlineData(@"C:\Users\me\AppData\Roaming\nvm")]
    public void AReadwriteFolderThatIsOrHoldsWhereAPathLinkLeads_IsRefused(string path)
    {
        var (environment, _) = LinkedPathFolders();

        var issue = Single(SandboxPathGuard.Validate(new McpSandboxPolicy([path], [], false), null, environment, fileExists: _ => true));

        Assert.Equal(SandboxPathIssueKind.HostProgramFolder, issue.Kind);
        Assert.Equal(path, issue.Path);
    }

    /// <summary>A read-write folder that is itself a link writes into its target.</summary>
    [Theory]
    [InlineData(LinkToPathFolder)]
    [InlineData(LinkAbovePathFolder)]
    public void AReadwriteFolderThatLeadsToOrAboveAPathFolder_IsRefused(string path)
    {
        var (environment, _) = LinkedPathFolders();

        var issue = Single(SandboxPathGuard.Validate(new McpSandboxPolicy([path], [], false), null, environment, fileExists: _ => true));

        Assert.Equal(SandboxPathIssueKind.HostProgramFolder, issue.Kind);
        Assert.Equal(path, issue.Path);
        PathFreeAssert.IsPathFree(new SandboxStartException(issue.Message));
    }

    [Fact]
    public void LinkedPathFolders_MayStillBeSharedReadOnly()
    {
        var (environment, _) = LinkedPathFolders();

        Assert.Empty(SandboxPathGuard.Validate(
            new McpSandboxPolicy([], [NvmVersion, LinkToPathFolder, LinkAbovePathFolder], false), null, environment, fileExists: _ => true));
    }

    [Fact]
    public void AReadwriteFolderBesideALinkTarget_IsFine()
    {
        var (environment, _) = LinkedPathFolders();

        Assert.Empty(SandboxPathGuard.Validate(
            new McpSandboxPolicy([@"C:\Users\me\AppData\Roaming\nvm\v18.20.0"], [], false), null, environment, fileExists: _ => true));
    }

    [Fact]
    public void TheCache_ResolvesAPolicyFolderOnThisPc_ButNeverANetworkOne()
    {
        var host = CountingHost();
        host.ResolveFinalPath(Environment.SystemDirectory).Returns(@"D:\elsewhere");
        var environment = SandboxGuardEnvironment.Current(new HostProgramFolderCache(host, () => 0));

        Assert.Equal(@"D:\elsewhere", environment.FinalPath!(Environment.SystemDirectory));
        Assert.Null(environment.FinalPath(@"\\fileserver\share\box"));
        host.DidNotReceive().ResolveFinalPath(Arg.Is<string>(path => path.StartsWith(@"\\", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheCurrentEnvironment_ProbesNothing_UntilAReadwriteFolderIsJudged()
    {
        var host = CountingHost();
        var environment = SandboxGuardEnvironment.Current(new HostProgramFolderCache(host, () => 0));

        SandboxPathGuard.JudgeRuntimeRoot(Path.Combine(environment.ProfileRoot, "tools", "node"), isProgramFolder: false, environment);
        host.DidNotReceive().RegistryPathEntries();
        host.DidNotReceive().FileExists(Arg.Any<string>());
        host.DidNotReceive().ResolveFinalPath(Arg.Any<string>());

        Assert.Contains(PathFolderWithoutUv, environment.HostProgramFolders!());
        host.Received(1).RegistryPathEntries();
    }

    [Fact]
    public void TheCurrentEnvironment_RefusesTheFixedUvFoldersAndThePathFolders_EvenWithoutUv()
    {
        var folders = SandboxGuardEnvironment.Current().HostProgramFolders?.Invoke();

        Assert.NotNull(folders);
        Assert.Contains(Path.Combine(PiaPaths.UserProfileRoot, ".local", "bin"), folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(PiaPaths.UserProfileRoot, ".cargo", "bin"), folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Environment.SystemDirectory, folders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHostProgramFolders_HoldEveryPathFolder_AndWhereTheUvLinksLead()
    {
        var host = new FakeRuntimeHost();
        var onPath = Path.Combine(@"D:\", "tools", "uv");
        var linkFolder = Path.Combine(@"D:\", "bin");
        var linkTarget = Path.Combine(@"D:\", "uv-versions", "0.13.1");
        var withoutUv = Path.Combine(@"D:\", "other");
        var notThereYet = Path.Combine(@"D:\", "later", "bin");
        host.WithUv(onPath).WithUv(linkTarget).WithFile(Path.Combine(linkFolder, "uv.exe")).WithDirectory(withoutUv);
        host.FinalPaths[Path.Combine(linkFolder, "uv.exe")] = Path.Combine(linkTarget, "uv.exe");
        host.PathEntries.AddRange([onPath, $"\"{linkFolder}\"", withoutUv, notThereYet + @"\", "relative", @"%UNSET%\bin"]);

        var folders = RuntimeCandidates.HostProgramFolders(host, _ => true);

        Assert.Contains(Path.Combine(host.UserProfile, ".local", "bin"), folders);
        Assert.Contains(Path.Combine(host.UserProfile, ".cargo", "bin"), folders);
        Assert.Contains(onPath, folders);
        Assert.Contains(linkFolder, folders);
        Assert.Contains(linkTarget, folders);
        Assert.Contains(withoutUv, folders);
        Assert.Contains(notThereYet, folders);
        Assert.DoesNotContain(folders, folder => folder.Contains("relative", StringComparison.Ordinal) || folder.Contains('%'));
        Assert.Equal(folders.Count, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheHostProgramFolders_HoldWhereEveryPathAndUvFolderLeads_WithOrWithoutUv()
    {
        var (_, host) = LinkedPathFolders();
        var fixedUv = Path.Combine(host.UserProfile, ".local", "bin");
        var fixedUvTarget = Path.Combine(@"D:\", "uv-home", "bin");
        host.WithDirectory(fixedUvTarget);
        host.FinalPaths[fixedUv] = fixedUvTarget;

        var folders = RuntimeCandidates.HostProgramFolders(host, _ => true);

        Assert.Contains(NodeOnPath, folders);
        Assert.Contains(NvmVersion, folders);
        Assert.Contains(fixedUv, folders);
        Assert.Contains(fixedUvTarget, folders);
        Assert.False(host.FileExists(Path.Combine(NvmVersion, "uv.exe")));
        Assert.Equal(folders.Count, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheHostProgramFolders_KeepAFolderItMayNotProbe_ButNeverTouchIt()
    {
        var host = CountingHost();
        host.RegistryPathEntries().Returns([@"\\fileserver\tools", PathFolderWithoutUv]);
        host.FileExists(Arg.Any<string>()).Returns(true);
        host.ResolveFinalPath(Arg.Any<string>()).Returns(call => call.Arg<string>());

        var folders = RuntimeCandidates.HostProgramFolders(host, folder => !folder.StartsWith(@"\\", StringComparison.Ordinal));

        Assert.Contains(@"\\fileserver\tools", folders);
        Assert.Contains(PathFolderWithoutUv, folders);
        host.DidNotReceive().FileExists(Arg.Is<string>(path => path.StartsWith(@"\\", StringComparison.Ordinal)));
        host.DidNotReceive().ResolveFinalPath(Arg.Is<string>(path => path.StartsWith(@"\\", StringComparison.Ordinal)));
        host.Received().ResolveFinalPath(Path.Combine(PathFolderWithoutUv, "uv.exe"));
    }

    [Theory]
    [InlineData(@"\\fileserver\share\bin")]
    [InlineData("//fileserver/share/bin")]
    [InlineData(@"\\?\C:\tools")]
    public void ANetworkFolder_IsNeverProbed(string folder)
    {
        Assert.False(HostProgramFolderCache.IsOnThisPc(folder));
    }

    [Fact]
    public void TheSystemDrive_IsProbed()
    {
        Assert.True(HostProgramFolderCache.IsOnThisPc(Environment.SystemDirectory));
    }

    [Fact]
    public void TheHostProgramFolders_AreCachedBriefly_AndPerProfile()
    {
        var host = CountingHost();
        var now = 0L;
        var cache = new HostProgramFolderCache(host, () => now);

        var first = cache.Get();
        now = HostProgramFolderCache.LifetimeTicks - 1;
        Assert.Same(first, cache.Get());
        host.Received(1).RegistryPathEntries();

        now = HostProgramFolderCache.LifetimeTicks;
        Assert.NotSame(first, cache.Get());
        host.Received(2).RegistryPathEntries();

        host.UserProfile.Returns(@"C:\Users\other");
        cache.Get();
        host.Received(3).RegistryPathEntries();
    }

    private static IRuntimeHostService CountingHost()
    {
        var host = Substitute.For<IRuntimeHostService>();
        host.UserProfile.Returns(@"C:\Users\me");
        host.LocalAppData.Returns(@"C:\Users\me\AppData\Local");
        host.RoamingAppData.Returns(@"C:\Users\me\AppData\Roaming");
        host.RegistryPathEntries().Returns([PathFolderWithoutUv]);
        host.RegistryEnvironmentValue(Arg.Any<string>()).Returns((string?)null);
        host.ReadAllText(Arg.Any<string>()).Returns((string?)null);
        host.ResolveFinalPath(Arg.Any<string>()).Returns((string?)null);
        return host;
    }

    [Fact]
    public void AFolderWithTooManyEntries_IsRefused_ForBothKinds()
    {
        var readwrite = Single(Validate([@"C:\Work\huge"], []));
        var readOnly = Single(Validate([], [@"C:\Work\one-over"]));

        Assert.Equal(SandboxPathIssueKind.TooManyFiles, readwrite.Kind);
        Assert.Equal(SandboxPathIssueKind.TooManyFiles, readOnly.Kind);
        Assert.Equal("This folder holds too many files to share with a sandboxed server. Pick a smaller subfolder.", readwrite.Message);
    }

    [Fact]
    public void AFolderAtExactlyTheCap_IsFine()
    {
        Assert.Empty(Validate([@"C:\Work\exactly"], []));
    }

    [Fact]
    public void TheSandboxRoot_IsExemptFromTheCap_BecausePiaOwnsWhatIsInIt()
    {
        Assert.Empty(Validate([@"C:\Users\me\AppData\Local\Pia\McpSandbox\big"], []));
    }

    [Fact]
    public void TheCount_IsAskedForOnlyOnePastTheCap_AndNotForFoldersThatAreAlreadyRefused()
    {
        var asked = new List<(string Path, int Limit)>();
        var environment = Fake with { CountEntries = (path, limit) => { asked.Add((path, limit)); return 1; } };

        SandboxPathGuard.Validate(new McpSandboxPolicy([@"C:\Work\box", @"C:\Windows"], [@"C:\gone"], false), null, environment, _ => true);

        Assert.Equal([(@"C:\Work\box", SandboxPathGuard.MaxEntriesPerRoot + 1)], asked);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Pia")]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\Logs")]
    [InlineData(@"C:\Users\me\AppData\Roaming\Pia\settings")]
    [InlineData(@"C:\Users\me\AppData\Local")]
    [InlineData(@"C:\Users\me\AppData\Roaming")]
    [InlineData(@"C:\Users\me\AppData")]
    public void PiasOwnData_AndTheFoldersThatContainIt_AreRefused(string path)
    {
        Assert.Equal(SandboxPathIssueKind.PiaDataFolder, Single(Validate([], [path])).Kind);
    }

    [Fact]
    public void TheSandboxRootItself_IsRefused_ButAFolderInsideItIsFine()
    {
        Assert.Equal(SandboxPathIssueKind.SandboxRootItself, Single(Validate([@"C:\Users\me\AppData\Local\Pia\McpSandbox"], [])).Kind);
        Assert.Empty(Validate([@"C:\Users\me\AppData\Local\Pia\McpSandbox\fs"], []));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\files.runtime")]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\FILES.RUNTIME\")]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\files.runtime\node_modules")]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\gone.runtime")]
    public void AServersRuntimeFolder_IsRefused_ForBothKinds(string path)
    {
        var readwrite = Single(Validate([path], []));
        var readOnly = Single(Validate([], [path]));

        Assert.Equal(SandboxPathIssueKind.ManagedRuntimeFolder, readwrite.Kind);
        Assert.Equal(SandboxPathIssueKind.ManagedRuntimeFolder, readOnly.Kind);
        Assert.Equal("Pia manages this folder for a server's runtime. Choose a different folder.", readwrite.Message);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\fs")]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\runtime")]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\fs.runtimes")]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\fs\cache.runtime")]
    [InlineData(@"C:\Work\project.runtime")]
    public void AFolderThatIsNoServersRuntimeFolder_IsStillAccepted(string path)
    {
        Assert.Empty(Validate([path], []));
    }

    [Fact]
    public void PiasGrantAndSupportFolders_AreRefusedAsPiasOwnData()
    {
        var environment = SandboxGuardEnvironment.Current() with { PathExists = _ => true, CountEntries = (_, _) => 1 };

        foreach (var path in new[] { PiaPaths.McpSandboxGrantsDirectory, PiaPaths.McpSandboxSupportDirectory })
        {
            var readwrite = Single(SandboxPathGuard.Validate(new McpSandboxPolicy([path], [], false), null, environment, _ => true));
            var readOnly = Single(SandboxPathGuard.Validate(new McpSandboxPolicy([], [path], false), null, environment, _ => true));

            Assert.Equal(SandboxPathIssueKind.PiaDataFolder, readwrite.Kind);
            Assert.Equal(SandboxPathIssueKind.PiaDataFolder, readOnly.Kind);
        }
    }

    [Fact]
    public void TheCurrentSandboxRoot_StillAcceptsAServersOwnFolder_ButNotItsRuntimeFolder()
    {
        var environment = SandboxGuardEnvironment.Current() with { PathExists = _ => true, CountEntries = (_, _) => 1 };
        var policy = new McpSandboxPolicy([Path.Combine(PiaPaths.McpSandboxRoot, "fs")], [PiaPaths.McpRuntimeStateDirectory("fs")], false);

        var issue = Single(SandboxPathGuard.Validate(policy, null, environment, _ => true));

        Assert.Equal(SandboxPathIssueKind.ManagedRuntimeFolder, issue.Kind);
        Assert.Equal(PiaPaths.McpRuntimeStateDirectory("fs"), issue.Path);
    }

    private static SandboxGuardEnvironment RuntimeEnvironment(params (string Path, int Count)[] counts)
    {
        var table = counts.ToDictionary(c => c.Path, c => c.Count, StringComparer.OrdinalIgnoreCase);
        return SandboxGuardEnvironment.Current() with
        {
            PathExists = _ => true,
            CountEntries = (path, limit) => Math.Min(table.GetValueOrDefault(path, 10), limit)
        };
    }

    private static string InProfile(params string[] parts) => Path.Combine([PiaPaths.UserProfileRoot, .. parts]);

    private static void AssertJudged(RuntimeIssue? toolchain, RuntimeIssue? programFolder, string root, SandboxGuardEnvironment environment)
    {
        Assert.Equal(toolchain, SandboxPathGuard.JudgeRuntimeRoot(root, isProgramFolder: false, environment));
        Assert.Equal(programFolder, SandboxPathGuard.JudgeRuntimeRoot(root, isProgramFolder: true, environment));
    }

    [Fact]
    public void ARuntimeRoot_AtTheProfileRoot_IsTooWide()
    {
        var environment = RuntimeEnvironment();

        AssertJudged(RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide, environment.ProfileRoot, environment);
        AssertJudged(RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide, Path.GetDirectoryName(environment.ProfileRoot)!, environment);
    }

    [Fact]
    public void ARuntimeRoot_ThatIsDownloadsItself_IsTooWide_ButAFolderInsideItIsNot()
    {
        var environment = RuntimeEnvironment();

        AssertJudged(RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide, InProfile("Downloads"), environment);
        AssertJudged(null, null, InProfile("Downloads", "node-v22"), environment);
    }

    [Fact]
    public void ARuntimeRoot_InPiasOwnData_IsTooWide()
    {
        var environment = RuntimeEnvironment();

        AssertJudged(RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide, PiaPaths.LocalDataDirectory, environment);
        AssertJudged(RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide, PiaPaths.McpSandboxSupportDirectory, environment);
        AssertJudged(RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide, PiaPaths.McpSandboxRoot, environment);
    }

    [Fact]
    public void ARuntimeRoot_InsideTheSandboxRoot_KeepsItsExemptions()
    {
        var state = PiaPaths.McpRuntimeStateDirectory("fs");
        var environment = RuntimeEnvironment((state, 90_000));

        AssertJudged(null, null, state, environment);
    }

    [Fact]
    public void ARuntimeRoot_WithMoreEntriesThanTheCap_IsTooLarge()
    {
        var big = InProfile("tools", "node");
        var exactly = InProfile("tools", "dotnet");
        var environment = RuntimeEnvironment((big, SandboxPathGuard.MaxEntriesPerRoot + 1), (exactly, SandboxPathGuard.MaxEntriesPerRoot));

        AssertJudged(RuntimeIssue.ToolchainTooLarge, RuntimeIssue.ProgramFolderTooLarge, big, environment);
        AssertJudged(null, null, exactly, environment);
    }

    [Fact]
    public void ARuntimeRoot_WithASemicolon_IsUnsupported()
    {
        var environment = RuntimeEnvironment();

        AssertJudged(RuntimeIssue.ToolchainPathUnsupported, RuntimeIssue.ToolchainPathUnsupported, InProfile("a;b", "node"), environment);
    }

    [Fact]
    public void ARuntimeRoot_UnderProgramFilesOrWindows_IsNeverGranted_SoNeverJudged()
    {
        var environment = RuntimeEnvironment();
        var programFiles = Path.Combine(environment.ProgramFilesDirectories[0], "nodejs");
        var huge = RuntimeEnvironment((programFiles, 90_000));

        AssertJudged(null, null, programFiles, huge);
        AssertJudged(null, null, Path.Combine(environment.WindowsDirectory, "System32"), environment);
    }

    [Fact]
    public void APlainUserFolder_MayBeGranted()
    {
        var environment = RuntimeEnvironment();

        AssertJudged(null, null, InProfile("tools", "node"), environment);
    }

    [Theory]
    [InlineData(@"C:\Work\box")]
    [InlineData(@"C:\Work\box\sub")]
    [InlineData(@"c:\work\BOX\")]
    public void AReadonlyFolder_InsideOrEqualToAReadwriteFolder_IsRefused(string readonlyPath)
    {
        var issue = Single(Validate([@"C:\Work\box"], [readonlyPath]));

        Assert.Equal(SandboxPathIssueKind.ReadonlyInsideReadwrite, issue.Kind);
        Assert.Equal(readonlyPath, issue.Path);
    }

    [Fact]
    public void AReadwriteFolder_InsideAnotherReadwriteFolder_IsRefused()
    {
        var issue = Single(Validate([@"C:\Work\box", @"C:\Work\box\sub"], []));

        Assert.Equal(SandboxPathIssueKind.NestedReadwrite, issue.Kind);
        Assert.Equal(@"C:\Work\box\sub", issue.Path);
    }

    [Fact]
    public void TheSameReadwriteFolderTwice_IsRefusedOnce()
    {
        Assert.Equal(SandboxPathIssueKind.NestedReadwrite, Single(Validate([@"C:\Work\box", @"c:\work\box\"], [])).Kind);
    }

    [Fact]
    public void AReadonlyFolder_BesideAReadwriteFolder_IsFine()
    {
        Assert.Empty(Validate([@"C:\Work\box"], [@"C:\Work\boxes", @"C:\Work\reference"]));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\server.exe")]
    [InlineData(@"C:\Users\me\Desktop\server.exe")]
    [InlineData(@"C:\Users\me\Documents\server.exe")]
    [InlineData(@"C:\Users\me\server.exe")]
    public void AProgramSittingInAWideFolder_IsRefused_BecauseItsFolderWouldBeShared(string command)
    {
        var issue = Single(Validate([@"C:\Work\box"], [], command));

        Assert.Equal(SandboxPathIssueKind.CommandFolderTooWide, issue.Kind);
        Assert.Equal(Path.GetDirectoryName(command), issue.Path);
        Assert.Equal("Put the server in its own folder: Pia would have to share that whole folder with it.", issue.Message);
    }

    /// <summary>Its folder is granted read-only too, so a 30,000-file virtual environment would make every start crawl.</summary>
    [Fact]
    public void AProgramWhoseFolderHoldsTooManyFiles_IsRefused()
    {
        var issue = Single(Validate([], [], @"C:\Tools\venv\python.exe"));

        Assert.Equal(SandboxPathIssueKind.CommandFolderTooLarge, issue.Kind);
        Assert.Equal(@"C:\Tools\venv", issue.Path);
        Assert.Equal("The server's folder holds too many files to share with a sandboxed server. Put the server in a smaller folder.", issue.Message);
        Assert.Null(Granted([], [], @"C:\Tools\venv\python.exe"));
    }

    [Fact]
    public void AProgramInABigFolderTheUserAlreadyGranted_NeedsNoSecondGrant_SoItIsNotCounted()
    {
        Assert.DoesNotContain(
            Validate([@"C:\Tools"], [], @"C:\Tools\venv\python.exe"),
            issue => issue.Kind == SandboxPathIssueKind.CommandFolderTooLarge);
    }

    [Fact]
    public void AProgramAtTheDriveRoot_IsRefused()
    {
        var issue = Single(Validate([], [], @"C:\server.exe"));

        Assert.Equal(SandboxPathIssueKind.CommandFolderTooWide, issue.Kind);
        PathFreeAssert.IsPathFree(new SandboxStartException(issue.Message));
    }

    [Fact]
    public void AProgramAtAShareRoot_IsRefused_WithoutNamingTheShare()
    {
        var environment = Fake with { WideFolders = [.. Fake.WideFolders, @"\\fileserver\jdoe$"] };

        var issue = Single(SandboxPathGuard.Validate(
            new McpSandboxPolicy([], [], false), @"\\fileserver\jdoe$\server.exe", environment, fileExists: _ => true));

        Assert.Equal(SandboxPathIssueKind.CommandFolderTooWide, issue.Kind);
        Assert.DoesNotContain("jdoe", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileserver", issue.Message, StringComparison.OrdinalIgnoreCase);
        PathFreeAssert.IsPathFree(new SandboxStartException(issue.Message));
    }

    [Fact]
    public void AProgramAtTheProfileRoot_IsRefused_WithoutNamingTheAccount()
    {
        var issue = Single(Validate([], [], "C:/Users/me/server.exe"));

        Assert.Equal(SandboxPathIssueKind.CommandFolderTooWide, issue.Kind);
        Assert.DoesNotMatch(@"(?i)\bme\b", issue.Message);
        PathFreeAssert.IsPathFree(new SandboxStartException(issue.Message));
    }

    [Fact]
    public void AProgramInItsOwnFolder_IsFine()
    {
        Assert.Empty(Validate([@"C:\Work\box"], [], @"C:\Tools\fs\fs.exe"));
        Assert.Empty(Validate([@"C:\Work\box"], [], @"C:\Users\me\Downloads\server\server.exe"));
    }

    [Fact]
    public void AProgramUnderProgramFiles_NeedsNoGrant_SoItsFolderIsNeverJudged()
    {
        Assert.Empty(Validate([], [], @"C:\Program Files\dotnet\dotnet.exe"));
    }

    [Fact]
    public void AProgramInsideAFolderTheUserAlreadyGranted_NeedsNoSecondGrant()
    {
        Assert.Empty(Validate([@"C:\Users\me\Downloads\srv"], [], @"C:\Users\me\Downloads\srv\a.exe"));
    }

    [Fact]
    public void ACommandThatCannotBeResolved_IsLeftToTheLaunch()
    {
        var issues = SandboxPathGuard.Validate(new McpSandboxPolicy([], [], false), "nope", Fake, fileExists: _ => false);

        Assert.Empty(issues);
    }

    private static string? Granted(IReadOnlyList<string> rw, IReadOnlyList<string> ro, string? command, Func<string, bool>? fileExists = null) =>
        SandboxPathGuard.GrantedCommandFolder(new McpSandboxPolicy(rw, ro, false), command, Fake, fileExists ?? (_ => true));

    [Fact]
    public void TheGrantedCommandFolder_IsTheProgramsOwnFolder()
    {
        Assert.Equal(@"C:\Tools\fs", Granted([@"C:\Work\box"], [], @"C:\Tools\fs\fs.exe"));
        Assert.Equal(@"C:\Tools\fs", Granted([], [], @"c:\tools\fs\FS.EXE"), ignoreCase: true);
    }

    [Fact]
    public void TheGrantedCommandFolder_IsNull_WhenNothingIsGranted()
    {
        Assert.Null(Granted([], [], @"C:\Program Files\dotnet\dotnet.exe"));
        Assert.Null(Granted([@"C:\Tools"], [], @"C:\Tools\fs\fs.exe"));
        Assert.Null(Granted([], [@"C:\Tools\fs"], @"C:\Tools\fs\fs.exe"));
    }

    [Fact]
    public void TheGrantedCommandFolder_IsNull_ForAWideFolder_WhichIsRefusedInstead()
    {
        Assert.Null(Granted([], [], @"C:\Users\me\Downloads\server.exe"));
    }

    [Fact]
    public void TheGrantedCommandFolder_IsNull_ForACommandThatCannotBeResolved()
    {
        Assert.Null(Granted([], [], "nope", fileExists: _ => false));
        Assert.Null(Granted([], [], null));
        Assert.Null(Granted([], [], "  "));
    }

    [Fact]
    public void AMessage_NeverRepeatsTheUsersPath()
    {
        var path = @"C:\Users\me\AppData\Local\Pia\Logs";

        var issue = Single(Validate([], [path]));

        Assert.DoesNotContain(path, issue.Message);
        Assert.DoesNotContain("Logs", issue.Message);

        var oneDrive = @"C:\Users\me\OneDrive - Contoso Ltd";
        var environment = Fake with { WideFolders = [.. Fake.WideFolders, oneDrive] };
        var wide = Single(SandboxPathGuard.Validate(new McpSandboxPolicy([oneDrive], [], false), null, environment, _ => true));
        Assert.Equal(SandboxPathIssueKind.WideFolder, wide.Kind);
        AssertNamesNoSegmentOf(oneDrive, wide.Message);

        var profileCommand = Single(Validate([], [], @"C:\Users\me\server.exe"));
        Assert.Equal(SandboxPathIssueKind.CommandFolderTooWide, profileCommand.Kind);
        AssertNamesNoSegmentOf(profileCommand.Path, profileCommand.Message);
    }

    private static void AssertNamesNoSegmentOf(string path, string message)
    {
        var segments = path.Split(['\\', '/', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim(':', '-'))
            .Where(segment => segment.Length > 0);
        foreach (var segment in segments)
            Assert.DoesNotMatch($@"(?i)\b{Regex.Escape(segment)}\b", message);
    }

    [Fact]
    public void EveryProblem_IsReported_InPolicyOrder()
    {
        var issues = Validate([@"relative", @"C:\Windows"], [@"C:\gone"], @"C:\Users\me\Downloads\s.exe");

        Assert.Equal(
            [SandboxPathIssueKind.NotRooted, SandboxPathIssueKind.SystemFolder, SandboxPathIssueKind.Missing, SandboxPathIssueKind.CommandFolderTooWide],
            issues.Select(i => i.Kind));
    }
}
