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
        Assert.True(result);
        Assert.Empty(fragment);
    }

    [Fact]
    public void ShouldShowAutocomplete_AtAfterSpace_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("hello @M", 8, out var fragment);
        Assert.True(result);
        Assert.Equal("M", fragment);
    }

    [Fact]
    public void ShouldShowAutocomplete_AtAfterNewline_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("hello\n@Mem", 10, out var fragment);
        Assert.True(result);
        Assert.Equal("Mem", fragment);
    }

    [Fact]
    public void ShouldShowAutocomplete_AtInMiddleOfWord_ReturnsFalse()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("email@domain", 6, out _);
        Assert.False(result);
    }

    [Fact]
    public void ShouldShowAutocomplete_WithDomainAndColon_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("@Memory:", 8, out var fragment);
        Assert.True(result);
        Assert.Equal("Memory:", fragment);
    }

    [Fact]
    public void ShouldShowAutocomplete_WithDomainColonAndFilter_ReturnsTrue()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("@Memory:Proj", 12, out var fragment);
        Assert.True(result);
        Assert.Equal("Memory:Proj", fragment);
    }

    [Fact]
    public void ShouldShowAutocomplete_EmptyText_ReturnsFalse()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("", 0, out _);
        Assert.False(result);
    }

    [Fact]
    public void ShouldShowAutocomplete_CaretNotAtTrigger_ReturnsFalse()
    {
        var result = AtCommandParser.ShouldShowAutocomplete("@Memory hello", 13, out _);
        Assert.False(result);
    }

    // --- ParseTriggerFragment ---

    [Fact]
    public void ParseTriggerFragment_Empty_ReturnsNulls()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("");
        Assert.Null(domain);
        Assert.Null(filter);
    }

    [Fact]
    public void ParseTriggerFragment_PartialDomain_ReturnsTier1Filter()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Mem");
        Assert.Null(domain);
        Assert.Equal("Mem", filter);
    }

    [Fact]
    public void ParseTriggerFragment_ExactDomain_ReturnsDomainNoFilter()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Memory");
        Assert.Equal(AtCommandDomain.Memory, domain);
        Assert.Null(filter);
    }

    [Fact]
    public void ParseTriggerFragment_DomainWithColon_ReturnsDomainEmptyFilter()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Memory:");
        Assert.Equal(AtCommandDomain.Memory, domain);
        Assert.Empty(filter!);
    }

    [Fact]
    public void ParseTriggerFragment_DomainWithFilter_ReturnsBoth()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Todo:Buy");
        Assert.Equal(AtCommandDomain.Todo, domain);
        Assert.Equal("Buy", filter);
    }

    [Fact]
    public void ParseTriggerFragment_CaseInsensitive()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("reminder:Call");
        Assert.Equal(AtCommandDomain.Reminder, domain);
        Assert.Equal("Call", filter);
    }

    [Fact]
    public void ParseTriggerFragment_InvalidDomainWithColon_ReturnsNulls()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("Unknown:stuff");
        Assert.Null(domain);
        Assert.Null(filter);
    }

    // --- ExtractAllCommands ---

    [Fact]
    public void ExtractAllCommands_SingleDomainOnly_ReturnsOne()
    {
        var commands = AtCommandParser.ExtractAllCommands("@Memory please save this");
        Assert.Single(commands);
        Assert.Equal(AtCommandDomain.Memory, commands[0].Domain);
        Assert.Null(commands[0].ItemTitle);
    }

    [Fact]
    public void ExtractAllCommands_DomainWithItem_ReturnsWithTitle()
    {
        var commands = AtCommandParser.ExtractAllCommands("@Todo:Groceries add milk");
        Assert.Single(commands);
        Assert.Equal(AtCommandDomain.Todo, commands[0].Domain);
        Assert.Equal("Groceries", commands[0].ItemTitle);
    }

    [Fact]
    public void ExtractAllCommands_MultipleCommands_ReturnsAll()
    {
        var commands = AtCommandParser.ExtractAllCommands("@Memory check @Todo list items");
        Assert.Equal(2, commands.Count);
        Assert.Equal(AtCommandDomain.Memory, commands[0].Domain);
        Assert.Equal(AtCommandDomain.Todo, commands[1].Domain);
    }

    [Fact]
    public void ExtractAllCommands_NoCommands_ReturnsEmpty()
    {
        var commands = AtCommandParser.ExtractAllCommands("just a normal message");
        Assert.Empty(commands);
    }

    [Fact]
    public void ExtractAllCommands_EmailAddress_NotTreatedAsCommand()
    {
        var commands = AtCommandParser.ExtractAllCommands("send to user@memory.com");
        Assert.Empty(commands);
    }

    [Fact]
    public void ExtractAllCommands_AtStartOfNewline_Works()
    {
        var commands = AtCommandParser.ExtractAllCommands("first line\n@Reminder call Bob");
        Assert.Single(commands);
        Assert.Equal(AtCommandDomain.Reminder, commands[0].Domain);
    }

    // --- StripCommands ---

    [Fact]
    public void StripCommands_RemovesCommandPreservesText()
    {
        var result = AtCommandParser.StripCommands("@Memory remember I like coffee");
        Assert.Equal("remember I like coffee", result);
    }

    [Fact]
    public void StripCommands_RemovesDomainWithItem()
    {
        var result = AtCommandParser.StripCommands("@Todo:Groceries add milk please");
        Assert.Equal("add milk please", result);
    }

    [Fact]
    public void StripCommands_MultipleCommands_RemovesAll()
    {
        var result = AtCommandParser.StripCommands("@Memory check @Todo stuff");
        Assert.Equal("check stuff", result);
    }

    [Fact]
    public void StripCommands_NoCommands_ReturnsOriginal()
    {
        var result = AtCommandParser.StripCommands("just a normal message");
        Assert.Equal("just a normal message", result);
    }

    // --- Quoted multi-word titles ---

    [Fact]
    public void ExtractAllCommands_QuotedMultiWordTitle_ExtractsFull()
    {
        var commands = AtCommandParser.ExtractAllCommands("""@Memory:"Favorite color" change to yellow""");
        Assert.Single(commands);
        Assert.Equal(AtCommandDomain.Memory, commands[0].Domain);
        Assert.Equal("Favorite color", commands[0].ItemTitle);
    }

    [Fact]
    public void StripCommands_QuotedTitle_RemovesCommandPreservesText()
    {
        var result = AtCommandParser.StripCommands("""@Memory:"Favorite color" change to yellow""");
        Assert.Equal("change to yellow", result);
    }

    [Fact]
    public void ParseTriggerFragment_QuotedFilter_StripsQuotes()
    {
        var (domain, filter) = AtCommandParser.ParseTriggerFragment("""Memory:"Fav""");
        Assert.Equal(AtCommandDomain.Memory, domain);
        Assert.Equal("Fav", filter);
    }

    [Fact]
    public void FormatItemTitle_SingleWord_NoQuotes()
    {
        var result = AtCommandParser.FormatItemTitle("Groceries");
        Assert.Equal("Groceries", result);
    }

    [Fact]
    public void FormatItemTitle_MultiWord_AddsQuotes()
    {
        var result = AtCommandParser.FormatItemTitle("Favorite color");
        Assert.Equal("\"Favorite color\"", result);
    }

    // --- GetTriggerStartIndex ---

    [Fact]
    public void GetTriggerStartIndex_FindsAt()
    {
        var index = AtCommandParser.GetTriggerStartIndex("hello @Mem", 10);
        Assert.Equal(6, index);
    }

    [Fact]
    public void GetTriggerStartIndex_AtStart()
    {
        var index = AtCommandParser.GetTriggerStartIndex("@Todo", 5);
        Assert.Equal(0, index);
    }

    [Fact]
    public void GetTriggerStartIndex_NoAt_ReturnsNegative()
    {
        var index = AtCommandParser.GetTriggerStartIndex("hello", 5);
        Assert.Equal(-1, index);
    }
}
