using System.Globalization;
using Pia.Shared;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Pins the shape byte-for-byte so a change fails here, not later as a drifted server preview.</summary>
public class PersonaPromptShapeTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 14, 30, 0, DateTimeKind.Local);

    [Fact]
    public void BuildIdentityBlock_WithoutGuardrails_IsPromptThenDateLine()
    {
        var block = PersonaPromptShape.BuildIdentityBlock(
            "You are a test persona.", guardrails: null, Now, CultureInfo.InvariantCulture);

        Assert.Equal(
            "You are a test persona.\nThe current date and time is 2026-08-01 14:30 (Saturday).",
            block);
    }

    [Fact]
    public void BuildIdentityBlock_WithGuardrails_InsertsThemAsOwnParagraph()
    {
        var block = PersonaPromptShape.BuildIdentityBlock(
            "You are a test persona.", "Never discuss pricing.", Now, CultureInfo.InvariantCulture);

        Assert.Equal(
            "You are a test persona.\n\nNever discuss pricing.\nThe current date and time is 2026-08-01 14:30 (Saturday).",
            block);
    }

    [Fact]
    public void BuildIdentityBlock_TrimsBothFields()
    {
        var block = PersonaPromptShape.BuildIdentityBlock(
            "  padded prompt  ", "\n  padded rail \n", Now, CultureInfo.InvariantCulture);

        Assert.StartsWith("padded prompt\n\npadded rail\n", block, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildIdentityBlock_RendersWeekdayInTheGivenCulture()
    {
        var block = PersonaPromptShape.BuildIdentityBlock(
            "You are a test persona.", guardrails: null, Now, CultureInfo.GetCultureInfo("de-DE"));

        Assert.Contains("(Samstag).", block, StringComparison.Ordinal);
    }
}
