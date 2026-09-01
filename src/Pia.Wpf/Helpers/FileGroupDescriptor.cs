using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace Pia.Helpers;

/// <summary>One item of a CFSTR_FILEDESCRIPTORW group: the name the source would give the file on disk,
/// and the size it declared, when it declared one.</summary>
public sealed record FileGroupDescriptorEntry(string FileName, long? Length);

/// <summary>
/// Reads the FILEGROUPDESCRIPTORW buffer a virtual-file drag carries. The names in it are the only place
/// a dragged mail has an extension — it has no path until we materialise one.
/// </summary>
public static class FileGroupDescriptor
{
    /// <summary>sizeof(FILEDESCRIPTORW): the fixed header plus <c>WCHAR cFileName[MAX_PATH]</c>.</summary>
    internal const int EntrySize = 592;

    private const int CountSize = 4;
    private const int SizeHighOffset = 64;
    private const int SizeLowOffset = 68;
    private const int FileNameOffset = 72;
    private const int FileNameChars = 260;

    private const uint FD_FILESIZE = 0x00000040;

    // A drag of more items than this is malformed or hostile; the count comes from another process.
    private const int MaxItems = 512;

    private static readonly SearchValues<char> InvalidNameChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    public static IReadOnlyList<FileGroupDescriptorEntry> Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < CountSize) return [];

        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (count == 0 || count > MaxItems) return [];

        var entries = new List<FileGroupDescriptorEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var start = CountSize + (i * EntrySize);
            if (start + EntrySize > buffer.Length) break;

            var entry = buffer.Slice(start, EntrySize);
            var name = ReadFileName(entry);
            if (name.Length == 0) continue;

            long? length = null;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(entry) & FD_FILESIZE) != 0)
            {
                length = ((long)BinaryPrimitives.ReadUInt32LittleEndian(entry[SizeHighOffset..]) << 32)
                    | BinaryPrimitives.ReadUInt32LittleEndian(entry[SizeLowOffset..]);
            }

            entries.Add(new FileGroupDescriptorEntry(name, length));
        }

        return entries;
    }

    /// <summary>
    /// Turns a descriptor name into a bare file name safe to append to our temp directory. It arrives from
    /// another process, so a name that is anything but a leaf — <c>..\..\x.msg</c> — must not become a path.
    /// </summary>
    public static string? ToSafeFileName(string descriptorName)
    {
        if (string.IsNullOrWhiteSpace(descriptorName)) return null;

        var cleaned = string.Create(descriptorName.Length, descriptorName, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = InvalidNameChars.Contains(source[i]) ? '_' : source[i];
        }).Trim().TrimEnd('.');

        if (cleaned.Length == 0) return null;
        if (cleaned is "." or "..") return null;

        // Names arrive from a mail subject, so they can be far longer than the extension-bearing tail we
        // need; keep the extension and enough of the stem to stay recognisable in the chip.
        var extension = Path.GetExtension(cleaned);
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (stem.Length > 80) stem = stem[..80].TrimEnd();
        if (stem.Length == 0) stem = "item";

        var result = stem + extension;
        return Path.GetFileName(result) == result ? result : null;
    }

    private static string ReadFileName(ReadOnlySpan<byte> entry)
    {
        var chars = MemoryMarshal.Cast<byte, char>(entry.Slice(FileNameOffset, FileNameChars * 2));
        var end = chars.IndexOf((char)0);
        if (end >= 0) chars = chars[..end];
        return chars.ToString().Trim();
    }
}
