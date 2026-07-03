using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Pins the memory @-command tool mapping to the post-migration tool set: the
/// Memory domain must expose EXACTLY recall/remember/forget and none of the
/// retired tool names (query_memory, list_memories, create_object, …).
/// </summary>
public class AssistantPromptComposerMemoryToolsTests
{
    private static readonly string[] RetiredMemoryToolNames =
    [
        "query_memory", "list_memories", "create_object",
        "update_object", "append_to_list", "delete_object",
    ];

    private static AIFunction Tool(string name) =>
        AIFunctionFactory.Create(() => string.Empty, name, $"{name} description");

    private static AiProvider ToolCapableProvider() => new()
    {
        Name = "Test",
        Endpoint = "https://example.test",
        SupportsToolCalling = true,
    };

    private static AssistantPromptComposer Composer(IPluginService pluginService)
    {
        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        return new AssistantPromptComposer(localization, pluginService);
    }

    [Fact]
    public void MemoryAtCommand_ExposesExactlyRecallRememberForget()
    {
        var pluginService = Substitute.For<IPluginService>();
        // The full tool catalog offers the new verbs, the retired verbs, and an
        // unrelated tool. Only the new memory verbs may survive the gating filter.
        IList<AITool> allTools =
        [
            Tool("recall"), Tool("remember"), Tool("forget"),
            .. RetiredMemoryToolNames.Select(Tool),
            Tool("query_todos"),
        ];
        pluginService.GetAllTools().Returns(allTools);
        pluginService.GetCombinedSystemPromptAdditions().Returns(string.Empty);

        var composer = Composer(pluginService);
        var atCommands = new[] { new AtCommand { Domain = AtCommandDomain.Memory } };

        var setup = composer.PrepareTurn(
            Persona(),
            ToolCapableProvider(),
            atCommands,
            tokenizationEnabled: false);

        var names = setup.Tools!.Select(t => t.Name).ToHashSet();

        Assert.Equal(new HashSet<string> { "recall", "remember", "forget" }, names);
        foreach (var retired in RetiredMemoryToolNames)
            Assert.DoesNotContain(retired, names);
    }

    private static Persona Persona() => new()
    {
        Name = "Test",
        SystemPrompt = "You are helpful.",
        ToolScope = PersonaToolScope.Full,
    };
}
