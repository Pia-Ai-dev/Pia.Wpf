using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public class RegenerateInstructionsTests
{
    [Fact]
    public void Default_HasNoInstruction()
    {
        Assert.Null(RegenerateInstructions.For(RegenerateStyle.Default));
    }

    [Fact]
    public void Default_HasNoInstruction_EvenWithAPreviousAnswer()
    {
        Assert.Null(RegenerateInstructions.For(RegenerateStyle.Default, "the answer that was on screen"));
    }

    [Theory]
    [InlineData(RegenerateStyle.Shorten, "concise")]
    [InlineData(RegenerateStyle.Detailed, "thorough")]
    [InlineData(RegenerateStyle.Exportable, "document")]
    public void StyledInstructions_AreNonEmpty_AndOnTopic(RegenerateStyle style, string expectedFragment)
    {
        var instruction = RegenerateInstructions.For(style);

        Assert.False(string.IsNullOrWhiteSpace(instruction));
        Assert.Contains(expectedFragment, instruction!, StringComparison.OrdinalIgnoreCase);
        // Each styled instruction asks the model to keep the answer's language.
        Assert.Contains("same language", instruction!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RegenerateStyle.Shorten)]
    [InlineData(RegenerateStyle.Detailed)]
    [InlineData(RegenerateStyle.Exportable)]
    public void StyledInstructions_QuoteThePreviousAnswer_AndPointAtIt(RegenerateStyle style)
    {
        var instruction = RegenerateInstructions.For(style, "NVDA rose 3.76% to $217.55.");

        Assert.Contains("<previous_answer>\nNVDA rose 3.76% to $217.55.\n</previous_answer>", instruction!, StringComparison.Ordinal);
        Assert.Contains("the answer in <previous_answer>", instruction!, StringComparison.Ordinal);
        // The dangling referent is the bug this guards: nothing may still say "your previous answer".
        Assert.DoesNotContain("your previous answer", instruction!, StringComparison.OrdinalIgnoreCase);
        // Regenerate is offered on a failed turn too, so the quoted block may be an error placeholder.
        Assert.Contains("is an error or a notice that no answer was produced", instruction!, StringComparison.Ordinal);
        Assert.Contains("same language", instruction!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StyledInstructions_FallBackToThePlainWording_WhenThereIsNoAnswerToQuote(string? previousAnswer)
    {
        var instruction = RegenerateInstructions.For(RegenerateStyle.Exportable, previousAnswer);

        Assert.DoesNotContain("<previous_answer>", instruction!, StringComparison.Ordinal);
        Assert.Contains("your previous answer", instruction!, StringComparison.OrdinalIgnoreCase);
    }
}
