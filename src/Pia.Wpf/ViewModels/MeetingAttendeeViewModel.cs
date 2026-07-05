using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Services.MeetingAttendee;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// Drives the "meeting attendee" overlay. Builds on <see cref="TranscriptOverlayViewModel"/> (shared
/// bubble mapping, Markdown export, save flow, reader plumbing); adds the attendee-specific wiring:
/// it subscribes to <see cref="IMeetingAttendeeService.StateChanged"/> for status, and gates
/// <see cref="StartCommand"/> on a valid <see cref="MeetingUrl"/> plus a one-time
/// <see cref="ConsentAcknowledged"/>.
///
/// <para>Differences from live transcription: there is no microphone pipeline, so there is no
/// <c>SpeakingChanged</c> event to honour (the attendee only ever produces <see cref="TranscriptSpeaker.Them"/>
/// utterances); and the session needs a meeting URL plus a one-time consent acknowledgement before it
/// can start.</para>
/// </summary>
public partial class MeetingAttendeeViewModel : TranscriptOverlayViewModel
{
    private readonly IMeetingAttendeeService _service;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Stop command. Constructed manually (not via <c>[RelayCommand]</c>) so <see cref="StopAsync"/>
    /// can stay public for <see cref="AssistantViewModel"/> to invoke directly, matching
    /// <c>LiveTranscriptionViewModel</c>.
    /// </summary>
    public IRelayCommand StopCommand { get; }

    /// <summary>The Teams meeting URL the user pastes. Gates <see cref="StartCommand"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _meetingUrl = string.Empty;

    /// <summary>
    /// One-time, in-session acknowledgement that the user is allowed to have an assistant join and
    /// transcribe the meeting. Gates <see cref="StartCommand"/> (see open questions re: org policy).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _consentAcknowledged;

    /// <summary>
    /// The display name the assistant joins the meeting under. Pre-filled on open by
    /// <see cref="PrepareForDisplayAsync"/> from the persisted <see cref="AppSettings.MeetingAttendeeDisplayName"/>
    /// (or the auto-built "{user}'s assistant" default), editable by the user, and persisted again when a
    /// meeting starts. Does NOT gate <see cref="StartCommand"/>: a blank value falls back to the auto-built
    /// default in the service, so an empty box never blocks joining.
    /// </summary>
    [ObservableProperty]
    private string _assistantDisplayName = string.Empty;

    /// <summary>
    /// True during the transitional join/leave phases (provisioning, joining, lobby, stopping) so the
    /// header can show a busy spinner next to the status text. Deliberately false in the steady
    /// <see cref="MeetingAttendeeState.Attending"/> state (transcribing is not "busy") and when idle or
    /// errored.
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Set once the assistant has actually been admitted to a meeting this overlay session. Drives
    /// <see cref="IsJoinSetupVisible"/>: after a meeting has been attended and left, the overlay shows
    /// the transcript alone rather than re-displaying the join form. A join that fails before admission
    /// leaves this false, so the form stays available for a retry. Reset on each (re)open by
    /// <see cref="PrepareForDisplayAsync"/>.
    /// </summary>
    private bool _hasAttendedMeeting;

    /// <summary>
    /// Whether the join setup (requirements + URL + name + consent + Join button) is shown. Visible only
    /// before the first meeting of this overlay session, and never while a session is running. Computed
    /// (get-only), so every mutation of its inputs (<see cref="TranscriptOverlayViewModel.IsRunning"/> and
    /// <see cref="_hasAttendedMeeting"/>) must raise its change notification explicitly.
    /// </summary>
    public bool IsJoinSetupVisible => !IsRunning && !_hasAttendedMeeting;

    protected override System.Threading.Channels.ChannelReader<TranscriptUtterance> UtteranceReader
        => _service.Utterances;

    protected override string TitleKey => "MeetingAttendee_Title";
    protected override string SaveDialogTitleKey => "MeetingAttendee_SaveDialog_Title";
    protected override string SaveDialogFilterKey => "MeetingAttendee_SaveDialog_Filter";
    protected override string SaveFileNamePrefix => "meeting";

