using Pia.Models.Flow;
using Pia.Services.Flow;
using Xunit;

namespace Pia.Tests.Services.Flow;

public class FlowRetentionTests
{
    [Theory]
    [InlineData(FlowSource.Snackbar, FlowSeverity.Error, 1)]
    [InlineData(FlowSource.Snackbar, FlowSeverity.Warning, 1)]
    [InlineData(FlowSource.InAppToast, FlowSeverity.Error, 1)]
    [InlineData(FlowSource.InAppToast, FlowSeverity.Info, 7)]
    [InlineData(FlowSource.Policy, FlowSeverity.Info, 3)]
    [InlineData(FlowSource.ScheduledJob, FlowSeverity.Success, 7)]
    [InlineData(FlowSource.ScheduledJob, FlowSeverity.Error, 30)]
    [InlineData(FlowSource.BackgroundChat, FlowSeverity.Success, 14)]
    [InlineData(FlowSource.BackgroundChat, FlowSeverity.Error, 14)]
    [InlineData(FlowSource.Assignment, FlowSeverity.Success, 14)]
    [InlineData(FlowSource.AgentRun, FlowSeverity.Success, 14)]
    [InlineData(FlowSource.AgentRun, FlowSeverity.Error, 14)]
    [InlineData(FlowSource.Reminder, FlowSeverity.ActionRequired, 30)]
    public void MaxAgeFor_MatchesThePublishedTable(FlowSource source, FlowSeverity severity, int expectedDays)
        => Assert.Equal(TimeSpan.FromDays(expectedDays), FlowRetention.MaxAgeFor(source, severity));

    [Theory]
    [InlineData(FlowSource.AgentRun)]
    [InlineData(FlowSource.BackgroundChat)]
    [InlineData(FlowSource.Snackbar)]
    [InlineData(FlowSource.ScheduledJob)]
    public void MaxAgeFor_ActionRequired_NeverAgesOut_ExceptReminder(FlowSource source)
        => Assert.Null(FlowRetention.MaxAgeFor(source, FlowSeverity.ActionRequired));

    [Theory]
    [InlineData(FlowSeverity.Warning)]
    [InlineData(FlowSeverity.Error)]
    [InlineData(FlowSeverity.ActionRequired)]
    public void MaxAgeFor_TodoDeadline_NeverAgesOut(FlowSeverity severity)
        => Assert.Null(FlowRetention.MaxAgeFor(FlowSource.TodoDeadline, severity));

    /// <summary>A source added without a row of its own must still age out, or it silently reopens the leak.</summary>
    [Fact]
    public void MaxAgeFor_EverySourceIsBounded_ExceptTheTwoDocumentedCarveOuts()
    {
        foreach (var source in Enum.GetValues<FlowSource>())
        {
            foreach (var severity in Enum.GetValues<FlowSeverity>())
            {
                var expectedNull = source == FlowSource.TodoDeadline
                    || (severity == FlowSeverity.ActionRequired && source != FlowSource.Reminder);

                var age = FlowRetention.MaxAgeFor(source, severity);
                if (expectedNull)
                    Assert.Null(age);
                else
                    Assert.True(age > TimeSpan.Zero, $"{source}/{severity} has no bounded age");
            }
        }
    }
}
