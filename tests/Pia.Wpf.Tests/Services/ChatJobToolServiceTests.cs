using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class ChatJobToolServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly JobSteeringStore _jobs = new();
    private readonly IAgentRunSteeringService _pause = Substitute.For<IAgentRunSteeringService>();
    private readonly IRunSteeringStore _steering = Substitute.For<IRunSteeringStore>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();
    private readonly AppSettings _settings = new() { UnifiedAssistantFlowEnabled = true };
    private readonly ChatJobToolService _service;

    public ChatJobToolServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaJobTools_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _resume.ResumeAsync(default, default, default, default).ReturnsForAnyArgs(true);
        _resume.CancelRunAsync(default, default).ReturnsForAnyArgs(true);
        _pause.PauseAsync(default, default).ReturnsForAnyArgs(true);
        _service = new ChatJobToolService(SettingsStubs.Returning(_settings), _runs, _jobs, _pause, _steering, _resume,
            _chats, NullLogger<ChatJobToolService>.Instance);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _chats.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_dir);
    }

    private async Task<AgentRun> JobAsync(bool chatJob = true, params AgentStepStatus[] statuses)
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId, SchemaVersion = 1, Title = "t", CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(), Messages = [],
        });
        var run = await _runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User,
            Goal: "g", PolicyJson: chatJob ? HeadlessRunLauncher.ChatJobEmptyEnvelopeJson : null));
        if (statuses.Length == 0)
            statuses = [AgentStepStatus.Done, AgentStepStatus.Running, AgentStepStatus.Pending, AgentStepStatus.Pending];
        await _runs.ReplaceStepsAsync(run.Id, statuses.Select((s, i) => new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = i, Title = $"s{i + 1}", Intent = $"s{i + 1}", Status = s,
        }).ToList(), Ct);
        await _runs.SetStateAsync(run.Id, AgentRunState.Running, Ct);
        return (await _runs.GetAsync(run.Id, Ct))!;
    }

    private async Task<AgentRun> ParkAsync(AgentRun run, string reason)
    {
        if (reason == "user")
            Assert.True(await _runs.TryPauseUserAsync(run.Id, Ct));
        else
            await _runs.PauseAsync(run.Id, reason, Ct);
        return (await _runs.GetAsync(run.Id, Ct))!;
    }

    private static string H(AgentRun run, string title) => JobTurnBlockBuilder.Handle(run.Plan.Single(s => s.Title == title).Id);

    private static Dictionary<string, object?> Args(object args) =>
        JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

    private Task<string> CallAsync(AgentRun run, string tool, object? args = null) =>
        _service.HandleAsync(run.ChatId, tool, args is null ? null : Args(args), Ct);

    // ---------------------------------------------------------------- offering and the block

    [Fact]
    public async Task TheTurn_GetsTheBlockAndTools_ForAnUnfinishedChatJob()
    {
        var run = await JobAsync();

        var turn = await _service.PrepareTurnAsync(run.ChatId, Ct);

        Assert.NotNull(turn);
        Assert.Equal(JobTools.Names.OrderBy(n => n), turn.Tools.Select(t => t.Name).OrderBy(n => n));
        Assert.Contains("State: working", turn.Block);
        Assert.Contains($"[{H(run, "s1")}] done: s1", turn.Block);
        Assert.Contains($"[{H(run, "s2")}] running: s2 (running for", turn.Block);
        Assert.Contains($"[{H(run, "s3")}] to do: s3", turn.Block);
        Assert.Contains("never start a plan", turn.PromptAddition);
        Assert.Empty((await _chats.GetAsync(run.ChatId, Ct))!.Messages);
    }

    [Fact]
    public async Task NoTools_WithTheSettingOff()
    {
        _settings.UnifiedAssistantFlowEnabled = false;
        var run = await JobAsync();

        Assert.Null(await _service.PrepareTurnAsync(run.ChatId, Ct));
        Assert.Equal("This chat has no unfinished job.", await CallAsync(run, JobTools.Stop));
        await _resume.DidNotReceiveWithAnyArgs().CancelRunAsync(default, Ct);
    }

    [Fact]
    public async Task NoTools_ForARunThatIsNotAChatJob()
    {
        var run = await JobAsync(chatJob: false);
        Assert.Null(await _service.PrepareTurnAsync(run.ChatId, Ct));
    }

    [Fact]
    public async Task NoTools_OnceTheJobHasFinished()
    {
        var run = await JobAsync();
        await _runs.CompleteAsync(run.Id, ct: Ct);
        Assert.Null(await _service.PrepareTurnAsync(run.ChatId, Ct));
        Assert.Null(await _service.PrepareTurnAsync(null, Ct));
    }

    [Theory]
    [InlineData("plan-approval", "start the plan with Start")]
    [InlineData("tool-approval", "decide on the tool")]
    [InlineData("needs-input", "job_answer")]
    [InlineData("interrupted-credit", "interruption")]
    [InlineData("step-cap", "budget")]
    public async Task TheBlock_SaysWhatTheJobWaitsFor(string reason, string expected)
    {
        var run = await ParkAsync(await JobAsync(), reason);
        var turn = await _service.PrepareTurnAsync(run.ChatId, Ct);
        Assert.Contains("Waiting for:", turn!.Block);
        Assert.Contains(expected, turn.Block);
    }

    // ---------------------------------------------------------------- job_step_details

    [Fact]
    public async Task StepDetails_ReturnsTheStepsReply()
    {
        var run = await JobAsync();
        var chat = (await _chats.GetAsync(run.ChatId, Ct))!;
        var reply = new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "assistant", Content = "found 3 files", Timestamp = DateTime.UtcNow };
        chat.Messages.Add(reply);
        await _chats.SaveAsync(chat, Ct);
        var s1 = run.Plan.Single(s => s.Title == "s1");
        await _runs.RecordStepResultAsync(s1.Id, AgentStepStatus.Done, reply.Id, reply.Id, null, Ct);

        var result = await CallAsync(run, JobTools.StepDetails, new { step = H(run, "s1") });

        Assert.Contains("found 3 files", result);
        Assert.Contains("No step", await CallAsync(run, JobTools.StepDetails, new { step = "zzzzzzzz" }));
    }

    // ---------------------------------------------------------------- job_update_plan

    [Fact]
    public async Task UpdatePlan_OnARunningJob_QueuesAndOverlays()
    {
        var run = await JobAsync();

        var result = await CallAsync(run, JobTools.UpdatePlan, new
        {
            changes = new object[]
            {
                new { action = "edit", step = H(run, "s3"), title = "s3 better" },
                new { action = "insert", after = H(run, "s3"), title = "extra" },
                new { action = "remove", step = H(run, "s4") },
            },
        });

        Assert.Contains("Queued", result);
        Assert.Equal(3, _jobs.PeekPlanChanges(run.Id).Count);
        var stored = (await _runs.GetAsync(run.Id, Ct))!;
        Assert.Equal(["s1", "s2", "s3", "s4"], stored.Plan.Select(s => s.Title));
        Assert.Contains("to do: s3 better (changed from this chat)", result);
        Assert.Contains("to do: extra (changed from this chat)", result);
        Assert.DoesNotContain("s4", result.Split("Steps:")[1]);
    }

    [Fact]
    public async Task UpdatePlan_DiscardsTheParkedCallsOfAnEditedOrRemovedStep_Only()
    {
        var store = Substitute.For<IAgentToolExchangeStore>();
        var service = new ChatJobToolService(SettingsStubs.Returning(_settings), _runs, _jobs, _pause, _steering,
            _resume, _chats, NullLogger<ChatJobToolService>.Instance, toolCalls: store);
        var run = await ParkAsync(await JobAsync(), AgentRunOrchestrator.ToolApprovalReason);
        var s3 = run.Plan.Single(s => s.Title == "s3").Id;
        var s4 = run.Plan.Single(s => s.Title == "s4").Id;

        await service.HandleAsync(run.ChatId, JobTools.UpdatePlan, Args(new
        {
            changes = new object[]
            {
                new { action = "edit", step = H(run, "s3"), details = "other" },
                new { action = "move", step = H(run, "s4"), after = "start" },
            },
        }), Ct);

        await store.Received(1).SupersedeStepUnreplayedAsync(run.Id, s3, Arg.Any<CancellationToken>());
        await store.DidNotReceive().SupersedeStepUnreplayedAsync(run.Id, s4, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePlan_RefusesAStartedStep_AndChangesNothing()
    {
        var run = await JobAsync();

        var result = await CallAsync(run, JobTools.UpdatePlan, new
        {
            changes = new object[]
            {
                new { action = "remove", step = H(run, "s3") },
                new { action = "edit", step = H(run, "s2"), title = "x" },
            },
        });

        Assert.Contains("already started", result);
        Assert.Empty(_jobs.PeekPlanChanges(run.Id));
    }

    [Fact]
    public async Task UpdatePlan_RefusesAnUnknownStepAndAnEmptyPlan()
    {
        var run = await JobAsync(true, AgentStepStatus.Pending);
        Assert.Contains("No step", await CallAsync(run, JobTools.UpdatePlan,
            new { changes = new object[] { new { action = "remove", step = "deadbeef" } } }));
        Assert.Contains("no steps", await CallAsync(run, JobTools.UpdatePlan,
            new { changes = new object[] { new { action = "remove", step = H(run, "s1") } } }));
        Assert.Empty(_jobs.PeekPlanChanges(run.Id));
    }

    [Fact]
    public async Task UpdatePlan_OnAPausedJob_AppliesDirectly_AndMarksTheSteps()
    {
        var run = await ParkAsync(await JobAsync(true, AgentStepStatus.Done, AgentStepStatus.Pending), "user");

        var result = await CallAsync(run, JobTools.UpdatePlan, new
        {
            changes = new object[] { new { action = "insert", after = "start", title = "first" } },
        });

        Assert.Contains("Changed the plan", result);
        Assert.Empty(_jobs.PeekPlanChanges(run.Id));
        var stored = (await _runs.GetAsync(run.Id, Ct))!;
        Assert.Equal(["s1", "first", "s2"], stored.Plan.OrderBy(s => s.Ordinal).Select(s => s.Title));
        Assert.Contains(stored.Plan.Single(s => s.Title == "first").Id, _jobs.ChangedSteps(run.Id));
    }

    // ---------------------------------------------------------------- steering

    [Fact]
    public async Task Note_IsKeptForTheRun()
    {
        var run = await JobAsync();
        Assert.Contains("Noted", await CallAsync(run, JobTools.Note, new { note = "use metric units" }));
        Assert.Equal(["use metric units"], _jobs.Notes(run.Id));
        Assert.Contains("empty", await CallAsync(run, JobTools.Note, new { note = " " }));
    }

    [Fact]
    public async Task Pause_AfterThisStep_SetsTheFlag_AndNowFiresThePause()
    {
        var run = await JobAsync();

        await CallAsync(run, JobTools.Pause, new { afterThisStep = true });
        Assert.True(_jobs.IsPauseAfterStepRequested(run.Id));
        await _pause.DidNotReceiveWithAnyArgs().PauseAsync(default, Ct);

        await CallAsync(run, JobTools.Pause, new { afterThisStep = false });
        await _pause.Received(1).PauseAsync(run.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pause_IsRefused_OnAParkedJob()
    {
        var run = await ParkAsync(await JobAsync(), "user");
        Assert.Contains("not working", await CallAsync(run, JobTools.Pause, new { afterThisStep = false }));
        await _pause.DidNotReceiveWithAnyArgs().PauseAsync(default, Ct);
    }

    [Fact]
    public async Task ParkingTheJob_DropsAPendingPauseAfterStep()
    {
        var run = await JobAsync();
        _jobs.RequestPauseAfterStep(run.Id);
        await _runs.PauseAsync(run.Id, "needs-input", Ct);
        Assert.False(_jobs.IsPauseAfterStepRequested(run.Id));
    }

    [Fact]
    public async Task RestartStep_PausesThenResumesWithTheNote()
    {
        var run = await JobAsync();
        _pause.PauseAsync(run.Id, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _runs.TryPauseUserAsync(run.Id, Ct);
            return true;
        });

        var result = await CallAsync(run, JobTools.RestartStep, new { note = "try the other folder" });

        Assert.Contains("Restarted", result);
        await _resume.Received(1).ResumeAsync(run.Id, "try the other folder", Arg.Any<CancellationToken>(), false, Arg.Any<Func<string?, bool>?>());
    }

    [Fact]
    public async Task RestartStep_NeverClaimsSuccess_WhenTheJobDoesNotStop()
    {
        ChatJobToolService.RestartPauseTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            var run = await JobAsync();
            var result = await CallAsync(run, JobTools.RestartStep, new { note = "n" });
            Assert.DoesNotContain("Restarted", result);
            await _resume.DidNotReceiveWithAnyArgs().ResumeAsync(default, default, Ct, default);
        }
        finally
        {
            ChatJobToolService.RestartPauseTimeout = TimeSpan.FromSeconds(60);
        }
    }

    [Theory]
    [InlineData("user")]
    [InlineData("step-cap")]
    [InlineData("wall-clock")]
    [InlineData("interrupted-credit")]
    [InlineData("interrupted-offline")]
    [InlineData("resume-interrupted")]
    public async Task Continue_ResumesEachContinuablePark(string reason)
    {
        var run = await ParkAsync(await JobAsync(), reason);
        Assert.Equal("Continued the job.", await CallAsync(run, JobTools.Continue));
        await _resume.Received(1).ResumeAsync(run.Id, null, Arg.Any<CancellationToken>(), false, Arg.Any<Func<string?, bool>?>());
    }

    [Theory]
    [InlineData("plan-approval")]
    [InlineData("tool-approval")]
    [InlineData("needs-input")]
    [InlineData("needs-goal")]
    public async Task Continue_NeverStartsAPlanOrAnswersAnApproval(string reason)
    {
        var run = await ParkAsync(await JobAsync(), reason);
        Assert.DoesNotContain("Continued", await CallAsync(run, JobTools.Continue));
        await _resume.DidNotReceiveWithAnyArgs().ResumeAsync(default, default, Ct, default);
    }

    [Fact]
    public async Task Continue_IsRefused_OnAWorkingJob()
    {
        var run = await JobAsync();
        Assert.Contains("not stopped", await CallAsync(run, JobTools.Continue));
    }

    [Fact]
    public async Task Answer_ResumesAQuestionPark_WithTheAnswer()
    {
        var run = await ParkAsync(await JobAsync(), "needs-input");
        Assert.Contains("Passed the answer", await CallAsync(run, JobTools.Answer, new { answer = "the staging one" }));
        await _resume.Received(1).ResumeAsync(run.Id, "the staging one", Arg.Any<CancellationToken>(), false, Arg.Any<Func<string?, bool>?>());
    }

    [Theory]
    [InlineData("tool-approval")]
    [InlineData("plan-approval")]
    public async Task Answer_IsRefused_WithoutAQuestion(string reason)
    {
        var run = await ParkAsync(await JobAsync(), reason);
        Assert.Contains("has not asked", await CallAsync(run, JobTools.Answer, new { answer = "yes go ahead" }));
        await _resume.DidNotReceiveWithAnyArgs().ResumeAsync(default, default, Ct, default);
    }

    [Fact]
    public async Task Stop_CancelsTheJob()
    {
        var run = await JobAsync();
        Assert.Equal("Stopped the job.", await CallAsync(run, JobTools.Stop));
        await _resume.Received(1).CancelRunAsync(run.Id, Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------- typed message during a park

    [Theory]
    [InlineData("interrupted-provider", true, true)]
    [InlineData("needs-input", true, true)]
    [InlineData("needs-goal", true, false)]
    [InlineData("interrupted-provider", false, false)]
    [InlineData("needs-input", false, false)]
    public async Task ATypedMessage_IsATurn_OnlyWithTheSettingOn(string reason, bool settingOn, bool expected)
    {
        _settings.UnifiedAssistantFlowEnabled = settingOn;
        var run = await ParkAsync(await JobAsync(), reason);
        Assert.Equal(expected, await _service.TypedMessageIsTurnAsync(run.Id, Ct));
    }

    [Fact]
    public async Task ATypedMessage_ToANonChatJob_KeepsTheOldRule()
    {
        var run = await ParkAsync(await JobAsync(chatJob: false), "interrupted-provider");
        Assert.False(await _service.TypedMessageIsTurnAsync(run.Id, Ct));
    }
}
