using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using System.Collections.Generic;

namespace Pia.ViewModels;

public partial class MeetingSettingsViewModel : UiThreadViewModel, IDisposable
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IPolicyService _policyService;
    private bool _isLoading;
    private bool _disposed;

    /// <summary>Bind IsEnabled to Policy[nameof(AppSettings.X)] to grey a control out while policy enforces it.</summary>
    public PolicyLock Policy { get; }

    public MeetingSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IPolicyService policyService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _policyService = policyService;
        Policy = new PolicyLock(policyService);

        _localizationService.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SpeakerEmbeddingThresholdDisplay));
            OnPropertyChanged(nameof(MeetingMaxSpeakersDisplay));
            OnPropertyChanged(nameof(MeetingMinSpeechDisplay));
        };

        _settingsService.SettingsChanged += OnSettingsChanged;
        _policyService.LocksChanged += OnLocksChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _policyService.LocksChanged -= OnLocksChanged;
        Policy.Dispose();
        GC.SuppressFinalize(this);
    }

    // SmartSpeakerDetectionEditable ANDs the lock into its own value, so the indexer raise cannot reach it.
    private void OnLocksChanged(object? sender, EventArgs e) =>
        Post(() => OnPropertyChanged(nameof(SmartSpeakerDetectionEditable)));

    // Meeting per-speaker diarization
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmartSpeakerDetectionEditable))]
    private bool _enableMeetingDiarization = true;

    /// <summary>The diarization sub-controls need diarization on AND no policy lock, so both gates AND.</summary>
    public bool SmartSpeakerDetectionEditable =>
        EnableMeetingDiarization && Policy[nameof(AppSettings.MeetingSmartSpeakerDetection)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakerEmbeddingThresholdDisplay))]
    private float _speakerEmbeddingThreshold = 0.50f;

    public string SpeakerEmbeddingThresholdDisplay =>
        _localizationService.Format("Settings_Diarization_ThresholdDisplay", SpeakerEmbeddingThreshold.ToString("F2"));

    // Cap on the number of distinct speakers Pia creates (0 = no limit)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeetingMaxSpeakersDisplay))]
    private int _meetingMaxSpeakers;

    public string MeetingMaxSpeakersDisplay =>
        MeetingMaxSpeakers <= 0
            ? _localizationService["Settings_Diarization_MaxSpeakers_NoLimit"]
            : _localizationService.Format("Settings_Diarization_MaxSpeakersDisplay", MeetingMaxSpeakers);

    // Minimum uninterrupted speech length (seconds) before a speaker is identified
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeetingMinSpeechDisplay))]
    private float _meetingMinSpeechSeconds = 1.5f;

    public string MeetingMinSpeechDisplay =>
        _localizationService.Format("Settings_Diarization_MinSpeechDisplay", MeetingMinSpeechSeconds.ToString("F1"));

    // Meeting browser selection + window visibility
    [ObservableProperty]
    private MeetingBrowserSelection _meetingBrowserSelection;

    [ObservableProperty]
    private bool _meetingAttendeeShowBrowserWindow;

    public IEnumerable<MeetingBrowserSelection> MeetingBrowserSelections => Enum.GetValues<MeetingBrowserSelection>();

    // Smart auto-detect replaces all manual diarization tuning while ON.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManualTuning))]
    private bool _meetingSmartSpeakerDetection = true;

    /// <summary>The manual tuning sliders are only shown while smart auto-detect is OFF.</summary>
    public bool ShowManualTuning => !MeetingSmartSpeakerDetection;

    [ObservableProperty]
    private bool _meetingSuppressSpeakerLabels;

    [ObservableProperty]
    private bool _micEchoCancellation = true;

    partial void OnMeetingSuppressSpeakerLabelsChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnMicEchoCancellationChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnMeetingSmartSpeakerDetectionChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnEnableMeetingDiarizationChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnMeetingBrowserSelectionChanged(MeetingBrowserSelection value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnMeetingAttendeeShowBrowserWindowChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnSpeakerEmbeddingThresholdChanged(float value)
    {
        if (_isLoading) return;
        // Slider.Value is double; clamp to the supported range and round to the 0.05 grid so the
        // float round-trip stays on a stable value and SpeakerEmbeddingThresholdDisplay reads cleanly.
        var clamped = SnapThreshold(value);
        if (Math.Abs(clamped - value) > 0.0001f)
        {
            SpeakerEmbeddingThreshold = clamped;
            return;
        }
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnMeetingMaxSpeakersChanged(int value)
    {
        if (_isLoading) return;
        var clamped = Math.Clamp(value, 0, 12);
        if (clamped != value)
        {
            MeetingMaxSpeakers = clamped;
            return;
        }
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnMeetingMinSpeechSecondsChanged(float value)
    {
        if (_isLoading) return;
        var snapped = SnapMinSpeech(value);
        if (Math.Abs(snapped - value) > 0.0001f)
        {
            MeetingMinSpeechSeconds = snapped;
            return;
        }
        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    // Clamp to the supported range and round to the 0.05 tick grid. Shared by the change handler and the
    // initial load so a stored off-grid value lands on-grid up front, instead of the snapping slider
    // writing it back through the two-way binding and triggering a one-time cosmetic re-save.
    private static float SnapThreshold(float value) =>
        (float)Math.Round(Math.Clamp(value, 0.30f, 0.95f) / 0.05f) * 0.05f;

    // Clamp to the supported range and round to the 0.5 s tick grid (same rationale as SnapThreshold).
    private static float SnapMinSpeech(float value) =>
        (float)Math.Round(Math.Clamp(value, 1.0f, 4.0f) / 0.5f) * 0.5f;

    public async Task InitializeAsync()
    {
        // Set before the await so a click landing mid-load cannot save the defaults.
        _isLoading = true;
        ApplySettings(await _settingsService.GetSettingsAsync());
    }

    // Raised from the policy pull thread, so the mirror has to be marshalled.
    private void OnSettingsChanged(object? sender, AppSettings settings) => Post(() => ApplySettings(settings));

    private void ApplySettings(AppSettings settings)
    {
        _isLoading = true;

        EnableMeetingDiarization = settings.EnableMeetingDiarization;
        SpeakerEmbeddingThreshold = SnapThreshold(settings.SpeakerEmbeddingThreshold);
        MeetingMaxSpeakers = Math.Clamp(settings.MeetingMaxSpeakers, 0, 12);
        MeetingMinSpeechSeconds = SnapMinSpeech(settings.MeetingMinSpeechSeconds);
        MeetingBrowserSelection = settings.MeetingBrowserSelection;
        MeetingAttendeeShowBrowserWindow = settings.MeetingAttendeeShowBrowserWindow;
        MeetingSmartSpeakerDetection = settings.MeetingSmartSpeakerDetection;
        MeetingSuppressSpeakerLabels = settings.MeetingSuppressSpeakerLabels;
        MicEchoCancellation = settings.MicEchoCancellation;

        _isLoading = false;
    }

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.EnableMeetingDiarization = EnableMeetingDiarization;
        settings.SpeakerEmbeddingThreshold = SpeakerEmbeddingThreshold;
        settings.MeetingMaxSpeakers = MeetingMaxSpeakers;
        settings.MeetingMinSpeechSeconds = MeetingMinSpeechSeconds;
        settings.MeetingBrowserSelection = MeetingBrowserSelection;
        settings.MeetingAttendeeShowBrowserWindow = MeetingAttendeeShowBrowserWindow;
        settings.MeetingSmartSpeakerDetection = MeetingSmartSpeakerDetection;
        settings.MeetingSuppressSpeakerLabels = MeetingSuppressSpeakerLabels;
        settings.MicEchoCancellation = MicEchoCancellation;
        await _settingsService.SaveSettingsAsync(settings);
    }
}
