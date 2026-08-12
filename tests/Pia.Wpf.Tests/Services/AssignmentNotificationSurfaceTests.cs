using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models.Flow;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AssignmentNotificationSurfaceTests
{
    private readonly Pia.Services.Flow.IFlowService _flow = Substitute.For<Pia.Services.Flow.IFlowService>();
    private readonly IWindowManagerService _windows = Substitute.For<IWindowManagerService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public AssignmentNotificationSurfaceTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private AssignmentNotificationSurface Create(AssignmentRunOrchestrator orchestrator) =>
        new(orchestrator, _flow, _windows, _loc, NullLogger<AssignmentNotificationSurface>.Instance);

    private AssignmentNotificationSurface Create() => Create(CreateOrchestrator(out _, out _, out _));

    private static AssignmentRunOrchestrator CreateOrchestrator(
        out IAssignmentPendingStore pending,
        out IAssignmentApiClient api,
        out IAssistantChatService chats)
    {
        pending = Substitute.For<IAssignmentPendingStore>();
        api = Substitute.For<IAssignmentApiClient>();
        chats = Substitute.For<IAssistantChatService>();
        return new AssignmentRunOrchestrator(
            api,
            Substitute.For<IAssignmentConsentStore>(),
            Substitute.For<IAssignmentScopeResolver>(),
            pending,
            chats,
            NullLogger<AssignmentRunOrchestrator>.Instance);
    }

    [Fact]
    public void ACollectedRun_PublishesOneItemPointingAtItsChat()
    {
        var assignmentId = Guid.NewGuid();
        var chatId = Guid.NewGuid();

        Create().Handle(new AssignmentCompleted(assignmentId, chatId, "research", Succeeded: true));

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d =>
            d.Source == FlowSource.Assignment &&
            d.DedupKey == assignmentId.ToString() &&
            d.Lifetime.IsPersistent &&
            d.RequestDurable &&
            d.Action is OpenChatAction &&
            ((OpenChatAction)d.Action!).ChatId == chatId));
    }

    [Theory]
    [InlineData(true, FlowSeverity.Success)]
    [InlineData(false, FlowSeverity.Error)]
    public void SucceededDecidesTheSeverity(bool succeeded, FlowSeverity expected)
    {
        Create().Handle(new AssignmentCompleted(Guid.NewGuid(), Guid.NewGuid(), "brief", succeeded));

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d => d.Severity == expected));
    }

    [Fact]
    public void SuccessAndFailureReadDifferently()
    {
        var drafts = new List<FlowItemDraft>();
        _flow.Publish(Arg.Do<FlowItemDraft>(drafts.Add));
        var surface = Create();

        surface.Handle(new AssignmentCompleted(Guid.NewGuid(), Guid.NewGuid(), "brief", Succeeded: true));
        surface.Handle(new AssignmentCompleted(Guid.NewGuid(), Guid.NewGuid(), "brief", Succeeded: false));

        Assert.Equal(2, drafts.Count);
        Assert.NotEqual(drafts[0].Body, drafts[1].Body);
    }

    // The surface has no caller: if the ctor stops subscribing, every finished assignment goes unannounced.
    [Fact]
    public async Task ConstructingTheSurfaceSubscribesToTheOrchestrator()
    {
        var orchestrator = CreateOrchestrator(out var pending, out var api, out _);
        var assignmentId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        pending.GetAllAsync().Returns([
            new PendingAssignment(assignmentId, chatId, "research", "what happened", DateTime.UtcNow)
        ]);
        api.GetAsync(assignmentId, Arg.Any<CancellationToken>()).Returns(Dto(assignmentId, "Completed"));
        api.CollectAsync(assignmentId, Arg.Any<CancellationToken>()).Returns(true);

        Create(orchestrator);
        var finished = await orchestrator.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, finished);
        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d => d.DedupKey == assignmentId.ToString()));
    }

    private static AssignmentDto Dto(Guid id, string status) => new(
        id, "research", "research", status, 1, 0, 0,
        DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
        ArtifactJson: null, ErrorCode: null, ErrorMessage: null, ArtifactText: "here it is");
}
