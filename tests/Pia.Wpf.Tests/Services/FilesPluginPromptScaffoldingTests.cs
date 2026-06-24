using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Phase 5 (§5) registration &amp; prompt-gating contract for the enriched files tool pack.
/// Asserts the built-in plugin's <c>systemPromptAddition</c> enumerates <c>search_files</c>
/// and describes the line-numbered/windowed <c>read_file</c> and diff-approval <c>write_file</c>.
/// </summary>
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
        // The full tool roster is still present.
        Assert.Contains("list_files", config);
        Assert.Contains("read_file", config);
        Assert.Contains("write_file", config);
        Assert.Contains("delete_file", config);
    }

    [Fact]
    public void FilesPlugin_ConfigJson_DescribesEnrichedReadAndWrite()
    {
        var config = FilesSystemPromptAddition();

        // Enriched read_file: line-numbered LINE|CONTENT output + offset/limit windowing.
        Assert.Contains("LINE|CONTENT", config);
        Assert.Contains("offset", config);
        Assert.Contains("limit", config);
        // Enriched write_file: diff-preview approval.
        Assert.Contains("diff", config);
    }

    [Fact]
    public void FilesPlugin_ConfigJson_KeepsSandboxGuardrails()
    {
        var config = FilesSystemPromptAddition();

        // The containment guardrail must survive the enrichment. Per locked decision #2 the
        // resolver now ACCEPTS in-base absolute paths, so the prompt must NOT forbid absolutes
        // outright (that under-describes capability) — it must instead say paths stay inside the
        // folder, with '..' escape and out-of-folder absolutes rejected.
        Assert.Contains("RELATIVE", config);
        Assert.Contains("'..'", config);
        Assert.Contains("Settings > Assistant", config);
        Assert.Contains("inside the configured folder", config);
        Assert.DoesNotContain("must not contain absolute paths", config);
    }
}
