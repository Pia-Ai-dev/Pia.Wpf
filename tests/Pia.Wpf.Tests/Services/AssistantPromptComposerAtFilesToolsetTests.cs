using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// An @Files tag names context, not a domain: a turn tagged only with files keeps the full toolset.
/// </summary>
public class AssistantPromptComposerAtFilesToolsetTests
{
    private const string McpTool = "srv__do_thing";
    private const string OnlyListedSentence = "Only the tools listed below";

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
        IList<AITool> allTools =
        [
            Tool(McpTool),
            Tool("git_status"),
            Tool("git_diff"),
            Tool("read_file"),
            Tool("write_file"),
            Tool("recall"),
            Tool("query_todos"),
            .. AssistantPromptComposer.RoutineToolNames.Select(Tool),
        ];
        plugins.GetAllTools().Returns(allTools);
        return new AssistantPromptComposer(localization, plugins);
    }

    private static AtCommand Files(string? path = null) => new() { Domain = AtCommandDomain.Files, ItemTitle = path };

    private static AtCommand Todo() => new() { Domain = AtCommandDomain.Todo };

    private static AssistantTurnSetup Turn(
        IReadOnlyList<AtCommand> atCommands,
        PersonaToolScope scope = PersonaToolScope.Full,
        bool unattended = false,
        bool suggestAgentModeEligible = false) =>
        Composer().PrepareTurn(Persona(scope), Provider(), atCommands, tokenizationEnabled: false,
            suggestAgentModeEligible: suggestAgentModeEligible, unattended: unattended);

    public static TheoryData<AtCommand[]> FilesOnlyTags => new()
    {
        { [Files("flows/close.md")] },
        { [Files()] },
        { [Files("flows/close.md"), Files("notes/todo.md")] },
    };

    [Theory]
    [MemberData(nameof(FilesOnlyTags))]
    public void FilesOnlyTurn_KeepsMcpAndGitTools(AtCommand[] tags)
    {
        var tools = Turn(tags).Tools;

        Assert.NotNull(tools);
        Assert.Contains(tools, t => t.Name == McpTool);
        Assert.Contains(tools, t => t.Name == "git_status");
        Assert.Contains(tools, t => t.Name == "git_diff");
        Assert.Contains(tools, t => t.Name == "recall");
    }

    [Theory]
    [MemberData(nameof(FilesOnlyTags))]
    public void FilesOnlyTurn_GetsTheSameToolsAsAnUntaggedTurn(AtCommand[] tags)
    {
        var untagged = Turn([]).Tools!.Select(t => t.Name);
        var tagged = Turn(tags).Tools!.Select(t => t.Name);

        Assert.Equal(untagged, tagged);
    }

    [Fact]
    public void FilesOnlyTurn_KeepsTheToolSelectionTree()
    {
        Assert.Contains("## Tool Selection", Turn([Files("flows/close.md")]).SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesOnlyTurn_HintDoesNotClaimTheToolsAreRestricted()
    {
        var prompt = Turn([Files("flows/close.md")]).SystemPrompt;

        Assert.DoesNotContain(OnlyListedSentence, prompt, StringComparison.Ordinal);
        Assert.Contains("targets the file at relative path \"flows/close.md\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesOnlyTurn_StillLeavesSuggestAgentModeOut()
    {
        var tools = Turn([Files("flows/close.md")], suggestAgentModeEligible: true).Tools;

        Assert.NotNull(tools);
        Assert.DoesNotContain(tools, t => t.Name == "suggest_agent_mode");
    }

    [Fact]
    public void FilesWithTodo_StaysNarrowedToTheUnionOfTheTaggedDomains()
    {
        var setup = Turn([Files("flows/close.md"), Todo()]);

        Assert.NotNull(setup.Tools);
        Assert.DoesNotContain(setup.Tools, t => t.Name == McpTool);
        Assert.DoesNotContain(setup.Tools, t => t.Name.StartsWith("git_", StringComparison.Ordinal));
        Assert.DoesNotContain(setup.Tools, t => t.Name == "recall");
        Assert.Contains(setup.Tools, t => t.Name == "read_file");
        Assert.Contains(setup.Tools, t => t.Name == "query_todos");
        Assert.DoesNotContain("## Tool Selection", setup.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(OnlyListedSentence, setup.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesOnlyTurn_WithToolScopeNone_HasNoTools()
    {
        var setup = Turn([Files("flows/close.md")], PersonaToolScope.None);

        Assert.False(setup.SupportsTools);
        Assert.Null(setup.Tools);
    }

    [Fact]
    public void FilesOnlyUnattendedTurn_StillWithholdsTheRoutineTools()
    {
        var tools = Turn([Files("flows/close.md")], unattended: true).Tools;

        Assert.NotNull(tools);
        Assert.Contains(tools, t => t.Name == McpTool);
        Assert.DoesNotContain(tools, t => AssistantPromptComposer.RoutineToolNames.Contains(t.Name));
    }
}
