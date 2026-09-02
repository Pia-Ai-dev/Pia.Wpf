using System.IO;
using Pia.Helpers;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Pulls the Teams join link out of a dropped meeting invite. Mail goes through the same readers the
/// chat attachments use, so the size ceilings and the deliberately path-free error text come along
/// unchanged; .ics is plain text and needs no parser.
/// </summary>
public static class MeetingInviteReader
{
    public enum ReadStatus
    {
        Ok,
        /// <summary>Read fine, but carried no joinable Teams link.</summary>
        NoUrl,
        /// <summary>Too large, or the parse failed.</summary>
        Unreadable,
    }

    public readonly record struct ReadResult(ReadStatus Status, string? Url)
    {
        public static ReadResult Found(string url) => new(ReadStatus.Ok, url);
        public static readonly ReadResult NoUrl = new(ReadStatus.NoUrl, null);
        public static readonly ReadResult Unreadable = new(ReadStatus.Unreadable, null);
    }

    public static async Task<ReadResult> ReadAsync(string path, CancellationToken ct)
    {
        // .ics is deliberately not in DroppedFileReader's extension map: adding it there would make it
        // a chat-attachable text kind everywhere, which is a wider change than this needs.
        var read = Path.GetExtension(path).Equals(".ics", StringComparison.OrdinalIgnoreCase)
            ? await DroppedFileReader.ReadTextAsync(path, ct).ConfigureAwait(false)
            : await DroppedFileReader.ReadEmailAsync(path, ct).ConfigureAwait(false);

        if (read.Status != DroppedFileReader.ReadStatus.Ok) return ReadResult.Unreadable;

        return TeamsMeetingUrl.ExtractFromText(read.Text) is { } url
            ? ReadResult.Found(url)
            : ReadResult.NoUrl;
    }
}
