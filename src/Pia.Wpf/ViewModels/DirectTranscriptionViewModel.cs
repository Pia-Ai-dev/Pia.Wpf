using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// Drives the direct-transcription overlay (local microphone + system-audio, consent-gated). Builds on
/// <see cref="TranscriptOverlayViewModel"/> (bubbles/journal/rebuild/relabel/save inherited); adds the
/// disclaimer + warmup + start/stop/resume wiring, per-speaker consent chips, a voice-stats flyout, and
/// the front-matter/stats-augmented Markdown export (design §3.7–§3.9).
///
/// <para>Differences from <see cref="MeetingAttendeeViewModel"/>: there is a real microphone pipeline
/// (mic utterances are always <see cref="TranscriptSpeaker.You"/>, never gated), the loopback side is
/// consent-gated per diarized speaker rather than joined-once, and a paused session (<see cref="StopAsync"/>)
/// is resumable without re-consenting — only <see cref="IDirectTranscriptionService.EndSessionAsync"/>
/// (driven from <see cref="PrepareForDisplayAsync"/> and <see cref="Dispose"/>) clears consent.</para>
/// </summary>
public sealed partial class DirectTranscriptionViewModel : TranscriptOverlayViewModel
{
    private const int ChipColorPaletteSize = 5;

    private readonly IDirectTranscriptionService _service;
    private readonly IClipboardService? _clipboardService;
    private readonly IConsentSoundPlayer? _consentSoundPlayer;

    private readonly Dictionary<string, int> _chipColorIndex = new(StringComparer.Ordinal);
    private int _nextChipColorIndex;

    private Task? _prepareTask;
    private bool _sessionStarted;

    /// <summary>Disclaimer panel visibility. True until the first <see cref="StartAsync"/> of a session.</summary>
    [ObservableProperty]
    private bool _isDisclaimerVisible = true;

    /// <summary>Bound to the disclaimer's ToggleSwitch. Gates <see cref="StartCommand"/>.</summary>
    [ObservableProperty]
    private bool _disclaimerAccepted;

    /// <summary>True while <see cref="IDirectTranscriptionService.PrepareAsync"/> runs in the background.</summary>
    [ObservableProperty]
    private bool _isPreparing;

    /// <summary>Whether the voice-stats flyout is shown.</summary>
    [ObservableProperty]
    private bool _areStatsVisible;

    /// <summary>
    /// True while the local microphone's VAD reports speech. Drives a header activity indicator, so mic
    /// activity is visible without materializing a bubble for speech that may never produce any text.
    /// </summary>
    [ObservableProperty]
    private bool _isMicListening;

    /// <summary>One chip per diarized loopback speaker seen this session, muted or consented.</summary>
    public ObservableCollection<SpeakerConsentChip> ConsentChips { get; } = [];

    /// <summary>Per-consented-speaker speaking statistics, refreshed by <see cref="ToggleStatsCommand"/> and on stop.</summary>
    public ObservableCollection<SpeakerVoiceStats> VoiceStats { get; } = [];

    /// <summary>
    /// Stop command. Constructed manually (not via <c>[RelayCommand]</c>), mirroring
    /// <see cref="MeetingAttendeeViewModel.StopCommand"/>, so <see cref="StopAsync"/> stays public for a
    /// host to invoke directly (e.g. hiding the overlay).
    /// </summary>
    public IRelayCommand StopCommand { get; }

    protected override ChannelReader<TranscriptUtterance> UtteranceReader => _service.Utterances;

    protected override string TitleKey => "DirectTrans_Title";
    protected override string SaveDialogTitleKey => "DirectTrans_SaveDialog_Title";
    protected override string SaveDialogFilterKey => "DirectTrans_SaveDialog_Filter";
    protected override string SaveFileNamePrefix => "direct-transcript";
    protected override string MeetingSourceKind => "direct";

    /// <summary>
    /// Raised when the user clicks "Summarize with assistant". Carries a ready-to-send prompt (a
    /// localized instruction followed by the front-matter-free transcript body) — mirrors
    /// <see cref="MeetingAttendeeViewModel.SummarizeRequested"/>. The old silent-save-then-hand-over-a-
    /// path flow cannot be ported: both types it needed were deleted from the current branch.
    /// </summary>
    public event EventHandler<string>? SummarizeRequested;

