using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;

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
        ILogger<MeetingAttendeeViewModel> logger)
        : base(settingsService, localizationService, fileDialogService, logger)
    {
        _service = service;
        CounterpartName = _localizationService["MeetingAttendee_Speaker_Placeholder"];

        // Construct StopCommand BEFORE subscribing: OnServiceStateChanged → OnRunningChanged calls
        // StopCommand.NotifyCanExecuteChanged(), so a state change raised during wiring must not NRE.
        StopCommand = new AsyncRelayCommand(StopAsync);

        _service.StateChanged += OnServiceStateChanged;

        StatusText = _localizationService["MeetingAttendee_Status_Idle"];
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

        try
        {
            await _service.StartAsync(MeetingUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The service already transitioned to Error and tore down its resources; surface the
            // failure via status and stop the reader. Don't log the URL.
            _logger.LogError(ex, "Failed to start meeting attendee");
            await StopReaderAsync().ConfigureAwait(false);
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
