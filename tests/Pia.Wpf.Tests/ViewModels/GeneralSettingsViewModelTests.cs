using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Covers the meeting-diarization settings added to the General settings VM: the
/// <see cref="GeneralSettingsViewModel.EnableMeetingDiarization"/> toggle and the
/// <see cref="GeneralSettingsViewModel.SpeakerEmbeddingThreshold"/> slider load from
/// <see cref="AppSettings"/> on Initialize and persist back through SaveSettings,
/// mirroring the existing AutoCaptureSelectedText/Threshold settings round-trip.
/// </summary>
public class GeneralSettingsViewModelTests
{
    private static (GeneralSettingsViewModel sut, ISettingsService settings, AppSettings stored) Create(
        AppSettings? initial = null)
    {
        var stored = initial ?? new AppSettings();

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));
        settingsService.SaveSettingsAsync(Arg.Any<AppSettings>()).Returns(Task.CompletedTask);

        var ttsService = Substitute.For<ITtsService>();
        ttsService.GetAvailableVoicesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TtsVoice>>([]));

        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns("display");

        var sut = new GeneralSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance,
            settingsService,
            Substitute.For<ITranscriptionService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<ITrayIconService>(),
            ttsService,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            localization,
            Substitute.For<IAutostartService>(),
            Substitute.For<IPolicyService>());

        return (sut, settingsService, stored);
    }

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
    public async Task ChangingThreshold_PersistsRoundedValueToAppSettings()
    {
        var (sut, settings, stored) = Create(new AppSettings { SpeakerEmbeddingThreshold = 0.70f });
        await sut.InitializeAsync();

        // A grid-aligned value persists directly (no re-clamp loop).
        sut.SpeakerEmbeddingThreshold = 0.80f;

        await settings.Received().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.Equal(0.80f, stored.SpeakerEmbeddingThreshold);
    }

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
}
