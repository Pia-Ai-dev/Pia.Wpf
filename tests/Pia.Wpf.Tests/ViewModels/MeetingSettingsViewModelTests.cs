using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Covers the meeting-diarization settings that moved out of the General settings VM into the new
/// <see cref="MeetingSettingsViewModel"/> (Meeting inner tab of the Assistant settings view): the
/// <see cref="MeetingSettingsViewModel.EnableMeetingDiarization"/> toggle, the
/// <see cref="MeetingSettingsViewModel.SpeakerEmbeddingThreshold"/>,
/// <see cref="MeetingSettingsViewModel.MeetingMaxSpeakers"/> and
/// <see cref="MeetingSettingsViewModel.MeetingMinSpeechSeconds"/> sliders, plus the meeting-browser
/// selection. Each loads from <see cref="AppSettings"/> on Initialize and persists back through
/// SaveSettings, with the off-grid clamp/snap behaviour exercised for the slider knobs.
/// </summary>
public class MeetingSettingsViewModelTests
{
    private static (MeetingSettingsViewModel sut, ISettingsService settings, AppSettings stored) Create(
        AppSettings? initial = null)
    {
        var stored = initial ?? new AppSettings();

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));
        settingsService.SaveSettingsAsync(Arg.Any<AppSettings>()).Returns(Task.CompletedTask);

        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns("display");
        localization[Arg.Any<string>()].Returns("display");

        var sut = new MeetingSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance,
            settingsService,
            localization,
            Substitute.For<IPolicyService>());

        return (sut, settingsService, stored);
    }

    // ---- diarization enable ---------------------------------------------------------------------

    [Fact]
    public async Task Initialize_LoadsDiarizationSettingsFromAppSettings()
    {
        var (sut, _, _) = Create(new AppSettings
        {
            EnableMeetingDiarization = false,
            SpeakerEmbeddingThreshold = 0.85f,
        });

        await sut.InitializeAsync();

        Assert.False(sut.EnableMeetingDiarization);
        Assert.Equal(0.85f, sut.SpeakerEmbeddingThreshold);
    }

    [Fact]
    public async Task TogglingDiarization_PersistsToAppSettings()
    {
        var (sut, settings, stored) = Create(new AppSettings { EnableMeetingDiarization = true });
        await sut.InitializeAsync();

        sut.EnableMeetingDiarization = false;

        // OnChanged fires SaveSettings fire-and-forget; the substitute completes synchronously.
        await settings.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.False(stored.EnableMeetingDiarization);
    }

    // ---- threshold ------------------------------------------------------------------------------

    [Fact]
    public async Task Initialize_SnapsOffGridThresholdToTickGrid()
    {
        // An off-grid stored value (reachable only via a manual settings-file edit) is snapped to the
        // 0.05 grid on load, so the snapping slider has nothing to write back through its two-way
        // binding and no one-time cosmetic re-save fires.
        var (sut, _, _) = Create(new AppSettings { SpeakerEmbeddingThreshold = 0.73f });

        await sut.InitializeAsync();

        Assert.Equal(0.75f, sut.SpeakerEmbeddingThreshold);
    }

    [Fact]
    public async Task Initialize_SnapsBelowFloorThresholdUpToMinimum()
    {
        // The slider range now extends down to 0.30 (over-segmentation fix). A stored value below the
        // new floor is clamped up to the minimum on load.
        var (sut, _, _) = Create(new AppSettings { SpeakerEmbeddingThreshold = 0.20f });

        await sut.InitializeAsync();

        Assert.Equal(0.30f, sut.SpeakerEmbeddingThreshold);
    }

    [Fact]
    public async Task ChangingThreshold_PersistsRoundedValueToAppSettings()
    {
        var (sut, settings, stored) = Create(new AppSettings { SpeakerEmbeddingThreshold = 0.70f });
        await sut.InitializeAsync();

        // A grid-aligned value persists directly (no re-clamp loop).
        sut.SpeakerEmbeddingThreshold = 0.80f;

        await settings.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.Equal(0.80f, stored.SpeakerEmbeddingThreshold);
    }

    // ---- max speakers ---------------------------------------------------------------------------

    [Fact]
    public async Task Initialize_LoadsMaxSpeakersFromAppSettings()
    {
        var (sut, _, _) = Create(new AppSettings { MeetingMaxSpeakers = 4 });

        await sut.InitializeAsync();

        Assert.Equal(4, sut.MeetingMaxSpeakers);
    }

    [Fact]
    public async Task ChangingMaxSpeakers_PersistsToAppSettings()
    {
        var (sut, settings, stored) = Create(new AppSettings { MeetingMaxSpeakers = 0 });
        await sut.InitializeAsync();

        sut.MeetingMaxSpeakers = 6;

        await settings.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.Equal(6, stored.MeetingMaxSpeakers);
    }

    [Fact]
    public async Task ChangingMaxSpeakers_ClampsToUpperBound()
    {
        var (sut, _, _) = Create();
        await sut.InitializeAsync();

        // The handler clamps to [0,12], reassigns, and returns; the reassignment settles the property.
        sut.MeetingMaxSpeakers = 99;

        Assert.Equal(12, sut.MeetingMaxSpeakers);
    }

    // ---- min speech length ----------------------------------------------------------------------

    [Fact]
    public async Task Initialize_LoadsMinSpeechSecondsFromAppSettings()
    {
        var (sut, _, _) = Create(new AppSettings { MeetingMinSpeechSeconds = 2.5f });

        await sut.InitializeAsync();

        Assert.Equal(2.5f, sut.MeetingMinSpeechSeconds);
    }

    [Fact]
    public async Task ChangingMinSpeechSeconds_PersistsToAppSettings()
    {
        var (sut, settings, stored) = Create(new AppSettings { MeetingMinSpeechSeconds = 1.5f });
        await sut.InitializeAsync();

        // A grid-aligned value persists directly (no re-snap loop).
        sut.MeetingMinSpeechSeconds = 2.0f;

        await settings.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.Equal(2.0f, stored.MeetingMinSpeechSeconds);
    }

    [Fact]
    public async Task ChangingMinSpeechSeconds_SnapsOffGridValueToTickGrid()
    {
        var (sut, _, _) = Create();
        await sut.InitializeAsync();

        // The handler snaps to the 0.5 grid, reassigns, and returns; the reassignment settles the property.
        sut.MeetingMinSpeechSeconds = 1.3f;

        Assert.Equal(1.5f, sut.MeetingMinSpeechSeconds);
    }

    // ---- meeting browser ------------------------------------------------------------------------

    [Fact]
    public async Task Initialize_LoadsMeetingBrowserSettingsFromAppSettings()
    {
        var (sut, _, _) = Create(new AppSettings
        {
            MeetingBrowserSelection = MeetingBrowserSelection.SystemEdge,
            MeetingAttendeeShowBrowserWindow = true,
        });

        await sut.InitializeAsync();

        Assert.Equal(MeetingBrowserSelection.SystemEdge, sut.MeetingBrowserSelection);
        Assert.True(sut.MeetingAttendeeShowBrowserWindow);
    }

    [Fact]
    public async Task ChangingMeetingBrowserSelection_PersistsToAppSettings()
    {
        var (sut, settings, stored) = Create();
        await sut.InitializeAsync();

        sut.MeetingBrowserSelection = MeetingBrowserSelection.SystemChrome;

        await settings.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.Equal(MeetingBrowserSelection.SystemChrome, stored.MeetingBrowserSelection);
    }

    [Fact]
    public async Task TogglingShowBrowserWindow_PersistsToAppSettings()
    {
        var (sut, settings, stored) = Create(new AppSettings { MeetingAttendeeShowBrowserWindow = false });
        await sut.InitializeAsync();

        sut.MeetingAttendeeShowBrowserWindow = true;

        await settings.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.True(stored.MeetingAttendeeShowBrowserWindow);
    }

    // ---- smart speaker detection ----------------------------------------------------------------

    [Fact]
    public async Task Initialize_LoadsSmartSpeakerDetection()
    {
        var (sut, _, _) = Create(new AppSettings { MeetingSmartSpeakerDetection = false });

        await sut.InitializeAsync();

        Assert.False(sut.MeetingSmartSpeakerDetection);
        Assert.True(sut.ShowManualTuning);
    }

    [Fact]
    public async Task TogglingSmartSpeakerDetection_PersistsAndFlipsManualTuningVisibility()
    {
        var (sut, settings, stored) = Create(new AppSettings { MeetingSmartSpeakerDetection = true });
        await sut.InitializeAsync();
        Assert.False(sut.ShowManualTuning);

        sut.MeetingSmartSpeakerDetection = false;

        Assert.True(sut.ShowManualTuning);
        await settings.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.False(stored.MeetingSmartSpeakerDetection);
    }
}
