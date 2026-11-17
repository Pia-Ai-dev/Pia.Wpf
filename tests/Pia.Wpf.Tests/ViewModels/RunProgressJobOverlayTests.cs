using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public sealed class RunProgressJobOverlayTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public RunProgressJobOverlayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaJobOverlay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => $"{ci[0]}:{((object[])ci[1])[0]}");
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_dir);
    }

    [Fact]
    public async Task TheBlock_MarksChangedSteps_AndSaysChangesWait_UntilTheStepStarts()
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var chats = new AssistantChatService(_ctx, _runs))
        {
            await chats.SaveAsync(new Pia.Shared.Models.SyncAssistantChat
            {
                Id = chatId, SchemaVersion = 1, Title = "t", CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(), Messages = [],
            }, Ct);
        }
        var run = await _runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "g"), Ct);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        await _runs.ReplaceStepsAsync(run.Id,
        [
            new AgentStep { Id = s1, Ordinal = 0, Title = "a", Intent = "a", Status = AgentStepStatus.Running },
            new AgentStep { Id = s2, Ordinal = 1, Title = "b", Intent = "b", Status = AgentStepStatus.Pending },
        ], Ct);
        await _runs.SetStateAsync(run.Id, AgentRunState.Running, Ct);

        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        using var vm = new RunProgressViewModel(_runs, run.Id, _loc, Substitute.For<IAgentRunResumeService>(), NullLogger.Instance);
        await Eventually.TrueAsync(() => vm.PlanSegments.Count == 2, "the plan projected", Ct);
        var store = new JobSteeringStore();
        vm.AttachJobSteering(store);

        store.QueuePlanChanges(run.Id, [new JobPlanChange(JobPlanChangeKind.Edit, s2, Title: "b2")]);
        store.QueuePlanChanges(run.Id, [new JobPlanChange(JobPlanChangeKind.Edit, s1, Title: "a2")]);

        Assert.True(vm.PlanSegments.Single(r => r.StepId == s2).IsChangedFromChat);
        Assert.False(vm.PlanSegments.Single(r => r.StepId == s1).IsChangedFromChat);
        Assert.Equal("JobTools_QueuedChanges:2", vm.QueuedChangesLine);

        store.TakePlanChanges(run.Id);
        await _runs.SetStepStatusAsync(s2, AgentStepStatus.Running, Ct);
        await Eventually.TrueAsync(() => vm.PlanSegments.Single(r => r.StepId == s2).Status == AgentStepStatus.Running, "s2 running", Ct);
        vm.ApplyJobOverlay();

        Assert.False(vm.PlanSegments.Single(r => r.StepId == s2).IsChangedFromChat);
        Assert.Null(vm.QueuedChangesLine);
    }
}