    public MeetingAttendeeViewModel(
        IMeetingAttendeeService service,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IFileDialogService fileDialogService,
        IDialogService dialogService,
        ILogger<MeetingAttendeeViewModel> logger)
        : base(settingsService, localizationService, fileDialogService, logger)
    {
        _service = service;
        _dialogService = dialogService;
        CounterpartName = _localizationService["MeetingAttendee_Speaker_Placeholder"];

        // Construct StopCommand BEFORE subscribing: OnServiceStateChanged → OnRunningChanged calls
        // StopCommand.NotifyCanExecuteChanged(), so a state change raised during wiring must not NRE.
        StopCommand = new AsyncRelayCommand(StopAsync);

        // The Summarize command shares Save's gating (transcript present + not running). The base
        // refreshes Save on bubble-collection changes; mirror that for this derived command here (the
        // IsRunning side is handled in the OnRunningChanged override below).
        Bubbles.CollectionChanged += (_, _) => SummarizeWithAssistantCommand.NotifyCanExecuteChanged();

        _service.StateChanged += OnServiceStateChanged;
        _service.SpeakersReassigned += OnSpeakersReassigned;

        StatusText = _localizationService["MeetingAttendee_Status_Idle"];
    }

    // ---- Open / pre-fill --------------------------------------------------------------------------

    /// <summary>
    /// Pre-fills <see cref="AssistantDisplayName"/> when the overlay is shown: the persisted
    /// <see cref="AppSettings.MeetingAttendeeDisplayName"/> if the user set one, otherwise the auto-built
    /// "{user}'s assistant" default. Called by <c>AssistantViewModel</c> just before revealing the overlay
    /// so the default reflects the currently signed-in user.
    /// </summary>
    public async Task PrepareForDisplayAsync()
    {
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        var name = string.IsNullOrWhiteSpace(settings.MeetingAttendeeDisplayName)
            ? MeetingAttendeeService.BuildDisplayName(settings.SyncUserDisplayName)
            : settings.MeetingAttendeeDisplayName;
        DispatchToUi(() =>
        {
            AssistantDisplayName = name;
            // Fresh open: show the join form again even if a previous meeting was attended this session,
            // and discard that meeting's transcript so the form is never rendered above a stale transcript
            // (Save/Summarize stay disabled until a new meeting produces bubbles). The post-meeting page
            // keeps the transcript until the overlay is closed; reopening is a deliberate clean slate.
            _hasAttendedMeeting = false;
            ClearTranscript();
            OnPropertyChanged(nameof(IsJoinSetupVisible));
        });
    }

    // ---- Start ------------------------------------------------------------------------------------

