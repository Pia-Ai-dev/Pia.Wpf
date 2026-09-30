using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class ActionCardBuilderKnowledgeBaseCategoryTests
{
    private static ActionCardBuilder CreateBuilder()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}({string.Join(",", ci.ArgAt<object[]>(1))})");
        localization.Format("ActionCard_Title_Format", Arg.Any<object[]>())
            .Returns(ci => string.Join(" ", ci.ArgAt<object[]>(1)));

        return new ActionCardBuilder(localization, Substitute.For<ITokenMapService>());
    }

    private static PluginToolCall Call(string toolName, string? details = null, string? warning = null) =>
        new(toolName, Guid.NewGuid(), "kb-manager", "summary", details,
            () => Task.FromResult<object?>(null), Warning: warning);

    [Theory]
    [InlineData("upload_kb_document", "ActionCard_Action_Upload ActionCard_Category_KnowledgeBase")]
    [InlineData("update_kb_document", "ActionCard_Action_Update ActionCard_Category_KnowledgeBase")]
    [InlineData("set_kb_prompt", "ActionCard_Action_Update ActionCard_Category_KnowledgeBase")]
    [InlineData("delete_kb_document", "ActionCard_Action_Delete ActionCard_Category_KnowledgeBase")]
    public void EachWrite_IsAKnowledgeBaseCard_WithItsVerb(string tool, string title)
    {
        var card = CreateBuilder().Build(Call(tool), detokenize: false);

        Assert.Equal(ActionCardCategory.KnowledgeBase, card.Category);
        Assert.Equal(title, card.Title);
    }

    [Fact]
    public void AHandlerWarning_ReachesANonDestructiveCard_AndShows()
    {
        var card = CreateBuilder().Build(Call("upload_kb_document", warning: "stored unencrypted"), detokenize: false);

        Assert.False(card.IsDestructive);
        Assert.Equal("stored unencrypted", card.WarningText);
        Assert.True(card.HasWarning);
    }

    [Fact]
    public void ADelete_CarriesItsOwnWarning_PlusTheHandlersSharedWarning()
    {
        var card = CreateBuilder().Build(Call("delete_kb_document", warning: "shared"), detokenize: false);

        Assert.True(card.IsDestructive);
        Assert.Equal("Msg_Assistant_PermanentDeleteKbDocument\nshared", card.WarningText);
    }

    [Fact]
    public void Details_AreParsedAsKeyValueLines()
    {
        var card = CreateBuilder().Build(
            Call("upload_kb_document", "Knowledge base: Handbook\nDocument: Onboarding: day one\nSize: 2 KB"),
            detokenize: false);

        Assert.Contains(card.Details, d => d.Label == "Knowledge base" && d.Value == "Handbook");
        Assert.Contains(card.Details, d => d.Label == "Document" && d.Value == "Onboarding: day one");
    }

    [Fact]
    public void AnExistingCardWithoutAWarning_StaysWithout()
    {
        var card = CreateBuilder().Build(
            new PluginToolCall("create_todo", Guid.NewGuid(), "todo", "s", null, () => Task.FromResult<object?>(null)),
            detokenize: false);

        Assert.Null(card.WarningText);
        Assert.False(card.HasWarning);
    }

    [Theory]
    [InlineData("list_knowledge_bases", "Msg_Assistant_StatusReadingKnowledgeBase")]
    [InlineData("read_kb_document", "Msg_Assistant_StatusReadingKnowledgeBase")]
    [InlineData("upload_kb_document", "Msg_Assistant_StatusUpdatingKnowledgeBase")]
    [InlineData("delete_kb_document", "Msg_Assistant_StatusUpdatingKnowledgeBase")]
    public void StatusText_NamesTheKnowledgeBase(string tool, string key)
    {
        Assert.Equal(key, CreateBuilder().ResolveStatusText(tool));
    }

    [Fact]
    public void SuccessTitle_NamesTheKnowledgeBase()
    {
        Assert.Equal("Msg_Assistant_KnowledgeBaseUpdated", CreateBuilder().ResolveSuccessTitle("kb-manager"));
    }
}
