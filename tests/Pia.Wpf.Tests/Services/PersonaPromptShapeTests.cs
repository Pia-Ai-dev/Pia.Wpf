using System.Globalization;
using Pia.Shared;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Pins the shared identity-block shape byte-for-byte. <see cref="PersonaPromptShape"/> is rendered by
/// two composers in two repos — the client's <c>AssistantPromptComposer</c> and the server's
/// <c>ManagedPersonaPreviewPrompt</c> (which pins the same bytes in
/// <c>ManagedPersonaPreviewPromptTests</c>). These exact-string pins are the point of the shared class:
/// a shape change must fail HERE, in the repo where it is made, not surface later as a drifted admin
/// preview after a submodule bump.
/// </summary>
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

    /// <summary>
    /// The provider is honoured for the weekday: the client passes CurrentCulture so end users see their
    /// own language; the server preview passes Invariant. If this stops holding, one side's deliberate
    /// culture choice is being silently ignored.
    /// </summary>
    [Fact]
    public void BuildIdentityBlock_RendersWeekdayInTheGivenCulture()
    {
        var block = PersonaPromptShape.BuildIdentityBlock(
            "You are a test persona.", guardrails: null, Now, CultureInfo.GetCultureInfo("de-DE"));

        Assert.Contains("(Samstag).", block, StringComparison.Ordinal);
    }
}
