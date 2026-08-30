using Pia.Shared;
using Xunit;

namespace Pia.Tests.Services;

public class BuiltInTemplateVoiceTests
{
    private const string VoiceMarker = "- Keep the user's own words wherever they already work";

    [Theory]
    [InlineData("00000001-0000-0000-0000-000000000001")] // Business Email
    [InlineData("00000001-0000-0000-0000-000000000002")] // Community Article
    [InlineData("00000001-0000-0000-0000-000000000003")] // Message to Friend
    [InlineData("00000001-0000-0000-0000-000000000005")] // Clarity & Grammar
    public void TransformingTemplates_PreserveTheUsersVoice(string id)
    {
        var template = BuiltInTemplates.All.Single(t => t.Id == id);

        Assert.Contains(VoiceMarker, template.Prompt);
    }

    [Theory]
    [InlineData("00000001-0000-0000-0000-000000000004")] // Grammar & Spelling Fix
    [InlineData("00000001-0000-0000-0000-000000000006")] // C# Code Prompt
    public void NonProseTemplates_AreLeftAlone(string id)
    {
        // Grammar & Spelling Fix already forbids rephrasing, and the C# prompt builder emits a spec
        // rather than prose, so the voice rules would only add noise.
        var template = BuiltInTemplates.All.Single(t => t.Id == id);

        Assert.DoesNotContain(VoiceMarker, template.Prompt);
    }
}
