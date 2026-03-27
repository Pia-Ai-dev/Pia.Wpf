using FluentAssertions;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class AtCommandParserTests
{
    // --- ShouldShowAutocomplete ---

    [Fact]
    public void ShouldShowAutocomplete_AtStartOfText_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("@", 1, out var fragment);
        result.Should().BeTrue();
        fragment.Should().BeEmpty();
    }

    [Fact]
    public void ShouldShowAutocomplete_AtAfterSpace_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("hello @M", 8, out var fragment);
        result.Should().BeTrue();
        fragment.Should().Be("M");
    }

    [Fact]
    public void ShouldShowAutocomplete_AtAfterNewline_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("hello\n@Mem", 10, out var fragment);
        result.Should().BeTrue();
        fragment.Should().Be("Mem");
    }

    [Fact]
    public void ShouldShowAutocomplete_AtInMiddleOfWord_ReturnsFalse()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("email@domain", 6, out _);
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldShowAutocomplete_WithDomainAndColon_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("@Memory:", 8, out var fragment);
        result.Should().BeTrue();
        fragment.Should().Be("Memory:");
    }

    [Fact]
    public void ShouldShowAutocomplete_WithDomainColonAndFilter_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("@Memory:Proj", 12, out var fragment);
        result.Should().BeTrue();
        fragment.Should().Be("Memory:Proj");
    }

    [Fact]
    public void ShouldShowAutocomplete_EmptyText_ReturnsFalse()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("", 0, out _);
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldShowAutocomplete_CaretNotAtTrigger_ReturnsFalse()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("@Memory hello", 13, out _);
        result.Should().BeFalse();
    }

    // --- ParseTriggerFragment ---

    [Fact]
    public void ParseTriggerFragment_Empty_ReturnsNulls()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("");
        domain.Should().BeNull();
        filter.Should().BeNull();
    }

    [Fact]
    public void ParseTriggerFragment_PartialDomain_ReturnsTier1Filter()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Mem");
        domain.Should().BeNull();
        filter.Should().Be("Mem");
    }

    [Fact]
    public void ParseTriggerFragment_ExactDomain_ReturnsDomainNoFilter()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Memory");
        domain.Should().Be(AtCommandDomain.Memory);
        filter.Should().BeNull();
    }

    [Fact]
    public void ParseTriggerFragment_DomainWithColon_ReturnsDomainEmptyFilter()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Memory:");
        domain.Should().Be(AtCommandDomain.Memory);
        filter.Should().BeEmpty();
    }

    [Fact]
    public void ParseTriggerFragment_DomainWithFilter_ReturnsBoth()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Todo:Buy");
        domain.Should().Be(AtCommandDomain.Todo);
        filter.Should().Be("Buy");
    }

    [Fact]
    public void ParseTriggerFragment_CaseInsensitive()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("reminder:Call");
        domain.Should().Be(AtCommandDomain.Reminder);
        filter.Should().Be("Call");
    }

    [Fact]
    public void ParseTriggerFragment_InvalidDomainWithColon_ReturnsNulls()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Unknown:stuff");
        domain.Should().BeNull();
        filter.Should().BeNull();
    }

    // --- ExtractAllCommands ---

    [Fact]
    public void ExtractAllCommands_SingleDomainOnly_ReturnsOne()
    {
        var commands = AtCommandParser.ExtractAllCommands("@Memory please save this");
        commands.Should().HaveCount(1);
        commands[0].Domain.Should().Be(AtCommandDomain.Memory);
        commands[0].ItemTitle.Should().BeNull();
    }

    [Fact]
    public void ExtractAllCommands_DomainWithItem_ReturnsWithTitle()
    {
        var commands = AtCommandParser.ExtractAllCommands("@Todo:Groceries add milk");
        commands.Should().HaveCount(1);
        commands[0].Domain.Should().Be(AtCommandDomain.Todo);
        commands[0].ItemTitle.Should().Be("Groceries");
    }

    [Fact]
    public void ExtractAllCommands_MultipleCommands_ReturnsAll()
    {
        var commands = AtCommandParser.ExtractAllCommands("@Memory check @Todo list items");
        commands.Should().HaveCount(2);
        commands[0].Domain.Should().Be(AtCommandDomain.Memory);
        commands[1].Domain.Should().Be(AtCommandDomain.Todo);
    }

    [Fact]
    public void ExtractAllCommands_NoCommands_ReturnsEmpty()
    {
        var commands = AtCommandParser.ExtractAllCommands("just a normal message");
        commands.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAllCommands_EmailAddress_NotTreatedAsCommand()
    {
        var commands = AtCommandParser.ExtractAllCommands("send to user@memory.com");
        commands.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAllCommands_AtStartOfNewline_Works()
    {
        var commands = AtCommandParser.ExtractAllCommands("first line\n@Reminder call Bob");
        commands.Should().HaveCount(1);
        commands[0].Domain.Should().Be(AtCommandDomain.Reminder);
    }

    // --- StripCommands ---

    [Fact]
    public void StripCommands_RemovesCommandPreservesText()
    {
        var result = AtCommandParser.StripCommands("@Memory remember I like coffee");
        result.Should().Be("remember I like coffee");
    }

    [Fact]
    public void StripCommands_RemovesDomainWithItem()
    {
        var result = AtCommandParser.StripCommands("@Todo:Groceries add milk please");
        result.Should().Be("add milk please");
    }

    [Fact]
    public void StripCommands_MultipleCommands_RemovesAll()
    {
        var result = AtCommandParser.StripCommands("@Memory check @Todo stuff");
        result.Should().Be("check stuff");
    }

    [Fact]
    public void StripCommands_NoCommands_ReturnsOriginal()
    {
        var result = AtCommandParser.StripCommands("just a normal message");
        result.Should().Be("just a normal message");
    }

    // --- Quoted multi-word titles ---

    [Fact]
    public void ExtractAllCommands_QuotedMultiWordTitle_ExtractsFull()
    {
        var commands = AtCommandParser.ExtractAllCommands("""@Memory:"Favorite color" change to yellow""");
        commands.Should().HaveCount(1);
        commands[0].Domain.Should().Be(AtCommandDomain.Memory);
        commands[0].ItemTitle.Should().Be("Favorite color");
    }

    [Fact]
    public void StripCommands_QuotedTitle_RemovesCommandPreservesText()
    {
        var result = AtCommandParser.StripCommands("""@Memory:"Favorite color" change to yellow""");
        result.Should().Be("change to yellow");
    }

    [Fact]
    public void ParseTriggerFragment_QuotedFilter_StripsQuotes()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("""Memory:"Fav""");
        domain.Should().Be(AtCommandDomain.Memory);
        filter.Should().Be("Fav");
    }

    [Fact]
    public void FormatItemTitle_SingleWord_NoQuotes()
    {
        var result = AtCommandParser.FormatItemTitle("Groceries");
        result.Should().Be("Groceries");
    }

    [Fact]
    public void FormatItemTitle_MultiWord_AddsQuotes()
    {
        var result = AtCommandParser.FormatItemTitle("Favorite color");
        result.Should().Be("\"Favorite color\"");
    }

    // --- GetTriggerStartIndex ---

    [Fact]
    public void GetTriggerStartIndex_FindsAt()
    {
        var index = AtCommandParser.GetTriggerStartIndex("hello @Mem", 10);
        index.Should().Be(6);
    }

    [Fact]
    public void GetTriggerStartIndex_AtStart()
    {
        var index = AtCommandParser.GetTriggerStartIndex("@Todo", 5);
        index.Should().Be(0);
    }

    [Fact]
    public void GetTriggerStartIndex_NoAt_ReturnsNegative()
    {
        var index = AtCommandParser.GetTriggerStartIndex("hello", 5);
        index.Should().Be(-1);
    }
}
