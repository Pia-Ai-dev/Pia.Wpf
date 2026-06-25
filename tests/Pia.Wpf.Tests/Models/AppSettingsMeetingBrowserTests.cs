using System.Text.Json;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Guards the meeting-browser settings added to <see cref="AppSettings"/>: the defaults and that both
/// fields survive a JSON round-trip through the same camelCase options the persistence layer uses
/// (<c>JsonPersistenceService.JsonOptions</c>). The enum serializes as its numeric value, matching the
/// existing <see cref="SttBackend"/> / <see cref="TargetSpeechLanguage"/> persistence.
/// </summary>
public class AppSettingsMeetingBrowserTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Defaults_AreBundledAndHidden()
    {
        var settings = new AppSettings();

        Assert.Equal(MeetingBrowserSelection.BundledChromium, settings.MeetingBrowserSelection);
        Assert.False(settings.MeetingAttendeeShowBrowserWindow);
    }

    [Theory]
    [InlineData(MeetingBrowserSelection.BundledChromium, false)]
    [InlineData(MeetingBrowserSelection.SystemChrome, true)]
    [InlineData(MeetingBrowserSelection.SystemEdge, false)]
    [InlineData(MeetingBrowserSelection.SystemDefault, true)]
    public void RoundTrip_PreservesMeetingBrowserSettings(MeetingBrowserSelection selection, bool showWindow)
    {
        var original = new AppSettings
        {
            MeetingBrowserSelection = selection,
            MeetingAttendeeShowBrowserWindow = showWindow,
        };

        var json = JsonSerializer.Serialize(original, Options);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(reloaded);
        Assert.Equal(selection, reloaded!.MeetingBrowserSelection);
        Assert.Equal(showWindow, reloaded.MeetingAttendeeShowBrowserWindow);
    }
}
