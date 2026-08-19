using System.IO;
using Pia.Paths;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Resolves the on-disk folder where meeting transcripts (Markdown exports) are written. Transcripts are user
/// data, so they follow <see cref="PiaPaths.LocalDataDirectory"/> — unlike
/// <see cref="LiveTranscriptionModels.ModelsDirectory"/>, which stays on the real profile.
/// </summary>
public static class MeetingTranscriptPaths
{
    public static string DefaultMeetingFolder =>
        Path.Combine(PiaPaths.LocalDataDirectory, "assistant", "meetings");

    public static string ResolveFolder(AppSettings settings)
        => string.IsNullOrWhiteSpace(settings?.MeetingTranscriptFolder)
            ? DefaultMeetingFolder
            : settings!.MeetingTranscriptFolder!;
}
