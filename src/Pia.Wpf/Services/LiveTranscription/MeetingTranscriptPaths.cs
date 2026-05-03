using System.IO;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Resolves the on-disk folder where meeting transcripts (Markdown exports) are written.
/// Mirrors the convention used by <see cref="LiveTranscriptionModels.ModelsDirectory"/>:
/// everything lives under <c>%LOCALAPPDATA%\Pia</c>.
/// </summary>
public static class MeetingTranscriptPaths
{
    public static string DefaultMeetingFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "assistant", "meetings");

    public static string ResolveFolder(AppSettings settings)
        => string.IsNullOrWhiteSpace(settings?.MeetingTranscriptFolder)
            ? DefaultMeetingFolder
            : settings!.MeetingTranscriptFolder!;
}
