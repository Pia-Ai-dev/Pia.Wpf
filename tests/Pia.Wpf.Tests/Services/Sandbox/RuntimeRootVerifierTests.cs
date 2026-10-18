using Pia.Paths;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class RuntimeRootVerifierTests
{
    private static readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Users\me\tools\node"] = SandboxPathGuard.MaxEntriesPerRoot + 1,
        [@"C:\Users\me\AppData\Local\Pia\McpSandbox\fs.runtime"] = 90_000,
        [@"C:\Program Files\nodejs"] = 90_000,
    };

    private static readonly SandboxGuardEnvironment Fake = new(
        WindowsDirectory: @"C:\Windows",
        ProgramFilesDirectories: [@"C:\Program Files", @"C:\Program Files (x86)"],
        ProfileRoot: @"C:\Users\me",
        PiaDataRoots: [@"C:\Users\me\AppData\Roaming\Pia", @"C:\Users\me\AppData\Local\Pia"],
        SandboxRoot: @"C:\Users\me\AppData\Local\Pia\McpSandbox",
        WideFolders: [@"C:\Users\me\Desktop", @"C:\Users\me\Documents", @"C:\Users\me\Downloads"],
        PathExists: _ => true,
        CountEntries: (path, limit) => Math.Min(Counts.GetValueOrDefault(path, 10), limit));

    private static readonly RuntimeRootVerifier Verifier = new(() => Fake);

    [Theory]
    [InlineData(@"C:\Users\me", RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide)]
    [InlineData(@"C:\Users", RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide)]
    [InlineData(@"C:\Users\me\Downloads", RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide)]
    [InlineData(@"C:\Users\me\Desktop", RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide)]
    [InlineData(@"C:\Users\me\AppData\Local\Pia", RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide)]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandboxSupport", RuntimeIssue.ToolchainTooWide, RuntimeIssue.ProgramFolderTooWide)]
    [InlineData(@"C:\Users\me\tools\node", RuntimeIssue.ToolchainTooLarge, RuntimeIssue.ProgramFolderTooLarge)]
    [InlineData(@"C:\Users\me\a;b\node", RuntimeIssue.ToolchainPathUnsupported, RuntimeIssue.ToolchainPathUnsupported)]
    public void EachIssue_ComesInItsToolchainAndItsProgramFolderFlavour(string root, RuntimeIssue toolchain, RuntimeIssue programFolder)
    {
        Assert.Equal(toolchain, Verifier.Check(root, isProgramFolder: false));
        Assert.Equal(programFolder, Verifier.Check(root, isProgramFolder: true));
    }

    [Theory]
    [InlineData(@"C:\Users\me\tools\dotnet")]
    [InlineData(@"C:\Users\me\Downloads\node-v22")]
    [InlineData(@"C:\Users\me\AppData\Local\Pia\McpSandbox\fs.runtime")]
    [InlineData(@"C:\Program Files\nodejs")]
    [InlineData(@"C:\Windows\System32")]
    public void AFolderItMayGrant_OrNeverGrants_HasNoIssue(string root)
    {
        Assert.Null(Verifier.Check(root, isProgramFolder: false));
        Assert.Null(Verifier.Check(root, isProgramFolder: true));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\")]
    [InlineData(@"c:\users\ME\downloads")]
    [InlineData(@"C:\Users\me\tools\..\Downloads")]
    public void TheRoot_IsNormalizedBeforeItIsJudged(string root)
    {
        Assert.Equal(RuntimeIssue.ToolchainTooWide, Verifier.Check(root, isProgramFolder: false));
    }

    [Fact]
    public void TheEnvironment_IsReadAgainOnEveryCheck()
    {
        var calls = 0;
        var verifier = new RuntimeRootVerifier(() => { calls++; return Fake; });

        verifier.Check(@"C:\Users\me\tools\dotnet", isProgramFolder: false);
        verifier.Check(@"C:\Users\me\tools\dotnet", isProgramFolder: true);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void TheDefaultVerifier_JudgesAgainstThisMachine()
    {
        var verifier = new RuntimeRootVerifier();

        Assert.Equal(RuntimeIssue.ToolchainTooWide, verifier.Check(PiaPaths.UserProfileRoot, isProgramFolder: false));
        Assert.Equal(RuntimeIssue.ProgramFolderTooWide, verifier.Check(PiaPaths.LocalDataDirectory, isProgramFolder: true));
    }
}
