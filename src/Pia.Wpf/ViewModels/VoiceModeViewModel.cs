using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class VoiceModeViewModel : ObservableObject, IDisposable
{
    private readonly IAudioRecordingService _audioRecordingService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly ITtsService _ttsService;
    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<string>> _streamResponseFunc;
    private readonly Action<string, string> _addToConversationFunc;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _modeCts;
    private System.Timers.Timer? _silenceTimer;
    private bool _hasSpoken;
    private DateTime _lastSpeechTime;
    private bool _disposed;

    private const float SpeechThreshold = 0.03f;
    private const double SilenceTimeoutMs = 1500;
    private const double SilenceCheckIntervalMs = 100;

    [ObservableProperty]
    private VoiceModeState _state = VoiceModeState.Idle;

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _transcriptText = string.Empty;

    public IRelayCommand DoneListeningCommand { get; }
    public IRelayCommand StopSpeakingCommand { get; }
    public IRelayCommand ExitCommand { get; }

    public VoiceModeViewModel(
        IAudioRecordingService audioRecordingService,
        ITranscriptionService transcriptionService,
        ITtsService ttsService,
        ILogger logger,
        Func<string, CancellationToken, IAsyncEnumerable<string>> streamResponseFunc,
        Action<string, string> addToConversationFunc)
    {
        _audioRecordingService = audioRecordingService;
        _transcriptionService = transcriptionService;
        _ttsService = ttsService;
        _logger = logger;
        _streamResponseFunc = streamResponseFunc;
        _addToConversationFunc = addToConversationFunc;
        _dispatcher = Dispatcher.CurrentDispatcher;

        DoneListeningCommand = new RelayCommand(OnDoneListening, () => State == VoiceModeState.Listening);
        StopSpeakingCommand = new RelayCommand(OnStopSpeaking, () => State == VoiceModeState.Speaking);
        ExitCommand = new RelayCommand(ExitVoiceMode);
    }

    public async Task EnterAsync()
    {
        _modeCts = new CancellationTokenSource();
        await TransitionToListeningAsync();
    }

    private async Task TransitionToListeningAsync()
    {
        State = VoiceModeState.Listening;
        StatusText = "Listening...";
        NotifyCommandStates();

        _hasSpoken = false;
        _lastSpeechTime = DateTime.UtcNow;

        _audioRecordingService.AudioLevelChanged += OnAudioLevelChanged;

        try
        {
            await _audioRecordingService.StartRecordingAsync(_modeCts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording in voice mode");
            ExitVoiceMode();
            return;
        }

        StartSilenceMonitor();
    }

    private async Task TransitionToProcessingAsync()
    {
        StopSilenceMonitor();
        _audioRecordingService.AudioLevelChanged -= OnAudioLevelChanged;
        AudioLevel = 0;

        State = VoiceModeState.Processing;
        StatusText = "Thinking...";
        NotifyCommandStates();

        string? audioFilePath = null;

        try
        {
            audioFilePath = await _audioRecordingService.StopRecordingAsync();

            // Play filler while transcribing
            var fillerTask = Task.Run(() => _ttsService.PlayFillerAsync(_modeCts?.Token ?? CancellationToken.None));

            if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
            {
                _logger.LogWarning("No audio file produced, returning to listening");
                _ttsService.Stop();
                await TransitionToListeningAsync();
                return;
            }

            var userText = await _transcriptionService.TranscribeAsync(audioFilePath, _modeCts?.Token ?? CancellationToken.None);

            if (string.IsNullOrWhiteSpace(userText))
            {
                _logger.LogInformation("Empty transcription, returning to listening");
                _ttsService.Stop();
                await TransitionToListeningAsync();
                return;
            }

            AppendToTranscript("You", userText);
            await TransitionToSpeakingAsync(userText);
        }
        catch (OperationCanceledException)
        {
            // Voice mode was cancelled
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during voice mode processing");
            ExitVoiceMode();
        }
        finally
        {
            if (audioFilePath is not null && File.Exists(audioFilePath))
            {
                try { File.Delete(audioFilePath); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    private async Task TransitionToSpeakingAsync(string userText)
    {
        State = VoiceModeState.Speaking;
        StatusText = "Speaking...";
        NotifyCommandStates();

        var token = _modeCts?.Token ?? CancellationToken.None;
        var fullResponse = new StringBuilder();

        try
        {
            var chunker = new SentenceChunker();

            // Create an async enumerable of sentences from the streamed response
            async IAsyncEnumerable<string> SentenceStream([EnumeratorCancellation] CancellationToken ct)
            {
                await foreach (var streamToken in _streamResponseFunc(userText, ct))
                {
                    fullResponse.Append(streamToken);

                    foreach (var sentence in chunker.AddToken(streamToken))
                    {
                        yield return sentence;
                    }
                }

                var remaining = chunker.Flush();
                if (remaining is not null)
                    yield return remaining;
            }

            await _ttsService.SpeakChunkedAsync(SentenceStream(token), token);

            var assistantText = fullResponse.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                AppendToTranscript("Pia", assistantText);
                _addToConversationFunc(userText, assistantText);
            }

            // Auto-continue: loop back to listening if not cancelled
            if (_modeCts is not null && !_modeCts.IsCancellationRequested)
            {
                await TransitionToListeningAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Voice mode was cancelled or speech was stopped
            if (_modeCts is not null && !_modeCts.IsCancellationRequested)
            {
                // Speech was stopped but voice mode continues
                var assistantText = fullResponse.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(assistantText))
                {
                    AppendToTranscript("Pia", assistantText);
                    _addToConversationFunc(userText, assistantText);
                }
                await TransitionToListeningAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during voice mode speaking");
            ExitVoiceMode();
        }
    }

    private void OnDoneListening()
    {
        if (State != VoiceModeState.Listening)
            return;

        _ = TransitionToProcessingAsync();
    }

    private void OnStopSpeaking()
    {
        if (State != VoiceModeState.Speaking)
            return;

        _ttsService.Stop();
    }

    public void ExitVoiceMode()
    {
        _modeCts?.Cancel();
        StopSilenceMonitor();
        _audioRecordingService.AudioLevelChanged -= OnAudioLevelChanged;

        _ttsService.Stop();

        if (_audioRecordingService.IsRecording)
        {
            _ = _audioRecordingService.StopRecordingAsync();
        }

        State = VoiceModeState.Idle;
        StatusText = string.Empty;
        AudioLevel = 0;
        NotifyCommandStates();
    }

    private void OnAudioLevelChanged(object? sender, float rmsLevel)
    {
        _dispatcher.BeginInvoke(() => AudioLevel = rmsLevel);

        if (rmsLevel > SpeechThreshold)
        {
            _hasSpoken = true;
            _lastSpeechTime = DateTime.UtcNow;
        }
    }

    private void StartSilenceMonitor()
    {
        StopSilenceMonitor();

        _silenceTimer = new System.Timers.Timer(SilenceCheckIntervalMs);
        _silenceTimer.Elapsed += (_, _) =>
        {
            if (_hasSpoken && (DateTime.UtcNow - _lastSpeechTime).TotalMilliseconds > SilenceTimeoutMs)
            {
                StopSilenceMonitor();
                _dispatcher.BeginInvoke(() =>
                {
                    if (State == VoiceModeState.Listening)
                    {
                        _ = TransitionToProcessingAsync();
                    }
                });
            }
        };
        _silenceTimer.AutoReset = true;
        _silenceTimer.Start();
    }

    private void StopSilenceMonitor()
    {
        _silenceTimer?.Stop();
        _silenceTimer?.Dispose();
        _silenceTimer = null;
    }

    private void AppendToTranscript(string speaker, string text)
    {
        var entry = $"{speaker}: {text}";
        TranscriptText = string.IsNullOrEmpty(TranscriptText)
            ? entry
            : $"{TranscriptText}\n\n{entry}";
    }

    private void NotifyCommandStates()
    {
        DoneListeningCommand.NotifyCanExecuteChanged();
        StopSpeakingCommand.NotifyCanExecuteChanged();
    }

    partial void OnStateChanged(VoiceModeState value)
    {
        _logger.LogDebug("Voice mode state: {State}", value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ExitVoiceMode();
        _modeCts?.Dispose();
        _modeCts = null;

        GC.SuppressFinalize(this);
    }
}
