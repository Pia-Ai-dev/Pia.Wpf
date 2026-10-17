using System.IO;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxPathGuardTests
{
    private static readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\gone",
        @"C:\Users\me\AppData\Local\Pia\McpSandbox\fresh",
        @"C:\Users\me\AppData\Local\Pia\McpSandbox\ro-fresh",
    };

    private static readonly SandboxGuardEnvironment Fake = new(
        WindowsDirectory: @"C:\Windows",
        ProgramFilesDirectories: [@"C:\Program Files", @"C:\Program Files (x86)"],
        ProfileRoot: @"C:\Users\me",
        PiaDataRoots: [@"C:\Users\me\AppData\Roaming\Pia", @"C:\Users\me\AppData\Local\Pia"],
        SandboxRoot: @"C:\Users\me\AppData\Local\Pia\McpSandbox",
        WideFolders: [@"C:\Users\me\Desktop", @"C:\Users\me\Documents", @"C:\Users\me\Downloads"],
        PathExists: path => !Missing.Contains(path));

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
    [InlineData(@"C:\Users\me\Documents")]
    [InlineData(@"C:\Users\me\Documents\Reference")]
    public void AFolderInsideTheProfile_IsTheUsersChoice(string path)
    {
        Assert.Empty(Validate([], [path]));
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
    [InlineData(@"C:\Users\me\Downloads\server.exe", "Downloads")]
    [InlineData(@"C:\Users\me\Desktop\server.exe", "Desktop")]
    [InlineData(@"C:\Users\me\Documents\server.exe", "Documents")]
    [InlineData(@"C:\Users\me\server.exe", "me")]
    public void AProgramSittingInAWideFolder_IsRefused_BecauseItsFolderWouldBeShared(string command, string folder)
    {
        var issue = Single(Validate([@"C:\Work\box"], [], command));

        Assert.Equal(SandboxPathIssueKind.CommandFolderTooWide, issue.Kind);
        Assert.Equal(Path.GetDirectoryName(command), issue.Path);
        Assert.Equal($"Put the server in its own folder: Pia would have to share all of {folder} with it.", issue.Message);
    }

    [Fact]
    public void AProgramAtTheDriveRoot_IsRefused()
    {
        Assert.Equal(SandboxPathIssueKind.CommandFolderTooWide, Single(Validate([], [], @"C:\server.exe")).Kind);
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
