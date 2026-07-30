using System.Text.Json;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Batch 04 D9: the per-run autonomy default. OFF by default is a decision, not an accident — with it on, an
/// unattended run can overwrite files in the assistant folder with nobody watching. The JSON round-trip is
/// the only automated proof the settings CheckBox can actually persist (no test constructs
/// <c>AssistantSettingsViewModel</c>, and nothing parses <c>Views/SettingsViews/AssistantView.xaml</c>).
/// </summary>
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
        // Null, not an empty policy: the persisted envelope then stays byte-identical to a pre-04 document.
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

        // D9's stated exclusions, as a test: git_switch/git_restore/git_stash are destructive but NOT
        // delete-like by name, so no rule would stop them; a class grant over External would make an MCP
        // server's next tool auto-approved retroactively; Unknown can never be authority; Ingest is never
        // gated at all.
        Assert.False(policy.Covers(ToolClass.Git));
        Assert.False(policy.Covers(ToolClass.External));
        Assert.False(policy.Covers(ToolClass.Unknown));
        Assert.False(policy.Covers(ToolClass.Ingest));
        Assert.Equal(5, policy.AutoApproveClasses.Count);
    }
}
