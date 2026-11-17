using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The block shows what a chat job waits on and carries the answer back.</summary>
public sealed class RunProgressAskingTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();
    private readonly IRunAskBroker _broker = Substitute.For<IRunAskBroker>();
    private readonly IRunToolAnswerService _answers = Substitute.For<IRunToolAnswerService>();

    public RunProgressAskingTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}|{string.Join(",", (object[])ci[1])}");
    }

    private RunProgressViewModel CreateVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance,
            askBroker: _broker, toolAnswers: _answers);
    }

    private void Stub(AgentRunState state, bool chatJob, string? extraJson = null) =>
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = state,
            Plan = [],
            PolicyJson = HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User, chatJob: chatJob),
            ExtraJson = extraJson,
            LedgerJson = """{"inputTokens":0,"outputTokens":0,"wallClockMs":4000,"perStep":[]}""",
        });

    private const string ToolPark = """{"paused":true,"reason":"tool-approval","tool":"mcp_tool","args":"x=1"}""";
    private const string InputPark = """{"paused":true,"reason":"needs-input"}""";

    private RunToolQuestion Question() =>
        new(Guid.NewGuid(), _runId, Guid.NewGuid(), null, "mcp_tool", Guid.NewGuid(), "x=1");

    [Fact]
    public async Task AWaitInPlace_ShowsTheCall_StopsTheClock_AndSaysSoOnTheActivityLine()
    {
        Stub(AgentRunState.Running, chatJob: true);
        var question = Question();
        _broker.GetPending(_runId).Returns(question);
        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.True(vm.IsAskingInPlace);
        Assert.True(vm.ShowToolAsk);
        Assert.Equal("x=1", vm.AskToolArguments);
        Assert.Equal("RunAsk_Activity_Waiting|mcp_tool", vm.CurrentActivity);
        Assert.True(vm.HasToolActivity);
        var held = vm.SubLine;
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        vm.AdvanceElapsedClock();
        Assert.Equal(held, vm.SubLine);

        await vm.AllowForSessionAskCommand.ExecuteAsync(null);
        _broker.Received(1).Answer(_runId, question.RequestId, ToolDecision.AllowForSession);
        await _answers.DidNotReceiveWithAnyArgs().AnswerParkedToolAsync(default, default, TestContext.Current.CancellationToken);
        vm.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWaitInPlace_OffersTheSessionChoice_OnlyWhenTheQuestionDoes(bool offers)
    {
        Stub(AgentRunState.Running, chatJob: true);
        _broker.GetPending(_runId).Returns(Question() with { OffersSessionGrant = offers });
        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.True(vm.IsAskingInPlace);
        Assert.Equal(offers, vm.OffersSessionAsk);
        vm.Dispose();
    }

    [Theory]
    [InlineData(ToolDecision.AllowOnce)]
    [InlineData(ToolDecision.AllowForSession)]
    [InlineData(ToolDecision.AlwaysAllow)]
    [InlineData(ToolDecision.Decline)]
    public async Task AParkedChatJob_OffersTheFourChoices_InsteadOfContinueAndDeny(ToolDecision decision)
    {
        Stub(AgentRunState.WaitingForInput, chatJob: true, ToolPark);
        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.True(vm.IsParkedToolAsk);
        Assert.True(vm.ShowToolAsk);
        Assert.False(vm.ShowContinueButton);
        Assert.False(vm.ShowDenyButton);
        Assert.Equal("mcp_tool", vm.AskToolName);

        var command = decision switch
        {
            ToolDecision.AllowOnce => vm.AllowOnceAskCommand,
            ToolDecision.AllowForSession => vm.AllowForSessionAskCommand,
            ToolDecision.AlwaysAllow => vm.AlwaysAllowAskCommand,
            _ => vm.DenyAskCommand,
        };
        await command.ExecuteAsync(null);
        await _answers.Received(1).AnswerParkedToolAsync(_runId, decision, Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    [Fact]
    public async Task AParkedRoutine_KeepsContinueAndDeny()
    {
        Stub(AgentRunState.WaitingForInput, chatJob: false, ToolPark);
        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.False(vm.ShowToolAsk);
        Assert.True(vm.ShowContinueButton);
        Assert.True(vm.ShowDenyButton);
        vm.Dispose();
    }

    [Fact]
    public async Task AQuestionFromTheJob_HasAnAnswerField_ThatResumesThroughTheAnswerPath()
    {
        Stub(AgentRunState.WaitingForInput, chatJob: true, InputPark);
        _broker.GetUserInputQuestion(_runId).Returns("Which folder?");
        _resume.ResumeAsync(_runId, "The docs folder", Arg.Any<CancellationToken>(), false).Returns(true);
        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.True(vm.ShowUserInputAsk);
        Assert.False(vm.ShowNudgeBox);
        Assert.False(vm.ShowContinueButton);
        Assert.Equal("Which folder?", vm.UserInputQuestion);
        Assert.False(vm.AnswerUserInputCommand.CanExecute(null));

        vm.UserInputAnswer = "The docs folder";
        Assert.True(vm.AnswerUserInputCommand.CanExecute(null));
        await vm.AnswerUserInputCommand.ExecuteAsync(null);

        await _resume.Received(1).ResumeAsync(_runId, "The docs folder", Arg.Any<CancellationToken>(), false);
        Assert.Null(vm.UserInputAnswer);
        _broker.Received(1).SetUserInputQuestion(_runId, null);
        vm.Dispose();
    }

    [Fact]
    public async Task AQuestionFromAnotherRun_StaysOnTheNudgeBox()
    {
        Stub(AgentRunState.WaitingForInput, chatJob: false, InputPark);
        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.False(vm.ShowUserInputAsk);
        Assert.True(vm.ShowNudgeBox);
        vm.Dispose();
    }
}
