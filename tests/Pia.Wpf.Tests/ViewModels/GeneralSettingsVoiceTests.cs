using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Localization;
using Pia.Models;
using Pia.Services.Diagnostics;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The voice picker's two silent states: a finished download that selected nothing, and a removed voice
/// that left the engine with none. Both are asserted through ITtsService, so no model is ever loaded.
/// </summary>
public sealed class GeneralSettingsVoiceTests
{
    private const string Downloaded = "de_DE-thorsten-medium";
    private const string Other = "en_US-amy-medium";

    [Fact]
    public async Task Downloading_a_voice_selects_it()
    {
        var harness = Build();
        var voice = Voice(Downloaded, isDownloaded: false);
        harness.Vm.TtsVoices.Add(voice);

        await harness.Vm.DownloadVoiceCommand.ExecuteAsync(voice);

        await harness.Tts.Received(1).SetVoiceAsync(Downloaded, Arg.Any<CancellationToken>());
        Assert.True(voice.IsSelected);
        Assert.Equal(Downloaded, harness.Vm.SelectedVoiceKey);
    }

    [Fact]
    public async Task Downloading_a_voice_leaves_the_choice_alone_while_policy_enforces_it()
    {
        var harness = Build();
        harness.Policy.IsEnforced(nameof(AppSettings.TtsVoiceModelKey)).Returns(true);
        var voice = Voice(Downloaded, isDownloaded: false);
        harness.Vm.TtsVoices.Add(voice);

        await harness.Vm.DownloadVoiceCommand.ExecuteAsync(voice);

        await harness.Tts.DidNotReceive().SetVoiceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.True(voice.IsDownloaded);
    }

    [Fact]
    public async Task Deleting_the_voice_in_use_falls_back_to_one_still_on_disk()
    {
        var harness = Build();
        harness.Dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));

        var inUse = Voice(Downloaded, isDownloaded: true);
        inUse.IsSelected = true;
        var spare = Voice(Other, isDownloaded: true);
        harness.Vm.TtsVoices.Add(inUse);
        harness.Vm.TtsVoices.Add(spare);

        await harness.Vm.DeleteVoiceCommand.ExecuteAsync(inUse);

        await harness.Tts.Received(1).DeleteVoiceAsync(Downloaded, Arg.Any<CancellationToken>());
        await harness.Tts.Received(1).SetVoiceAsync(Other, Arg.Any<CancellationToken>());
        Assert.False(inUse.IsDownloaded);
        Assert.False(inUse.IsSelected);
        Assert.True(spare.IsSelected);
    }

    [Fact]
    public async Task Declining_the_confirmation_deletes_nothing()
    {
        var harness = Build();
        harness.Dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));

        var voice = Voice(Downloaded, isDownloaded: true);
        harness.Vm.TtsVoices.Add(voice);

        await harness.Vm.DeleteVoiceCommand.ExecuteAsync(voice);

        await harness.Tts.DidNotReceive().DeleteVoiceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.True(voice.IsDownloaded);
    }

    [Fact]
    public async Task Deleting_the_last_voice_clears_the_selection()
    {
        var harness = Build();
        harness.Dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));

        var only = Voice(Downloaded, isDownloaded: true);
        only.IsSelected = true;
        harness.Vm.TtsVoices.Add(only);

        await harness.Vm.DeleteVoiceCommand.ExecuteAsync(only);

        await harness.Tts.DidNotReceive().SetVoiceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(string.Empty, harness.Vm.SelectedVoiceKey);
    }

    private static TtsVoice Voice(string key, bool isDownloaded) => new()
    {
        Key = key,
        DisplayName = key,
        Language = "German",
        Quality = "Medium",
        Gender = "Male",
        SizeBytes = 1,
        IsDownloaded = isDownloaded,
    };

    private sealed record Harness(
        GeneralSettingsViewModel Vm, ITtsService Tts, IDialogService Dialogs, IPolicyService Policy);

    private static Harness Build()
    {
        var logger = NullLogger<SettingsViewModel>.Instance;
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(Task.FromResult(new AppSettings()));
        var policy = Substitute.For<IPolicyService>();
        var tts = Substitute.For<ITtsService>();
        var dialogs = Substitute.For<IDialogService>();

        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        localization[Arg.Any<string>()].Returns(c => LocalizationSource.Instance[(string)c[0]]);
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(c => (string)c[0]);

        var vm = new GeneralSettingsViewModel(
            logger, settings, Substitute.For<ITranscriptionService>(), dialogs,
            Substitute.For<ITrayIconService>(), tts, Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            localization, Substitute.For<IAutostartService>(), policy,
            new PrivacySettingsViewModel(logger, settings, policy),
            Substitute.For<ISyncClientService>(), Substitute.For<IDiagnosticsExportService>());

        return new Harness(vm, tts, dialogs, policy);
    }
}
