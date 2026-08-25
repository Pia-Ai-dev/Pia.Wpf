using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The asymmetry every routine surface has to render: an agent job's empty list is not "no writes".</summary>
public class ScheduledJobGrantsTests
{
    [Fact]
    public void AnAgentJobWithNoGrants_TakesTheLauncherDefault()
    {
        Assert.Equal(
            HeadlessRunRequest.DefaultGrantedWrites,
            ScheduledJobGrants.Effective([], ScheduledJobKind.AgentTask));
    }

    [Fact]
    public void AResearchJobWithNoGrants_IsGenuinelyReadOnly()
    {
        Assert.Empty(ScheduledJobGrants.Effective([], ScheduledJobKind.Research));
    }

    [Theory]
    [InlineData(ScheduledJobKind.AgentTask)]
    [InlineData(ScheduledJobKind.Research)]
    public void AnExplicitList_PassesThroughUnchanged_OnEitherKind(ScheduledJobKind kind)
    {
        string[] granted = ["delete_file", "create_todo"];

        Assert.Equal(granted, ScheduledJobGrants.Effective(granted, kind));
    }

    /// <summary>A named list REPLACES the floor rather than adding to it, so an agent job granting one unrelated
    /// tool loses write_file. Surfaces that showed the union would promise a write that never happens.</summary>
    [Fact]
    public void AnExplicitList_ReplacesTheDefault_RatherThanAddingToIt()
    {
        var effective = ScheduledJobGrants.Effective(["create_todo"], ScheduledJobKind.AgentTask);

        Assert.DoesNotContain("write_file", effective);
    }
}
