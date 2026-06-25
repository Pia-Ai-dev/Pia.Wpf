using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using System.Collections.Generic;

namespace Pia.ViewModels;

public partial class MeetingSettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private bool _isLoading;

    public MeetingSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        ILocalizationService localizationService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _localizationService = localizationService;

        _localizationService.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SpeakerEmbeddingThresholdDisplay));
            OnPropertyChanged(nameof(MeetingMaxSpeakersDisplay));
            OnPropertyChanged(nameof(MeetingMinSpeechDisplay));
        };
    }

    // Meeting per-speaker diarization
    [ObservableProperty]
    private bool _enableMeetingDiarization = true;

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
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();
        EnableMeetingDiarization = settings.EnableMeetingDiarization;
        SpeakerEmbeddingThreshold = SnapThreshold(settings.SpeakerEmbeddingThreshold);
        MeetingMaxSpeakers = Math.Clamp(settings.MeetingMaxSpeakers, 0, 12);
        MeetingMinSpeechSeconds = SnapMinSpeech(settings.MeetingMinSpeechSeconds);
        MeetingBrowserSelection = settings.MeetingBrowserSelection;
        MeetingAttendeeShowBrowserWindow = settings.MeetingAttendeeShowBrowserWindow;

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
        await _settingsService.SaveSettingsAsync(settings);
    }
}
