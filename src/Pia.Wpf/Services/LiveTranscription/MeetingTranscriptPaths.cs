using System.IO;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Resolves the on-disk folder where meeting transcripts (Markdown exports) are written.
/// User-authored content lives under roaming <c>%APPDATA%\Pia</c>; cached models continue to
/// live under <c>%LOCALAPPDATA%</c> via <see cref="LiveTranscriptionModels.ModelsDirectory"/>.
/// </summary>
public static class MeetingTranscriptPaths
{
    public static string DefaultMeetingFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pia", "assistant", "meetings");

    public static string ResolveFolder(AppSettings settings)
        => string.IsNullOrWhiteSpace(settings?.MeetingTranscriptFolder)
            ? DefaultMeetingFolder
            : settings!.MeetingTranscriptFolder!;
}
