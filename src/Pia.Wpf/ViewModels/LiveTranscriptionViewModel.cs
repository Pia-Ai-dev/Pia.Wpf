using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Converters;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;

namespace Pia.ViewModels;

public partial class LiveTranscriptionViewModel : ObservableObject, IDisposable
{
    private const int MaxBubbles = 200;
    private const int TrimBatch = 20;
    private const int BubbleWindowSeconds = 25;

    private readonly ILiveMeetingService _service;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IDialogService _dialogService;
    private readonly IFileDialogService _fileDialogService;
    private readonly ILogger<LiveTranscriptionViewModel> _logger;

    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private Task? _prepareTask;

    private DateTimeOffset _sessionStart;
    private bool _sessionStarted;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// Privacy disclaimer overlay visibility — true when the user has not yet accepted that
    /// they have informed meeting participants. Reset to true every time the overlay reopens.
    /// </summary>
    [ObservableProperty]
    private bool _isDisclaimerVisible = true;

    /// <summary>
    /// Bound to the disclaimer toggle. The Start command is only enabled once this is true.
    /// </summary>
    [ObservableProperty]
    private bool _disclaimerAccepted;

    /// <summary>
    /// True while <see cref="ILiveMeetingService.PrepareAsync"/> runs in the background. The
    /// Start button shows a spinner / disabled state while this is in flight.
    /// </summary>
    [ObservableProperty]
    private bool _isPreparing;

    public ObservableCollection<TranscriptBubble> Bubbles { get; } = [];

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand SaveTranscriptCommand { get; }

    public event EventHandler? CloseRequested;

    public LiveTranscriptionViewModel(
        ILiveMeetingService service,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IDialogService dialogService,
        IFileDialogService fileDialogService,
        ILogger<LiveTranscriptionViewModel> logger)
    {
        _service = service;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _dialogService = dialogService;
        _fileDialogService = fileDialogService;
        _logger = logger;

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        SaveTranscriptCommand = new AsyncRelayCommand(SaveTranscriptAsync, CanSaveTranscript);

        _service.StateChanged += OnServiceStateChanged;
        _service.SpeakingChanged += OnServiceSpeakingChanged;

        Bubbles.CollectionChanged += OnBubblesCollectionChanged;

        StatusText = _localizationService["LiveTrans_Status_Idle"];
    }

    /// <summary>
    /// Kicks off model/device preparation in the background while the disclaimer is shown,
    /// so that the StartCommand only has to flip switches once the user accepts.
    /// </summary>
    public void BeginWarmup()
    {
        if (_prepareTask is { IsCompleted: false }) return;
        // Setting IsPreparing fires OnIsPreparingChanged → StartCommand.NotifyCanExecuteChanged,
        // which must run on the UI thread. BeginWarmup is invoked from both UI- and worker-thread
        // contexts (e.g., the StopAsync continuation), so always marshal.
        DispatchToUi(() => IsPreparing = true);
        _prepareTask = Task.Run(async () =>
        {
            try
            {
                await _service.PrepareAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Live meeting prepare failed");
            }
            finally
            {
                DispatchToUi(() => IsPreparing = false);
            }
        });
    }

