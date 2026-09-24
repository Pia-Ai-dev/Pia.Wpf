using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services.Tts;

/// <summary>
/// Deleting the voice in use has to clear the saved key too, or startup keeps naming a voice that can
/// never load. The key here is deliberately not a curated one, so the resolved directory cannot exist
/// and nothing under the real profile is touched.
/// </summary>
public sealed class TtsVoiceDeletionTests
{
    [Fact]
    public async Task Clears_the_saved_key_when_it_named_the_deleted_voice()
    {
        var absent = "pia-tests-absent-voice-" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { TtsVoiceModelKey = absent };
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(Task.FromResult(settings));

        var service = new TtsService(
            NullLogger<TtsService>.Instance, settingsService, Substitute.For<IAssetDownloader>());

        await service.DeleteVoiceAsync(absent, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, settings.TtsVoiceModelKey);
        await settingsService.Received(1).SaveSettingsAsync(settings);
    }

    [Fact]
    public async Task Leaves_the_saved_key_of_another_voice_alone()
    {
        var kept = "pia-tests-kept-voice-" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { TtsVoiceModelKey = kept };
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(Task.FromResult(settings));

        var service = new TtsService(
            NullLogger<TtsService>.Instance, settingsService, Substitute.For<IAssetDownloader>());

        await service.DeleteVoiceAsync(
            "pia-tests-absent-voice-" + Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);

        Assert.Equal(kept, settings.TtsVoiceModelKey);
        await settingsService.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }
}