    public DirectTranscriptionViewModel(
        IDirectTranscriptionService service,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IFileDialogService fileDialogService,
        IDialogService dialogService,
        IMemoryService memoryService,
        IIngestScheduler ingestScheduler,
        Wpf.Ui.ISnackbarService snackbarService,
        ILogger<DirectTranscriptionViewModel> logger,
        IUiDispatcher uiDispatcher,
        IClipboardService? clipboardService = null,
        IConsentSoundPlayer? consentSoundPlayer = null)
        : base(settingsService, localizationService, fileDialogService, dialogService, memoryService,
            ingestScheduler, snackbarService, logger, uiDispatcher)
    {
        _service = service;
        _clipboardService = clipboardService;
        _consentSoundPlayer = consentSoundPlayer;

        // Construct StopCommand BEFORE subscribing to StateChanged: a state change raised during wiring
        // would NRE in OnRunningChanged (mirrors MeetingAttendeeViewModel's ctor ordering).
        StopCommand = new AsyncRelayCommand(StopAsync);

        // The Summarize command shares Save's gating (transcript present + not running). The base
        // refreshes Save on bubble-collection changes; mirror that here (the IsRunning side is handled
        // in OnRunningChanged below).
        Bubbles.CollectionChanged += (_, _) => SummarizeWithAssistantCommand.NotifyCanExecuteChanged();

        _service.StateChanged += OnServiceStateChanged;
        _service.SpeakerConsentChanged += OnSpeakerConsentChanged;
        _service.SpeakerRegistered += OnSpeakerRegistered;
        _service.SpeakingChanged += OnSpeakingChanged;
        _service.ConsentSessionReset += OnConsentSessionReset;
        _localizationService.LanguageChanged += OnUiLanguageChanged;

        StatusText = _localizationService["DirectTrans_Status_Idle"];
    }

    // ---- Consent sentence ----------------------------------------------------------------------------

    /// <summary>
    /// The consent sentence in the UI language, for the in-session reminder. The three sentences are
    /// keyed by the language they are SPOKEN in, not by the UI locale, so all three resx files carry the
    /// same values and the UI language has to pick among the keys here.
    /// </summary>
    public string ConsentSentenceForUiLanguage => _localizationService.CurrentLanguage switch
    {
        TargetLanguage.DE => _localizationService["DirectTrans_Disclaimer_ConsentSentence_De"],
        TargetLanguage.FR => _localizationService["DirectTrans_Disclaimer_ConsentSentence_Fr"],
        _ => _localizationService["DirectTrans_Disclaimer_ConsentSentence_En"],
    };

    private void OnUiLanguageChanged(object? sender, TargetLanguage e)
        => DispatchToUi(() => OnPropertyChanged(nameof(ConsentSentenceForUiLanguage)));

