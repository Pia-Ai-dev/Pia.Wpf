using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The model cannot see where its file tools are rooted, so the tools path names the effective folder in
/// an environment block. The no-tools prompt has no file tools, so it must never carry one.
/// </summary>
public class AssistantPromptComposerEnvironmentTests
{
    private const string Root = @"C:\sandbox\workspace";

    private static AIFunction Tool(string name) =>
        AIFunctionFactory.Create(() => string.Empty, name, $"{name} description");

    private static AiProvider Provider() => new()
    {
        Name = "Test",
        Endpoint = "https://example.test",
        ProviderType = AiProviderType.OpenRouter,
        SupportsToolCalling = true,
    };

    private static Persona Persona(PersonaToolScope scope = PersonaToolScope.Full) => new()
    {
        Name = "Test",
        SystemPrompt = "You are helpful.",
        ToolScope = scope,
    };

    private static AssistantPromptComposer Composer()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        var plugins = Substitute.For<IPluginService>();
        IList<AITool> allTools = [Tool("list_files"), Tool("find_files"), Tool("read_file"), Tool("search_files")];
        plugins.GetAllTools().Returns(allTools);
        plugins.GetCombinedSystemPromptAdditions().Returns(string.Empty);
        return new AssistantPromptComposer(localization, plugins);
    }

    private static string Prompt(string? environmentRoot, PersonaToolScope scope = PersonaToolScope.Full) =>
        Composer().PrepareTurn(Persona(scope), Provider(), [], tokenizationEnabled: false, environmentRoot: environmentRoot)
            .SystemPrompt;

    // The identity block stamps the current minute, so two prompts composed either side of a minute
    // boundary differ on that line alone.
    private static string WithoutTimestamp(string prompt) =>
        Regex.Replace(prompt, @"The current date and time is [^\n]*", "<stamp>");

    [Fact]
    public void NoRoot_RendersNoEnvironmentSection_AndLeavesThePromptUnchanged()
    {
        var composer = Composer();
        var defaulted = composer.PrepareTurn(Persona(), Provider(), [], tokenizationEnabled: false).SystemPrompt;
        var explicitNull = composer.PrepareTurn(Persona(), Provider(), [], tokenizationEnabled: false, environmentRoot: null).SystemPrompt;

        Assert.DoesNotContain("## Environment", defaulted, StringComparison.Ordinal);
        Assert.Equal(WithoutTimestamp(defaulted), WithoutTimestamp(explicitNull));
    }

    [Fact]
    public void ToolsPath_WithRoot_NamesTheWorkingFolderInsideTheEnvBlock()
    {
        var prompt = Prompt(Root);

        Assert.Contains("## Environment", prompt, StringComparison.Ordinal);
        Assert.Contains($"<env>\nWorking folder: {Root}\nPlatform: Windows\n</env>", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Absolute paths are accepted only when they stay inside it.\n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoToolsPath_WithRoot_RendersNoEnvironmentSection()
    {
        var prompt = Prompt(Root, PersonaToolScope.None);

        Assert.DoesNotContain("## Environment", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(Root, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentBlock_CarriesNoDate()
    {
        var prompt = Prompt(Root);
        var block = prompt[prompt.IndexOf("## Environment", StringComparison.Ordinal)..];

        Assert.DoesNotContain("The current date and time is", block, StringComparison.Ordinal);
        Assert.DoesNotContain(DateTime.Now.Year.ToString(CultureInfo.InvariantCulture), block, StringComparison.Ordinal);
    }

    // Pinned whole: the rule's value is the promise it makes, and a paraphrase that says "in parallel"
    // would describe a dispatch the runtime does not do.
    private const string BatchingRule =
        "- When you need several independent lookups (file reads, searches, listings, recall), issue all of those tool calls together in one reply instead of one per turn — you get every result back in a single round-trip. Do not re-issue a search or read that has already returned its result.";

    [Fact]
    public void ToolsPath_CarriesTheBatchingRule()
    {
        Assert.Contains(BatchingRule, Prompt(null), StringComparison.Ordinal);
    }

    [Fact]
    public void AtCommandTurn_StillCarriesTheBatchingRuleAndTheEnvBlock()
    {
        // An @-command turn skips "## Tool Selection", so anything placed there would vanish on exactly
        // the turns that batch the most. The at-command hint trails the env block, hence Contains.
        var prompt = AtFilesTurn().SystemPrompt;

        Assert.DoesNotContain("## Tool Selection", prompt, StringComparison.Ordinal);
        Assert.Contains(BatchingRule, prompt, StringComparison.Ordinal);
        Assert.Contains($"<env>\nWorking folder: {Root}\nPlatform: Windows\n</env>", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoToolsPath_CarriesNoBatchingRule()
    {
        Assert.DoesNotContain(BatchingRule, Prompt(null, PersonaToolScope.None), StringComparison.Ordinal);
    }

    [Fact]
    public void BatchingRule_PromisesRoundTrips_NotConcurrentExecution()
    {
        // The runtime answers every call of a round before replying, but dispatches them one at a time.
        Assert.DoesNotContain("parallel", BatchingRule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("simultaneous", BatchingRule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("single round-trip", BatchingRule, StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionTree_SendsFilenameLookupsToFindFiles()
    {
        var prompt = Prompt(null);

        Assert.Contains("find_files to locate files by name or path glob", prompt, StringComparison.Ordinal);
        Assert.Contains("search_files to find text inside files", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AtFilesTurn_KeepsFindFilesInTheAllowedToolSet()
    {
        var tools = AtFilesTurn().Tools;

        Assert.NotNull(tools);
        Assert.Contains(tools, t => t.Name == "find_files");
    }

    private static AssistantTurnSetup AtFilesTurn() =>
        Composer().PrepareTurn(
            Persona(),
            Provider(),
            [new AtCommand { Domain = AtCommandDomain.Files, ItemTitle = "notes/todo.md" }],
            tokenizationEnabled: false,
            environmentRoot: Root);
}
