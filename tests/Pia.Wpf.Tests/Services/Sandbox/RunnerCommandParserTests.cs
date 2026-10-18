using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class RunnerCommandParserTests
{
    private const string SystemDirectory = @"C:\Windows\System32";
    private const string WorkingDirectory = @"C:\work";

    private static RunnerParseResult Parse(string command, params string[] args) =>
        RunnerCommandParser.Parse(command, args, null, SystemDirectory, _ => false);

    private static RunnerParseResult ParseIn(string? workingDirectory, string command, params string[] args) =>
        RunnerCommandParser.Parse(command, args, workingDirectory, SystemDirectory, _ => false);

    private static void AssertRefused(RunnerParseResult result, RunnerRefusal refusal, string? subject)
    {
        Assert.Equal(refusal, result.Refusal);
        Assert.Equal(subject, result.Subject);
        Assert.Equal(RunnerKind.None, result.Kind);
        Assert.Null(result.Runner);
    }

    private static RunnerRequest AssertRunner(RunnerParseResult result, RunnerKind kind)
    {
        Assert.Equal(RunnerRefusal.None, result.Refusal);
        Assert.Null(result.Subject);
        Assert.Equal(kind, result.Kind);
        Assert.NotNull(result.Runner);
        Assert.Equal(kind, result.Runner.Kind);
        return result.Runner;
    }

    [Theory]
    [InlineData("npx", RunnerKind.Npx, "-y", "@x/y")]
    [InlineData("NPX", RunnerKind.Npx, "-y", "@x/y")]
    [InlineData("npx.cmd", RunnerKind.Npx, "-y", "@x/y")]
    [InlineData("npx.exe", RunnerKind.Npx, "-y", "@x/y")]
    [InlineData(@"""C:\Program Files\nodejs\npx.cmd""", RunnerKind.Npx, "-y", "@x/y")]
    [InlineData("npm", RunnerKind.NpmExec, "exec", "@x/y")]
    [InlineData("npm", RunnerKind.NpmExec, "x", "@x/y")]
    [InlineData("uvx", RunnerKind.Uvx, "mcp-server-time")]
    [InlineData("uvx.exe", RunnerKind.Uvx, "mcp-server-time")]
    [InlineData(@"C:\Users\me\.local\bin\uvx.exe", RunnerKind.Uvx, "mcp-server-time")]
    [InlineData("uv", RunnerKind.Uvx, "tool", "run", "mcp-server-time")]
    [InlineData("dnx", RunnerKind.Dnx, "Community.Mcp.DotNet@1.*")]
    [InlineData("dotnet", RunnerKind.Dnx, "dnx", "Community.Mcp.DotNet@1.*")]
    [InlineData("dotnet", RunnerKind.DotnetDll, @"C:\srv\x.dll")]
    [InlineData("dotnet", RunnerKind.DotnetDll, "exec", @"C:\srv\x.dll")]
    [InlineData("node", RunnerKind.NodeScript, @"C:\srv\s.js")]
    [InlineData("node.exe", RunnerKind.NodeScript, @"C:\srv\s.js")]
    public void Runners_AreRecognised(string command, RunnerKind kind, params string[] args)
    {
        AssertRunner(Parse(command, args), kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("python")]
    [InlineData("python.exe")]
    [InlineData(@"C:\Tools\fs\fs.exe")]
    [InlineData(@"C:\Tools\npx-helper\server.exe")]
    [InlineData(@"""C:\Program Files\srv\server.exe""")]
    [InlineData("npx-helper")]
    public void OtherPrograms_AreKindNone(string command)
    {
        var result = Parse(command, "--db", @"C:\d\c.json");

        Assert.Equal(RunnerKind.None, result.Kind);
        Assert.Equal(RunnerRefusal.None, result.Refusal);
        Assert.Null(result.Runner);
        Assert.Null(result.Subject);
        Assert.Equal(command.Trim().Trim('"'), result.EffectiveCommand);
        Assert.Equal(["--db", @"C:\d\c.json"], result.EffectiveArgs);
    }

    [Theory]
    [InlineData("pnpm")]
    [InlineData("pnpx")]
    [InlineData("yarn")]
    [InlineData("bunx")]
    [InlineData("bun")]
    [InlineData("deno")]
    [InlineData("pipx")]
    [InlineData("docker")]
    public void NonGoalLaunchers_AreRefusedByName(string name)
    {
        AssertRefused(Parse(name, "run", "x"), RunnerRefusal.NonGoalLauncher, name);
        AssertRefused(Parse(name.ToUpperInvariant() + ".exe", "run", "x"), RunnerRefusal.NonGoalLauncher, name);
        AssertRefused(Parse($@"C:\Tools\{name}.cmd", "run", "x"), RunnerRefusal.NonGoalLauncher, name);
    }

    [Theory]
    [InlineData("x.cmd")]
    [InlineData("x.bat")]
    [InlineData("x.ps1")]
    [InlineData(@"C:\Tools\run.cmd")]
    [InlineData(@"C:\Tools\run.BAT")]
    [InlineData(@"C:\Tools\run.ps1")]
    [InlineData("npx.bat")]
    [InlineData("npx.ps1")]
    [InlineData("node.ps1")]
    [InlineData("uvx.bat")]
    [InlineData("dotnet.bat")]
    [InlineData("pnpm.ps1")]
    [InlineData("npx.js")]
    public void Scripts_AreRefused(string command)
    {
        AssertRefused(Parse(command, "-y", "x"), RunnerRefusal.ScriptFile, null);
    }

    [Fact]
    public void Npx_KeepsItsArgumentsUnchanged()
    {
        var result = Parse("npx", "-y", "@modelcontextprotocol/server-everything", "--port", "1");

        var runner = AssertRunner(result, RunnerKind.Npx);
        Assert.Equal(["-y", "@modelcontextprotocol/server-everything", "--port", "1"], runner.Arguments);
        Assert.Null(runner.LauncherPathHint);
        Assert.Null(runner.ScriptOrDll);
        Assert.Empty(runner.RuntimeOptions);
        Assert.Null(runner.Uvx);
        Assert.Equal("npx", result.EffectiveCommand);
        Assert.Equal(["-y", "@modelcontextprotocol/server-everything", "--port", "1"], result.EffectiveArgs);
    }

    [Fact]
    public void ARootedLauncher_IsTheHint()
    {
        var runner = AssertRunner(Parse(@"  ""C:\Program Files\nodejs\npx.cmd""  ", "-y", "x"), RunnerKind.Npx);

        Assert.Equal(@"C:\Program Files\nodejs\npx.cmd", runner.LauncherPathHint);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("x")]
    public void NpmExec_TakesTheArgumentsAfterTheSubcommand(string subcommand)
    {
        var runner = AssertRunner(Parse("npm", subcommand, "--", "@x/y", "--flag"), RunnerKind.NpmExec);

        Assert.Equal(["--", "@x/y", "--flag"], runner.Arguments);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("run")]
    [InlineData("install")]
    [InlineData("Exec")]
    [InlineData("--yes")]
    public void OtherNpmForms_AreUnsupported(string first)
    {
        AssertRefused(Parse("npm", first, "x"), RunnerRefusal.UnsupportedForm, "npm");
    }

    [Fact]
    public void NpmWithoutArguments_IsUnsupported()
    {
        AssertRefused(Parse("npm"), RunnerRefusal.UnsupportedForm, "npm");
    }

    [Fact]
    public void CmdC_IsUnwrapped_FromSeparateItems()
    {
        var result = Parse("cmd", "/c", "npx", "-y", "@x/y");

        var runner = AssertRunner(result, RunnerKind.Npx);
        Assert.Equal(["-y", "@x/y"], runner.Arguments);
        Assert.Equal("npx", result.EffectiveCommand);
        Assert.Equal(["-y", "@x/y"], result.EffectiveArgs);
    }

    [Fact]
    public void CmdC_IsUnwrapped_FromOneLine_AfterLeadingSwitches()
    {
        var result = Parse("cmd", "/d", "/s", "/c", "npx -y @x/y");

        var runner = AssertRunner(result, RunnerKind.Npx);
        Assert.Equal(["-y", "@x/y"], runner.Arguments);
    }

    [Theory]
    [InlineData("cmd.exe", "/C")]
    [InlineData("CMD", "/c")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c")]
    [InlineData(@"c:\windows\system32\CMD.EXE", "/c")]
    [InlineData(@"""C:\Windows\System32\cmd.exe""", "/c")]
    [InlineData(@"C:\Windows\System32\cmd", "/c")]
    public void CmdC_IsUnwrapped_ForTheSystemCommandProcessor(string cmd, string c)
    {
        AssertRunner(Parse(cmd, "/Q", "/D", c, "npx", "-y", "@x/y"), RunnerKind.Npx);
    }

    [Theory]
    [InlineData(@"C:\Tools\cmd.exe")]
    [InlineData(@"C:\Windows\SysWOW64\cmd.exe")]
    [InlineData(@"D:\cmd")]
    public void ACmdOutsideSystem32_IsRefused(string cmd)
    {
        AssertRefused(Parse(cmd, "/c", "npx", "x"), RunnerRefusal.CmdNotSystem, null);
    }

    [Fact]
    public void TheSystemDirectory_DefaultsToTheRealOne()
    {
        var cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var result = RunnerCommandParser.Parse(cmd, ["/c", "npx", "x"], null, fileExists: _ => false);

        AssertRunner(result, RunnerKind.Npx);
    }

    [Theory]
    [InlineData("/k", "npx")]
    [InlineData("/d", "/s")]
    [InlineData("/d")]
    [InlineData("npx", "-y")]
    [InlineData("/c")]
    [InlineData("/c", "")]
    [InlineData("/c", "   ")]
    [InlineData("/c", "\"\"")]
    [InlineData("/c npx -y x")]
    [InlineData("/c", "cmd", "/c", "npx")]
    [InlineData("/c", "cmd.exe /c npx")]
    [InlineData("/c", @"C:\Windows\System32\cmd.exe", "/c", "npx")]
    public void CmdWithoutC_IsRefused(params string[] args)
    {
        AssertRefused(Parse("cmd", args), RunnerRefusal.CmdWithoutC, null);
    }

    [Fact]
    public void CmdWithNoArguments_IsRefused()
    {
        AssertRefused(Parse("cmd"), RunnerRefusal.CmdWithoutC, null);
    }

    [Theory]
    [InlineData("npx -y x & calc")]
    [InlineData("npx -y x | more")]
    [InlineData("npx -y x < in.txt")]
    [InlineData("npx -y x > out.txt")]
    [InlineData("npx -y ^x")]
    [InlineData("npx -y %SECRET%")]
    public void CmdMetacharacters_InOneLine_AreRefused(string line)
    {
        AssertRefused(Parse("cmd", "/c", line), RunnerRefusal.CmdMetacharacters, null);
    }

    [Theory]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("^")]
    [InlineData("%")]
    [InlineData("\"")]
    public void CmdMetacharactersAndQuotes_InSeparateItems_AreRefused(string metacharacter)
    {
        AssertRefused(Parse("cmd", "/c", "npx", "-y", "x" + metacharacter + "y"), RunnerRefusal.CmdMetacharacters, null);
    }

    [Fact]
    public void QuotesInOneLine_AreSplitAway()
    {
        var result = Parse("cmd", "/c", @"""C:\Program Files\nodejs\npx.cmd"" -y ""@x/y""");

        var runner = AssertRunner(result, RunnerKind.Npx);
        Assert.Equal(@"C:\Program Files\nodejs\npx.cmd", runner.LauncherPathHint);
        Assert.Equal(["-y", "@x/y"], runner.Arguments);
    }

    [Fact]
    public void OneExistingFullyQualifiedItem_IsTheProgramAsAWhole()
    {
        const string Server = @"C:\Users\Jane Doe\mcp\server.exe";
        var asked = new List<string>();

        var result = RunnerCommandParser.Parse("cmd", ["/c", Server], null, SystemDirectory, path =>
        {
            asked.Add(path);
            return path == Server;
        });

        Assert.Equal(RunnerKind.None, result.Kind);
        Assert.Equal(RunnerRefusal.None, result.Refusal);
        Assert.Equal(Server, result.EffectiveCommand);
        Assert.Empty(result.EffectiveArgs);
        Assert.Equal([Server], asked);
    }

    [Fact]
    public void OneMissingItemWithSpaces_IsSplit()
    {
        var result = Parse("cmd", "/c", @"C:\Users\Jane Doe\mcp\server.exe");

        Assert.Equal(RunnerKind.None, result.Kind);
        Assert.Equal(@"C:\Users\Jane", result.EffectiveCommand);
        Assert.Equal([@"Doe\mcp\server.exe"], result.EffectiveArgs);
    }

    [Fact]
    public void FileExists_IsAskedOnlyAboutAFullyQualifiedSingleItem()
    {
        var asked = new List<string>();
        bool Exists(string path)
        {
            asked.Add(path);
            return true;
        }

        RunnerCommandParser.Parse("cmd", ["/c", "npx -y x"], null, SystemDirectory, Exists);
        RunnerCommandParser.Parse("cmd", ["/c", @"C:\t\s.exe", "--a"], null, SystemDirectory, Exists);
        RunnerCommandParser.Parse("npx", [@"C:\t\s.exe"], null, SystemDirectory, Exists);
        RunnerCommandParser.Parse(@"C:\t\s.exe", [], null, SystemDirectory, Exists);

        Assert.Empty(asked);
    }

    [Fact]
    public void CmdC_WithAnExe_IsUnwrappedToKindNone()
    {
        var result = Parse("cmd", "/c", @"C:\t\s.exe", "--db", @"C:\d\c.json");

        Assert.Equal(RunnerKind.None, result.Kind);
        Assert.Equal(RunnerRefusal.None, result.Refusal);
        Assert.Equal(@"C:\t\s.exe", result.EffectiveCommand);
        Assert.Equal(["--db", @"C:\d\c.json"], result.EffectiveArgs);
    }

    [Fact]
    public void CmdC_WithAScript_IsAScriptFile()
    {
        AssertRefused(Parse("cmd", "/c", @"C:\t\run.bat"), RunnerRefusal.ScriptFile, null);
    }

    [Fact]
    public void ARefusedCmdLine_KeepsTheCmdAsTheEffectiveCommand()
    {
        var result = Parse("cmd", "/k", "npx");

        Assert.Equal("cmd", result.EffectiveCommand);
        Assert.Equal(["/k", "npx"], result.EffectiveArgs);
    }

    [Fact]
    public void Uvx_PackageIsTheFirstToken_AndTheRestAreServerArgs()
    {
        var runner = AssertRunner(Parse("uvx", "mcp-server-time", "--local-timezone", "Europe/Berlin"), RunnerKind.Uvx);

        Assert.NotNull(runner.Uvx);
        Assert.Equal("mcp-server-time", runner.Uvx.Package);
        Assert.Equal("mcp-server-time", runner.Uvx.CommandName);
        Assert.Empty(runner.Uvx.With);
        Assert.Null(runner.Uvx.Python);
        Assert.Null(runner.ScriptOrDll);
        Assert.Equal(["--local-timezone", "Europe/Berlin"], runner.Arguments);
    }

    [Fact]
    public void UvToolRun_IsUvx()
    {
        var runner = AssertRunner(Parse("uv", "tool", "run", "--from", "a", "b", "--x"), RunnerKind.Uvx);

        Assert.Equal("a", runner.Uvx!.Package);
        Assert.Equal("b", runner.Uvx.CommandName);
        Assert.Equal(["--x"], runner.Arguments);
    }

    [Theory]
    [InlineData("run", "x")]
    [InlineData("tool", "install", "x")]
    [InlineData("tool")]
    [InlineData("pip", "install", "x")]
    public void OtherUvForms_AreUnsupported(params string[] args)
    {
        AssertRefused(Parse("uv", args), RunnerRefusal.UnsupportedForm, "uv");
    }

    [Fact]
    public void UvxFrom_NamesThePackage_AndTheTokenIsTheCommand()
    {
        var runner = AssertRunner(Parse("uvx", "--from", "a", "b"), RunnerKind.Uvx);

        Assert.Equal("a", runner.Uvx!.Package);
        Assert.Equal("b", runner.Uvx.CommandName);
        Assert.Empty(runner.Arguments);
    }

    [Fact]
    public void UvxOptions_AcceptTheirEqualsForms_AndIgnoreQuietAndIsolated()
    {
        var runner = AssertRunner(
            Parse("uvx", "-q", "--from=mcp-git==1.2", "--with", "httpx", "--with=rich>=13", "--quiet", "--isolated",
                "--python=3.12", "mcp-git", "--repo", "x"),
            RunnerKind.Uvx);

        Assert.Equal("mcp-git==1.2", runner.Uvx!.Package);
        Assert.Equal("mcp-git", runner.Uvx.CommandName);
        Assert.Equal(["httpx", "rich>=13"], runner.Uvx.With);
        Assert.Equal("3.12", runner.Uvx.Python);
        Assert.Equal(["--repo", "x"], runner.Arguments);
    }

    [Theory]
    [InlineData("-p", "3.12", "3.12")]
    [InlineData("--python", "cpython-3.12.4", "cpython-3.12.4")]
    [InlineData("-p", "python3.11", "python3.11")]
    [InlineData("-p", "3", "3")]
    public void UvxPython_TakesAVersion(string option, string value, string expected)
    {
        var runner = AssertRunner(Parse("uvx", option, value, "pkg"), RunnerKind.Uvx);

        Assert.Equal(expected, runner.Uvx!.Python);
    }

    [Fact]
    public void UvxShortPython_AcceptsTheEqualsForm()
    {
        var runner = AssertRunner(Parse("uvx", "-p=3.13", "pkg"), RunnerKind.Uvx);

        Assert.Equal("3.13", runner.Uvx!.Python);
    }

    [Theory]
    [InlineData("pkg@1.2", "pkg")]
    [InlineData("pkg[extra]", "pkg")]
    [InlineData("pkg[a,b]@latest", "pkg")]
    [InlineData("pkg==1.2", "pkg")]
    [InlineData("pkg>=1,<2", "pkg")]
    [InlineData("pkg~=1.4", "pkg")]
    [InlineData("pkg===1.0", "pkg")]
    [InlineData("mcp_server.time-2", "mcp_server.time-2")]
    [InlineData("a", "a")]
    public void UvxPackageSpecs_AreAccepted_AndTheCommandNameDropsTheVersion(string token, string commandName)
    {
        var runner = AssertRunner(Parse("uvx", token), RunnerKind.Uvx);

        Assert.Equal(token, runner.Uvx!.Package);
        Assert.Equal(commandName, runner.Uvx.CommandName);
    }

    [Theory]
    [InlineData("--index-url=https://u:t@h/simple", "--index-url")]
    [InlineData("--index-url", "--index-url")]
    [InlineData("--refresh", "--refresh")]
    [InlineData("-v", "-v")]
    [InlineData("-qq", "-q")]
    [InlineData("-p3.12", "-p")]
    [InlineData(@"-i\\host\share", "-i")]
    [InlineData("--quiet=1", "--quiet")]
    [InlineData("--", "--")]
    public void OtherUvxOptions_AreRefused_WithTheNameOnly(string option, string subject)
    {
        AssertRefused(Parse("uvx", option, "pkg"), RunnerRefusal.UvxOption, subject);
    }

    [Theory]
    [InlineData("-p", @"\\host\s\python.exe")]
    [InlineData("--python", @"C:\Python\python.exe")]
    [InlineData("--python", "pypy3.10")]
    [InlineData("--python", "3.12.1.4")]
    [InlineData("--from=--index-url=x", "b")]
    [InlineData("--with", "-e", ".")]
    [InlineData("--with", "./local")]
    [InlineData("--from", "git+https://github.com/x/y", "y")]
    [InlineData("--from", "file:///C:/w/x.whl", "x")]
    [InlineData(@"C:\w\x.whl")]
    [InlineData(@"\\host\share\x.whl")]
    [InlineData("https://h/x.whl")]
    [InlineData("pkg @ https://h/x.whl")]
    [InlineData("pkg[extra")]
    [InlineData("pkg-")]
    [InlineData("pkg\n")]
    [InlineData("--from", "pkg\n", "pkg")]
    [InlineData("--python", "3.12\n", "pkg")]
    [InlineData("--from")]
    [InlineData("--with")]
    public void UvxValues_OutsideTheIndexGrammar_AreRefused(params string[] args)
    {
        AssertRefused(Parse("uvx", args), RunnerRefusal.UvxValue, null);
    }

    [Fact]
    public void UvxOptionsAfterThePackage_AreServerArgs()
    {
        var runner = AssertRunner(Parse("uvx", "pkg", "-p", "3", "--index-url=x"), RunnerKind.Uvx);

        Assert.Null(runner.Uvx!.Python);
        Assert.Equal(["-p", "3", "--index-url=x"], runner.Arguments);
    }

    [Theory]
    [InlineData]
    [InlineData("-q")]
    [InlineData("--from", "a")]
    [InlineData("--from", "a", "C:\\x\\b.exe")]
    [InlineData("--from", "a", "b c")]
    [InlineData("--from", "a", "@1.2")]
    public void UvxWithoutAReadableCommand_IsUnsupported(params string[] args)
    {
        AssertRefused(Parse("uvx", args), RunnerRefusal.UnsupportedForm, "uv");
    }

    [Fact]
    public void Dnx_KeepsItsArguments()
    {
        var runner = AssertRunner(Parse("dnx", "Community.Mcp.DotNet@1.*", "--yes", "--", "--port", "1"), RunnerKind.Dnx);

        Assert.Equal(["Community.Mcp.DotNet@1.*", "--yes", "--", "--port", "1"], runner.Arguments);
    }

    [Fact]
    public void DotnetDnx_TakesTheArgumentsAfterDnx()
    {
        var runner = AssertRunner(Parse(@"C:\Program Files\dotnet\dotnet.exe", "dnx", "Pkg", "-y"), RunnerKind.Dnx);

        Assert.Equal(["Pkg", "-y"], runner.Arguments);
        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", runner.LauncherPathHint);
    }

    [Fact]
    public void DotnetDll_WithAFullPath_IsTheDll()
    {
        var runner = AssertRunner(Parse("dotnet", @"C:\srv\bin\..\x.dll", "--db", "c.json"), RunnerKind.DotnetDll);

        Assert.Equal(@"C:\srv\x.dll", runner.ScriptOrDll);
        Assert.Equal(["--db", "c.json"], runner.Arguments);
        Assert.Empty(runner.RuntimeOptions);
    }

    [Theory]
    [InlineData("x.dll", @"C:\work\x.dll")]
    [InlineData(@".\bin\x.dll", @"C:\work\bin\x.dll")]
    [InlineData(@"..\x.DLL", @"C:\x.DLL")]
    public void DotnetDll_RelativeToTheWorkingDirectory(string dll, string expected)
    {
        var runner = AssertRunner(ParseIn(WorkingDirectory, "dotnet", dll, "--a"), RunnerKind.DotnetDll);

        Assert.Equal(expected, runner.ScriptOrDll);
        Assert.Equal(["--a"], runner.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(@"relative\dir")]
    public void DotnetDll_RelativeWithoutAWorkingDirectory_IsRefused(string? workingDirectory)
    {
        AssertRefused(ParseIn(workingDirectory, "dotnet", @".\bin\x.dll"), RunnerRefusal.RelativePath, null);
    }

    [Fact]
    public void DotnetExecDll_IsTheDll()
    {
        var runner = AssertRunner(ParseIn(WorkingDirectory, "dotnet", "exec", "x.dll", "--a"), RunnerKind.DotnetDll);

        Assert.Equal(@"C:\work\x.dll", runner.ScriptOrDll);
        Assert.Equal(["--a"], runner.Arguments);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet.exe")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void DotnetAlone_IsUnsupported(string command)
    {
        AssertRefused(Parse(command), RunnerRefusal.UnsupportedForm, "dotnet");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "--project", "x")]
    [InlineData("tool", "run", "x")]
    [InlineData("exec")]
    [InlineData("exec", "--runtimeconfig", "x.json", "x.dll")]
    [InlineData("x.exe")]
    public void OtherDotnetForms_AreUnsupported(params string[] args)
    {
        AssertRefused(ParseIn(WorkingDirectory, "dotnet", args), RunnerRefusal.UnsupportedForm, "dotnet");
    }

    [Fact]
    public void Node_KeepsLeadingOptions_AndTheScriptArgs()
    {
        var runner = AssertRunner(
            Parse("node", "--require", "a", "--max-old-space-size=4096", "-r", "b", "--no-warnings", @"C:\srv\s.js", "--port", "-e"),
            RunnerKind.NodeScript);

        Assert.Equal(["--require", "a", "--max-old-space-size=4096", "-r", "b", "--no-warnings"], runner.RuntimeOptions);
        Assert.Equal(@"C:\srv\s.js", runner.ScriptOrDll);
        Assert.Equal(["--port", "-e"], runner.Arguments);
    }

    [Theory]
    [InlineData("--import")]
    [InlineData("--loader")]
    [InlineData("--experimental-loader")]
    public void NodeOptionsWithAValue_TakeTheNextItem(string option)
    {
        var runner = AssertRunner(Parse("node", option, "hook.mjs", @"C:\srv\s.js"), RunnerKind.NodeScript);

        Assert.Equal([option, "hook.mjs"], runner.RuntimeOptions);
        Assert.Equal(@"C:\srv\s.js", runner.ScriptOrDll);
    }

    [Theory]
    [InlineData("-e", "1")]
    [InlineData("-p", "1")]
    [InlineData("--eval", "1")]
    [InlineData("--eval=1")]
    [InlineData("--print", "1")]
    [InlineData("-i")]
    [InlineData("--interactive")]
    [InlineData("--test")]
    [InlineData("--run", "x")]
    [InlineData("-")]
    [InlineData("-pe", "1")]
    [InlineData("-c", "s.js")]
    [InlineData("--require")]
    [InlineData("--no-warnings", "-e", "1", "s.js")]
    public void RefusedNodeForms_AreUnsupported(params string[] args)
    {
        AssertRefused(ParseIn(WorkingDirectory, "node", args), RunnerRefusal.UnsupportedForm, "node");
    }

    [Theory]
    [InlineData("node")]
    [InlineData("node.exe")]
    public void NodeWithoutAScript_IsUnsupported(string command)
    {
        AssertRefused(Parse(command), RunnerRefusal.UnsupportedForm, "node");
        AssertRefused(Parse(command, "--no-warnings"), RunnerRefusal.UnsupportedForm, "node");
    }

    [Theory]
    [InlineData("s.js", @"C:\work\s.js")]
    [InlineData(@"dist\index.js", @"C:\work\dist\index.js")]
    public void NodeScript_RelativeToTheWorkingDirectory(string script, string expected)
    {
        var runner = AssertRunner(ParseIn(WorkingDirectory, "node", script), RunnerKind.NodeScript);

        Assert.Equal(expected, runner.ScriptOrDll);
    }

    [Fact]
    public void NodeScript_RelativeWithoutAWorkingDirectory_IsRefused()
    {
        AssertRefused(Parse("node", @"dist\index.js"), RunnerRefusal.RelativePath, null);
    }

    [Fact]
    public void ARunnerBehindCmd_KeepsItsWorkingDirectoryResolution()
    {
        var runner = AssertRunner(ParseIn(WorkingDirectory, "cmd", "/c", "node", "s.js", "--a"), RunnerKind.NodeScript);

        Assert.Equal(@"C:\work\s.js", runner.ScriptOrDll);
        Assert.Equal(["--a"], runner.Arguments);
    }
}
