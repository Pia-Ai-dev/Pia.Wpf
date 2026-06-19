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
using Pia.Services.MeetingAttendee;

namespace Pia.ViewModels;

/// <summary>
/// Drives the "meeting attendee" overlay. Modelled on <see cref="LiveTranscriptionViewModel"/>:
/// it subscribes to <see cref="IMeetingAttendeeService.Utterances"/>, maps them to
/// <see cref="TranscriptBubble"/>s, maps <see cref="IMeetingAttendeeService.StateChanged"/> to a
/// status string, and reuses the existing save flow (<see cref="MeetingTranscriptPaths"/> +
/// <see cref="IFileDialogService"/>).
///
/// <para>Differences from live transcription: there is no microphone pipeline, so there is no
/// <c>SpeakingChanged</c> event to honour (the attendee only ever produces <see cref="TranscriptSpeaker.Them"/>
/// utterances); and the session needs a meeting URL plus a one-time consent acknowledgement before it
/// can start, so <see cref="StartCommand"/> is gated by <see cref="MeetingUrl"/> validity and
/// <see cref="ConsentAcknowledged"/>.</para>
/// </summary>
public partial class MeetingAttendeeViewModel : ObservableObject, IDisposable
{
    private const int MaxBubbles = 200;
    private const int TrimBatch = 20;
    private const int BubbleWindowSeconds = 25;

    private readonly IMeetingAttendeeService _service;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IFileDialogService _fileDialogService;
    private readonly ILogger<MeetingAttendeeViewModel> _logger;

    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    private DateTimeOffset _sessionStart;

    // The label used for the meeting's speakers in the transcript / bubbles. The attendee never has a
    // "you" stream, so a single counterpart label covers the whole meeting.
    [ObservableProperty]
    private string _counterpartName = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

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

    public ObservableCollection<TranscriptBubble> Bubbles { get; } = [];

    public IRelayCommand SaveTranscriptCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event EventHandler? CloseRequested;

    public MeetingAttendeeViewModel(
        IMeetingAttendeeService service,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IFileDialogService fileDialogService,
        ILogger<MeetingAttendeeViewModel> logger)
    {
        _service = service;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _fileDialogService = fileDialogService;
        _logger = logger;
        _counterpartName = _localizationService["MeetingAttendee_Speaker_Placeholder"];

        SaveTranscriptCommand = new AsyncRelayCommand(SaveTranscriptAsync, CanSaveTranscript);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        _service.StateChanged += OnServiceStateChanged;
        Bubbles.CollectionChanged += OnBubblesCollectionChanged;

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
        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ConsumeUtterancesAsync(_readerCts.Token), CancellationToken.None);

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

    [RelayCommand]
    private async Task StopAsync()
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

    private async Task StopReaderAsync()
    {
        try { _readerCts?.Cancel(); }
        catch { /* ignore */ }
        try { if (_readerTask is not null) await _readerTask.ConfigureAwait(false); }
        catch { /* ignore */ }
        _readerCts?.Dispose();
        _readerCts = null;
        _readerTask = null;
    }

    private async Task ConsumeUtterancesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Meeting attendee consumer started");
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
            _logger.LogError(ex, "Meeting attendee utterance consumer failed");
        }
        finally
        {
            _logger.LogInformation("Meeting attendee consumer stopped");
        }
    }

    // ---- Bubble mapping (ported verbatim from LiveTranscriptionViewModel) --------------------------

    internal void AddUtterance(TranscriptUtterance utterance)
    {
        DispatchToUi(() =>
        {
            try
            {
                var bubble = GetOrCreateBubble(utterance.Speaker, utterance.Timestamp, createIfMissing: true);
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
    /// Reuses the most recently appended bubble when it's the same speaker and still inside the rolling
    /// window; otherwise creates a fresh bubble. Mirrors <see cref="LiveTranscriptionViewModel"/>.
    /// </summary>
    internal TranscriptBubble? GetOrCreateBubble(TranscriptSpeaker speaker, DateTimeOffset timestamp, bool createIfMissing)
    {
        var last = Bubbles.Count > 0 ? Bubbles[^1] : null;
        if (last is not null
            && last.Speaker == speaker
            && (timestamp - last.StartTimestamp).TotalSeconds < BubbleWindowSeconds)
        {
            return last;
        }

        if (!createIfMissing) return null;

        var bubble = new TranscriptBubble(speaker, timestamp);
        Bubbles.Add(bubble);
        return bubble;
    }

    private void TrimIfNeeded()
    {
        if (Bubbles.Count <= MaxBubbles) return;
        for (int i = 0; i < TrimBatch && Bubbles.Count > MaxBubbles - TrimBatch; i++)
            Bubbles.RemoveAt(0);
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

    partial void OnIsRunningChanged(bool value)
    {
        SaveTranscriptCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnBubblesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => SaveTranscriptCommand.NotifyCanExecuteChanged();

    // ---- Save (reuses the existing transcript flow) -----------------------------------------------

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

        var defaultName = $"meeting-{_sessionStart.LocalDateTime:yyyyMMdd-HHmmss}.md";
        var path = _fileDialogService.PromptSaveFile(
            title: _localizationService["MeetingAttendee_SaveDialog_Title"],
            filter: _localizationService["MeetingAttendee_SaveDialog_Filter"],
            defaultFileName: defaultName,
            initialDirectory: folder);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var markdown = BuildMarkdown();
            await File.WriteAllTextAsync(path, markdown, Encoding.UTF8).ConfigureAwait(false);
            _logger.LogInformation("Saved meeting transcript to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save meeting transcript to {Path}", path);
        }
    }

    internal string BuildMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(_localizationService["MeetingAttendee_Title"])
          .Append(" — ").Append(_sessionStart.LocalDateTime.ToString("yyyy-MM-dd HH:mm")).AppendLine();
        sb.AppendLine();
        foreach (var bubble in Bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, CounterpartName);
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

    public void Dispose()
    {
        _service.StateChanged -= OnServiceStateChanged;
        Bubbles.CollectionChanged -= OnBubblesCollectionChanged;
        try { _readerCts?.Cancel(); }
        catch { /* ignore */ }
        _readerCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