    private bool CanStart()
        => DisclaimerAccepted && !IsRunning && _service.State is not LiveMeetingState.Starting and not LiveMeetingState.Stopping;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_service.State is LiveMeetingState.Running or LiveMeetingState.Starting) return;

        _logger.LogInformation("LiveTranscription ViewModel: StartAsync invoked (resume={Resume})", _sessionStarted);

        // Make sure the warmup task we kicked off when the overlay opened is finished.
        if (_prepareTask is not null)
        {
            try { await _prepareTask.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Prepare task ended with an error; StartAsync will retry"); }
        }

        if (!_sessionStarted)
        {
            DispatchToUi(() =>
            {
                Bubbles.Clear();
                _sessionStart = DateTimeOffset.Now;
                StatusText = _localizationService["LiveTrans_Status_Starting"];
            });
        }
        else
        {
            DispatchToUi(() => StatusText = _localizationService["LiveTrans_Status_Starting"]);
        }

        await _service.StartAsync(cancellationToken).ConfigureAwait(false);
        _sessionStarted = true;
        _logger.LogInformation("LiveTranscription ViewModel: service started, launching consumer");

        DispatchToUi(() => IsDisclaimerVisible = false);

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

        // Clear any lingering listening dots — VAD has been torn down.
        DispatchToUi(() =>
        {
            foreach (var b in Bubbles)
                if (b.IsListening) b.IsListening = false;
        });

        // Kick off another warmup so a future Resume click is fast (Stop disposes everything).
        _prepareTask = null;
        BeginWarmup();
    }

    /// <summary>
    /// Reset session state when the overlay closes so the next open starts fresh
    /// (disclaimer reappears, bubbles cleared, new _sessionStart on first Start).
    /// </summary>
    public void ResetForNewSession()
    {
        _sessionStarted = false;
        DisclaimerAccepted = false;
        IsDisclaimerVisible = true;
        DispatchToUi(() => Bubbles.Clear());
    }

    private async Task ConsumeUtterancesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Live transcription consumer started");
        try
        {
            await foreach (var utt in _service.Utterances.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug(
                    "Consumer received utterance from {Speaker} (len={Len})",
                    utt.Speaker, utt.Text?.Length ?? 0);
                AddUtterance(utt);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live transcription utterance consumer failed");
        }
        finally
        {
            _logger.LogInformation("Live transcription consumer stopped");
        }
    }

    internal void AddUtterance(TranscriptUtterance utterance)
    {
        DispatchToUi(() =>
        {
            try
            {
                var bubble = GetOrCreateBubble(utterance.Speaker, utterance.Timestamp, utterance.SpeakerLabel, createIfMissing: true);
                bubble!.Append(utterance.Text, utterance.Timestamp);
                TrimIfNeeded();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add utterance to UI collection");
            }
        });
    }

    /// <summary>
    /// Reuses the most recently appended bubble when it's the same speaker and within the
    /// rolling window. If the existing bubble has a different <paramref name="speakerLabel"/>
    /// but no text yet (it was a "listening dot" placeholder created on VAD open), the new
    /// label is adopted in place — this keeps the listening dot from leaving an empty
    /// "Speaker N" stub before the diarized utterance arrives. Otherwise creates a fresh
    /// bubble (when <paramref name="createIfMissing"/> is true) and appends it to
    /// <see cref="Bubbles"/>. Using the *last* bubble — instead of per-speaker tracking —
    /// keeps the conversation in chronological order.
    /// </summary>
    internal TranscriptBubble? GetOrCreateBubble(TranscriptSpeaker speaker, DateTimeOffset timestamp, string? speakerLabel, bool createIfMissing)
    {
        var last = Bubbles.Count > 0 ? Bubbles[^1] : null;
        if (last is not null
            && last.Speaker == speaker
            && (timestamp - last.StartTimestamp).TotalSeconds < BubbleWindowSeconds)
        {
            if (string.Equals(last.SpeakerLabel, speakerLabel, StringComparison.Ordinal))
                return last;
            // Same speaker, within window, but label differs. If the existing bubble has no
            // text yet (it was a listening-dot placeholder), adopt the new label rather than
            // creating a second bubble — the user gets one continuous bubble per turn.
            if (string.IsNullOrWhiteSpace(last.Text))
            {
                last.SpeakerLabel = speakerLabel;
                return last;
            }
        }

        if (!createIfMissing) return null;

        var bubble = new TranscriptBubble(speaker, timestamp, speakerLabel: speakerLabel);
        Bubbles.Add(bubble);
        return bubble;
    }

    private void TrimIfNeeded()
    {
        if (Bubbles.Count <= MaxBubbles) return;
        for (int i = 0; i < TrimBatch && Bubbles.Count > MaxBubbles - TrimBatch; i++)
            Bubbles.RemoveAt(0);
    }

    private void OnServiceStateChanged(object? sender, LiveMeetingState newState)
    {
        DispatchToUi(() =>
        {
            IsRunning = newState == LiveMeetingState.Running;
            StatusText = newState switch
            {
                LiveMeetingState.Idle => _localizationService["LiveTrans_Status_Idle"],
                LiveMeetingState.Preparing => _localizationService["LiveTrans_Status_Preparing"],
                LiveMeetingState.Prepared => _localizationService["LiveTrans_Status_Idle"],
                LiveMeetingState.Starting => _localizationService["LiveTrans_Status_Starting"],
                LiveMeetingState.Running => _localizationService["LiveTrans_Status_Listening"],
                LiveMeetingState.Stopping => _localizationService["LiveTrans_Status_Stopping"],
                LiveMeetingState.Error => _localizationService["LiveTrans_Status_Error"],
                _ => string.Empty,
            };
            StartCommand.NotifyCanExecuteChanged();
        });
    }

    internal void OnServiceSpeakingChanged(object? sender, SpeakingChangedEventArgs e)
    {
        DispatchToUi(() =>
        {
            if (e.IsSpeaking)
            {
                var bubble = GetOrCreateBubble(e.Speaker, DateTimeOffset.Now, speakerLabel: null, createIfMissing: true);
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

    partial void OnIsRunningChanged(bool value)
    {
        SaveTranscriptCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnDisclaimerAcceptedChanged(bool value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnIsPreparingChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    private void OnBubblesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => SaveTranscriptCommand.NotifyCanExecuteChanged();

    private bool CanSaveTranscript() => !IsRunning && Bubbles.Count > 0;

    private async Task SaveTranscriptAsync()
    {
        if (!CanSaveTranscript()) return;

        string folder;
        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            folder = MeetingTranscriptPaths.ResolveFolder(settings);
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to ensure transcript folder {Folder}", folder); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve meeting transcript folder");
            folder = MeetingTranscriptPaths.DefaultMeetingFolder;
        }

        var defaultName = $"transcript-{_sessionStart.LocalDateTime:yyyyMMdd-HHmmss}.md";
        var path = _fileDialogService.PromptSaveFile(
            title: _localizationService["LiveTrans_SaveDialog_Title"],
            filter: _localizationService["LiveTrans_SaveDialog_Filter"],
            defaultFileName: defaultName,
            initialDirectory: folder);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var markdown = BuildMarkdown();
            await File.WriteAllTextAsync(path, markdown, Encoding.UTF8).ConfigureAwait(false);
            _logger.LogInformation("Saved live transcript to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save transcript to {Path}", path);
        }
    }

    internal string BuildMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(_localizationService["LiveTrans_Title"])
          .Append(" — ").Append(_sessionStart.LocalDateTime.ToString("yyyy-MM-dd HH:mm")).AppendLine();
        sb.AppendLine();
        foreach (var bubble in Bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, bubble.SpeakerLabel);
            sb.Append("**").Append(label).Append("** _")
              .Append(bubble.StartTimestamp.LocalDateTime.ToString("HH:mm:ss"));
            if (bubble.EndTimestamp != bubble.StartTimestamp)
                sb.Append('–').Append(bubble.EndTimestamp.LocalDateTime.ToString("HH:mm:ss"));
            sb.Append('_').AppendLine().AppendLine();
            sb.AppendLine(bubble.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void DispatchToUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        try
        {
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatcher invoke failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRenameSpeakerLabel))]
    private async Task RenameSpeakerLabelAsync(string? oldLabel)
    {
        if (string.IsNullOrWhiteSpace(oldLabel)) return;

        var title = _localizationService["LiveTrans_RenameSpeaker_Title"];
        var prompt = string.Format(_localizationService["LiveTrans_RenameSpeaker_Prompt"], oldLabel);
        var newLabel = await _dialogService.ShowInputDialogAsync(title, prompt).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newLabel) || newLabel == oldLabel) return;

        _service.RenameSpeaker(oldLabel, newLabel);

        DispatchToUi(() =>
        {
            foreach (var bubble in Bubbles)
            {
                if (bubble.SpeakerLabel == oldLabel)
                    bubble.SpeakerLabel = newLabel;
            }
        });
    }

    private static bool CanRenameSpeakerLabel(string? oldLabel)
        => !string.IsNullOrWhiteSpace(oldLabel);

    public void Dispose()
    {
        _service.StateChanged -= OnServiceStateChanged;
        _service.SpeakingChanged -= OnServiceSpeakingChanged;
        Bubbles.CollectionChanged -= OnBubblesCollectionChanged;
        try { _readerCts?.Cancel(); }
        catch { /* ignore */ }
        _readerCts?.Dispose();
    }
}
