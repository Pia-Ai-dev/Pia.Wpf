using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The paste box has to accept what people actually copy out of Claude Desktop, and the prefix it derives is
/// what keeps a local server's tools from colliding with Pia's built-ins.
/// </summary>
public class LocalMcpJsonParserTests
{
    [Fact]
    public void ClaudeDesktopEnvelope_IsUnwrapped()
    {
        var result = LocalMcpJsonParser.Parse("""
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github"],
                  "env": { "GITHUB_TOKEN": "ghp_secret" }
                }
              }
            }
            """);

        Assert.True(result.Success);
        var definition = result.Definition!;
        Assert.Equal("github", definition.Name);
        Assert.Equal("npx", definition.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], definition.Args);
        Assert.Equal("ghp_secret", definition.Env["GITHUB_TOKEN"]);
        Assert.Equal("github", definition.ToolPrefix);
    }

    [Fact]
    public void SingleNamedEntry_WithoutEnvelope_IsUnwrapped()
    {
        var result = LocalMcpJsonParser.Parse("""{ "files": { "command": "uvx", "args": ["mcp-server-files"] } }""");

        Assert.True(result.Success);
        Assert.Equal("files", result.Definition!.Name);
        Assert.Equal("uvx", result.Definition.Command);
    }

    [Fact]
    public void BareServerObject_TakesTheFallbackName()
    {
        var result = LocalMcpJsonParser.Parse("""{ "command": "node", "args": ["server.js"] }""", "My server");

        Assert.True(result.Success);
        Assert.Equal("My server", result.Definition!.Name);
        Assert.Equal("my_server", result.Definition.ToolPrefix);
    }

    [Fact]
    public void WorkingDirectory_IsReadFromEitherSpelling()
    {
        Assert.Equal("C:/work",
            LocalMcpJsonParser.Parse("""{ "command": "node", "cwd": "C:/work" }""").Definition!.WorkingDirectory);
        Assert.Equal("C:/work",
            LocalMcpJsonParser.Parse("""{ "command": "node", "workingDirectory": "C:/work" }""").Definition!.WorkingDirectory);
    }

    [Fact]
    public void TrailingCommasAndComments_AreTolerated()
    {
        var result = LocalMcpJsonParser.Parse("""
            {
              // pasted out of a docs page
              "command": "npx",
              "args": ["-y", "server"],
            }
            """, "x");

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("""{ "url": "https://example.com/mcp" }""")]
    [InlineData("""{ "command": "npx", "type": "sse" }""")]
    [InlineData("""{ "command": "npx", "type": "streamable-http" }""")]
    public void RemoteServers_AreRejectedByName(string json)
    {
        Assert.Equal(LocalMcpParseError.RemoteTransport, LocalMcpJsonParser.Parse(json, "x").Error);
    }

    [Fact]
    public void MoreThanOneServer_IsRejectedRatherThanGuessed()
    {
        var result = LocalMcpJsonParser.Parse("""
            { "mcpServers": { "a": { "command": "x" }, "b": { "command": "y" } } }
            """);

        Assert.Equal(LocalMcpParseError.MultipleServers, result.Error);
    }

    [Theory]
    [InlineData("not json at all", LocalMcpParseError.InvalidJson)]
    [InlineData("", LocalMcpParseError.InvalidJson)]
    [InlineData("""{ "args": ["x"] }""", LocalMcpParseError.NoServerObject)]
    [InlineData("""{ "command": "  " }""", LocalMcpParseError.MissingCommand)]
    [InlineData("""{ "command": "npx", "args": "one string" }""", LocalMcpParseError.BadArgs)]
    [InlineData("""{ "command": "npx", "env": { "PORT": 8080 } }""", LocalMcpParseError.BadEnv)]
    public void MalformedInput_NamesTheProblem(string json, LocalMcpParseError expected)
    {
        Assert.Equal(expected, LocalMcpJsonParser.Parse(json, "x").Error);
    }
}

public class LocalMcpConfigTests
{
    private static string Reverse(string value) => new([.. value.Reverse()]);

    [Fact]
    public void ConfigJson_RoundTrips_AndEncryptsOnlyEnvValues()
    {
        var definition = new LocalMcpDefinition(
            "GitHub", "npx", ["-y", "server"],
            new Dictionary<string, string> { ["TOKEN"] = "secret" },
            "C:/work", "github", ["create_issue"], "desc");

        var json = LocalMcpConfig.ToConfigJson(definition, Reverse);

        Assert.Contains("terces", json);
        Assert.DoesNotContain("\"secret\"", json);
        Assert.Contains("TOKEN", json);

        // Field by field: a record's synthesised equality compares the collections by reference.
        var back = LocalMcpConfig.FromConfigJson("GitHub", "desc", json, Reverse)!;
        Assert.Equal(definition.Name, back.Name);
        Assert.Equal(definition.Command, back.Command);
        Assert.Equal(definition.Args, back.Args);
        Assert.Equal(definition.Env, back.Env);
        Assert.Equal(definition.WorkingDirectory, back.WorkingDirectory);
        Assert.Equal(definition.ToolPrefix, back.ToolPrefix);
        Assert.Equal(definition.AllowedTools, back.AllowedTools);
        Assert.Equal(definition.DefaultEnabled, back.DefaultEnabled);
    }

