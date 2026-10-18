using System.IO;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Trees mirror what uv 0.13.1 wrote for <c>uv tool install mcp-server-time</c> on the build host.</summary>
public sealed class UvEntryPointResolverTests : IDisposable
{
    internal const string TimeReceipt = """
        [tool]
        requirements = [{ name = "mcp-server-time" }]
        entrypoints = [
            { name = "mcp-server-time", install-path = "C:/Users/Jane/AppData/Local/Pia/McpSandbox/time.runtime/tool-bin/mcp-server-time.exe", from = "mcp-server-time" },
        ]

        [tool.options]
        no-build = true
        """;

    private const string MultiLineReceipt = """
        [tool]
        requirements = [
            { name = "mcp-server-time" },
            { name = "tzdata" },
        ]
        python = 'C:\Users\Jane\AppData\Local\Pia\McpSandbox\time.runtime\python\cpython-3.15.0-windows-x86_64-none\python.exe'
        entrypoints = [
            { name = "mcp-server-time", install-path = "C:/Users/Jane/AppData/Local/Pia/McpSandbox/time.runtime/tool-bin/mcp-server-time.exe", from = "mcp-server-time" },
        ]

        [tool.options]
        no-build = true
        """;

    internal static readonly string[] TimeDistInfos =
    [
        "annotated_types-0.8.0", "anyio-4.15.1", "certifi-2026.10.18", "click-8.5.0", "httpx_sse-0.4.3",
        "httpx-0.28.1", "jsonschema_specifications-2025.9.1", "mcp_server_time-2026.10.10", "mcp-1.30.0",
        "pydantic_core-2.50.1", "python_dotenv-1.2.4", "typing_extensions-4.16.0", "tzdata-2026.5", "uvicorn-0.54.0",
    ];

    private static readonly UvxSpec Time = new("mcp-server-time", "mcp-server-time", [], null);

    private readonly string _state = Path.Combine(Path.GetTempPath(), "pia-uvresolver-" + Guid.NewGuid().ToString("N"), "time.runtime");

    public void Dispose() => TempPath.Remove(Path.GetDirectoryName(_state));

    /// <summary>Lays out tools\mcp-server-time as uv does, with the tool's own and a dependency's console scripts.</summary>
    internal static string InstallTimeTool(string state, string receipt = TimeReceipt)
    {
        var tool = Directory.CreateDirectory(Path.Combine(state, "tools", "mcp-server-time")).FullName;
        File.WriteAllText(Path.Combine(tool, "uv-receipt.toml"), receipt);
        Directory.CreateDirectory(Path.Combine(tool, "Scripts"));
        File.WriteAllText(Path.Combine(tool, "Scripts", "python.exe"), "");
        var sitePackages = Path.Combine(tool, "Lib", "site-packages");
        foreach (var distInfo in TimeDistInfos)
            Directory.CreateDirectory(Path.Combine(sitePackages, distInfo + ".dist-info"));

        EntryPoints(tool, "mcp_server_time-2026.10.10", "[console_scripts]\nmcp-server-time = mcp_server_time:main\n");
        EntryPoints(tool, "mcp-1.30.0", "[console_scripts]\nmcp = mcp.cli:app [cli]\n");
        EntryPoints(tool, "uvicorn-0.54.0", "[console_scripts]\nuvicorn = uvicorn.main:main\n");
        return tool;
    }

    private static void EntryPoints(string tool, string distInfo, string content)
    {
        var folder = Directory.CreateDirectory(Path.Combine(tool, "Lib", "site-packages", distInfo + ".dist-info")).FullName;
        File.WriteAllText(Path.Combine(folder, "entry_points.txt"), content);
    }

    [Fact]
    public void FindsTheToolsOwnConsoleScript()
    {
        var tool = InstallTimeTool(_state);

        var entry = UvEntryPointResolver.Find(_state, Time);

        Assert.Equal((tool, "mcp_server_time", "main"), entry);
    }

    [Theory]
    [InlineData("mcp-server-time@2026.10.10")]
    [InlineData("mcp-server-time==2026.10.10")]
    [InlineData("MCP_Server.Time")]
    [InlineData("mcp-server-time[extra]>=1,<3000")]
    public void ThePackageNameIsNormalized_AndItsVersionAndExtrasDropped(string package)
    {
        InstallTimeTool(_state);

        var entry = UvEntryPointResolver.Find(_state, Time with { Package = package });

        Assert.Equal("mcp_server_time", entry?.Module);
    }

    [Fact]
    public void ADependencyDeclaringTheSameConsoleScript_IsIgnored()
    {
        var tool = InstallTimeTool(_state);
        EntryPoints(tool, "aaa_hijack-1.0", "[console_scripts]\nmcp-server-time = aaa_hijack:main\n");
        EntryPoints(tool, "mcp-1.30.0", "[console_scripts]\nmcp-server-time = mcp.hijack:main\n");

        var entry = UvEntryPointResolver.Find(_state, Time);

        Assert.Equal("mcp_server_time", entry?.Module);
    }

    [Fact]
    public void ACommandMissingFromTheReceipt_GivesNull_EvenWhenADependencyDeclaresIt()
    {
        InstallTimeTool(_state);

        Assert.Null(UvEntryPointResolver.Find(_state, Time with { CommandName = "uvicorn" }));
        Assert.Null(UvEntryPointResolver.Find(_state, Time with { CommandName = "mcp" }));
    }

    [Fact]
    public void TheMultiLineRequirementsAndPythonPathVariant_StillResolves()
    {
        InstallTimeTool(_state, MultiLineReceipt);

        Assert.Equal("mcp_server_time", UvEntryPointResolver.Find(_state, Time)?.Module);
    }

