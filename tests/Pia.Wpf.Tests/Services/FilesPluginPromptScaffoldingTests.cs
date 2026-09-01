using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services;

public class FilesPluginPromptScaffoldingTests
{
    private static string FilesSystemPromptAddition()
    {
        var config = BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.FilesPluginId].ConfigJson;
        Assert.NotNull(config);
        return config!;
    }

    [Fact]
    public void FilesPlugin_ConfigJson_EnumeratesSearchFiles()
    {
        var config = FilesSystemPromptAddition();

        Assert.Contains("search_files", config);
        Assert.Contains("find_files", config);
        Assert.Contains("list_files", config);
        Assert.Contains("read_file", config);
        Assert.Contains("write_file", config);
        Assert.Contains("delete_file", config);
    }

    [Fact]
    public void FilesPlugin_ConfigJson_DescribesEnrichedReadAndWrite()
    {
        var config = FilesSystemPromptAddition();

        Assert.Contains("LINE|CONTENT", config);
        Assert.Contains("offset", config);
        Assert.Contains("limit", config);
        Assert.Contains("diff", config);
    }

    [Fact]
    public void FilesPlugin_ConfigJson_KeepsSandboxGuardrails()
    {
        var config = FilesSystemPromptAddition();

        // The resolver accepts in-base absolute paths, so a prompt that forbids absolutes outright would
        // under-describe the capability.
        Assert.Contains("RELATIVE", config);
        Assert.Contains("'..'", config);
        Assert.Contains("Settings > Assistant", config);
        Assert.Contains("inside the configured folder", config);
        Assert.DoesNotContain("must not contain absolute paths", config);
    }

    // The two halves of the two-stores disambiguation are asserted together on purpose: deleting either
    // one leaves the model with no way to tell the sandbox folder from the vault.
    [Fact]
    public void FilesPlugin_ConfigJson_SaysTheFolderIsNotTheVault()
    {
        var config = FilesSystemPromptAddition();

        Assert.Contains("NOT the user's memory vault", config);
        Assert.Contains("create_source", config);
        Assert.Contains("update_source", config);
        Assert.Contains("sources/", config);
    }

    [Fact]
    public void MemoryPlugin_ConfigJson_StillForbidsWriteFileForAVaultSource()
    {
        var config = BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.MemoryPluginId].ConfigJson;

        Assert.Contains("Do not use write_file for a vault source", config);
        Assert.Contains("create_source", config);
    }
}
