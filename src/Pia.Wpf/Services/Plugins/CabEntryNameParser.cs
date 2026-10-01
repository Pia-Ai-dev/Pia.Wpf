using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Pia.Services.Plugins;

/// <summary>Reads a cabinet's file names (MS-CAB CFFILE records) so a traversal entry is refused before expand.exe sees it.</summary>
public static class CabEntryNameParser
{
    private const int HeaderLength = 36;
    private const int FileRecordFixedLength = 16;
    private const int MaxNameLength = 256;
    private const ushort NameIsUtf8 = 0x80;

    public static IReadOnlyList<string> ReadNames(ReadOnlySpan<byte> cab)
    {
        if (cab.Length < HeaderLength || !cab[..4].SequenceEqual("MSCF"u8))
            throw new InvalidDataException("Not a cabinet file.");

        var offset = BinaryPrimitives.ReadUInt32LittleEndian(cab[16..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(cab[28..]);
        var names = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            if (offset > (uint)(cab.Length - FileRecordFixedLength))
                throw new InvalidDataException("Cabinet file table is truncated.");

            var record = cab[(int)offset..];
            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(record[14..]);
            var nameBytes = record[FileRecordFixedLength..];
            var terminator = nameBytes[..Math.Min(nameBytes.Length, MaxNameLength + 1)].IndexOf((byte)0);
            if (terminator < 0)
                throw new InvalidDataException("Cabinet file name is unterminated.");

            var raw = nameBytes[..terminator];
            names.Add((attributes & NameIsUtf8) != 0 ? Encoding.UTF8.GetString(raw) : Encoding.Latin1.GetString(raw));
            offset += (uint)(FileRecordFixedLength + terminator + 1);
        }

        return names;
    }

    public static bool IsContained(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains(':') || Path.IsPathRooted(name))
            return false;

        return !name.Split('\\', '/').Any(segment => segment == "..");
    }

    /// <summary>The first entry that would land outside the extraction folder; a malformed cabinet counts as unsafe.</summary>
    public static string? FindUnsafeEntry(ReadOnlySpan<byte> cab)
    {
        IReadOnlyList<string> names;
        try
        {
            names = ReadNames(cab);
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }

        return names.FirstOrDefault(name => !IsContained(name));
    }
}
