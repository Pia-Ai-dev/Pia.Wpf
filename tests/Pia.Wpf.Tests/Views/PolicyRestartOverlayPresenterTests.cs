using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Views.Overlays;
using Xunit;

namespace Pia.Tests.Views;

public class PolicyRestartOverlayPresenterTests
{
    private readonly IPolicyService _policy = Substitute.For<IPolicyService>();
    private readonly IDialogOverlayService _overlay = Substitute.For<IDialogOverlayService>();
    private readonly IDirectTranscriptionService _transcription = Substitute.For<IDirectTranscriptionService>();
    private readonly IMeetingAttendeeService _meeting = Substitute.For<IMeetingAttendeeService>();
    private readonly IExecutingRunStore _runs = Substitute.For<IExecutingRunStore>();
    private readonly IAgentRunService _runService = Substitute.For<IAgentRunService>();
    private readonly IAppRestartService _restart = Substitute.For<IAppRestartService>();
    private readonly VolatileWorkStore _work = new();

    public PolicyRestartOverlayPresenterTests()
    {
        _policy.IsRestartRequired.Returns(true);
        _transcription.State.Returns(DirectTranscriptionState.Idle);
        _meeting.State.Returns(MeetingAttendeeState.Idle);
        _runs.IsAnyExecuting.Returns(false);
        _runs.IsAnyExecutingExcept(Arg.Any<Guid>()).Returns(false);
    }

    /// <summary>Building the real panel needs the UI thread; the gate does not, so the show is the seam.</summary>
    private sealed class TestPresenter : PolicyRestartOverlayPresenter
    {
        public TestPresenter(
            IPolicyService policy,
            IDialogOverlayService overlay,
            IDirectTranscriptionService transcription,
            IMeetingAttendeeService meeting,
            IExecutingRunStore runs,
            IAgentRunService runService,
            IVolatileWorkStore work,
            IAppRestartService restart)
            : base(policy, overlay, transcription, meeting, runs, runService, work, restart,
                new Pia.Tests.Services.InlineUiDispatcher(),
                NullLogger<PolicyRestartOverlayPresenter>.Instance)
        {
        }

        public int Shows { get; private set; }

        /// <summary>Set to throw the way an overlay host that was never handed over does.</summary>
        public Exception? ShowThrows { get; set; }

        protected override Task ShowOverlayAsync(Func<Task> onRestartRequested)
        {
            Shows++;
            if (ShowThrows is { } ex)
                throw ex;

            // The real panel raises no result and the real host never collapses, so the restart is driven
            // by the panel's own callback while the scrim is still up.
            return onRestartRequested();
        }
    }

    private TestPresenter Create() =>
        new(_policy, _overlay, _transcription, _meeting, _runs, _runService, _work, _restart);

    [Fact]
    public void WithNothingInFlight_TheOverlayIsShownAndRestartsOnTheResult()
    {
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(1, presenter.Shows);
        _restart.Received(1).RestartAsync();
    }

    [Fact]
    public void WithNoRestartRequired_NothingIsShown()
    {
        _policy.IsRestartRequired.Returns(false);
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(0, presenter.Shows);
    }

    [Theory]
    [InlineData(DirectTranscriptionState.Starting)]
    [InlineData(DirectTranscriptionState.Running)]
    [InlineData(DirectTranscriptionState.Stopping)]
    public void ALiveTranscription_DefersTheOverlay(DirectTranscriptionState state)
    {
        _transcription.State.Returns(state);
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(0, presenter.Shows);
    }

    /// <summary>A capture state is not the whole answer, so a state the service can sit in with no transcript
    /// behind it must not defer on its own. The open-overlay report below is what covers the transcript.</summary>
    [Theory]
    [InlineData(DirectTranscriptionState.Idle)]
    [InlineData(DirectTranscriptionState.Error)]
    public void ATranscriptionThatIsNotCapturing_DoesNotDeferTheOverlay(DirectTranscriptionState state)
    {
        _transcription.State.Returns(state);
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(1, presenter.Shows);
    }

