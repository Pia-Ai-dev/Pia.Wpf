using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class RunRegenerateGateTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);

    private static AssistantMessage Msg(int minutes, ChatRole? role = null) =>
        new(Guid.NewGuid(), role ?? ChatRole.Assistant, "x", T0.AddMinutes(minutes));

    // prompt, proposal, [block after the proposal], reply
    private readonly AssistantMessage _prompt = Msg(0, ChatRole.User);
    private readonly AssistantMessage _proposal = Msg(1);
    private readonly AssistantMessage _reply = Msg(5);
    private readonly AssistantMessage _later = Msg(9, ChatRole.User);
    private readonly AssistantMessage _laterAnswer = Msg(10);

    private IReadOnlyList<AssistantMessage> Transcript => [_prompt, _proposal, _reply, _later, _laterAnswer];

    private RegenerateJob Job(RunProgressState state, bool planPark = false, bool projected = true) =>
        new(Guid.NewGuid(), state, planPark, projected, new RunBlockSlot(_proposal.Id, Before: false), [_reply.Id]);

    private const int CutAtProposal = 0;   // regenerating the proposal removes from its prompt on
    private const int CutBelowJob = 3;     // regenerating the later answer removes from the later prompt on

    [Theory]
    [InlineData(RunProgressState.Running)]
    [InlineData(RunProgressState.Planning)]
    [InlineData(RunProgressState.WaitingForChildren)]
    public void ARunningJob_BlocksEveryRegenerate(RunProgressState state)
    {
        Assert.Equal(RegenerateVerdict.BlockedRunning,
            RunRegenerateGate.Evaluate(Transcript, CutBelowJob, [Job(state)]).Verdict);
        Assert.Equal(RegenerateVerdict.BlockedRunning,
            RunRegenerateGate.Evaluate(Transcript, CutAtProposal, [Job(state)]).Verdict);
    }

    [Fact]
    public void AWaitingPlan_AtTheCut_IsDiscardedFirst()
    {
        var job = Job(RunProgressState.WaitingForInput, planPark: true);

        var decision = RunRegenerateGate.Evaluate(Transcript, CutAtProposal, [job]);

        Assert.Equal(RegenerateVerdict.Allowed, decision.Verdict);
        Assert.Equal([job.RunId], decision.DiscardFirst);
    }

    [Theory]
    [InlineData(RunProgressState.Paused)]
    [InlineData(RunProgressState.WaitingForInput)]
    public void AParkedJob_AtTheCut_BlocksUntilStopped(RunProgressState state)
    {
        Assert.Equal(RegenerateVerdict.BlockedUntilStopped,
            RunRegenerateGate.Evaluate(Transcript, CutAtProposal, [Job(state)]).Verdict);
    }

    [Theory]
    [InlineData(RunProgressState.Paused, false)]
    [InlineData(RunProgressState.WaitingForInput, false)]
    [InlineData(RunProgressState.WaitingForInput, true)]
    [InlineData(RunProgressState.Completed, false)]
    public void BelowAJobsLastMessage_EveryStateButRunningIsAllowed(RunProgressState state, bool planPark)
    {
        var decision = RunRegenerateGate.Evaluate(Transcript, CutBelowJob, [Job(state, planPark)]);

        Assert.Equal(RegenerateVerdict.Allowed, decision.Verdict);
        Assert.Empty(decision.DiscardFirst);
    }

    [Theory]
    [InlineData(RunProgressState.Completed)]
    [InlineData(RunProgressState.Failed)]
    [InlineData(RunProgressState.Stopped)]
    public void AFinishedJob_AtTheCut_IsAllowed(RunProgressState state)
    {
        Assert.Equal(RegenerateVerdict.Allowed,
            RunRegenerateGate.Evaluate(Transcript, CutAtProposal, [Job(state)]).Verdict);
    }

    [Fact]
    public void AJobWhoseMessagesTheCutRemoves_IsTouched_EvenIfItsBlockSitsAbove()
    {
        var job = new RegenerateJob(Guid.NewGuid(), RunProgressState.Paused, false, true,
            new RunBlockSlot(_prompt.Id, Before: true), [_laterAnswer.Id]);

        Assert.Equal(RegenerateVerdict.BlockedUntilStopped,
            RunRegenerateGate.Evaluate(Transcript, CutBelowJob, [job]).Verdict);
    }

    [Fact]
    public void ATrailingBlock_IsAtTheCut()
    {
        var job = new RegenerateJob(Guid.NewGuid(), RunProgressState.Paused, false, true, null, []);

        Assert.Equal(RegenerateVerdict.BlockedUntilStopped,
            RunRegenerateGate.Evaluate(Transcript, CutBelowJob, [job]).Verdict);
    }

    [Fact]
    public void ABlockNotLoadedYet_AtTheCut_Blocks()
    {
        Assert.Equal(RegenerateVerdict.BlockedRunning,
            RunRegenerateGate.Evaluate(Transcript, CutAtProposal,
                [Job(RunProgressState.Planning, projected: false)]).Verdict);
        Assert.Equal(RegenerateVerdict.Allowed,
            RunRegenerateGate.Evaluate(Transcript, CutBelowJob,
                [Job(RunProgressState.Planning, projected: false)]).Verdict);
    }
}