    /// <summary>
    /// Puts one consent sentence on the clipboard so the host can paste it into the meeting chat — the
    /// only channel that reaches a participant who never sees this window.
    /// </summary>
    [RelayCommand]
    private void CopyConsentSentence(string? sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence) || _clipboardService is null) return;

        try { _clipboardService.SetText(sentence); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to copy the consent sentence to the clipboard"); }
    }

    // ---- Open / warmup -----------------------------------------------------------------------------

    /// <summary>
    /// Called by the host just before revealing the overlay. Defensively ends any session left running
    /// from a previous open (so consent and the session id never leak into a fresh one), resets the
    /// local UI state to a clean slate, and kicks off <see cref="BeginWarmup"/> so the disclaimer's Start
    /// button is fast once the user accepts.
    /// </summary>
    public async Task PrepareForDisplayAsync()
    {
        if (_service.State is not DirectTranscriptionState.Idle)
        {
            try { await _service.EndSessionAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to end previous direct transcription session"); }
        }

        DispatchToUi(() =>
        {
            _sessionStarted = false;
            IsDisclaimerVisible = true;
            DisclaimerAccepted = false;
            AreStatsVisible = false;
            ClearTranscript();
            ConsentChips.Clear();
            VoiceStats.Clear();
            _chipColorIndex.Clear();
            _nextChipColorIndex = 0;
        });

        BeginWarmup();
    }

    /// <summary>
    /// Kicks off model/diarizer preparation in the background while the disclaimer is shown, so
    /// <see cref="StartCommand"/> only has to flip switches once the user accepts. Idempotent: a second
    /// call while a prior warmup is still running is a no-op.
    /// </summary>
    private void BeginWarmup()
    {
        if (_prepareTask is { IsCompleted: false }) return;
        DispatchToUi(() => IsPreparing = true);
        _prepareTask = Task.Run(async () =>
        {
            try
            {
                await _service.PrepareAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Direct transcription prepare failed");
                DispatchToUi(() => StatusText = _localizationService["DirectTrans_Status_Error"]);
            }
            finally
            {
                DispatchToUi(() => IsPreparing = false);
            }
        });
    }

    // ---- Start / Resume / Stop ---------------------------------------------------------------------

    private bool CanStart()
        => DisclaimerAccepted
           && !IsRunning
           && _service.State is not (DirectTranscriptionState.Starting or DirectTranscriptionState.Stopping);

    /// <summary>
    /// Starts a session from the disclaimer screen. Clears the transcript/chips/stats only when this is
    /// a genuinely new session (<c>_sessionStarted</c> false — i.e. not resuming after a mere
    /// <see cref="StopAsync"/> pause).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!CanStart()) return;

        _logger.LogInformation("DirectTranscription ViewModel: StartAsync invoked (resume={Resume})", _sessionStarted);

        if (_prepareTask is not null)
        {
            try { await _prepareTask.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Prepare task ended with an error; StartAsync will retry"); }
        }

        if (!_sessionStarted)
        {
            DispatchToUi(() =>
            {
                ClearTranscript();
                ConsentChips.Clear();
                VoiceStats.Clear();
                _chipColorIndex.Clear();
                _nextChipColorIndex = 0;
                _sessionStart = DateTimeOffset.Now;
            });
        }

        DispatchToUi(() => IsDisclaimerVisible = false);

        await StartCaptureAsync(cancellationToken, "Failed to start direct transcription").ConfigureAwait(false);
    }

    private bool CanResume()
        => !IsRunning
           && !IsDisclaimerVisible
           && _service.State is not (DirectTranscriptionState.Starting or DirectTranscriptionState.Stopping);

    /// <summary>Resumes a paused session (after <see cref="StopAsync"/>) without touching the transcript.</summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (!CanResume()) return;

        _logger.LogInformation("DirectTranscription ViewModel: ResumeAsync invoked");

        await StartCaptureAsync(cancellationToken, "Failed to resume direct transcription").ConfigureAwait(false);
    }

    /// <summary>
    /// Shared body of <see cref="StartAsync"/> and <see cref="ResumeAsync"/>: launch the utterance
    /// consumer before starting the service (so no utterance is missed), then start the service itself.
    /// On failure, logs with the caller's own message and tears the reader back down.
    /// </summary>
    private async Task StartCaptureAsync(CancellationToken cancellationToken, string failureMessage)
    {
        await StartReaderAsync().ConfigureAwait(false);

        try
        {
            await _service.StartAsync(cancellationToken).ConfigureAwait(false);
            _sessionStarted = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, failureMessage);
            await StopReaderAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pauses the pipeline: the service returns to <c>Prepared</c> (consent, diarizer and shared STT
    /// engine all survive), so <see cref="ResumeAsync"/> is fast and nobody has to re-consent.
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            await _service.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop direct transcription service");
        }

        await StopReaderAsync().ConfigureAwait(false);

        // Clear any lingering listening dots — the audio pipeline has been torn down.
        DispatchToUi(() =>
        {
            IsMicListening = false;
            foreach (var bubble in Bubbles)
                if (bubble.IsListening) bubble.IsListening = false;
        });

        RefreshVoiceStats();
    }

    // ---- Voice stats --------------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleStats()
    {
        var showing = !AreStatsVisible;
        if (showing) RefreshVoiceStats();
        DispatchToUi(() => AreStatsVisible = showing);
    }

    private void RefreshVoiceStats()
    {
        var stats = _service.GetVoiceStats();
        DispatchToUi(() =>
        {
            VoiceStats.Clear();
            foreach (var s in stats) VoiceStats.Add(s);
        });
    }

    // ---- Rename / revoke (per-speaker chip actions) ------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRenameSpeakerLabel))]
    private async Task RenameSpeakerLabelAsync(string? oldLabel)
    {
        if (string.IsNullOrWhiteSpace(oldLabel)) return;

        var title = _localizationService["DirectTrans_RenameSpeaker_Title"];
        var prompt = string.Format(_localizationService["DirectTrans_RenameSpeaker_Prompt"], oldLabel);
        var newLabel = await _dialogService.ShowInputDialogAsync(title, prompt).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newLabel) || newLabel == oldLabel) return;

        if (!_service.RenameSpeaker(oldLabel, newLabel)) return;

        RelabelSpeaker(oldLabel, newLabel);   // base helper: palette re-key + bubble/journal walk
        RelabelChip(oldLabel, newLabel);
    }

    private static bool CanRenameSpeakerLabel(string? oldLabel) => !string.IsNullOrWhiteSpace(oldLabel);

    /// <summary>
    /// Withdraws a speaker's consent (§3.3): tells the service, removes their bubbles/journal entries
    /// from the in-memory transcript (<see cref="TranscriptOverlayViewModel.RemoveSpeaker"/>), and marks
    /// the chip revoked. No confirmation dialog in v1 (no localized key exists for one).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevokeSpeaker))]
    private Task RevokeSpeakerAsync(string? speakerLabel)
    {
        if (string.IsNullOrWhiteSpace(speakerLabel)) return Task.CompletedTask;

        _service.RevokeSpeaker(speakerLabel);
        ApplyRevocation(speakerLabel);
        return Task.CompletedTask;
    }

    private static bool CanRevokeSpeaker(string? speakerLabel) => !string.IsNullOrWhiteSpace(speakerLabel);

    // ---- Summarize with the assistant ---------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSummarize))]
    private void SummarizeWithAssistant()
    {
        if (!CanSummarize()) return;
        // Do NOT log the prompt or transcript (sensitive user content); only that a summary was requested.
        _logger.LogInformation("DirectTranscription ViewModel: summary requested");
        SummarizeRequested?.Invoke(this, BuildSummaryPrompt());
    }

    private bool CanSummarize() => !IsRunning && Bubbles.Count > 0;

    /// <summary>
    /// Prompt-only (no silent save + hand-over-a-path): both types the old flow needed
    /// (<c>MeetingSummarizationRequest</c>, <c>PathShortener</c>) were deleted from the current branch.
    /// </summary>
    private string BuildSummaryPrompt()
    {
        var when = _sessionStart.LocalDateTime.ToString("f");
        var instruction = _localizationService.Format("DirectTrans_SummaryPrompt", when);

        var sb = new StringBuilder();
        sb.Append(instruction);
        sb.AppendLine().AppendLine();
        sb.Append(DirectTranscriptMarkdown.RenderBody(_localizationService[TitleKey], Bubbles.ToList(), CounterpartName));
        return sb.ToString();
    }

    // ---- Save (front matter + stats block) ----------------------------------------------------------

    /// <summary>Prepends YAML front matter (schema/session bounds/speakers) and the voice-stats block.</summary>
    internal override string BuildMarkdown()
    {
        var sessionEnd = Bubbles.Count > 0 ? Bubbles[^1].EndTimestamp : _sessionStart;
        return DirectTranscriptMarkdown.Render(
            _localizationService[TitleKey],
            _sessionStart,
            sessionEnd,
            Bubbles.ToList(),
            SuppressSpeakerLabels ? [] : _service.GetVoiceStats(),
            CounterpartName);
    }

    // ---- Consent chips -------------------------------------------------------------------------------

    private void OnSpeakerRegistered(object? sender, string speakerLabel)
    {
        DispatchToUi(() =>
        {
            if (FindChip(speakerLabel) is not null) return;
            var chip = new SpeakerConsentChip
            {
                SpeakerLabel = speakerLabel,
                DisplayName = speakerLabel,
                IsConsented = false,
                StatusText = _localizationService["DirectTrans_Chip_AwaitingConsent"],
                ColorIndex = AssignChipColorIndex(speakerLabel),
            };
            ConsentChips.Add(chip);
        });
    }

    private void OnSpeakerConsentChanged(object? sender, SpeakerConsentChangedEventArgs e)
    {
        switch (e.NewState)
        {
            case ConsentState.Granted:
                ApplyGrant(e);
                break;
            case ConsentState.Revoked:
                ApplyRevocation(e.SpeakerLabel);
                break;
        }
    }

    /// <summary>
    /// Applies a grant to the transcript and the chip.
    ///
    /// <para><b>The chip's key is <see cref="SpeakerConsentChangedEventArgs.SpeakerLabel"/>, never the
    /// extracted name.</b> The service resolves a grant-time rename that was REFUSED (the name is already
    /// taken) by keeping the original diarizer label as the consent-map key while still reporting the
    /// name. Deriving the chip's key from the name instead made the chip disagree with the consent map, and
    /// since Revoke is issued with the chip's key, revoking then either hit a different speaker's entry or
    /// silently did nothing at all — a requested withdrawal of consent that was never honoured. The name is
    /// display text only.</para>
    /// </summary>
    private void ApplyGrant(SpeakerConsentChangedEventArgs e)
    {
        var authoritativeLabel = e.SpeakerLabel;
        var previousLabel = e.OriginalSpeakerLabel ?? e.SpeakerLabel;
        var displayName = string.IsNullOrWhiteSpace(e.ExtractedName) ? authoritativeLabel : e.ExtractedName!;

        // SpeakerConsentChanged can fire on a background thread (interface contract), and
        // RelabelSpeaker's speaker-color-palette dictionary write is NOT itself marshaled (only the
        // bubble/journal walk inside it is) — it is only race-free today because MeetingAttendeeViewModel
        // happens to always call it from the UI thread. Doing the whole rename-plus-chip-update sequence
        // inside ONE DispatchToUi puts that dictionary write on the same thread as AddUtterance's palette
        // access (which runs inside ITS OWN DispatchToUi), rather than introducing a second unguarded
        // caller thread for it.
        DispatchToUi(() =>
        {
            // Mirror the label move the diarizer + consent map actually performed (design §3.5 step 2).
            // RelabelSpeaker's own DispatchToUi call is a no-op re-marshal here — we are already on the
            // UI thread, so IUiDispatcher.PostOrRun runs it inline.
            if (!string.Equals(previousLabel, authoritativeLabel, StringComparison.Ordinal))
                RelabelSpeaker(previousLabel, authoritativeLabel);

            var chip = FindChip(previousLabel) ?? FindChip(authoritativeLabel);
            if (chip is null)
            {
                chip = new SpeakerConsentChip
                {
                    SpeakerLabel = authoritativeLabel,
                    ColorIndex = AssignChipColorIndex(authoritativeLabel),
                };
                ConsentChips.Add(chip);
            }
            chip.SpeakerLabel = authoritativeLabel;
            chip.DisplayName = displayName;
            chip.IsConsented = true;
            chip.StatusText = _localizationService["DirectTrans_Chip_Consented"];
        });

        // Outside the dispatch so a slow audio device cannot delay the relabel. The host hears it; a
        // remote participant only would if their conferencing client did not cancel the loudspeaker
        // path, so this confirms the grant to the person running the session, not to the room.
        _consentSoundPlayer?.PlayConsentGranted();
    }

    /// <summary>
    /// The service discarded the consent map (a re-prepare after a failed start rebuilds the diarizer, so
    /// old labels now belong to different voices). Drop every chip and every statistic: leaving a chip
    /// reading "consented" while the gate has reverted that speaker to Unknown would tell the user a
    /// participant is being recorded while their speech is in fact being dropped. Existing bubbles stay —
    /// that text was emitted lawfully under the consent that existed at the time.
    /// </summary>
    private void OnConsentSessionReset(object? sender, EventArgs e)
    {
        DispatchToUi(() =>
        {
            ConsentChips.Clear();
            VoiceStats.Clear();
            _chipColorIndex.Clear();
            _nextChipColorIndex = 0;
        });
    }

    private void ApplyRevocation(string speakerLabel)
    {
        RemoveSpeaker(speakerLabel);   // base helper: removes bubbles + journal entries for this label
        DispatchToUi(() =>
        {
            var chip = FindChip(speakerLabel);
            if (chip is null) return;
            chip.IsConsented = false;
            chip.StatusText = _localizationService["DirectTrans_Chip_Revoked"];
        });
    }

    private void RelabelChip(string oldLabel, string newLabel)
    {
        if (_chipColorIndex.Remove(oldLabel, out var carried))
            _chipColorIndex[newLabel] = carried;

        DispatchToUi(() =>
        {
            var chip = FindChip(oldLabel);
            if (chip is null) return;
            chip.SpeakerLabel = newLabel;
            chip.DisplayName = newLabel;
        });
    }

    private SpeakerConsentChip? FindChip(string speakerLabel)
        => ConsentChips.FirstOrDefault(c => string.Equals(c.SpeakerLabel, speakerLabel, StringComparison.Ordinal));

    /// <summary>
    /// Independent copy of the base bubble-palette wrap-around scheme (that map is private to
    /// <see cref="TranscriptOverlayViewModel"/>). Chip appearance itself is driven by
    /// <see cref="SpeakerConsentChip.IsConsented"/> (muted vs accent), not by this color — it exists so a
    /// speaker's chip and bubble usually land on the same hue.
    /// </summary>
    private int AssignChipColorIndex(string speakerLabel)
    {
        if (_chipColorIndex.TryGetValue(speakerLabel, out var idx)) return idx;
        idx = _nextChipColorIndex % ChipColorPaletteSize;
        _chipColorIndex[speakerLabel] = idx;
        _nextChipColorIndex++;
        return idx;
    }

    // ---- Mic level indicator (You side only) ---------------------------------------------------------

    /// <summary>
    /// Only the local microphone side drives a "listening" indicator. The loopback ("Them") side is
    /// deliberately ignored here: <see cref="TranscriptionSpeakingChangedEventArgs"/> carries no
    /// per-speaker label, and "unconsented speakers produce no bubbles at all" (design §3.9) would be
    /// violated by materializing a placeholder bubble for an as-yet-unconsented loopback speaker just to
    /// show activity.
    ///
    /// <para>Voice activity is surfaced through <see cref="IsMicListening"/> (a header indicator) and, when
    /// a "me" bubble already exists, on that bubble. It deliberately does NOT create one:
    /// <c>createIfMissing: true</c> materialized an EMPTY bubble on every voice-activity start, and a VAD
    /// trigger that produced no transcribable text (a cough, a keystroke) left it there forever — rendering
    /// as a blank pill, emitting an empty block into the exported Markdown, enabling Save and Summarize for
    /// a session with no transcribed speech, and vanishing on any journal rebuild because it had no journal
    /// entry.</para>
    /// </summary>
    private void OnSpeakingChanged(object? sender, TranscriptionSpeakingChangedEventArgs e)
    {
        if (e.Speaker != TranscriptSpeaker.You) return;

        DispatchToUi(() =>
        {
            IsMicListening = e.IsSpeaking;

            if (e.IsSpeaking)
            {
                var bubble = GetOrCreateBubble(TranscriptSpeaker.You, DateTimeOffset.Now, speakerLabel: null, createIfMissing: false);
                if (bubble is not null) bubble.IsListening = true;
            }
            else
            {
                for (int i = Bubbles.Count - 1; i >= 0; i--)
                {
                    if (Bubbles[i].Speaker == TranscriptSpeaker.You && Bubbles[i].IsListening)
                    {
                        Bubbles[i].IsListening = false;
                        break;
                    }
                }
            }
        });
    }

    // ---- State → status -------------------------------------------------------------------------------

    private void OnServiceStateChanged(object? sender, DirectTranscriptionState newState)
    {
        DispatchToUi(() =>
        {
            // "Running" spans Starting/Running/Stopping so Stop stays visible through the transitional
            // teardown, and Save/Resume only show once back to Prepared (or Idle/Error).
            IsRunning = newState is DirectTranscriptionState.Starting
                or DirectTranscriptionState.Running
                or DirectTranscriptionState.Stopping;

            StatusText = newState switch
            {
                DirectTranscriptionState.Idle => _localizationService["DirectTrans_Status_Idle"],
                DirectTranscriptionState.Preparing => _localizationService["DirectTrans_Status_Preparing"],
                DirectTranscriptionState.Prepared => _localizationService["DirectTrans_Status_Idle"],
                DirectTranscriptionState.Starting => _localizationService["DirectTrans_Status_Starting"],
                DirectTranscriptionState.Running => _localizationService["DirectTrans_Status_Listening"],
                DirectTranscriptionState.Stopping => _localizationService["DirectTrans_Status_Stopping"],
                DirectTranscriptionState.Error => _localizationService["DirectTrans_Status_Error"],
                _ => string.Empty,
            };
        });
    }

    protected override void OnRunningChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        SummarizeWithAssistantCommand.NotifyCanExecuteChanged();
    }

    partial void OnDisclaimerAcceptedChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    partial void OnIsDisclaimerVisibleChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    public override void Dispose()
    {
        // Unsubscribe BEFORE tearing down: the backing service is a DI singleton, so with the handlers
        // detached nothing in EndSessionAsync's teardown can DispatchToUi back onto whatever thread a
        // sync-over-async wait below might block (mirrors MeetingAttendeeViewModel.Dispose).
        _service.StateChanged -= OnServiceStateChanged;
        _service.SpeakerConsentChanged -= OnSpeakerConsentChanged;
        _service.SpeakerRegistered -= OnSpeakerRegistered;
        _service.SpeakingChanged -= OnSpeakingChanged;
        _service.ConsentSessionReset -= OnConsentSessionReset;
        _localizationService.LanguageChanged -= OnUiLanguageChanged;

        if (_service.State is not DirectTranscriptionState.Idle)
        {
            try { _service.EndSessionAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to end direct transcription session on dispose"); }
        }

        base.Dispose();
    }
}
