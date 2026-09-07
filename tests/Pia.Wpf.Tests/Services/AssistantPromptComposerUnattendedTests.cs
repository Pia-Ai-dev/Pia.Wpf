using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// A fired routine used to receive its goal as a plain user message under the ordinary chat prompt, with the
/// routine-management tools still in the list — so a goal phrased as a setup brief read as one and the run
/// asked when it should run instead of running. These pin both halves of the framing that stops that.
/// </summary>
public class AssistantPromptComposerUnattendedTests
{
    private const string RoutinePluginAddition = "You can schedule recurring research jobs.";
    private const string MemoryPluginAddition = "You have a memory vault.";

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

    private static (AssistantPromptComposer Composer, IPluginService Plugins) Build()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);

        var plugins = Substitute.For<IPluginService>();
        IList<AITool> allTools =
        [
            Tool("recall"),
            Tool("create_todo"),
            .. AssistantPromptComposer.RoutineToolNames.Select(Tool),
        ];
        plugins.GetAllTools().Returns(allTools);
        // The routine family's own handler owns the first addition, so excluding the family must drop it.
        plugins.GetCombinedSystemPromptAdditions(null).Returns($"{RoutinePluginAddition}\n\n{MemoryPluginAddition}");
        plugins.GetCombinedSystemPromptAdditions(Arg.Is<IReadOnlySet<string>>(s => s.Contains("create_scheduled_research")))
            .Returns(MemoryPluginAddition);

        return (new AssistantPromptComposer(localization, plugins), plugins);
    }

    private static AssistantTurnSetup Setup(bool unattended, PersonaToolScope scope = PersonaToolScope.Full) =>
        Build().Composer.PrepareTurn(Persona(scope), Provider(), [], tokenizationEnabled: false, unattended: unattended);

    [Fact]
    public void Unattended_TellsTheModelItIsTheRun_AndNotToAskWhenTheGoalLeavesSomethingOpen()
    {
        var prompt = Setup(unattended: true).SystemPrompt;

        Assert.Contains("## Unattended Run", prompt, StringComparison.Ordinal);
        Assert.Contains("This turn runs unattended", prompt, StringComparison.Ordinal);
        Assert.Contains("already exists and is fully configured", prompt, StringComparison.Ordinal);
        Assert.Contains("not a request to set anything up", prompt, StringComparison.Ordinal);
        Assert.Contains("never offer to create, change or confirm a schedule", prompt, StringComparison.Ordinal);
        Assert.Contains("so do not ask one", prompt, StringComparison.Ordinal);
        // The escape hatch: silence is not the alternative to asking — an assumption, stated, is.
        Assert.Contains("take the reasonable reading", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFraming_NeverClaimsAScheduleFired_BecauseAnAssignmentLandsHereToo()
    {
        // "Run in background" launches with AgentRunTrigger.User and a delegated child run with neither, so
        // an opener naming a schedule would be a false premise the model then narrates back.
        var prompt = Setup(unattended: true).SystemPrompt;

        Assert.Contains("background assignment", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("A schedule fired this turn", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("the next scheduled fire", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInteractiveTurn_IsLeftExactlyAsItWas()
    {
        var interactive = Setup(unattended: false).SystemPrompt;

        Assert.DoesNotContain("## Unattended Run", interactive, StringComparison.Ordinal);
        Assert.DoesNotContain("A schedule fired this turn.", interactive, StringComparison.Ordinal);
        Assert.Contains(RoutinePluginAddition, interactive, StringComparison.Ordinal);
    }

    [Fact]
    public void Unattended_WithholdsTheRoutineTools_SoTheRunCannotBeAskedToConfigureItself()
    {
        var unattended = Setup(unattended: true);
        var interactive = Setup(unattended: false);

        Assert.NotEmpty(AssistantPromptComposer.RoutineToolNames);  // non-vacuity
        foreach (var name in AssistantPromptComposer.RoutineToolNames)
        {
            Assert.DoesNotContain(name, unattended.Tools!.Select(t => t.Name), StringComparer.Ordinal);
            Assert.Contains(name, interactive.Tools!.Select(t => t.Name), StringComparer.Ordinal);
        }

        // Only that family goes: withholding more would silently narrow every scheduled run.
        Assert.Contains("recall", unattended.Tools!.Select(t => t.Name), StringComparer.Ordinal);
        Assert.Contains("create_todo", unattended.Tools!.Select(t => t.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void Unattended_AlsoWithholdsTheRoutinePluginsPrompt_SoNoProseNamesAToolThatIsGone()
    {
        var prompt = Setup(unattended: true).SystemPrompt;

        Assert.DoesNotContain(RoutinePluginAddition, prompt, StringComparison.Ordinal);
        Assert.Contains(MemoryPluginAddition, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRoutineFamily_IsTheSameSetTheAtCommandDomainNames()
    {
        var mapped = AssistantPromptComposer.GetAtCommandToolMapping(AtCommandDomain.Routine).ToolNames;

        Assert.Equal(mapped.OrderBy(n => n, StringComparer.Ordinal), AssistantPromptComposer.RoutineToolNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void TheFlagRidesOnTheSetup_SoAStepPersonaCanMirrorTheRun()
    {
        Assert.True(Setup(unattended: true).Unattended);
        Assert.False(Setup(unattended: false).Unattended);
    }

    [Fact]
    public void ANoToolsPersona_StillGetsTheFraming()
    {
        var prompt = Setup(unattended: true, scope: PersonaToolScope.None).SystemPrompt;

        Assert.Null(Setup(unattended: true, scope: PersonaToolScope.None).Tools);
        Assert.Contains("## Unattended Run", prompt, StringComparison.Ordinal);
        Assert.Contains("## Output Format", prompt, StringComparison.Ordinal);
    }
}
