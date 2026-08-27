namespace Pia.Services.Exceptions;

/// <summary>
/// Thrown when nobody admitted the attendee from the lobby before the admission deadline. Distinct from
/// a general join failure because it is the one outcome worth retrying unattended: the usual cause is
/// that the organiser had not started the meeting yet.
/// </summary>
public sealed class MeetingAdmissionTimeoutException : Exception
{
    public MeetingAdmissionTimeoutException(string message)
        : base(message)
    {
    }
}
