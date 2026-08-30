using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// A web-search provider gives the model no tool to call, so the system prompt is the only place it can
/// learn it can search at all — without that it hunts the tool list and reports it cannot search the web.
/// </summary>
public class AssistantPromptComposerWebSearchTests
{
    private static AIFunction Tool(string name) =>
        AIFunctionFactory.Create(() => string.Empty, name, $"{name} description");

    private static AiProvider Provider(bool enableWebSearch, AiProviderType type = AiProviderType.OpenRouter) => new()
    {
        Name = "Test",
        Endpoint = "https://example.test",
        ProviderType = type,
        SupportsToolCalling = true,
        EnableWebSearch = enableWebSearch,
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
        IList<AITool> allTools = [Tool("recall"), Tool("search_chats"), Tool("search_files")];
        plugins.GetAllTools().Returns(allTools);
        plugins.GetCombinedSystemPromptAdditions().Returns(string.Empty);
        return new AssistantPromptComposer(localization, plugins);
    }

    private static string Prompt(bool enableWebSearch, PersonaToolScope scope = PersonaToolScope.Full) =>
        Composer().PrepareTurn(Persona(scope), Provider(enableWebSearch), [], tokenizationEnabled: false)
            .SystemPrompt;

    [Fact]
    public void WebSearchOn_TellsTheModelItCanSearch_AndNotToSubstituteAnotherSearch()
    {
        var prompt = Prompt(enableWebSearch: true);

        Assert.Contains("## Web Search", prompt, StringComparison.Ordinal);
        Assert.Contains("Web search is enabled for this conversation", prompt, StringComparison.Ordinal);
        Assert.Contains("There is no web-search tool in your tool list", prompt, StringComparison.Ordinal);
        Assert.Contains("never substitute chat-history, vault or file search for it", prompt, StringComparison.Ordinal);
        // The escape hatch stays open: a turn that got no results must not answer from memory instead.
        Assert.Contains("If no results reached you, say that plainly", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void WebSearchOn_KeepsTheCitationRules()
    {
        var prompt = Prompt(enableWebSearch: true);

        Assert.Contains("[Title](https://example.com)", prompt, StringComparison.Ordinal);
        Assert.Contains("Never use reference-style brackets", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void WebSearchOff_SaysNothingAboutWebSearch()
    {
        var prompt = Prompt(enableWebSearch: false);

        Assert.DoesNotContain("## Web Search", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[Title](https://example.com)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PiaCloud_GetsTheSection_WithoutTheProviderFlag()
    {
        var prompt = Composer()
            .PrepareTurn(Persona(), Provider(false, AiProviderType.PiaCloud), [], tokenizationEnabled: false)
            .SystemPrompt;

        Assert.Contains("## Web Search", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolSelectionTree_RoutesACurrentWebQuestion_ToTheEnabledSearch()
    {
        var prompt = Prompt(enableWebSearch: true);

        Assert.Contains("6. Does the request need CURRENT information from the web", prompt, StringComparison.Ordinal);
        Assert.Contains("Web search is already enabled for this conversation", prompt, StringComparison.Ordinal);
        Assert.Contains("do not reach for search_chats, recall or search_files instead", prompt, StringComparison.Ordinal);
        // Step 5 must hand over rather than dead-end, or step 6 is unreachable.
        Assert.Contains("- NO → Continue to step 6.", prompt, StringComparison.Ordinal);
        Assert.Contains("- NO → Respond conversationally without tools.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolSelectionTree_WithoutWebSearch_SaysItCannotBrowse()
    {
        var prompt = Prompt(enableWebSearch: false);

        Assert.Contains("6. Does the request need CURRENT information from the web", prompt, StringComparison.Ordinal);
        Assert.Contains("You cannot browse. Say so plainly", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Web search is already enabled", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoToolsTurn_StillGetsTheWebSearchSection_ButNoTree()
    {
        var prompt = Prompt(enableWebSearch: true, scope: PersonaToolScope.None);

        Assert.Contains("## Web Search", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Tool Selection", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AtCommandTurn_DropsTheTree_SoTheWebStepGoesWithIt()
    {
        var atCommands = new[] { new AtCommand { Domain = AtCommandDomain.Todo } };

        var prompt = Composer()
            .PrepareTurn(Persona(), Provider(true), atCommands, tokenizationEnabled: false)
            .SystemPrompt;

        Assert.DoesNotContain("## Tool Selection", prompt, StringComparison.Ordinal);
        Assert.Contains("## Web Search", prompt, StringComparison.Ordinal);
    }
}