    [Theory]
    [InlineData(MeetingAttendeeState.Joining)]
    [InlineData(MeetingAttendeeState.InLobby)]
    [InlineData(MeetingAttendeeState.Attending)]
    [InlineData(MeetingAttendeeState.Stopping)]
    public void ALiveMeeting_DefersTheOverlay(MeetingAttendeeState state)
    {
        _meeting.State.Returns(state);
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(0, presenter.Shows);
    }

    [Fact]
    public void AnExecutingRun_DefersTheOverlay()
    {
        _runs.IsAnyExecuting.Returns(true);
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(0, presenter.Shows);
    }

    /// <summary>Stop leaves the session Prepared, which is the ONLY state Save works in, so gating on the
    /// capture state alone puts the scrim over the Save button at the instant it appears.</summary>
    [Fact]
    public void AnOpenTranscriptOverlay_DefersTheOverlayEvenWhenNothingIsCapturing()
    {
        _transcription.State.Returns(DirectTranscriptionState.Prepared);
        _work.Report(this, true);
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(0, presenter.Shows);
    }

    [Fact]
    public void AStreamingChat_DefersTheOverlay()
    {
        _work.Report(this, true);
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(0, presenter.Shows);
    }

    /// <summary>The gate defers rather than suppresses, so the transition has to be covered too.</summary>
    [Fact]
    public void WhenTheTranscriptionStops_TheDeferredOverlayAppears()
    {
        _transcription.State.Returns(DirectTranscriptionState.Running);
        using var presenter = Create();
        presenter.Start();
        Assert.Equal(0, presenter.Shows);

        _transcription.State.Returns(DirectTranscriptionState.Idle);
        _transcription.StateChanged += Raise.Event<EventHandler<DirectTranscriptionState>>(
            _transcription, DirectTranscriptionState.Idle);

        Assert.Equal(1, presenter.Shows);
    }

    [Fact]
    public void WhenTheMeetingEnds_TheDeferredOverlayAppears()
    {
        _meeting.State.Returns(MeetingAttendeeState.Attending);
        using var presenter = Create();
        presenter.Start();
        Assert.Equal(0, presenter.Shows);

        _meeting.State.Returns(MeetingAttendeeState.Idle);
        _meeting.StateChanged += Raise.Event<EventHandler<MeetingAttendeeState>>(
            _meeting, MeetingAttendeeState.Idle);

        Assert.Equal(1, presenter.Shows);
    }

    /// <summary>The store is the only re-evaluation trigger for the two per-window inputs.</summary>
    [Fact]
    public void WhenTheTranscriptOverlayCloses_TheDeferredOverlayAppears()
    {
        _work.Report(this, true);
        using var presenter = Create();
        presenter.Start();
        Assert.Equal(0, presenter.Shows);

        _work.Report(this, false);

        Assert.Equal(1, presenter.Shows);
    }

    /// <summary>A closed window's report must not defer the overlay for the rest of the process.</summary>
    [Fact]
    public void WhenTheReportingWindowIsForgotten_TheDeferredOverlayAppears()
    {
        _work.Report(this, true);
        using var presenter = Create();
        presenter.Start();
        Assert.Equal(0, presenter.Shows);

        _work.Forget(this);

        Assert.Equal(1, presenter.Shows);
    }

    /// <summary>The terminal RunChanged arrives while the launch bracket is still open, and nothing fires
    /// again afterwards, so re-reading "is anything executing" there defers the overlay forever.</summary>
    [Fact]
    public void WhenTheRunSettles_TheDeferredOverlayAppears()
    {
        var runId = Guid.NewGuid();
        _runs.IsAnyExecuting.Returns(true);
        _runs.IsAnyExecutingExcept(runId).Returns(false);
        using var presenter = Create();
        presenter.Start();
        Assert.Equal(0, presenter.Shows);

        _runService.RunChanged += Raise.Event<EventHandler<AgentRunChangedEventArgs>>(
            _runService, new AgentRunChangedEventArgs(runId, Pia.Models.AgentRunState.Completed));

        Assert.Equal(1, presenter.Shows);
        // Releasing instead would erase the chat id the manager's own handler still needs.
        _runs.DidNotReceive().Release(Arg.Any<Guid>());
    }

