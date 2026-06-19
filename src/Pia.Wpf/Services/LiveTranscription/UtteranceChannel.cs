using System.Threading.Channels;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Factory for the bounded utterance channel shared by the transcription orchestrators
/// (<see cref="LiveMeetingService"/> and <see cref="Pia.Services.MeetingAttendee.MeetingAttendeeService"/>).
/// Single reader (the ViewModel's consumer), multiple writers (one per engine), and drops the oldest
/// utterance when the UI cannot keep up so producers never block.
/// </summary>
internal static class UtteranceChannel
{
    public static Channel<TranscriptUtterance> CreateBounded()
        => Channel.CreateBounded<TranscriptUtterance>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
}
