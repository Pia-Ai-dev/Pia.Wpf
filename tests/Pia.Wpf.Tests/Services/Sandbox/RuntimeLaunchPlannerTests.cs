using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class RuntimeLaunchPlannerTests
{
    private const string State = @"C:\Users\Jane\AppData\Local\Pia\McpSandbox\files.runtime";
    private const string Support = @"C:\Users\Jane\AppData\Local\Pia\McpSandboxSupport";
    private const string Container = "pia-mcp-files";
    private const string RegistryPath = @"C:\Windows\system32;C:\Windows";
    private const string NodeRoot = @"C:\Users\Jane\AppData\Local\Programs\nodejs";
    private const string DotnetRoot = @"C:\Users\Jane\AppData\Local\Microsoft\dotnet";
    private const string UvRoot = @"C:\Users\Jane\.local\bin";
    private const string Preload = Support + @"\node-sandbox.cjs";
    private const string DnxEntry = DotnetRoot + @"\sdk\10.0.100\dotnet.dll";
    private const string NodeOptions =
        "--preserve-symlinks --preserve-symlinks-main --require \"C:/Users/Jane/AppData/Local/Pia/McpSandboxSupport/node-sandbox.cjs\"";

    private static readonly IReadOnlyDictionary<string, string> NoProcessEnvironment = new Dictionary<string, string>();

    private static readonly string[] HomeDirectories =
    [
        State,
        State + @"\home",
        State + @"\home\AppData\Roaming",
        State + @"\home\AppData\Local\Packages\pia-mcp-files\AC\Temp",
    ];

    private static RuntimeInstall NodeInstall(string? entryScript, bool readable = false) =>
        new(RuntimeFamily.Node, NodeRoot, NodeRoot + @"\node.exe", "22.11.0", "10.9.0", entryScript, readable, AdminOwned: false);

    private static RuntimeInstall DotnetInstall(string? entryScript, bool readable = false) =>
        new(RuntimeFamily.Dotnet, DotnetRoot, DotnetRoot + @"\dotnet.exe", "10.0.100", null, entryScript, readable, AdminOwned: false);

    private static RuntimeResolution Ready(RunnerKind kind, RuntimeInstall install, string? programFolder = null) =>
        new(kind, RuntimeOutcome.Ready, RuntimeIssue.None, install,
            install.ReadableAlready || install.Family == RuntimeFamily.Uv ? [] : [install.Root], programFolder);

    private static RunnerRequest Runner(
        RunnerKind kind, string[] arguments, string? scriptOrDll = null, string[]? runtimeOptions = null, UvxSpec? uvx = null) =>
        new(kind, null, arguments, scriptOrDll, runtimeOptions ?? [], uvx);

    private static RuntimePlanInput Input(
        RunnerRequest runner,
        RuntimeResolution resolution,
        bool allowNetwork = true,
        string[]? sandboxRoots = null,
        IReadOnlyDictionary<string, string>? userEnvironment = null,
        string state = State,
        string support = Support,
        string container = Container,
        string? machineNodeOptions = null) =>
        new(runner, resolution, container, state, support, allowNetwork, sandboxRoots ?? [], RegistryPath, userEnvironment,
            machineNodeOptions);

    private static RuntimeLaunchPlan Plan(RuntimePlanInput input, IReadOnlyDictionary<string, string>? processEnvironment = null) =>
        RuntimeLaunchPlanner.Plan(input, () => new Dictionary<string, string>(processEnvironment ?? NoProcessEnvironment));

    private static Dictionary<string, string> Expected(params (string Key, string Value)[] variables)
    {
        var expected = new Dictionary<string, string>
        {
            ["USERPROFILE"] = State + @"\home",
            ["HOME"] = State + @"\home",
            ["HOMEDRIVE"] = "C:",
            ["HOMEPATH"] = @"\Users\Jane\AppData\Local\Pia\McpSandbox\files.runtime\home",
            ["APPDATA"] = State + @"\home\AppData\Roaming",
            ["LOCALAPPDATA"] = State + @"\home\AppData\Local",
        };
        foreach (var (key, value) in variables)
            expected.Add(key, value);
        return expected;
    }

    private static void AssertEnvironment(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual) =>
        Assert.Equal(
            expected.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value),
            actual.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value));

    [Fact]
    public void Npx_Allow_RunsTheBundledNpxWithTheCompleteEnvironment()
    {
        var npxCli = NodeRoot + @"\node_modules\npm\bin\npx-cli.js";
        var runner = Runner(RunnerKind.Npx, ["-y", "@modelcontextprotocol/server-everything"]);

        var plan = Plan(Input(runner, Ready(RunnerKind.Npx, NodeInstall(npxCli)), sandboxRoots: [@"C:\work\data"]));

        Assert.Equal(NodeRoot + @"\node.exe", plan.Command);
        Assert.Equal([npxCli, "-y", "@modelcontextprotocol/server-everything"], plan.Args);
        AssertEnvironment(
            Expected(
                ("NODE_OPTIONS", NodeOptions),
                ("PIA_SANDBOX_ROOTS", @"C:\work\data|" + NodeRoot + "|" + State + "|" + Support),
                ("PATH", NodeRoot + ";" + RegistryPath),
                ("npm_config_cache", State + @"\npm-cache"),
                ("npm_config_prefix", State + @"\npm-prefix"),
                ("npm_config_userconfig", State + @"\home\.npmrc"),
                ("npm_config_update_notifier", "false"),
                ("npm_config_audit", "false")),
            plan.Environment);
        Assert.Equal([NodeRoot], plan.ToolchainRoots);
        Assert.Equal(State, plan.StateDirectory);
        Assert.False(plan.StateReadOnly);
        Assert.Equal(Support, plan.SupportDirectory);
        Assert.Equal([.. HomeDirectories, State + @"\npm-cache", State + @"\npm-prefix"], plan.Preparation.Directories);
        Assert.Equal([new PreparedFile(Preload, NodeSandboxPreload.Content)], plan.Preparation.Files);
        Assert.Null(plan.Preparation.Uv);
    }

    [Fact]
    public void Npx_Deny_AddsTheOfflineNpmVariables()
    {
        var npxCli = NodeRoot + @"\node_modules\npm\bin\npx-cli.js";
        var runner = Runner(RunnerKind.Npx, ["-y", "@modelcontextprotocol/server-memory"]);

        var plan = Plan(Input(runner, Ready(RunnerKind.Npx, NodeInstall(npxCli)), allowNetwork: false));

        AssertEnvironment(
            Expected(
                ("NODE_OPTIONS", NodeOptions),
                ("PIA_SANDBOX_ROOTS", NodeRoot + "|" + State + "|" + Support),
                ("PATH", NodeRoot + ";" + RegistryPath),
                ("npm_config_cache", State + @"\npm-cache"),
                ("npm_config_prefix", State + @"\npm-prefix"),
                ("npm_config_userconfig", State + @"\home\.npmrc"),
                ("npm_config_update_notifier", "false"),
                ("npm_config_audit", "false"),
                ("npm_config_prefer_offline", "true"),
                ("npm_config_fetch_retries", "0")),
            plan.Environment);
    }

    [Fact]
    public void NpmExec_RunsNpmCliWithExecAndTheRest()
    {
        var npmCli = NodeRoot + @"\node_modules\npm\bin\npm-cli.js";
        var runner = Runner(RunnerKind.NpmExec, ["--yes", "@scope/server", "--port", "3"]);

        var plan = Plan(Input(runner, Ready(RunnerKind.NpmExec, NodeInstall(npmCli))));

        Assert.Equal(NodeRoot + @"\node.exe", plan.Command);
        Assert.Equal([npmCli, "exec", "--yes", "@scope/server", "--port", "3"], plan.Args);
        AssertEnvironment(
            Expected(
                ("NODE_OPTIONS", NodeOptions),
                ("PIA_SANDBOX_ROOTS", NodeRoot + "|" + State + "|" + Support),
                ("PATH", NodeRoot + ";" + RegistryPath),
                ("npm_config_cache", State + @"\npm-cache"),
                ("npm_config_prefix", State + @"\npm-prefix"),
                ("npm_config_userconfig", State + @"\home\.npmrc"),
                ("npm_config_update_notifier", "false"),
                ("npm_config_audit", "false")),
            plan.Environment);
        Assert.Equal([.. HomeDirectories, State + @"\npm-cache", State + @"\npm-prefix"], plan.Preparation.Directories);
        Assert.Equal(Support, plan.SupportDirectory);
    }

    [Fact]
    public void NodeScript_PutsRuntimeOptionsBeforeTheScriptAndSetsNoNpmVariables()
    {
        const string programFolder = @"C:\srv\mcp";
        const string script = programFolder + @"\dist\index.js";
        var runner = Runner(
            RunnerKind.NodeScript, ["--port", "3"], script, ["--require", programFolder + @"\register.js", "--no-warnings"]);

        var plan = Plan(Input(
            runner, Ready(RunnerKind.NodeScript, NodeInstall(null), programFolder), sandboxRoots: [programFolder]));

        Assert.Equal(NodeRoot + @"\node.exe", plan.Command);
        Assert.Equal(["--require", programFolder + @"\register.js", "--no-warnings", script, "--port", "3"], plan.Args);
        AssertEnvironment(
            Expected(
                ("NODE_OPTIONS", NodeOptions),
                ("PIA_SANDBOX_ROOTS", programFolder + "|" + NodeRoot + "|" + State + "|" + Support),
                ("PATH", NodeRoot + ";" + RegistryPath)),
            plan.Environment);
        Assert.Equal(HomeDirectories, plan.Preparation.Directories);
        Assert.Equal([new PreparedFile(Preload, NodeSandboxPreload.Content)], plan.Preparation.Files);
        Assert.Equal(Support, plan.SupportDirectory);
        Assert.False(plan.StateReadOnly);
    }

    private static Dictionary<string, string> DnxEnvironment() =>
        Expected(
            ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
            ("DOTNET_CLI_HOME", State + @"\home"),
            ("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "0"),
            ("DOTNET_NOLOGO", "1"),
            ("DOTNET_GENERATE_ASPNET_CERTIFICATE", "0"),
            ("DOTNET_CLI_UI_LANGUAGE", "en-US"),
            ("NUGET_PACKAGES", State + @"\nuget\packages"),
            ("NUGET_HTTP_CACHE_PATH", State + @"\nuget\http-cache"),
            ("NUGET_SCRATCH", State + @"\nuget\scratch"),
            ("NUGET_PLUGINS_CACHE_PATH", State + @"\nuget\plugins-cache"),
            ("PATH", DotnetRoot + ";" + RegistryPath));

    [Fact]
    public void Dnx_Allow_RunsTheSdkDnxWithTheNuGetOrgConfig()
    {
        var runner = Runner(RunnerKind.Dnx, ["Community.Mcp.DotNet@1.*"]);

        var plan = Plan(Input(runner, Ready(RunnerKind.Dnx, DotnetInstall(DnxEntry))));

        Assert.Equal(DotnetRoot + @"\dotnet.exe", plan.Command);
        Assert.Equal(["exec", DnxEntry, "dnx", "Community.Mcp.DotNet@1.*", "--yes"], plan.Args);
        AssertEnvironment(DnxEnvironment(), plan.Environment);
        Assert.Equal([DotnetRoot], plan.ToolchainRoots);
        Assert.False(plan.StateReadOnly);
        Assert.Null(plan.SupportDirectory);
        Assert.Equal([.. HomeDirectories, State + @"\nuget\packages"], plan.Preparation.Directories);
        var config = Assert.Single(plan.Preparation.Files);
        Assert.Equal(State + @"\home\AppData\Roaming\NuGet\NuGet.Config", config.Path);
        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<configuration>\r\n"
            + "  <packageSources>\r\n"
            + "    <clear />\r\n"
            + "    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" protocolVersion=\"3\" />\r\n"
            + "  </packageSources>\r\n"
            + "</configuration>\r\n",
            config.Content);
        Assert.Null(plan.Preparation.Uv);
    }

    [Fact]
    public void Dnx_Deny_PointsNuGetAtTheLocalPackagesOnly()
    {
        var runner = Runner(RunnerKind.Dnx, ["Community.Mcp.DotNet@1.*"]);

        var plan = Plan(Input(runner, Ready(RunnerKind.Dnx, DotnetInstall(DnxEntry)), allowNetwork: false));

        AssertEnvironment(DnxEnvironment(), plan.Environment);
        var config = Assert.Single(plan.Preparation.Files);
        Assert.Equal(State + @"\home\AppData\Roaming\NuGet\NuGet.Config", config.Path);
        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<configuration>\r\n"
            + "  <packageSources>\r\n"
            + "    <clear />\r\n"
            + "    <add key=\"pia-local\" value=\"" + State + "\\nuget\\packages\" />\r\n"
            + "  </packageSources>\r\n"
            + "</configuration>\r\n",
            config.Content);
    }

    [Fact]
    public void Dnx_Deny_EscapesTheLocalPackagesPath()
    {
        const string state = @"C:\Users\Tom & 'Jerry'\AppData\Local\Pia\McpSandbox\files.runtime";
        var runner = Runner(RunnerKind.Dnx, ["Community.Mcp.DotNet"]);

        var plan = Plan(Input(runner, Ready(RunnerKind.Dnx, DotnetInstall(DnxEntry)), allowNetwork: false, state: state));

        var config = Assert.Single(plan.Preparation.Files);
        Assert.Contains(
            "<add key=\"pia-local\" value=\"C:\\Users\\Tom &amp; &apos;Jerry&apos;\\AppData\\Local\\Pia\\McpSandbox\\files.runtime\\nuget\\packages\" />",
            config.Content);
    }

    [Theory]
    [InlineData("Pkg", "Pkg --yes")]
    [InlineData("Pkg@1.* --source feed", "Pkg@1.* --yes --source feed")]
    [InlineData("--prerelease Pkg", "--prerelease Pkg --yes")]
    [InlineData("Pkg -- arg", "Pkg --yes -- arg")]
    [InlineData("Pkg -- --yes", "Pkg --yes -- --yes")]
    [InlineData("Pkg --yes", "Pkg --yes")]
    [InlineData("-y Pkg", "-y Pkg")]
    [InlineData("Pkg --source feed -y", "Pkg --source feed -y")]
    [InlineData("-- Pkg", "-- Pkg")]
    [InlineData("--prerelease", "--prerelease")]
    public void Dnx_InsertsYesAfterThePackageUnlessTheUserGaveIt(string arguments, string expected)
    {
        var runner = Runner(RunnerKind.Dnx, arguments.Split(' '));

        var plan = Plan(Input(runner, Ready(RunnerKind.Dnx, DotnetInstall(DnxEntry))));

        Assert.Equal(["exec", DnxEntry, "dnx", .. expected.Split(' ')], plan.Args);
    }

    [Fact]
    public void DotnetDll_RunsTheDllWithACliHome()
    {
        const string dll = @"C:\srv\app\Server.dll";
        const string programFiles = @"C:\Program Files\dotnet";
        var install = new RuntimeInstall(
            RuntimeFamily.Dotnet, programFiles, programFiles + @"\dotnet.exe", "10.0.0", null, null, ReadableAlready: true, AdminOwned: true);
        var runner = Runner(RunnerKind.DotnetDll, ["--stdio"], dll);

        var plan = Plan(Input(runner, Ready(RunnerKind.DotnetDll, install, @"C:\srv\app"), sandboxRoots: [@"C:\srv\app"]));

        Assert.Equal(programFiles + @"\dotnet.exe", plan.Command);
        Assert.Equal([dll, "--stdio"], plan.Args);
        AssertEnvironment(
            Expected(
                ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
                ("DOTNET_CLI_HOME", State + @"\home"),
                ("DOTNET_NOLOGO", "1"),
                ("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "0"),
                ("PATH", programFiles + ";" + RegistryPath)),
            plan.Environment);
        Assert.Empty(plan.ToolchainRoots);
        Assert.False(plan.StateReadOnly);
        Assert.Null(plan.SupportDirectory);
        Assert.Equal(HomeDirectories, plan.Preparation.Directories);
        Assert.Empty(plan.Preparation.Files);
        Assert.Null(plan.Preparation.Uv);
    }

    [Fact]
    public void Uvx_PreparesAHostInstallAndLaunchesNothingYet()
    {
        var spec = new UvxSpec("mcp-server-time", "mcp-server-time", ["tzdata"], "3.12");
        var runner = Runner(RunnerKind.Uvx, ["--local-timezone", "Europe/Berlin"], uvx: spec);
        var install = new RuntimeInstall(
            RuntimeFamily.Uv, UvRoot, UvRoot + @"\uv.exe", "0.9.2", null, null, ReadableAlready: false, AdminOwned: false);
        var processEnvironment = new Dictionary<string, string>
        {
            ["Path"] = @"C:\Windows",
            ["HTTPS_PROXY"] = "http://proxy:8080",
            ["TEMP"] = @"C:\Users\Jane\AppData\Local\Temp",
            ["UV_NATIVE_TLS"] = "1",
            ["UV_CACHE_DIR"] = @"C:\elsewhere\uv",
            ["uv_index_url"] = "https://user:token@host/simple",
            ["UV_PYTHON"] = "3.11",
            ["PIA_DATA_DIR"] = @"C:\pia",
            ["pia_debug_flag"] = "1",
            ["MXC_FFI_DIR"] = @"C:\mxc",
        };

        var plan = Plan(Input(runner, Ready(RunnerKind.Uvx, install), sandboxRoots: [@"C:\work\data"]), processEnvironment);

        Assert.Equal(string.Empty, plan.Command);
        Assert.Empty(plan.Args);
        Assert.Empty(plan.Environment);
        Assert.Empty(plan.ToolchainRoots);
        Assert.Equal(State, plan.StateDirectory);
        Assert.True(plan.StateReadOnly);
        Assert.Null(plan.SupportDirectory);
        Assert.Equal([State, State + @"\tmp"], plan.Preparation.Directories);
        Assert.Empty(plan.Preparation.Files);

        var uv = Assert.IsType<UvPrepareRequest>(plan.Preparation.Uv);
        Assert.Equal(Container, uv.ContainerName);
        Assert.Equal(UvRoot + @"\uv.exe", uv.UvExecutable);
        Assert.Same(spec, uv.Spec);
        Assert.False(uv.Offline);
        Assert.Equal(State, uv.StateDirectory);
        Assert.Equal(["--local-timezone", "Europe/Berlin"], uv.ServerArgs);
        Assert.Equal([State + @"\uv-cache", State + @"\python-cache"], uv.CacheDirectoriesToPrune);
        AssertEnvironment(
            new Dictionary<string, string>
            {
                ["Path"] = @"C:\Windows",
                ["HTTPS_PROXY"] = "http://proxy:8080",
                ["UV_NATIVE_TLS"] = "1",
                ["UV_CACHE_DIR"] = State + @"\uv-cache",
                ["UV_PYTHON_INSTALL_DIR"] = State + @"\python",
                ["UV_PYTHON_CACHE_DIR"] = State + @"\python-cache",
                ["UV_TOOL_DIR"] = State + @"\tools",
                ["UV_TOOL_BIN_DIR"] = State + @"\tool-bin",
                ["UV_PYTHON_BIN_DIR"] = State + @"\python-bin",
                ["UV_NO_BUILD"] = "1",
                ["UV_NO_CONFIG"] = "1",
                ["UV_PYTHON_PREFERENCE"] = "only-managed",
                ["UV_PYTHON_INSTALL_REGISTRY"] = "0",
                ["UV_PYTHON_INSTALL_BIN"] = "0",
                ["TEMP"] = State + @"\tmp",
                ["TMP"] = State + @"\tmp",
            },
            uv.Environment);
    }

    [Fact]
    public void Uvx_Deny_IsOffline()
    {
        var spec = new UvxSpec("mcp-server-time", "mcp-server-time", [], null);
        var install = new RuntimeInstall(
            RuntimeFamily.Uv, UvRoot, UvRoot + @"\uv.exe", null, null, null, ReadableAlready: false, AdminOwned: false);

        var plan = Plan(Input(Runner(RunnerKind.Uvx, [], uvx: spec), Ready(RunnerKind.Uvx, install), allowNetwork: false));

        Assert.True(Assert.IsType<UvPrepareRequest>(plan.Preparation.Uv).Offline);
    }

    [Fact]
    public void NodeOptions_QuotesASupportPathWithASpaceInForwardSlashes()
    {
        const string state = @"C:\Users\Jane Doe\AppData\Local\Pia\McpSandbox\files.runtime";
        const string support = @"C:\Users\Jane Doe\AppData\Local\Pia\McpSandboxSupport";
        var runner = Runner(RunnerKind.NodeScript, [], @"C:\srv\index.js");

        var plan = Plan(Input(runner, Ready(RunnerKind.NodeScript, NodeInstall(null)), state: state, support: support));

        Assert.Equal(
            "--preserve-symlinks --preserve-symlinks-main --require \"C:/Users/Jane Doe/AppData/Local/Pia/McpSandboxSupport/node-sandbox.cjs\"",
            plan.Environment["NODE_OPTIONS"]);
        Assert.Equal(support + @"\node-sandbox.cjs", Assert.Single(plan.Preparation.Files).Path);
        Assert.Equal("C:", plan.Environment["HOMEDRIVE"]);
        Assert.Equal(@"\Users\Jane Doe\AppData\Local\Pia\McpSandbox\files.runtime\home", plan.Environment["HOMEPATH"]);
    }

    [Fact]
    public void NodeOptions_AppendsTheUsersOwnValue()
    {
        var runner = Runner(RunnerKind.NodeScript, [], @"C:\srv\index.js");
        var user = new Dictionary<string, string> { ["node_options"] = "--max-old-space-size=4096" };

        var plan = Plan(Input(runner, Ready(RunnerKind.NodeScript, NodeInstall(null)), userEnvironment: user));

        Assert.Equal(NodeOptions + " --max-old-space-size=4096", plan.Environment["NODE_OPTIONS"]);
    }

    /// <summary>The plan's NODE_OPTIONS replaces the one the container would inherit, so a machine value must be merged in.</summary>
    [Theory]
    [InlineData("--use-openssl-ca", null, NodeOptions + " --use-openssl-ca")]
    [InlineData("--use-openssl-ca", "--max-old-space-size=4096", NodeOptions + " --use-openssl-ca --max-old-space-size=4096")]
    [InlineData(" --use-openssl-ca ", "--use-openssl-ca", NodeOptions + " --use-openssl-ca")]
    [InlineData("--use-openssl-ca", "--max-old-space-size=4096  --use-openssl-ca", NodeOptions + " --max-old-space-size=4096  --use-openssl-ca")]
    [InlineData("--use-openssl-ca", "--use-openssl-ca-store", NodeOptions + " --use-openssl-ca --use-openssl-ca-store")]
    [InlineData("--require \"C:/ca/hook.js\"", "--require \"C:/app/trace.js\"",
        NodeOptions + " --require \"C:/ca/hook.js\" --require \"C:/app/trace.js\"")]
    [InlineData("   ", "--inspect", NodeOptions + " --inspect")]
    [InlineData(null, null, NodeOptions)]
    public void NodeOptions_AreThePreload_ThenTheMachineValue_ThenTheServersOwn(string? machine, string? server, string expected)
    {
        var runner = Runner(RunnerKind.NodeScript, [], @"C:\srv\index.js");
        var user = server is null ? null : new Dictionary<string, string> { ["NODE_OPTIONS"] = server };

        var plan = Plan(Input(runner, Ready(RunnerKind.NodeScript, NodeInstall(null)), userEnvironment: user, machineNodeOptions: machine));

        Assert.Equal(expected, plan.Environment["NODE_OPTIONS"]);
    }

    [Theory]
    [InlineData(RunnerKind.Npx)]
    [InlineData(RunnerKind.NpmExec)]
    public void NodeOptions_KeepTheMachineValue_ForEveryNodeKind(RunnerKind kind)
    {
        var entry = kind == RunnerKind.Npx ? NodeRoot + @"\node_modules\npm\bin\npx-cli.js" : NodeRoot + @"\node_modules\npm\bin\npm-cli.js";

        var plan = Plan(Input(Runner(kind, ["-y", "@x/y"]), Ready(kind, NodeInstall(entry)), machineNodeOptions: "--use-openssl-ca"));

        Assert.Equal(NodeOptions + " --use-openssl-ca", plan.Environment["NODE_OPTIONS"]);
    }

    [Fact]
    public void AMachineNodeOptions_StaysOutOfOtherRuntimes()
    {
        var dll = Runner(RunnerKind.DotnetDll, [], @"C:\srv\app\Server.dll");

        var plan = Plan(Input(dll, Ready(RunnerKind.DotnetDll, DotnetInstall(null)), machineNodeOptions: "--use-openssl-ca"));

        Assert.False(plan.Environment.ContainsKey("NODE_OPTIONS"));
    }

    [Fact]
    public void Path_PrefersTheUsersPathOverTheRegistryPath()
    {
        var user = new Dictionary<string, string> { ["Path"] = @"C:\tools;C:\bin" };
        var node = Runner(RunnerKind.NodeScript, [], @"C:\srv\index.js");
        var dll = Runner(RunnerKind.DotnetDll, [], @"C:\srv\app\Server.dll");

        var nodeWith = Plan(Input(node, Ready(RunnerKind.NodeScript, NodeInstall(null)), userEnvironment: user));
        var nodeWithout = Plan(Input(node, Ready(RunnerKind.NodeScript, NodeInstall(null))));
        var dllWith = Plan(Input(dll, Ready(RunnerKind.DotnetDll, DotnetInstall(null)), userEnvironment: user));
        var dllWithout = Plan(Input(dll, Ready(RunnerKind.DotnetDll, DotnetInstall(null))));

        Assert.Equal(NodeRoot + @";C:\tools;C:\bin", nodeWith.Environment["PATH"]);
        Assert.Equal(NodeRoot + ";" + RegistryPath, nodeWithout.Environment["PATH"]);
        Assert.Equal(DotnetRoot + @";C:\tools;C:\bin", dllWith.Environment["PATH"]);
        Assert.Equal(DotnetRoot + ";" + RegistryPath, dllWithout.Environment["PATH"]);
    }

    [Fact]
    public void SandboxRoots_AreDistinctAndJoinedWithAPipe()
    {
        var runner = Runner(RunnerKind.NodeScript, [], @"C:\work\rw;odd\index.js");
        string[] sandboxRoots = [@"C:\work\rw;odd", @"c:\WORK\RW;ODD", @"C:\data\ro", NodeRoot.ToUpperInvariant()];

        var plan = Plan(Input(runner, Ready(RunnerKind.NodeScript, NodeInstall(null)), sandboxRoots: sandboxRoots));

        Assert.Equal(
            [@"C:\work\rw;odd", @"C:\data\ro", NodeRoot.ToUpperInvariant(), State, Support],
            plan.Environment["PIA_SANDBOX_ROOTS"].Split('|'));
    }

    [Theory]
    [InlineData("pia-mcp-x")]
    [InlineData("pia-mcp-x-probe")]
    public void AcTemp_IsCreatedUnderTheContainersPackageFolder(string container)
    {
        var runner = Runner(RunnerKind.NodeScript, [], @"C:\srv\index.js");

        var plan = Plan(Input(runner, Ready(RunnerKind.NodeScript, NodeInstall(null)), container: container));

        Assert.Equal(State + @"\home\AppData\Local", plan.Environment["LOCALAPPDATA"]);
        Assert.Contains(State + @"\home\AppData\Local\Packages\" + container + @"\AC\Temp", plan.Preparation.Directories);
        Assert.DoesNotContain(plan.Preparation.Directories, d => d.Contains(@"\Packages\pia-mcp-files\", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RunnerKind.Npx)]
    [InlineData(RunnerKind.NodeScript)]
    [InlineData(RunnerKind.Dnx)]
    [InlineData(RunnerKind.DotnetDll)]
    public void ReadableAlready_GivesNoToolchainRoots(RunnerKind kind)
    {
        var node = kind is RunnerKind.Npx or RunnerKind.NodeScript;
        var install = node
            ? NodeInstall(NodeRoot + @"\node_modules\npm\bin\npx-cli.js", readable: true)
            : DotnetInstall(DnxEntry, readable: true);
        var runner = Runner(kind, ["Pkg"], kind is RunnerKind.NodeScript ? @"C:\srv\index.js" : @"C:\srv\app\Server.dll");

        var plan = Plan(Input(runner, Ready(kind, install)));

        Assert.Empty(plan.ToolchainRoots);
    }
}
