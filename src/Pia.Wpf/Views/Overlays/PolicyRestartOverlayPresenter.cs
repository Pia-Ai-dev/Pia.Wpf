using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Views.Dialogs.Overlay;

namespace Pia.Views.Overlays;

/// <summary>
/// Shows one window's forcing policy-restart overlay. The flag itself lives on the singleton
/// <see cref="IPolicyService"/>, so both windows show it and a window opened later shows it on open.
/// </summary>
public class PolicyRestartOverlayPresenter : IDisposable
{
    private readonly IPolicyService _policyService;
    private readonly IDialogOverlayService _overlayService;
    private readonly IDirectTranscriptionService _directTranscription;
    private readonly IMeetingAttendeeService _meetingAttendee;
    private readonly IExecutingRunStore _executingRuns;
    private readonly IAgentRunService _agentRunService;
    private readonly IVolatileWorkStore _volatileWork;
    private readonly IAppRestartService _appRestartService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILogger<PolicyRestartOverlayPresenter> _logger;

    private bool _restartRequired;
    private bool _started;
    private bool _shown;
    private bool _disposed;

    public PolicyRestartOverlayPresenter(
        IPolicyService policyService,
        IDialogOverlayService overlayService,
        IDirectTranscriptionService directTranscription,
        IMeetingAttendeeService meetingAttendee,
        IExecutingRunStore executingRuns,
        IAgentRunService agentRunService,
        IVolatileWorkStore volatileWork,
        IAppRestartService appRestartService,
        IUiDispatcher uiDispatcher,
        ILogger<PolicyRestartOverlayPresenter> logger)
    {
        _policyService = policyService;
        _overlayService = overlayService;
        _directTranscription = directTranscription;
        _meetingAttendee = meetingAttendee;
        _executingRuns = executingRuns;
        _agentRunService = agentRunService;
        _volatileWork = volatileWork;
        _appRestartService = appRestartService;
        _uiDispatcher = uiDispatcher;
        _logger = logger;

        // Seeded here rather than in Start: a window opened after the change must still show the overlay.
        _restartRequired = policyService.IsRestartRequired;

        _policyService.RestartRequiredChanged += OnRestartRequiredChanged;
        _directTranscription.StateChanged += OnTranscriptionStateChanged;
        _meetingAttendee.StateChanged += OnMeetingStateChanged;
        _agentRunService.RunChanged += OnRunChanged;
        _volatileWork.Changed += OnVolatileWorkChanged;
    }

    /// <summary>Arms the presenter once the window's overlay host is live.</summary>
    public void Start()
    {
        if (_disposed || _started)
            return;

        _started = true;
        Reevaluate();
    }

    /// <summary>
    /// The deferral gate. There is no dismiss and no Escape, so a scrim raised over a live transcript or a
    /// streaming turn would cover the very controls that could rescue the work.
    /// </summary>
    internal bool CanShow(Guid? settlingRun = null) =>
        _directTranscription.State is not (DirectTranscriptionState.Starting
            or DirectTranscriptionState.Running
            or DirectTranscriptionState.Stopping)
        && _meetingAttendee.State is not (MeetingAttendeeState.Joining
            or MeetingAttendeeState.InLobby
            or MeetingAttendeeState.Attending
            or MeetingAttendeeState.Stopping)
        && !(settlingRun is { } runId
            ? _executingRuns.IsAnyExecutingExcept(runId)
            : _executingRuns.IsAnyExecuting)
        // The per-window inputs (an open transcript overlay, a streaming turn) come through the singleton
        // store, so an Optimize window cannot answer "nothing in flight" for the Assistant window's work.
        && !_volatileWork.HasVolatileWork;

    /// <summary>Overridden by tests, which have no UI thread to build a panel on.</summary>
    protected virtual Task ShowOverlayAsync(Func<Task> onRestartRequested)
    {
        // Never completes, and gets no token: the panel raises no result, so the host holds the scrim up
        // while Pia shuts down instead of collapsing it and handing back a live app.
        return _overlayService.GetOverlayHost()
            .ShowAsync<Views.Controls.OverlayDialogResult>(CreatePanel(onRestartRequested));
    }

    /// <summary>Split from the show so a test can drive the real panel and this wiring without a host.</summary>
    internal PolicyRestartOverlayPanel CreatePanel(Func<Task> onRestartRequested)
    {
        var panel = new PolicyRestartOverlayPanel();
        panel.RestartRequested += (_, _) => onRestartRequested().SafeFireAndForget(_logger);
        return panel;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _policyService.RestartRequiredChanged -= OnRestartRequiredChanged;
        _directTranscription.StateChanged -= OnTranscriptionStateChanged;
        _meetingAttendee.StateChanged -= OnMeetingStateChanged;
        _agentRunService.RunChanged -= OnRunChanged;
        _volatileWork.Changed -= OnVolatileWorkChanged;
    }

    // Marshaled around the whole evaluation, not just the show: every trigger below can fire off the UI
    // thread, and both the overlay host and the work store's publishers belong to it.
    private void OnRestartRequiredChanged(object? sender, EventArgs e) => _uiDispatcher.Post(() =>
    {
        _restartRequired = _policyService.IsRestartRequired;
        Reevaluate();
    });

    private void OnTranscriptionStateChanged(object? sender, DirectTranscriptionState e) =>
        _uiDispatcher.Post(() => Reevaluate());

    private void OnMeetingStateChanged(object? sender, MeetingAttendeeState e) =>
        _uiDispatcher.Post(() => Reevaluate());

    // The settling run is excluded by id: AgentRunService raises the terminal state before the launcher's
    // finally closes the bracket, so "is anything executing" would otherwise answer yes forever.
    private void OnRunChanged(object? sender, AgentRunChangedEventArgs e) => _uiDispatcher.Post(() =>
        Reevaluate(e.State is Models.AgentRunState.Planning or Models.AgentRunState.Running
            or Models.AgentRunState.Verifying or Models.AgentRunState.WaitingForChildren
            ? null
            : e.RunId));

    private void OnVolatileWorkChanged(object? sender, EventArgs e) => _uiDispatcher.Post(() => Reevaluate());

    private void Reevaluate(Guid? settlingRun = null)
    {
        if (_disposed || !_started || _shown || !_restartRequired || !CanShow(settlingRun))
            return;

        // Latched before the await and never cleared while the show holds: a second show would replace the
        // host's content and leave the first result task pending forever.
        _shown = true;
        ShowGuardedAsync().SafeFireAndForget(_logger);
    }

    private async Task ShowGuardedAsync()
    {
        try
        {
            await ShowOverlayAsync(_appRestartService.RestartAsync);
        }
        catch (Exception ex)
        {
            // A host that was not ready must not cost this window its forcing overlay for the whole process.
            _shown = false;
            _logger.LogWarning(ex, "Failed to show the policy-restart overlay");
        }
    }
}
