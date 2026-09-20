using Pia.Models;
using Pia.Services.Tts;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Two defaults that are compliance positions rather than preferences, so a silent flip back would
/// ship a breach rather than an annoyance.
/// </summary>
public class AppSettingsLicenceAndBiometricDefaultsTests
{
    /// <summary>Speaker attribution derives a voice embedding per participant, so it must be opt-in.</summary>
    [Fact]
    public void Speaker_labels_are_suppressed_until_the_user_asks_for_them()
    {
        Assert.True(new AppSettings().MeetingSuppressSpeakerLabels);
    }

    [Fact]
    public void The_default_voice_is_one_whose_licence_permits_commercial_use()
    {
        var settings = new AppSettings();

        Assert.Equal(TtsVoiceCatalog.DefaultVoiceKey, settings.TtsVoiceModelKey);
        Assert.False(TtsVoiceCatalog.IsRetired(settings.TtsVoiceModelKey));
    }
}
