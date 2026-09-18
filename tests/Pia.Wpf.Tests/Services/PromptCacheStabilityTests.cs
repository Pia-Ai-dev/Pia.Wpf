using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Providers cache the request prefix, so a minute-resolution clock anywhere in the system prompt or a
/// tool description costs the whole conversation its cache hit on every turn that crosses a minute.
/// </summary>
public class PromptCacheStabilityTests
{
    // A wall-clock stamp, not the fixed "timeOfDay=21:00" examples the reminder tools spell out.
    private static readonly Regex Timestamp = new(@"\d{4}-\d{2}-\d{2}[ T]\d{1,2}:\d{2}", RegexOptions.None);

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
        IList<AITool> noTools = [];
        plugins.GetAllTools().Returns(noTools);
        plugins.GetCombinedSystemPromptAdditions().Returns(string.Empty);
        return new AssistantPromptComposer(localization, plugins);
    }

    [Theory]
    [InlineData(PersonaToolScope.Full)]
    [InlineData(PersonaToolScope.None)]
    public void SystemPrompt_CarriesNoMinuteResolutionTimestamp(PersonaToolScope scope)
    {
        var prompt = Composer().PrepareTurn(Persona(scope), Provider(), [], tokenizationEnabled: true,
            environmentRoot: @"C:\sandbox\workspace", unattended: true).SystemPrompt;

        Assert.DoesNotMatch(Timestamp, prompt);
        Assert.DoesNotContain(DateTime.Now.ToString("HH:mm"), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ReminderToolDescriptions_CarryNoTimestamp()
    {
        var handler = new ReminderToolHandler(
            Substitute.For<IReminderService>(),
            Substitute.For<ILocalizationService>(),
            NullLogger<ReminderToolHandler>.Instance);

        AssertNoTimestamp(handler.GetTools());
    }

    [Fact]
    public void ScheduledJobToolDescriptions_CarryNoTimestamp()
    {
        var handler = new ScheduledJobToolHandler(
            Substitute.For<IScheduledJobService>(),
            Substitute.For<IProviderService>(),
            Substitute.For<IScheduledJobRunner>(),
            Substitute.For<ILocalizationService>(),
            NullLogger<ScheduledJobToolHandler>.Instance);

        AssertNoTimestamp(handler.GetTools());
    }

    [Fact]
    public void AppendTimeNote_PutsTheMinuteAfterTheUserText()
    {
        var stamped = AssistantPromptComposer.AppendTimeNote("what time is it?");

        Assert.StartsWith("what time is it?", stamped, StringComparison.Ordinal);
        // The minute itself is pinned by PersonaPromptShapeTests against a fixed clock; asserting it here
        // would fail on whichever run straddles a minute.
        Assert.Contains("The current local time is ", stamped, StringComparison.Ordinal);
    }

    // The note lands in a ChatRole.User message, which TokenizingAiClientService runs through the PII
    // detector — a yyyy-MM-dd in it would reach the model as [Phone_1].
    [Fact]
    public void TimeNote_SurvivesThePiiDetector()
    {
        var note = AssistantPromptComposer.AppendTimeNote(null);

        Assert.Empty(new StructuredPiiDetector().DetectPii(note));
    }

    private static void AssertNoTimestamp(IList<AITool> tools)
    {
        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            Assert.DoesNotMatch(Timestamp, tool.Description);
            Assert.DoesNotContain(DateTime.Now.ToString("yyyy-MM-dd"), tool.Description, StringComparison.Ordinal);
        }
    }
}