    /// <summary>A second run still holding a bracket keeps the overlay deferred.</summary>
    [Fact]
    public void WhenOneOfTwoRunsSettles_TheOverlayStaysDeferred()
    {
        var runId = Guid.NewGuid();
        _runs.IsAnyExecuting.Returns(true);
        _runs.IsAnyExecutingExcept(runId).Returns(true);
        using var presenter = Create();
        presenter.Start();

        _runService.RunChanged += Raise.Event<EventHandler<AgentRunChangedEventArgs>>(
            _runService, new AgentRunChangedEventArgs(runId, Pia.Models.AgentRunState.Completed));

        Assert.Equal(0, presenter.Shows);
    }

    /// <summary>A run reaching an EXECUTING state says nothing about its own bracket being closed.</summary>
    [Fact]
    public void WhenARunStartsExecuting_TheSettlingExemptionDoesNotApply()
    {
        var runId = Guid.NewGuid();
        _runs.IsAnyExecuting.Returns(true);
        _runs.IsAnyExecutingExcept(runId).Returns(false);
        using var presenter = Create();
        presenter.Start();

        _runService.RunChanged += Raise.Event<EventHandler<AgentRunChangedEventArgs>>(
            _runService, new AgentRunChangedEventArgs(runId, Pia.Models.AgentRunState.Running));

        Assert.Equal(0, presenter.Shows);
    }

    /// <summary>A window opened before the change has to pick it up from the event.</summary>
    [Fact]
    public void WhenTheRestartFlagArrivesLater_TheOverlayAppears()
    {
        _policy.IsRestartRequired.Returns(false);
        using var presenter = Create();
        presenter.Start();
        Assert.Equal(0, presenter.Shows);

        _policy.IsRestartRequired.Returns(true);
        _policy.RestartRequiredChanged += Raise.Event<EventHandler>(_policy, EventArgs.Empty);

        Assert.Equal(1, presenter.Shows);
    }

    /// <summary>A second show would replace the host content and leave the first result pending forever.</summary>
    [Fact]
    public void TheOverlayIsShownOnlyOnce()
    {
        using var presenter = Create();
        presenter.Start();

        _policy.RestartRequiredChanged += Raise.Event<EventHandler>(_policy, EventArgs.Empty);
        _transcription.StateChanged += Raise.Event<EventHandler<DirectTranscriptionState>>(
            _transcription, DirectTranscriptionState.Idle);

        Assert.Equal(1, presenter.Shows);
    }

    /// <summary>Nothing is shown before the window has handed its overlay host over.</summary>
    [Fact]
    public void WithoutStart_NothingIsShown()
    {
        using var presenter = Create();

        _policy.RestartRequiredChanged += Raise.Event<EventHandler>(_policy, EventArgs.Empty);

        Assert.Equal(0, presenter.Shows);
    }

    /// <summary>Every window obeys the store, so an Optimize window cannot offer Restart while the Assistant
    /// window is mid-turn. Its own scope holds neither the chat manager nor the transcript overlays.</summary>
    [Fact]
    public void AWindowOfItsOwn_StillObeysAnotherWindowsWork()
    {
        _work.Report(new object(), true);
        using var presenter = Create();

        presenter.Start();

        Assert.Equal(0, presenter.Shows);
    }

    /// <summary>A host that was not ready must not retire the forcing overlay for the window's lifetime.</summary>
    [Fact]
    public void WhenTheShowThrows_ALaterTriggerShowsItAgain()
    {
        using var presenter = Create();
        presenter.ShowThrows = new InvalidOperationException("no overlay host has been set");
        presenter.Start();
        Assert.Equal(1, presenter.Shows);

        presenter.ShowThrows = null;
        _policy.RestartRequiredChanged += Raise.Event<EventHandler>(_policy, EventArgs.Empty);

        Assert.Equal(2, presenter.Shows);
        _restart.Received(1).RestartAsync();
    }

    [Fact]
    public void AfterDispose_NoTriggerShowsTheOverlay()
    {
        _policy.IsRestartRequired.Returns(false);
        var presenter = Create();
        presenter.Start();
        presenter.Dispose();

        _policy.IsRestartRequired.Returns(true);
        _policy.RestartRequiredChanged += Raise.Event<EventHandler>(_policy, EventArgs.Empty);
        _work.Report(this, true);
        _work.Report(this, false);

        Assert.Equal(0, presenter.Shows);
    }
}
