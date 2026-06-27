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
}
