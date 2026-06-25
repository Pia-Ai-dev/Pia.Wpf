using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
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

        _service.StateChanged += OnServiceStateChanged;

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
        DispatchToUi(() => AssistantDisplayName = name);
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
            Bubbles.Clear();
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

    // ---- State → status --------------------------------------------------------------------------

    private void OnServiceStateChanged(object? sender, MeetingAttendeeState newState)
    {
        DispatchToUi(() =>
        {
            // "Running" spans the whole active lifecycle (provisioning → attending → stopping) so the
            // Stop button shows while busy and Save only shows once idle/errored.
            IsRunning = newState is not (MeetingAttendeeState.Idle or MeetingAttendeeState.Error);
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
        });
    }

    protected override void OnRunningChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
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

        if (_service.State is not (MeetingAttendeeState.Idle or MeetingAttendeeState.Error))
        {
            try { _service.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to stop meeting attendee service on dispose"); }
        }

        base.Dispose();
    }
}
