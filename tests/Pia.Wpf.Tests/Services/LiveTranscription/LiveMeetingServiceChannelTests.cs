using NetArchTest.Rules;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class LiveMeetingServiceChannelTests
{
    [Fact]
    public void LiveMeetingService_DoesNotReassign_UtterancesField()
    {
        var type = typeof(Pia.Services.LiveTranscription.LiveMeetingService);
        var field = type.GetField("_utterances",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.True(field!.IsInitOnly,
            "_utterances must be readonly so the public reader is stable across sessions.");
    }
}
