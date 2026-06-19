using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class LiveTranscriptionViewModel : TranscriptOverlayViewModel
{
    private readonly ILiveMeetingService _service;

    public IRelayCommand StopCommand { get; }

    protected override System.Threading.Channels.ChannelReader<TranscriptUtterance> UtteranceReader
        => _service.Utterances;

    protected override string TitleKey => "LiveTrans_Title";
    protected override string SaveDialogTitleKey => "LiveTrans_SaveDialog_Title";
    protected override string SaveDialogFilterKey => "LiveTrans_SaveDialog_Filter";
    protected override string SaveFileNamePrefix => "transcript";

    public LiveTranscriptionViewModel(
        ILiveMeetingService service,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IFileDialogService fileDialogService,
        ILogger<LiveTranscriptionViewModel> logger)
        : base(settingsService, localizationService, fileDialogService, logger)
    {
        _service = service;
        CounterpartName = _localizationService["LiveTrans_OtherSpeaker_Placeholder"];

        StopCommand = new AsyncRelayCommand(StopAsync);

        _service.StateChanged += OnServiceStateChanged;
        _service.SpeakingChanged += OnServiceSpeakingChanged;

        StatusText = _localizationService["LiveTrans_Status_Idle"];
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_service.State is LiveMeetingState.Running or LiveMeetingState.Starting) return;

        _logger.LogInformation("LiveTranscription ViewModel: StartAsync invoked");

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(settings.LastCounterpartName))
            DispatchToUi(() => CounterpartName = settings.LastCounterpartName!);

        DispatchToUi(() =>
        {
            Bubbles.Clear();
            _sessionStart = DateTimeOffset.Now;
            StatusText = _localizationService["LiveTrans_Status_Starting"];
        });

        await _service.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("LiveTranscription ViewModel: service started, launching consumer");

        await StartReaderAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        DispatchToUi(() => StatusText = _localizationService["LiveTrans_Status_Stopping"]);

        try
        {
            await _service.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop live meeting service");
        }

        await StopReaderAsync().ConfigureAwait(false);

        // Clear any lingering listening dots — VAD has been torn down.
        DispatchToUi(() =>
        {
            foreach (var b in Bubbles)
                if (b.IsListening) b.IsListening = false;
        });

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

    private void OnServiceStateChanged(object? sender, LiveMeetingState newState)
    {
        DispatchToUi(() =>
        {
            IsRunning = newState == LiveMeetingState.Running;
            StatusText = newState switch
            {
                LiveMeetingState.Idle => _localizationService["LiveTrans_Status_Idle"],
                LiveMeetingState.Starting => _localizationService["LiveTrans_Status_Starting"],
                LiveMeetingState.Running => _localizationService["LiveTrans_Status_Listening"],
                LiveMeetingState.Stopping => _localizationService["LiveTrans_Status_Stopping"],
                LiveMeetingState.Error => _localizationService["LiveTrans_Status_Error"],
                _ => string.Empty,
            };
        });
    }

    internal void OnServiceSpeakingChanged(object? sender, SpeakingChangedEventArgs e)
    {
        DispatchToUi(() =>
        {
            if (e.IsSpeaking)
            {
                var bubble = GetOrCreateBubble(e.Speaker, DateTimeOffset.Now, createIfMissing: true);
                if (bubble is not null) bubble.IsListening = true;
            }
            else
            {
                // Speech ended — clear the listening flag on the most recent bubble belonging
                // to this speaker. Walking backwards is fine; only one bubble per speaker is
                // ever marked listening at a time.
                for (int i = Bubbles.Count - 1; i >= 0; i--)
                {
                    if (Bubbles[i].Speaker == e.Speaker && Bubbles[i].IsListening)
                    {
                        Bubbles[i].IsListening = false;
                        break;
                    }
                }
            }
        });
    }

    public override void Dispose()
    {
        _service.StateChanged -= OnServiceStateChanged;
        _service.SpeakingChanged -= OnServiceSpeakingChanged;
        base.Dispose();
    }
}
