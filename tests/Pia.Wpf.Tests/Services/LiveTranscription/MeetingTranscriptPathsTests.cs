using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class MeetingTranscriptPathsTests
{
    [Fact]
    public void DefaultMeetingFolder_EndsWithExpectedSegments()
    {
        var path = MeetingTranscriptPaths.DefaultMeetingFolder;

        // Path.Combine yields the platform separator; check both for portability.
        Assert.Contains("Pia", path);
        Assert.Contains("assistant", path);
        Assert.Contains("meetings", path);
        Assert.EndsWith("meetings", path);
    }

    [Fact]
    public void ResolveFolder_ReturnsDefault_WhenSettingIsNull()
    {
        var settings = new AppSettings { MeetingTranscriptFolder = null };

        Assert.Equal(MeetingTranscriptPaths.DefaultMeetingFolder, MeetingTranscriptPaths.ResolveFolder(settings));
    }

    [Fact]
    public void ResolveFolder_ReturnsDefault_WhenSettingIsWhitespace()
    {
        var settings = new AppSettings { MeetingTranscriptFolder = "   " };

        Assert.Equal(MeetingTranscriptPaths.DefaultMeetingFolder, MeetingTranscriptPaths.ResolveFolder(settings));
    }

    [Fact]
    public void ResolveFolder_ReturnsConfigured_WhenSettingProvided()
    {
        var settings = new AppSettings { MeetingTranscriptFolder = @"X:\custom\transcripts" };

        Assert.Equal(@"X:\custom\transcripts", MeetingTranscriptPaths.ResolveFolder(settings));
    }
}