    [Fact]
    public void ARequirementsLineWithTheCommandsName_IsNotAnEntryPoint()
    {
        InstallTimeTool(_state, """
            [tool]
            requirements = [{ name = "mcp-server-time", from = "mcp-server-time" }]
            entrypoints = []
            """);

        Assert.Null(UvEntryPointResolver.Find(_state, Time));
    }

    [Fact]
    public void ASingleLineEntryPointArray_IsReadPerTable()
    {
        InstallTimeTool(_state, """
            [tool]
            entrypoints = [{ name = "other", install-path = "C:/x/other.exe", from = "uvicorn" }, { name = "mcp-server-time", install-path = "C:/x/t.exe", from = "mcp-server-time" }]
            """);

        Assert.Equal("mcp_server_time", UvEntryPointResolver.Find(_state, Time)?.Module);
    }

    [Fact]
    public void AKeyInsideAQuotedPath_IsNeverReadAsAKey()
    {
        InstallTimeTool(_state, """
            [tool]
            entrypoints = [
                { name = "mcp-server-time", install-path = "C:/odd, from = 'uvicorn'/t.exe", from = "mcp-server-time" },
            ]
            """);

        Assert.Equal("mcp_server_time", UvEntryPointResolver.Find(_state, Time)?.Module);
    }

    [Fact]
    public void AnExtraSuffix_IsDropped()
    {
        var tool = Directory.CreateDirectory(Path.Combine(_state, "tools", "mcp")).FullName;
        File.WriteAllText(Path.Combine(tool, "uv-receipt.toml"), """
            [tool]
            requirements = [{ name = "mcp", extras = ["cli"] }]
            entrypoints = [
                { name = "mcp", install-path = "C:/x/mcp.exe", from = "mcp" },
            ]
            """);
        EntryPoints(tool, "mcp-1.30.0", "[console_scripts]\nmcp = mcp.cli:app [cli]\n");

        var entry = UvEntryPointResolver.Find(_state, new UvxSpec("mcp[cli]==1.30.0", "mcp", [], null));

        Assert.Equal((tool, "mcp.cli", "app"), entry);
    }

    [Theory]
    [InlineData("mcp-server-time = mcp_server_time:main; import os")]
    [InlineData("mcp-server-time = os.system('x'):main")]
    [InlineData("mcp-server-time = mcp_server_time:1main")]
    [InlineData("mcp-server-time = mcp_server_time")]
    [InlineData("mcp-server-time = mcp_server_time:ma'in")]
    public void InvalidIdentifiers_AreRejected(string line)
    {
        var tool = InstallTimeTool(_state);
        EntryPoints(tool, "mcp_server_time-2026.10.10", $"[console_scripts]\n{line}\n");

        Assert.Null(UvEntryPointResolver.Find(_state, Time));
    }

    [Fact]
    public void TheLineMustSitInConsoleScripts()
    {
        var tool = InstallTimeTool(_state);
        EntryPoints(tool, "mcp_server_time-2026.10.10", "[gui_scripts]\nmcp-server-time = mcp_server_time:main\n");

        Assert.Null(UvEntryPointResolver.Find(_state, Time));
    }

    [Fact]
    public void WhitespaceAndCrLf_AreTolerated()
    {
        var tool = InstallTimeTool(_state);
        EntryPoints(tool, "mcp_server_time-2026.10.10", "[console_scripts]\r\n  mcp-server-time=  mcp_server_time.app : run.main  \r\n");

        Assert.Equal((tool, "mcp_server_time.app", "run.main"), UvEntryPointResolver.Find(_state, Time));
    }

    [Fact]
    public void AMissingStep_GivesNull()
    {
        Assert.Null(UvEntryPointResolver.Find(_state, Time));

        var tool = InstallTimeTool(_state);
        Directory.Delete(Path.Combine(tool, "Lib", "site-packages", "mcp_server_time-2026.10.10.dist-info"), recursive: true);
        Assert.Null(UvEntryPointResolver.Find(_state, Time));

        File.Delete(Path.Combine(tool, "uv-receipt.toml"));
        Assert.Null(UvEntryPointResolver.Find(_state, Time));
    }

    [Theory]
    [InlineData("mcp-server-time", "mcp-server-time")]
    [InlineData("Mcp_Server.Time", "mcp-server-time")]
    [InlineData("mcp_server_time", "mcp-server-time")]
    [InlineData("a--_.b", "a-b")]
    [InlineData("Friendly.Bard", "friendly-bard")]
    [InlineData("pywin32", "pywin32")]
    public void NormalizeName_FollowsPep503(string name, string expected) =>
        Assert.Equal(expected, UvEntryPointResolver.NormalizeName(name));

    [Fact]
    public void Code_IsExactlyTheEntryPointCall() =>
        Assert.Equal(
            "import sys, importlib, functools; sys.argv[0] = 'mcp-server-time'; sys.exit(functools.reduce(getattr, 'main'.split('.'), importlib.import_module('mcp_server_time'))())",
            UvEntryPointResolver.Code("mcp-server-time", "mcp_server_time", "main"));

    [Theory]
    [InlineData("x'; import os; '", "m", "a")]
    [InlineData("x", "m;", "a")]
    [InlineData("x", "m", "a b")]
    public void Code_RefusesAnythingButNames(string command, string module, string attr) =>
        Assert.Throws<ArgumentException>(() => UvEntryPointResolver.Code(command, module, attr));

    [Fact]
    public void PythonFor_IsTheVenvsInterpreter() =>
        Assert.Equal(Path.Combine(@"C:\s\tools\t", "Scripts", "python.exe"), UvEntryPointResolver.PythonFor(@"C:\s\tools\t"));
}
