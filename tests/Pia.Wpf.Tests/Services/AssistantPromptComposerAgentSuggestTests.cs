using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// R7/G1: the <c>suggest_agent_mode</c> tool is injected into a turn's tool list ONLY when it is an
/// eligible interactive Chat turn on a tool-capable provider with no @-commands. Every other shape
/// (ineligible, ToolScope==None, @-command narrowed) leaves the tool out so those turns stay byte-stable.
/// </summary>
public class AssistantPromptComposerAgentSuggestTests
{
    private const string ToolName = "suggest_agent_mode";

    private static AIFunction Tool(string name) =>
        AIFunctionFactory.Create(() => string.Empty, name, $"{name} description");

    private static AiProvider ToolCapableProvider() => new()
    {
        Name = "Test",
        Endpoint = "https://example.test",
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
        IList<AITool> allTools = [Tool("recall"), Tool("query_todos")];
        plugins.GetAllTools().Returns(allTools);
        plugins.GetCombinedSystemPromptAdditions().Returns(string.Empty);
        return new AssistantPromptComposer(localization, plugins);
    }

    [Fact]
    public void Eligible_ToolCapable_NoAtCommands_InjectsSuggestTool()
    {
        var setup = Composer().PrepareTurn(
            Persona(), ToolCapableProvider(), [], tokenizationEnabled: false, suggestAgentModeEligible: true);

        Assert.Contains(setup.Tools!, t => t.Name == ToolName);
    }

    [Fact]
    public void NotEligible_DoesNotInject()
    {
        var setup = Composer().PrepareTurn(
            Persona(), ToolCapableProvider(), [], tokenizationEnabled: false, suggestAgentModeEligible: false);

        Assert.DoesNotContain(setup.Tools!, t => t.Name == ToolName);
    }

    [Fact]
    public void ToolScopeNone_NeverInjects_EvenWhenEligible()
    {
        // ToolScope==None → the no-tools path (SupportsTools false, Tools null): the suggestion can never appear.
        var setup = Composer().PrepareTurn(
            Persona(PersonaToolScope.None), ToolCapableProvider(), [], tokenizationEnabled: false, suggestAgentModeEligible: true);

        Assert.False(setup.SupportsTools);
        Assert.Null(setup.Tools);
    }

    [Fact]
    public void AtCommandsPresent_DoesNotInject_EvenWhenEligible()
    {
        var atCommands = new[] { new AtCommand { Domain = AtCommandDomain.Todo } };

        var setup = Composer().PrepareTurn(
            Persona(), ToolCapableProvider(), atCommands, tokenizationEnabled: false, suggestAgentModeEligible: true);

        Assert.DoesNotContain(setup.Tools!, t => t.Name == ToolName);
    }

    [Fact]
    public void DefaultArg_IsFalse_NoInjection()
    {
        // The default overload (no flag) preserves today's behavior at every untouched call site.
        var setup = Composer().PrepareTurn(Persona(), ToolCapableProvider(), [], tokenizationEnabled: false);

        Assert.DoesNotContain(setup.Tools!, t => t.Name == ToolName);
    }
}
