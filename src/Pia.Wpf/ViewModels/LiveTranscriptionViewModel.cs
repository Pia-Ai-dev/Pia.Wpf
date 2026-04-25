using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class LiveTranscriptionViewModel : ObservableObject, IDisposable
{
    private const int MaxUtterances = 2000;
    private const int TrimBatch = 200;

    private readonly ILiveMeetingService _service;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<LiveTranscriptionViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    [ObservableProperty]
    private string _counterpartName = "them";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ObservableCollection<TranscriptUtteranceViewModel> Utterances { get; } = [];

    public IRelayCommand StopCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event EventHandler? CloseRequested;

    public LiveTranscriptionViewModel(
        ILiveMeetingService service,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        ILogger<LiveTranscriptionViewModel> logger)
    {
        _service = service;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        StopCommand = new AsyncRelayCommand(StopAsync);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        _service.StateChanged += OnServiceStateChanged;
        StatusText = _localizationService["LiveTrans_Status_Idle"];
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_service.State is LiveMeetingState.Running or LiveMeetingState.Starting) return;

        // Restore last counterpart name if any.
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(settings.LastCounterpartName))
            DispatchToUi(() => CounterpartName = settings.LastCounterpartName!);

        DispatchToUi(() =>
        {
            Utterances.Clear();
            StatusText = _localizationService["LiveTrans_Status_Starting"];
        });

        await _service.StartAsync(cancellationToken).ConfigureAwait(false);

        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ConsumeUtterancesAsync(_readerCts.Token));
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

        try { _readerCts?.Cancel(); }
        catch { /* ignore */ }
        try { if (_readerTask is not null) await _readerTask.ConfigureAwait(false); }
        catch { /* ignore */ }
        _readerCts?.Dispose();
        _readerCts = null;
        _readerTask = null;

        // Persist counterpart name for next session.
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

    private async Task ConsumeUtterancesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var utt in _service.Utterances.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                AddUtterance(utt);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live transcription utterance consumer failed");
        }
    }

    private void AddUtterance(TranscriptUtterance utterance)
    {
        DispatchToUi(() =>
        {
            var vm = new TranscriptUtteranceViewModel(utterance, () => CounterpartName);
            Utterances.Add(vm);
            if (Utterances.Count > MaxUtterances)
            {
                for (int i = 0; i < TrimBatch && Utterances.Count > MaxUtterances - TrimBatch; i++)
                    Utterances.RemoveAt(0);
            }
        });
    }

    partial void OnCounterpartNameChanged(string value)
    {
        // Broadcast to every bubble VM so every "them" label re-renders.
        DispatchToUi(() =>
        {
            foreach (var u in Utterances) u.RefreshDisplayName();
        });
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

    private void DispatchToUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }

    public void Dispose()
    {
        _service.StateChanged -= OnServiceStateChanged;
        try { _readerCts?.Cancel(); }
        catch { /* ignore */ }
        _readerCts?.Dispose();
    }
}
