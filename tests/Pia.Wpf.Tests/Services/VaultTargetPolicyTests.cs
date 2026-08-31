using Microsoft.Extensions.AI;
using Pia.Infrastructure;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class VaultTargetPolicyTests
{
    [Theory]
    [InlineData(@"C:\ws\Vault\sources\a.md", "sources/a.md")]
    [InlineData(@"C:\ws\Vault\sources\sub\a.md", "sources/sub/a.md")]
    [InlineData(@"C:\ws\Vault\a.md", "sources/a.md")]
    [InlineData(@"C:\ws\Vault\deep\a.md", "sources/a.md")]
    [InlineData(@"C:\ws\Vault", "sources/<name>.md")]
    public void SuggestedReference_Theory(string resolvedPath, string expected)
        => Assert.Equal(expected, VaultTargetPolicy.SuggestedReference(@"C:\ws", resolvedPath));

    [Fact]
    public void WriteRefusal_NamesBothMemoryTools_AndTheVaultFolder()
    {
        var refusal = VaultTargetPolicy.WriteRefusal(@"C:\ws", @"C:\ws\Vault\sources\a.md");

        Assert.Contains(VaultTargetPolicy.CreateSourceToolName, refusal, StringComparison.Ordinal);
        Assert.Contains(VaultTargetPolicy.UpdateSourceToolName, refusal, StringComparison.Ordinal);
        Assert.Contains(AssistantWorkspace.VaultSubfolderName, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "create_source", false)]
    [InlineData(@"C:\ws", null, false)]
    [InlineData(@"C:\ws", "noop", false)]
    [InlineData(@"C:\ws", "noop,create_source", true)]
    [InlineData("", "create_source", false)]
    public void StepHintApplies_Theory(string? workspaceRoot, string? toolNames, bool expected)
    {
        var tools = toolNames?.Split(',').Select(n => (AITool)AIFunctionFactory.Create(() => string.Empty, n)).ToList();

        Assert.Equal(expected, VaultTargetPolicy.StepHintApplies(workspaceRoot, tools));
    }

    /// <summary>The ask stays with the tool that owns it: request_user_input's own description says calling it
    /// abandons the step, so the hint must not push it.</summary>
    [Fact]
    public void StepHint_NamesCreateSourceAndSourcesAndArtifactRef()
    {
        Assert.Contains(VaultTargetPolicy.CreateSourceToolName, VaultTargetPolicy.StepHint, StringComparison.Ordinal);
        Assert.Contains("sources/", VaultTargetPolicy.StepHint, StringComparison.Ordinal);
        Assert.Contains("artifact_ref", VaultTargetPolicy.StepHint, StringComparison.Ordinal);
        Assert.DoesNotContain("request_user_input", VaultTargetPolicy.StepHint, StringComparison.Ordinal);
    }
}
