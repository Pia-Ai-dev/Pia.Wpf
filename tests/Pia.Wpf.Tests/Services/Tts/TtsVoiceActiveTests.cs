using Pia.Models;
using Xunit;

namespace Pia.Tests.Services.Tts;

/// <summary>
/// The saved voice key outlives the files it names — the Piper→sherpa move deletes the old tree on
/// startup without clearing the setting — so the list badge must not call an absent voice active.
/// </summary>
public class TtsVoiceActiveTests
{
    private static TtsVoice Voice() => new()
    {
        Key = "vits-piper-de_DE-eva_k-x_low",
        DisplayName = "Eva",
        Language = "German",
        Quality = "Low",
        Gender = "Female",
        SizeBytes = 1
    };

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void IsActive_RequiresBothSelectedAndDownloaded(bool selected, bool downloaded, bool expected)
    {
        var voice = Voice();
        voice.IsSelected = selected;
        voice.IsDownloaded = downloaded;

        Assert.Equal(expected, voice.IsActive);
    }

    [Fact]
    public void IsActive_RaisesPropertyChanged_WhenDownloadedFlips()
    {
        var voice = Voice();
        voice.IsSelected = true;
        var raised = 0;
        voice.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TtsVoice.IsActive)) raised++; };

        voice.IsDownloaded = true;

        Assert.Equal(1, raised);
        Assert.True(voice.IsActive);
    }

    [Fact]
    public void IsActive_RaisesPropertyChanged_WhenSelectionFlips()
    {
        var voice = Voice();
        voice.IsDownloaded = true;
        var raised = 0;
        voice.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TtsVoice.IsActive)) raised++; };

        voice.IsSelected = true;

        Assert.Equal(1, raised);
        Assert.True(voice.IsActive);
    }
}
