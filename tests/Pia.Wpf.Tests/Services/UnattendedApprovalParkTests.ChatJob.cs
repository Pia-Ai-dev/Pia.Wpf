using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public sealed partial class UnattendedApprovalParkTests
{
    /// <summary>A parked call answered on a chat job's block means what a card means, and nothing run-wide.</summary>
    private async Task<(HeadlessRunLauncher Launcher, Guid RunId, ToolProbe Probe, IToolPermissionService Permissions,
        RunToolAnswerService Answers)> ParkedChatJobAsync(Guid pluginId)
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var permissions = Substitute.For<IToolPermissionService>();
        var (launcher, _) = Build(probe, pluginId: pluginId, permissions: permissions,
            dispatchScript: [[new("write_file", "a.txt")], []]);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.User, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await _runs.UpdatePolicyJsonAsync(
            handle.RunId, HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User, chatJob: true), ct);
        Assert.False(probe.Executed);

        var answers = new RunToolAnswerService(
            _runs, launcher, permissions, NullLogger<RunToolAnswerService>.Instance, _exchanges);
        return (launcher, handle.RunId, probe, permissions, answers);
    }

    [Theory]
    [InlineData(ToolDecision.AllowOnce)]
    [InlineData(ToolDecision.AllowForSession)]
    [InlineData(ToolDecision.AlwaysAllow)]
    public async Task AParkedChatJob_AnsweredOnTheBlock_ReplaysTheCall_WithoutWideningTheRun(ToolDecision decision)
    {
        var pluginId = Guid.NewGuid();
        var (launcher, runId, probe, permissions, answers) = await ParkedChatJobAsync(pluginId);

        Assert.True(await answers.AnswerParkedToolAsync(runId, decision, TestContext.Current.CancellationToken));
        await AwaitSettledAsync(runId);

        var run = await GetRunAsync(runId);
        Assert.Equal(AgentRunState.Completed, run.State);
        Assert.Equal(["write_file"], probe.ExecutedNames);
        Assert.Empty(HeadlessRunLauncher.TryRestoreGrantEnvelope(run.PolicyJson)!);
        permissions.Received(decision == ToolDecision.AllowForSession ? 1 : 0).GrantForSession(pluginId, "write_file");
        await permissions.Received(decision == ToolDecision.AlwaysAllow ? 1 : 0).GrantAsync(pluginId, "write_file");

        await launcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AParkedChatJob_Denied_SeedsTheDecline_WithoutARunWideDenial()
    {
        var (launcher, runId, probe, permissions, answers) = await ParkedChatJobAsync(Guid.NewGuid());

        Assert.True(await answers.AnswerParkedToolAsync(runId, ToolDecision.Decline, TestContext.Current.CancellationToken));
        await AwaitSettledAsync(runId);

        var run = await GetRunAsync(runId);
        Assert.Equal(AgentRunState.Completed, run.State);
        Assert.False(probe.Executed);
        Assert.Empty(HeadlessRunLauncher.TryRestoreDeniedWritesEnvelope(run.PolicyJson));
        var resumed = probe.Dispatches[^1].Request;
        Assert.Contains(resumed, m => m.Text.Contains("User declined") || m.Contents.Any(c =>
            c is Microsoft.Extensions.AI.FunctionResultContent r && (r.Result?.ToString() ?? "").Contains("User declined")));
        Assert.DoesNotContain(resumed, m => m.Text.Contains("was given"));
        permissions.DidNotReceiveWithAnyArgs().GrantForSession(default, default!);

        await launcher.StopAsync(CancellationToken.None);
    }

    private sealed class OnScreen : IChatAttendance
    {
        public bool IsAttended(Guid chatId) => true;
    }

    [Fact]
    public async Task AnAttendedChatJobsStep_AsksInPlace_AndFinishesWithoutARerun()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = new RunAskCoordinator(new OnScreen(), _runs, NullLogger<RunAskCoordinator>.Instance);
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, askBroker: broker,
            dispatchScript: [[new("write_file", "a.txt")], [new("write_file", "b.txt")], []]);
        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.User, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await _runs.UpdatePolicyJsonAsync(
            handle.RunId, HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User, chatJob: true), ct);

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        RunToolQuestion? question = null;
        for (var i = 0; i < 1000 && question is null; i++)
        {
            question = broker.GetPending(handle.RunId);
            if (question is null) await Task.Delay(10, ct);
        }

        Assert.NotNull(question);
        Assert.Equal(AgentRunState.Running, (await GetRunAsync(handle.RunId)).State);
        Assert.True(broker.Answer(handle.RunId, question!.RequestId, ToolDecision.AllowOnce));
        await AwaitSettledAsync(handle.RunId);

        Assert.Equal(AgentRunState.Completed, (await GetRunAsync(handle.RunId)).State);
        Assert.Equal(2, probe.Dispatches.Count);
        Assert.Equal(["write_file", "write_file"], probe.ExecutedNames);
        Assert.Contains(_timeline.Rows, r => r.Decision == ToolGateDecision.ApprovedOnce);
        await launcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AParkedRoutine_CannotBeAnsweredWithTheBlocksChoices()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var permissions = Substitute.For<IToolPermissionService>();
        var (launcher, _) = Build(probe, permissions: permissions);
        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        var answers = new RunToolAnswerService(
            _runs, launcher, permissions, NullLogger<RunToolAnswerService>.Instance, _exchanges);

        Assert.False(await answers.AnswerParkedToolAsync(handle.RunId, ToolDecision.AlwaysAllow, ct));

        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);
        await permissions.DidNotReceiveWithAnyArgs().GrantAsync(default, default!);
        await launcher.StopAsync(CancellationToken.None);
    }
}
