using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Converters;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// Shared base for the transcript overlays (<see cref="DirectTranscriptionViewModel"/> and
/// <see cref="MeetingAttendeeViewModel"/>). Hoists the behaviour the two overlays share verbatim:
/// the rolling <see cref="TranscriptBubble"/> collection and its merge/trim rules, the background
/// utterance consumer over <see cref="UtteranceReader"/>, the Markdown export + save flows, and the
/// dispatcher marshalling. The divergent pieces — the backing service, its state→status mapping, and
/// the start/stop command wiring (different state enums and gating) — stay in the derived classes.
///
/// <para>The few save/export strings that differ between overlays are exposed as abstract hooks
/// (<see cref="TitleKey"/>, <see cref="SaveDialogTitleKey"/>, <see cref="SaveDialogFilterKey"/>,
/// <see cref="SaveFileNamePrefix"/>) so <see cref="BuildMarkdown"/> and <see cref="SaveTranscriptAsync"/>
/// can live here unchanged.</para>
/// </summary>
public abstract partial class TranscriptOverlayViewModel : ObservableObject, IDisposable
{
    private const int MaxBubbles = 200;
    private const int TrimBatch = 20;
    private const int BubbleWindowSeconds = 25;
    private const int SpeakerColorPaletteSize = 5;
    private const int MaxVaultReferenceAttempts = 50;

    private readonly Dictionary<string, int> _speakerColorIndex = new(StringComparer.Ordinal);
    private int _nextSpeakerColorIndex;

    // Per-utterance retention so adaptive reassignments can rebuild bubbles retroactively.
    // Comfortably above MaxBubbles; the rebuild trims to MaxBubbles at the end.
    private const int JournalCap = 1000;
    private readonly List<UtteranceEntry> _journal = [];

    protected readonly ISettingsService _settingsService;
    protected readonly ILocalizationService _localizationService;
    protected readonly IFileDialogService _fileDialogService;
    protected readonly IDialogService _dialogService;
    protected readonly IMemoryService _memoryService;
    protected readonly IIngestScheduler _ingestScheduler;
    protected readonly Wpf.Ui.ISnackbarService _snackbarService;
    protected readonly ILogger _logger;
    protected readonly IUiDispatcher _uiDispatcher;

    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    protected DateTimeOffset _sessionStart;

    [ObservableProperty]
    private string _counterpartName = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ObservableCollection<TranscriptBubble> Bubbles { get; } = [];

    public IRelayCommand CloseCommand { get; }
    public IRelayCommand SaveTranscriptCommand { get; }
    public IRelayCommand SaveToVaultCommand { get; }

    public event EventHandler? CloseRequested;

    protected TranscriptOverlayViewModel(
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IFileDialogService fileDialogService,
        IDialogService dialogService,
        IMemoryService memoryService,
        IIngestScheduler ingestScheduler,
        Wpf.Ui.ISnackbarService snackbarService,
        ILogger logger,
        IUiDispatcher uiDispatcher)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _fileDialogService = fileDialogService;
        _dialogService = dialogService;
        _memoryService = memoryService;
        _ingestScheduler = ingestScheduler;
        _snackbarService = snackbarService;
        _logger = logger;
        _uiDispatcher = uiDispatcher;

        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        SaveTranscriptCommand = new AsyncRelayCommand(SaveTranscriptAsync, CanSaveTranscript);
        SaveToVaultCommand = new AsyncRelayCommand(SaveToVaultAsync, CanSaveTranscript);