    private bool CanStart()
        => !IsRunning
           && ConsentAcknowledged
           && TeamsMeetingUrl.IsLikelyTeamsUrl(MeetingUrl);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!CanStart()) return;

        // Do NOT log the meeting URL (privacy); only that a start was requested.
        _logger.LogInformation("MeetingAttendee ViewModel: StartAsync invoked");

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(settings.LastCounterpartName))
            DispatchToUi(() => CounterpartName = settings.LastCounterpartName!);

        // Persist the (possibly edited) assistant display name BEFORE starting the service: the service
        // reads it back from settings to name the bot for THIS meeting, and it pre-fills the field next
        // time. Blank → null so a cleared field falls back to the auto-built default.
        settings.MeetingAttendeeDisplayName = string.IsNullOrWhiteSpace(AssistantDisplayName)
            ? null
            : AssistantDisplayName.Trim();
        await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);

        DispatchToUi(() =>
        {
            ClearTranscript();
            _sessionStart = DateTimeOffset.Now;
            StatusText = _localizationService["MeetingAttendee_Status_Provisioning"];
        });

        // Launch the utterance consumer before starting the service so no utterance is missed.
        // StartReaderAsync first tears down any reader left parked by a prior session (e.g. the
        // service auto-stopped on natural meeting end, which completes the service but not the
        // channel) so a restart never stacks two readers on the SingleReader channel.
        await StartReaderAsync().ConfigureAwait(false);

        // Speaker-model download progress dialog. ONLY surfaced when diarization is enabled AND the model
        // is not already on disk — otherwise the speaker model emits no Downloading report and we never
        // show anything. The dialog is dismissed by a terminal Completed report from the service (which
        // fires on success, failure→null, AND cancellation), so it can never be left stuck and the meeting
        // join is never blocked on it. We deliberately do NOT tie its lifetime to StartAsync completing —
        // StartAsync also covers the up-to-120s join, which must not hold the dialog open.
        var showSpeakerProgress = settings.EnableMeetingDiarization
            && !Services.LiveTranscription.LiveTranscriptionModels.IsSpeakerEmbeddingAvailable();
        var speakerDownload = showSpeakerProgress
            ? new SpeakerModelDownloadUi(_dialogService, _localizationService, DispatchToUi)
            : null;

        try
        {
            await _service.StartAsync(MeetingUrl, cancellationToken, speakerDownload?.Progress).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The service already transitioned to Error and tore down its resources; surface the
            // failure via status and stop the reader. Don't log the URL.
            _logger.LogError(ex, "Failed to start meeting attendee");
            await StopReaderAsync().ConfigureAwait(false);
        }
        finally
        {
            // Backstop: the primary dismissal is the service's terminal Completed report (before the join),
            // but if the start faulted before the speaker step or no terminal report arrived, ensure the
            // dialog is closed and disposed. No-op when it was never shown.
            if (speakerDownload is not null) await speakerDownload.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- Stop -------------------------------------------------------------------------------------

    public async Task StopAsync()
    {
        DispatchToUi(() => StatusText = _localizationService["MeetingAttendee_Status_Stopping"]);

        try
        {
            await _service.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop meeting attendee service");
        }

        await StopReaderAsync().ConfigureAwait(false);

        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            settings.LastCounterpartName = string.IsNullOrWhiteSpace(CounterpartName) ? null : CounterpartName;
            await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist last counterpart name");
        }
    }

    // ---- Summarize with the assistant ------------------------------------------------------------

    /// <summary>
    /// Raised when the user clicks "Summarize with assistant" on the post-meeting transcript. Carries a
    /// ready-to-send prompt (a localized instruction describing the transcript's provenance, followed by
    /// the transcript Markdown). The host <see cref="AssistantViewModel"/> handles it by hiding the
    /// overlay and sending the prompt to a fresh chat. Meeting-specific, so it lives here rather than on
    /// the shared base.
    /// </summary>
    public event EventHandler<string>? SummarizeRequested;

    /// <summary>
    /// Hands a summarization prompt to the host assistant. Shares <see cref="CanSummarize"/> gating with
    /// the Save command (a transcript exists and the meeting is no longer running).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSummarize))]
    private void SummarizeWithAssistant()
    {
        if (!CanSummarize()) return;
        // Do NOT log the prompt or transcript (sensitive user content); only that a summary was requested
        // — mirrors the URL-omitting StartAsync log line.
        _logger.LogInformation("MeetingAttendee ViewModel: summary requested");
        SummarizeRequested?.Invoke(this, BuildSummaryPrompt());
    }

    private bool CanSummarize() => !IsRunning && Bubbles.Count > 0;

    /// <summary>
    /// Builds the prompt sent to the assistant: a localized instruction that explains the transcript's
    /// provenance (a Teams meeting the assistant attended, under which name, and when) followed by the
    /// transcript Markdown. The transcript is appended in code (not via the format args) so the resource
    /// template stays free of the large payload; braces in the transcript would be safe regardless, since
    /// only the format template is parsed.
    /// </summary>
    private string BuildSummaryPrompt()
    {
        var name = string.IsNullOrWhiteSpace(AssistantDisplayName)
            ? MeetingAttendeeService.BuildDisplayName(null)
            : AssistantDisplayName.Trim();
        var when = _sessionStart.LocalDateTime.ToString("f");
        var instruction = _localizationService.Format("MeetingAttendee_SummaryPrompt", name, when);

        var sb = new System.Text.StringBuilder();
        sb.Append(instruction);

        // Attendee roster (if any was observed) as metadata: a localized lead-in plus a bulleted list of
        // the names seen in the meeting, so the assistant can map the diarized "Speaker N" labels to people.
        var attendees = _service.ObservedAttendees;
        if (attendees.Count > 0)
        {
            sb.AppendLine().AppendLine();
            sb.AppendLine(_localizationService["MeetingAttendee_SummaryPrompt_Attendees"]);
            foreach (var attendee in attendees)
                sb.Append("- ").AppendLine(attendee);
        }

        sb.AppendLine().AppendLine().Append(BuildMarkdown());
        return sb.ToString();
    }

    // ---- Open meeting settings -------------------------------------------------------------------

    /// <summary>
    /// Raised when the user clicks the "Meeting settings" link on the join setup page. The host
    /// <see cref="AssistantViewModel"/> handles it by deep-linking to the Assistant settings → Meeting
    /// tab. Lives here (not on the shared base) because only the meeting attendee exposes the link, and
    /// it keeps the settings tab indices co-located with the other deep-links in the host.
    /// </summary>
    public event EventHandler? OpenSettingsRequested;

    /// <summary>
    /// Raises <see cref="OpenSettingsRequested"/> so the host can navigate to the meeting settings. The
    /// link is only shown on the join setup page (hidden once a meeting is running via
    /// <see cref="IsJoinSetupVisible"/>), so this never fires mid-meeting and needs no session teardown.
    /// </summary>
    [RelayCommand]
    private void OpenMeetingSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    // ---- Rename speaker (in-session only) --------------------------------------------------------

    /// <summary>
    /// Renames a diarized speaker label for the current meeting only: prompts for a new name, retargets
    /// the live diarizer label map via <see cref="IMeetingAttendeeService.RenameSpeaker"/>, then re-keys
    /// the palette slot and retroactively relabels existing bubbles via the base
    /// <see cref="TranscriptOverlayViewModel.RelabelSpeaker"/>. No persistence — discarded at meeting end.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRenameSpeakerLabel))]
    private async Task RenameSpeakerLabelAsync(string? oldLabel)
    {
        if (string.IsNullOrWhiteSpace(oldLabel)) return;

        var title = _localizationService["MeetingAttendee_RenameSpeaker_Title"];
        var prompt = string.Format(_localizationService["MeetingAttendee_RenameSpeaker_Prompt"], oldLabel);
        var newLabel = await _dialogService.ShowInputDialogAsync(title, prompt).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newLabel) || newLabel == oldLabel) return;

        _service.RenameSpeaker(oldLabel, newLabel);
        RelabelSpeaker(oldLabel, newLabel);   // base helper: palette re-key + bubble walk
    }

    private static bool CanRenameSpeakerLabel(string? oldLabel) => !string.IsNullOrWhiteSpace(oldLabel);

    /// <summary>Test seam: exposes the base VM's protected <see cref="TranscriptOverlayViewModel.RelabelSpeaker"/>.</summary>
    internal void RelabelSpeakerForTest(string oldLabel, string newLabel) => RelabelSpeaker(oldLabel, newLabel);

    private void OnSpeakersReassigned(object? sender, IReadOnlyList<SpeakerReassignment> changes)
        => ApplyReassignments(changes);

    // ---- State → status --------------------------------------------------------------------------

    private void OnServiceStateChanged(object? sender, MeetingAttendeeState newState)
    {
        DispatchToUi(() =>
        {
            // "Running" spans the whole active lifecycle (provisioning → attending → stopping) so the
            // Stop button shows while busy and Save only shows once idle/errored.
            IsRunning = newState is not (MeetingAttendeeState.Idle or MeetingAttendeeState.Error);

            // Busy spinner: shown next to the status during the transitional join/leave phases — covers
            // both "joining" and "leaving" — but not the steady Attending state nor idle/error.
            IsBusy = newState is MeetingAttendeeState.ProvisioningBrowser
                or MeetingAttendeeState.Joining
                or MeetingAttendeeState.InLobby
                or MeetingAttendeeState.Stopping;

            // Once admitted, leaving the meeting must land on the transcript alone (never re-show the join
            // form). Latched true and only cleared on the next (re)open.
            if (newState == MeetingAttendeeState.Attending)
                _hasAttendedMeeting = true;

            StatusText = newState switch
            {
                MeetingAttendeeState.Idle => _localizationService["MeetingAttendee_Status_Idle"],
                MeetingAttendeeState.ProvisioningBrowser => _localizationService["MeetingAttendee_Status_Provisioning"],
                MeetingAttendeeState.Joining => _localizationService["MeetingAttendee_Status_Joining"],
                MeetingAttendeeState.InLobby => _localizationService["MeetingAttendee_Status_InLobby"],
                MeetingAttendeeState.Attending => _localizationService["MeetingAttendee_Status_Attending"],
                MeetingAttendeeState.Stopping => _localizationService["MeetingAttendee_Status_Stopping"],
                MeetingAttendeeState.Error => _localizationService["MeetingAttendee_Status_Error"],
                _ => string.Empty,
            };

            // IsJoinSetupVisible is computed from IsRunning + _hasAttendedMeeting; the IsRunning side is
            // covered by OnRunningChanged, but the _hasAttendedMeeting latch above also needs a nudge.
            OnPropertyChanged(nameof(IsJoinSetupVisible));
        });
    }

    protected override void OnRunningChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SummarizeWithAssistantCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsJoinSetupVisible));
    }

    public override void Dispose()
    {
        // Unsubscribe BEFORE stopping: the backing MeetingAttendeeService is a singleton, so the only
        // teardown of its active meeting (off-screen Chromium + WASAPI capture + end-watch loop) on the
        // app-shutdown path (CloseAndDisposeAll → scope dispose → AssistantViewModel.Dispose →
        // MeetingAttendee.Dispose) is here. With the handler detached, nothing in StopAsync's teardown
        // can DispatchToUi back onto the thread we block below, so the sync-over-async is safe (StopAsync
        // uses ConfigureAwait(false) throughout). A meeting left running would otherwise orphan an
        // invisible chrome.exe tree that survives process exit.
        _service.StateChanged -= OnServiceStateChanged;
        _service.SpeakersReassigned -= OnSpeakersReassigned;

        if (_service.State is not (MeetingAttendeeState.Idle or MeetingAttendeeState.Error))
        {
            try { _service.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to stop meeting attendee service on dispose"); }
        }

        base.Dispose();
    }
}
