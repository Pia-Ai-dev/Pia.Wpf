using System.Text.Json;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>Off by default because with it on an unattended run can overwrite assistant-folder files with nobody
/// watching; the JSON round-trip is the only automated proof the settings CheckBox can persist it.</summary>
public class AppSettingsAgentAutonomyTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void AgentRunAutoApproveBuiltInWrites_DefaultsOff()
    {
        Assert.False(new AppSettings().AgentRunAutoApproveBuiltInWrites);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_PreservesAgentRunAutoApproveBuiltInWrites(bool enabled)
    {
        var original = new AppSettings { AgentRunAutoApproveBuiltInWrites = enabled };

        var json = JsonSerializer.Serialize(original, Options);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(reloaded);
        Assert.Equal(enabled, reloaded!.AgentRunAutoApproveBuiltInWrites);
    }

    [Fact]
    public void FromSettings_OffYieldsNoPolicy()
    {
        // Null, not an empty policy, so the persisted envelope stays byte-identical to an older document.
        Assert.Null(RunAutonomyPolicy.FromSettings(new AppSettings { AgentRunAutoApproveBuiltInWrites = false }));
    }

    [Fact]
    public void FromSettings_OnYieldsThePresetClasses_AndNoneOfTheExclusions()
    {
        var policy = RunAutonomyPolicy.FromSettings(new AppSettings { AgentRunAutoApproveBuiltInWrites = true });

        Assert.NotNull(policy);
        Assert.True(policy!.Covers(ToolClass.Memory));
        Assert.True(policy.Covers(ToolClass.Todo));
        Assert.True(policy.Covers(ToolClass.Reminder));
        Assert.True(policy.Covers(ToolClass.Scheduling));
        Assert.True(policy.Covers(ToolClass.Files));

        // The git_* tools are destructive but not delete-like by name, so no rule would stop them; a class grant
        // over External would retroactively auto-approve an MCP server's next tool.
        Assert.False(policy.Covers(ToolClass.Git));
        Assert.False(policy.Covers(ToolClass.External));
        Assert.False(policy.Covers(ToolClass.Unknown));
        Assert.False(policy.Covers(ToolClass.Ingest));
        // A preset must not blanket-approve starting a background assignment: that is the one write that
        // sends decrypted records off the device, and only a named grant may authorize it unattended.
        Assert.False(policy.Covers(ToolClass.Assignment));
        Assert.Equal(5, policy.AutoApproveClasses.Count);
    }
}