        Bubbles.CollectionChanged += OnBubblesCollectionChanged;
    }

    /// <summary>The backing service's merged utterance stream that the consumer loop reads.</summary>
    protected abstract ChannelReader<TranscriptUtterance> UtteranceReader { get; }

    /// <summary>Localization key for the transcript Markdown title.</summary>
    protected abstract string TitleKey { get; }

    /// <summary>Localization key for the save dialog title.</summary>
    protected abstract string SaveDialogTitleKey { get; }

    /// <summary>Localization key for the save dialog filter.</summary>
    protected abstract string SaveDialogFilterKey { get; }

    /// <summary>Default export file name prefix (e.g. <c>transcript</c> or <c>meeting</c>).</summary>
    protected abstract string SaveFileNamePrefix { get; }

    /// <summary>The <c>source:</c> value written into a vault-saved meeting's metadata.</summary>
    protected abstract string MeetingSourceKind { get; }

    /// <summary>
    /// Attendee names to pre-fill the vault-save form with. The transcript's own speakers are the
    /// fallback; an overlay with a real roster overrides this.
    /// </summary>
    protected virtual IReadOnlyCollection<string> DefaultAttendees
        => Bubbles
            .Select(b => SpeakerToDisplayNameConverter.Resolve(b.Speaker, b.SpeakerLabel, CounterpartName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // ---- Reader plumbing -------------------------------------------------------------------------

    /// <summary>
    /// Launches the background utterance consumer. Callers start this once the backing service is
    /// (about to be) producing so no utterance is missed.
    ///
    /// <para>Idempotent by design: it first tears down any existing reader (cancelling its CTS and
    /// awaiting the loop) before starting a new one. The backing channel is created with
    /// <c>SingleReader=true</c>, so two concurrent <c>ConsumeUtterancesAsync</c> loops would be a
    /// contract violation. A reader can be left parked if a session ends without the ViewModel's
    /// stop path running (e.g. the meeting attendee's auto-stop on natural meeting end completes the
    /// service but not the channel); restarting must not stack a second reader on top of it.</para>
    /// </summary>
    protected async Task StartReaderAsync()
    {
        await StopReaderAsync().ConfigureAwait(false);
        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ConsumeUtterancesAsync(_readerCts.Token), CancellationToken.None);
    }

    /// <summary>Cancels and awaits the background consumer, then disposes its CTS.</summary>
    protected async Task StopReaderAsync()
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
        _logger.LogInformation("Transcript consumer started");
        try
        {
            await foreach (var utt in UtteranceReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
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
            _logger.LogError(ex, "Transcript utterance consumer failed");
        }
        finally
        {
            _logger.LogInformation("Transcript consumer stopped");
        }
    }

    // ---- Bubble mapping --------------------------------------------------------------------------

    internal void AddUtterance(TranscriptUtterance utterance)
    {
        DispatchToUi(() =>
        {
            try
            {
                _journal.Add(new UtteranceEntry
                {
                    Speaker = utterance.Speaker,
                    Text = utterance.Text,
                    Timestamp = utterance.Timestamp,
                    Label = utterance.SpeakerLabel,
                    SegmentId = utterance.SegmentId,
                });
                if (_journal.Count > JournalCap) _journal.RemoveAt(0);

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
    /// Reuses the most recently appended bubble when it's the same speaker, the same per-speaker
    /// label, and still inside the rolling window. Otherwise creates a fresh bubble (when
    /// <paramref name="createIfMissing"/> is true) and appends it to <see cref="Bubbles"/>. Using the
    /// *last* bubble — instead of per-speaker tracking — keeps the conversation in chronological
    /// order: an interleaved "Them" turn always splits the prior "You" stream into two visual bubbles.
    ///
    /// <para>The label is part of the merge key (ordinal equality): two distinct
    /// <paramref name="speakerLabel"/>s in the same window produce two separate, separately-colored
    /// bubbles. An unlabeled segment (too short to diarize — "ja", "genau", laughter) inherits the
    /// in-window run's label instead of splitting it and rendering as the generic placeholder; with
    /// no labeled predecessor in-window it keeps the placeholder. The journal keeps the truthful
    /// null, so a rebuild re-derives the same inheritance.</para>
    /// </summary>
    internal TranscriptBubble? GetOrCreateBubble(
        TranscriptSpeaker speaker, DateTimeOffset timestamp, string? speakerLabel, bool createIfMissing)
    {
        var last = Bubbles.Count > 0 ? Bubbles[^1] : null;
        bool sameWindow = last is not null
            && last.Speaker == speaker
            && (timestamp - last.StartTimestamp).TotalSeconds < BubbleWindowSeconds;

        if (sameWindow && string.Equals(last!.SpeakerLabel, speakerLabel, StringComparison.Ordinal))
            return last;

        if (sameWindow
            && string.IsNullOrWhiteSpace(speakerLabel)
            && !string.IsNullOrWhiteSpace(last!.SpeakerLabel))
            return last;

        if (!createIfMissing) return null;

        var bubble = new TranscriptBubble(speaker, timestamp, speakerLabel: speakerLabel)
        {
            ColorIndex = GetOrAssignSpeakerColorIndex(speakerLabel),
        };
        Bubbles.Add(bubble);
        return bubble;
    }

    /// <summary>
    /// Returns a stable palette slot (0..<see cref="SpeakerColorPaletteSize"/>-1) for a speaker label.
    /// Undiarized (null/blank) labels map to slot 0. Distinct labels get successive slots, wrapping
    /// mod the palette size — with 6+ stable speakers the 6th reuses slot 0's hue (cosmetic only;
    /// identity is carried by the label, so bubbles still split correctly).
    /// </summary>
    private int GetOrAssignSpeakerColorIndex(string? speakerLabel)
    {
        if (string.IsNullOrWhiteSpace(speakerLabel)) return 0;          // undiarized → slot 0
        if (_speakerColorIndex.TryGetValue(speakerLabel, out var idx)) return idx;
        idx = _nextSpeakerColorIndex % SpeakerColorPaletteSize;
        _speakerColorIndex[speakerLabel] = idx;
        _nextSpeakerColorIndex++;
        return idx;
    }

    private void TrimIfNeeded()
    {
        if (Bubbles.Count <= MaxBubbles) return;
        for (int i = 0; i < TrimBatch && Bubbles.Count > MaxBubbles - TrimBatch; i++)
            Bubbles.RemoveAt(0);
    }

    /// <summary>
    /// Applies a batch of adaptive-diarization label corrections: updates the utterance journal
    /// (keyed by segment id) and, if anything actually changed, rebuilds the bubble collection
    /// from the journal so merges/splits/relabels all render correctly. Journal and bubbles are
    /// UI-thread state; the whole batch runs as one dispatcher action.
    /// </summary>
    internal void ApplyReassignments(IReadOnlyList<SpeakerReassignment> changes)
    {
        if (changes.Count == 0) return;
        DispatchToUi(() =>
        {
            try
            {
                var labelBySegment = new Dictionary<long, string>(changes.Count);
                foreach (var c in changes) labelBySegment[c.SegmentId] = c.NewLabel;

                var any = false;
                foreach (var entry in _journal)
                {
                    if (entry.SegmentId is not long id) continue;
                    if (!labelBySegment.TryGetValue(id, out var newLabel)) continue;
                    if (string.Equals(entry.Label, newLabel, StringComparison.Ordinal)) continue;
                    entry.Label = newLabel;
                    any = true;
                }
                if (any) RebuildBubblesFromJournal();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply speaker reassignments");
            }
        });
    }

    /// <summary>
    /// Replays the journal through the SAME incremental path (<see cref="GetOrCreateBubble"/> +
    /// Append), so rebuild-vs-incremental equivalence holds by construction. The palette map is
    /// deliberately NOT reset — speakers keep their colors across rebuilds. Trims in a loop
    /// (TrimIfNeeded removes at most one batch per call).
    /// </summary>
    private void RebuildBubblesFromJournal()
    {
        Bubbles.Clear();
        foreach (var entry in _journal)
        {
            var bubble = GetOrCreateBubble(entry.Speaker, entry.Timestamp, entry.Label, createIfMissing: true);
            bubble!.Append(entry.Text, entry.Timestamp);
        }
        while (Bubbles.Count > MaxBubbles) Bubbles.RemoveAt(0);
    }

    /// <summary>Clears the visible transcript AND its journal — they must never diverge.</summary>
    protected void ClearTranscript()
    {
        Bubbles.Clear();
        _journal.Clear();
    }

    /// <summary>
    /// Applies an in-session speaker-label rename to the base-VM state: carries the renamed label's
    /// palette slot over (so the speaker keeps its bubble color and future utterances under the new
    /// label hit the same entry) and retroactively relabels every existing bubble that carried the old
    /// label. The diarizer-side rename (so future segments arrive already labelled) is the subclass's
    /// responsibility — it calls its service before this helper. Eventually-consistent: a segment in
    /// flight when the rename runs can land a stray old-label bubble; it self-corrects on the next
    /// utterance.
    /// </summary>
    protected void RelabelSpeaker(string oldLabel, string newLabel)
    {
        // Carry the color slot over to the new label so the speaker keeps the same bubble color and any
        // future utterances under the renamed label still hit the same palette entry.
        if (_speakerColorIndex.Remove(oldLabel, out var carried))
            _speakerColorIndex[newLabel] = carried;

        DispatchToUi(() =>
        {
            foreach (var bubble in Bubbles)
            {
                if (bubble.SpeakerLabel == oldLabel)
                    bubble.SpeakerLabel = newLabel;
            }

            foreach (var entry in _journal)
            {
                if (entry.Label == oldLabel)
                    entry.Label = newLabel;
            }
        });
    }

    /// <summary>
    /// Withdraws a speaker from the in-memory transcript entirely (direct-transcription §3.3
    /// revocation): removes every journal entry and bubble carrying <paramref name="speakerLabel"/>
    /// (ordinal match), releases its palette slot so a later re-consent starts from a fresh color, and
    /// rebuilds the bubble collection from what remains. A blank label is a no-op. Unlike
    /// <see cref="RelabelSpeaker"/> (which rewrites a label in place), this removes the speaker's
    /// contribution entirely — the shape revocation needs, that renaming does not provide.
    /// </summary>
    protected void RemoveSpeaker(string speakerLabel)
    {
        if (string.IsNullOrWhiteSpace(speakerLabel)) return;

        DispatchToUi(() =>
        {
            try
            {
                _journal.RemoveAll(entry => string.Equals(entry.Label, speakerLabel, StringComparison.Ordinal));
                _speakerColorIndex.Remove(speakerLabel);
                RebuildBubblesFromJournal();
            }
            catch (Exception ex)
            {
                // Never log the label: post-grant it can be the extracted personal name.
                _logger.LogError(ex, "Failed to remove speaker from transcript");
            }
        });
    }

    // ---- IsRunning → command refresh -------------------------------------------------------------

    partial void OnIsRunningChanged(bool value)
    {
        SaveTranscriptCommand.NotifyCanExecuteChanged();
        SaveToVaultCommand.NotifyCanExecuteChanged();
        OnRunningChanged();
    }

    /// <summary>
    /// Hook for derived classes to refresh their own start/stop commands when <see cref="IsRunning"/>
    /// flips. The source-generated start/stop commands live in the derived class, so the base cannot
    /// name them. The base already refreshes <see cref="SaveTranscriptCommand"/>.
    /// </summary>
    protected virtual void OnRunningChanged() { }

    private void OnBubblesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SaveTranscriptCommand.NotifyCanExecuteChanged();
        SaveToVaultCommand.NotifyCanExecuteChanged();
    }

    // ---- Save (shared transcript export flow) ----------------------------------------------------

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

        var defaultName = $"{SaveFileNamePrefix}-{_sessionStart.LocalDateTime:yyyyMMdd-HHmmss}.md";
        var path = _fileDialogService.PromptSaveFile(
            title: _localizationService[SaveDialogTitleKey],
            filter: _localizationService[SaveDialogFilterKey],
            defaultFileName: defaultName,
            initialDirectory: folder);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var markdown = BuildMarkdown();
            await File.WriteAllTextAsync(path, markdown, Encoding.UTF8).ConfigureAwait(false);
            _logger.LogInformation("Saved transcript to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save transcript to {Path}", path);
        }
    }

    // ---- Save into the memory vault --------------------------------------------------------------

    private async Task SaveToVaultAsync()
    {
        if (!CanSaveTranscript()) return;

        var model = new MeetingSaveEditModel(
            _sessionStart,
            _localizationService.Format(
                "MeetingSave_DefaultTitle", _sessionStart.LocalDateTime.ToString("yyyy-MM-dd HH:mm")),
            string.Join(", ", DefaultAttendees));

        // CanSave is re-checked here, not just bound to the dialog's primary button: a broken binding
        // leaves that button enabled, and a blank title would slug to the meaningless "section".
        if (!await _dialogService.ShowMeetingSaveDialogAsync(model) || !model.CanSave)
            return;

        var (reference, refusal) = await ResolveFreeVaultReferenceAsync(model.Title);
        if (reference is null)
        {
            await ShowVaultSaveFailedAsync(refusal ?? _localizationService["Msg_MeetingSave_NoFreeName"]);
            return;
        }

        // The end timestamp is not tracked by either service; the last bubble is the practical session end.
        var sessionEnd = Bubbles.Count > 0 ? Bubbles[^1].EndTimestamp : _sessionStart;
        var markdown = MeetingVaultMarkdown.Render(
            model.ToMetadata(sessionEnd, MeetingSourceKind),
            DirectTranscriptMarkdown.RenderBody(_localizationService[TitleKey], [.. Bubbles], CounterpartName));

        var write = await _memoryService.CreateSourceAsync(reference, markdown);
        if (!write.Success)
        {
            _logger.LogWarning("Saving a meeting into the vault failed");
            await ShowVaultSaveFailedAsync(write.Error ?? string.Empty);
            return;
        }

        _logger.LogInformation("Saved a meeting into the vault ({Chars} chars)", markdown.Length);
        _logger.SensitiveDebug("Meeting saved as {Ref}", write.Ref);

        // Manual RunAsync always executes and shares the scheduler's serial queue, so it is deterministic
        // whether or not the sources watcher is enabled, and cannot race the watcher's own pickup.
        _ingestScheduler.RunAsync(write.Ref).SafeFireAndForget(_logger);

        _snackbarService.Show(
            _localizationService["Msg_MeetingSave_Saved"],
            _localizationService.Format("Msg_MeetingSave_SavedDetail", write.Ref),
            Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The first <c>sources/</c> ref the title yields that does not already exist, suffixed <c>-2</c>,
    /// <c>-3</c>, … so a second save of the same meeting can never clobber the first. The error is
    /// carried back so a refusal the suffix cannot fix (a rejected path) is not reported as a collision.
    /// </summary>
    private async Task<(string? Reference, string? Error)> ResolveFreeVaultReferenceAsync(string title)
    {
        var baseTitle = title.Trim();
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxVaultReferenceAttempts; attempt++)
        {
            var candidate = MeetingVaultMarkdown.BuildReference(
                _sessionStart, attempt == 1 ? baseTitle : $"{baseTitle} {attempt}");

            var preview = await _memoryService.ResolveCreateSourceAsync(candidate);
            if (preview.CanWrite)
                return (candidate, null);

            lastError = preview.Error;
        }

        return (null, lastError);
    }

    private Task ShowVaultSaveFailedAsync(string detail)
        => _dialogService.ShowMessageDialogAsync(
            _localizationService["Msg_Error"],
            _localizationService.Format("Msg_MeetingSave_Failed", detail));

    /// <summary>
    /// Virtual so <c>DirectTranscriptionViewModel</c> can prepend YAML front matter and a voice-stats
    /// block ahead of the same bubble rendering the Teams attendee uses unchanged.
    /// </summary>
    internal virtual string BuildMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(_localizationService[TitleKey])
          .Append(" — ").Append(_sessionStart.LocalDateTime.ToString("yyyy-MM-dd HH:mm")).AppendLine();
        sb.AppendLine();
        foreach (var bubble in Bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, bubble.SpeakerLabel, CounterpartName);
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

    protected void DispatchToUi(Action action)
    {
        // The try/catch stays HERE, wrapped around the call, so nothing thrown by `action` on the inline
        // path can escape into a caller that has no net of its own: RelabelSpeaker (:321) and
        // MeetingAttendeeViewModel.OnServiceStateChanged (:362) have none. (AddUtterance :166 and
        // ApplyReassignments :256 do.)
        //
        // In PRODUCTION it is now the OUTER of two nets and will rarely fire: UiDispatcherService.PostOrRun
        // has its own try/catch around the inline call, so an action that throws on the UI thread is logged
        // there as "UI dispatch failed (PostOrRun)" under the Pia.Services.UiDispatcherService category,
        // not here — and a queued action's failure is logged there too. Keeping this catch is still right:
        // it is the only net under the InlineUiDispatcher test double, which catches nothing, and it is
        // what makes an exception from a *future* propagating IUiDispatcher land in the attendee's own
        // logger. Do not read "Dispatcher invoke failed" as the string a release support bundle will show.
        try
        {
            _uiDispatcher.PostOrRun(action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatcher invoke failed");
        }
    }

    public virtual void Dispose()
    {
        Bubbles.CollectionChanged -= OnBubblesCollectionChanged;
        try { _readerCts?.Cancel(); }
        catch { /* ignore */ }
        _readerCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
