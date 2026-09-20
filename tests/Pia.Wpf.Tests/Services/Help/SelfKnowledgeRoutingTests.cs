using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services.Help;

/// <summary>
/// Retrieval is worthless if the model never reaches for it. Two carriers say so, deliberately: the
/// plugin's prompt addition, which survives an @-command turn, and this lead-in above the tool tree,
/// whose terminal branch otherwise tells the model to answer conversationally without tools.
/// </summary>
public class SelfKnowledgeRoutingTests
{
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

    private static AssistantPromptComposer Composer(bool helpPackEnabled)
    {
        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);

        var plugins = Substitute.For<IPluginService>();
        IList<AITool> tools = helpPackEnabled
            ? [Tool("read_file"), Tool("pia_help"), Tool("pia_settings")]
            : [Tool("read_file")];
        plugins.GetAllTools().Returns(tools);
        plugins.GetCombinedSystemPromptAdditions().Returns(string.Empty);

        return new AssistantPromptComposer(localization, plugins);
    }

    [Fact]
    public void TheLeadInSitsAboveTheTreeWhenTheHelpPackIsOn()
    {
        var setup = Composer(helpPackEnabled: true).PrepareTurn(Persona(), Provider(), [], tokenizationEnabled: false);

        Assert.Contains(AssistantPromptComposer.SelfKnowledgeLeadIn, setup.SystemPrompt, StringComparison.Ordinal);
        Assert.True(
            setup.SystemPrompt.IndexOf(AssistantPromptComposer.SelfKnowledgeLeadIn, StringComparison.Ordinal)
            < setup.SystemPrompt.IndexOf("Follow this decision tree strictly:", StringComparison.Ordinal),
            "the lead-in has to come before the tree, or the tree's terminal branch wins");
    }

    [Fact]
    public void TheLeadInIsAbsentWhenTheUserHasTurnedTheHelpPackOff()
    {
        var setup = Composer(helpPackEnabled: false).PrepareTurn(Persona(), Provider(), [], tokenizationEnabled: false);

        Assert.DoesNotContain("pia_help", setup.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Follow this decision tree strictly:", setup.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTreeStillReadsAsSixNumberedStepsSoTheLeadInDidNotRenumberIt()
    {
        var setup = Composer(helpPackEnabled: true).PrepareTurn(Persona(), Provider(), [], tokenizationEnabled: false);

        Assert.Contains("- NO → Continue to step 6.", setup.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("1. Does the request mention a specific TIME", setup.SystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>An @-command turn drops the whole tool tree, which is why the plugin addition — not this
    /// lead-in — is the carrier that has to be present on every turn.</summary>
    [Fact]
    public void AnAtCommandTurnLosesTheLeadInWithTheRestOfTheTree()
    {
        var atCommands = new List<AtCommand> { new() { Domain = AtCommandDomain.Memory, ItemTitle = "x" } };

        var setup = Composer(helpPackEnabled: true).PrepareTurn(Persona(), Provider(), atCommands, tokenizationEnabled: false);

        Assert.DoesNotContain("## Tool Selection", setup.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantPromptComposer.SelfKnowledgeLeadIn, setup.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoToolsProviderGetsNeitherTheLeadInNorTheTools()
    {
        var provider = Provider();
        provider.SupportsToolCalling = false;

        var setup = Composer(helpPackEnabled: true).PrepareTurn(Persona(), provider, [], tokenizationEnabled: false);

        Assert.Null(setup.Tools);
        Assert.DoesNotContain("pia_help", setup.SystemPrompt, StringComparison.Ordinal);
    }
}