    [Fact]
    public void AllowedTools_NullMeansEveryTool_EmptyMeansNone()
    {
        var all = Round(null);
        var none = Round([]);

        Assert.Null(all.AllowedTools);
        Assert.NotNull(none.AllowedTools);
        Assert.Empty(none.AllowedTools);

        static LocalMcpDefinition Round(IReadOnlyList<string>? allowed)
        {
            var definition = new LocalMcpDefinition("n", "cmd", [], new Dictionary<string, string>(), null, "n", allowed);
            return LocalMcpConfig.FromConfigJson("n", null, LocalMcpConfig.ToConfigJson(definition, v => v), v => v)!;
        }
    }

    [Fact]
    public void ServerPushedConfig_IsNotLocal()
    {
        Assert.False(LocalMcpConfig.IsLocal("""{"transport":"stdio","command":"npx"}"""));
        Assert.False(LocalMcpConfig.IsLocal("not json"));
        Assert.False(LocalMcpConfig.IsLocal(null));
        Assert.True(LocalMcpConfig.IsLocal("""{"source":"local","command":"npx"}"""));
    }

    [Theory]
    [InlineData("GitHub", "github")]
    [InlineData("My Server 2", "my_server_2")]
    [InlineData("  spaced  out  ", "spaced_out")]
    [InlineData("@modelcontextprotocol/server-git", "modelcontextprotocol_ser")]
    [InlineData("2fa", "mcp_2fa")]
    [InlineData("???", "mcp")]
    [InlineData("", "mcp")]
    public void ToolPrefix_IsAValidFunctionNameToken(string name, string expected)
    {
        Assert.Equal(expected, LocalMcpConfig.DeriveToolPrefix(name));
    }
}

/// <summary>
/// Exposure is decided once, in the handler, because <c>GetTools</c> feeds both the route table and the
/// grant catalogue.
/// </summary>
public class McpToolExposureTests
{
    [Fact]
    public void NullAllowlist_ExposesEverything()
    {
        Assert.True(McpPluginToolHandler.IsExposed("anything", null));
    }

    [Fact]
    public void EmptyAllowlist_ExposesNothing()
    {
        Assert.False(McpPluginToolHandler.IsExposed("read_file", []));
    }

    [Fact]
    public void AllowlistMatching_IsCaseSensitive()
    {
        Assert.True(McpPluginToolHandler.IsExposed("read_file", ["read_file"]));
        Assert.False(McpPluginToolHandler.IsExposed("Read_File", ["read_file"]));
        Assert.False(McpPluginToolHandler.IsExposed("write_file", ["read_file"]));
    }

    [Fact]
    public void PrefixSeparatesALocalServerFromABuiltIn()
    {
        Assert.Equal("github__read_file", McpPluginToolHandler.ExposedName("read_file", "github"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoPrefix_LeavesTheServerName(string? prefix)
    {
        Assert.Equal("read_file", McpPluginToolHandler.ExposedName("read_file", prefix));
    }
}

/// <summary>The editor's two multi-line boxes are the only place a command's arguments and secrets are
/// typed, so what they split on is load-bearing.</summary>
public class McpServerEditorParsingTests
{
    [Fact]
    public void ArgumentsSplitPerLine_AndBlankLinesAreDropped()
    {
        Assert.Equal(
            ["-y", "@modelcontextprotocol/server-github"],
            Pia.ViewModels.McpServersSettingsViewModel.SplitLines("-y\r\n\r\n  @modelcontextprotocol/server-github  \n"));
    }

    [Fact]
    public void OnlyTheFirstEqualsSplitsAnEnvironmentLine()
    {
        var env = Pia.ViewModels.McpServersSettingsViewModel.ParseEnvironment(
            "TOKEN=abc=def==\r\nDSN=Server=x;Db=y");

        Assert.Equal("abc=def==", env["TOKEN"]);
        Assert.Equal("Server=x;Db=y", env["DSN"]);
    }

    [Theory]
    [InlineData("=novalue")]
    [InlineData("nokeyvalue")]
    [InlineData("   ")]
    public void ALineWithoutAKey_IsSkippedRatherThanStoredEmpty(string line)
    {
        Assert.Empty(Pia.ViewModels.McpServersSettingsViewModel.ParseEnvironment(line));
    }
}
