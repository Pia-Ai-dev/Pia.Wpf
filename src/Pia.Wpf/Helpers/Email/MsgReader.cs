using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Pia.Helpers.Email;

internal static class MsgReader
{
    private const string SubStreamPrefix = "__substg1.0_";
    private const string PropertiesStreamName = "__properties_version1.0";
    private const string RecipientStoragePrefix = "__recip_version1.0_#";
    private const string AttachmentStoragePrefix = "__attach_version1.0_#";

    private const int RootPropertiesHeader = 32;
    private const int SubStoragePropertiesHeader = 8;
    private const int PropertyRecordSize = 16;

    private const ushort PtLong = 0x0003;
    private const ushort PtString8 = 0x001E;
    private const ushort PtUnicode = 0x001F;
    private const ushort PtSysTime = 0x0040;
    private const ushort PtBinary = 0x0102;

    private const ushort TagSubject = 0x0037;
    private const ushort TagClientSubmitTime = 0x0039;
    private const ushort TagTransportHeaders = 0x007D;
    private const ushort TagSenderName = 0x0C1A;
    private const ushort TagSenderAddressType = 0x0C1E;
    private const ushort TagSenderEmailAddress = 0x0C1F;
    private const ushort TagRecipientType = 0x0C15;
    private const ushort TagDisplayCc = 0x0E03;
    private const ushort TagDisplayTo = 0x0E04;
    private const ushort TagNormalizedSubject = 0x0E1D;
    private const ushort TagBody = 0x1000;
    private const ushort TagHtml = 0x1013;
    private const ushort TagRecipientDisplayName = 0x3001;
    private const ushort TagRecipientAddressType = 0x3002;
    private const ushort TagRecipientEmailAddress = 0x3003;
    private const ushort TagAttachFilename = 0x3704;
    private const ushort TagAttachLongFilename = 0x3707;
    private const ushort TagInternetCodePage = 0x3FDE;
    private const ushort TagRecipientSmtpAddress = 0x39FE;
    private const ushort TagSmtpSenderAddress = 0x5D01;

    private const int RecipientTypeCc = 2;
    private const int RecipientTypeBcc = 3;

    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    public static EmailMessage Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static EmailMessage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            return ReadCore(stream);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new CompoundFileException($"The .msg file is malformed ({ex.GetType().Name}).");
        }
    }

    private static EmailMessage ReadCore(Stream stream)
    {
        var file = CompoundFile.Open(stream);
        var root = file.Root;
        var properties = ReadProperties(file, root, RootPropertiesHeader);
        // PR_INTERNET_CPID is a fixed-width record, not a __substg1.0_ stream; looking for the
        // stream yields nothing and every ANSI property silently decodes as UTF-8.
        var ansi = MimeDecoding.GetEncoding(GetInt32(properties, TagInternetCodePage));

        var to = new List<string>();
        var cc = new List<string>();
        var hasRecipientStorage = ReadRecipients(file, root, ansi, to, cc);
        if (!hasRecipientStorage)
        {
            AddDisplayNames(GetString(file, root, TagDisplayTo, ansi), to);
            AddDisplayNames(GetString(file, root, TagDisplayCc, ansi), cc);
        }

        var body = GetString(file, root, TagBody, ansi);
        var bodyIsFromHtml = false;
        if (string.IsNullOrWhiteSpace(body) && TryReadStream(file, root, TagHtml, PtBinary, out var html))
        {
            body = MimeDecoding.HtmlToText(MimeDecoding.DecodeText(html, ansi));
            bodyIsFromHtml = body.Length > 0;
        }

        var senderType = GetString(file, root, TagSenderAddressType, ansi);
        var sender = SelectAddress(
            senderType,
            GetString(file, root, TagSenderEmailAddress, ansi),
            GetString(file, root, TagSmtpSenderAddress, ansi));

        return new EmailMessage(
            MimeDecoding.NormalizeSubject(
                FirstNonEmpty(GetString(file, root, TagSubject, ansi), GetString(file, root, TagNormalizedSubject, ansi))),
            MimeDecoding.FormatAddress(GetString(file, root, TagSenderName, ansi), sender),
            to,
            cc,
            GetFileTime(properties, TagClientSubmitTime)
                ?? GetTransportHeaderDate(GetString(file, root, TagTransportHeaders, ansi)),
            MimeDecoding.NormalizeBody(body),
            ReadAttachmentNames(file, root, ansi),
            bodyIsFromHtml);
    }

    private static bool ReadRecipients(
        CompoundFile file,
        CompoundFile.DirectoryEntry root,
        Encoding ansi,
        List<string> to,
        List<string> cc)
    {
        var found = false;
        foreach (var storage in SubStorages(file, root, RecipientStoragePrefix))
        {
            found = true;
            var properties = ReadProperties(file, storage, SubStoragePropertiesHeader);
            var address = SelectAddress(
                GetString(file, storage, TagRecipientAddressType, ansi),
                GetString(file, storage, TagRecipientEmailAddress, ansi),
                GetString(file, storage, TagRecipientSmtpAddress, ansi));

            var formatted = MimeDecoding.FormatAddress(GetString(file, storage, TagRecipientDisplayName, ansi), address);
            if (formatted is null)
            {
                continue;
            }

            switch (GetInt32(properties, TagRecipientType))
            {
                case RecipientTypeCc:
                    cc.Add(formatted);
                    break;
                case RecipientTypeBcc:
                    break;
                default:
                    to.Add(formatted);
                    break;
            }
        }

        return found;
    }

    private static List<string> ReadAttachmentNames(CompoundFile file, CompoundFile.DirectoryEntry root, Encoding ansi)
    {
        var names = new List<string>();
        foreach (var storage in SubStorages(file, root, AttachmentStoragePrefix))
        {
            var name = FirstNonEmpty(
                GetString(file, storage, TagAttachLongFilename, ansi),
                GetString(file, storage, TagAttachFilename, ansi));
            if (name is not null)
            {
                names.Add(name.Trim());
            }
        }

        return names;
    }

    private static IEnumerable<CompoundFile.DirectoryEntry> SubStorages(
        CompoundFile file,
        CompoundFile.DirectoryEntry storage,
        string prefix) =>
        file.GetChildren(storage).Values
            .Where(entry => entry.IsStorage && entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal);

    private static Dictionary<ushort, PropertyRecord> ReadProperties(
        CompoundFile file,
        CompoundFile.DirectoryEntry storage,
        int headerSize)
    {
        var properties = new Dictionary<ushort, PropertyRecord>();
        if (!file.GetChildren(storage).TryGetValue(PropertiesStreamName, out var entry) || !entry.IsStream)
        {
            return properties;
        }

        var bytes = file.ReadStream(entry);
        for (var offset = headerSize; offset + PropertyRecordSize <= bytes.Length; offset += PropertyRecordSize)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
            var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2));
            properties[id] = new PropertyRecord(type, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8)));
        }

        return properties;
    }

    private static int GetInt32(Dictionary<ushort, PropertyRecord> properties, ushort tag) =>
        properties.TryGetValue(tag, out var record) && record.Type == PtLong
            ? unchecked((int)(uint)record.Value)
            : 0;

    private static DateTimeOffset? GetFileTime(Dictionary<ushort, PropertyRecord> properties, ushort tag)
    {
        if (!properties.TryGetValue(tag, out var record) || record.Type != PtSysTime)
        {
            return null;
        }

        if (record.Value == 0 || record.Value > (ulong)MaxFileTime)
        {
            return null;
        }

        // FromFileTime would hand back the machine's local time, which renders a different clock
        // reading on every time zone while still comparing equal as an instant.
        return new DateTimeOffset(DateTime.FromFileTimeUtc((long)record.Value), TimeSpan.Zero);
    }

    private static DateTimeOffset? GetTransportHeaderDate(string? headers)
    {
        if (string.IsNullOrEmpty(headers))
        {
            return null;
        }

        var lines = MimeDecoding.UnfoldHeaderLines(headers);
        return MimeDecoding.TryFindHeader(lines, "Date", out var value) && MimeDecoding.TryParseDate(value, out var date)
            ? date
            : null;
    }

    private static string? GetString(CompoundFile file, CompoundFile.DirectoryEntry storage, ushort tag, Encoding ansi)
    {
        if (TryReadStream(file, storage, tag, PtUnicode, out var unicode))
        {
            var even = unicode.Length - (unicode.Length % 2);
            return MimeDecoding.DecodeText(unicode.AsSpan(0, even), Encoding.Unicode).TrimEnd('\0');
        }

        return TryReadStream(file, storage, tag, PtString8, out var ansiBytes)
            ? MimeDecoding.DecodeText(ansiBytes, ansi).TrimEnd('\0')
            : null;
    }

    private static bool TryReadStream(
        CompoundFile file,
        CompoundFile.DirectoryEntry storage,
        ushort tag,
        ushort type,
        out byte[] bytes)
    {
        var name = $"{SubStreamPrefix}{tag:X4}{type:X4}";
        if (file.GetChildren(storage).TryGetValue(name, out var entry) && entry.IsStream)
        {
            bytes = file.ReadStream(entry);
            return bytes.Length > 0;
        }

        bytes = [];
        return false;
    }

    private static void AddDisplayNames(string? displayList, List<string> target)
    {
        if (string.IsNullOrWhiteSpace(displayList))
        {
            return;
        }

        foreach (var part in displayList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            target.Add(part);
        }
    }

    private static string? SelectAddress(string? addressType, string? typedAddress, string? smtpAddress)
    {
        if (!string.IsNullOrWhiteSpace(smtpAddress))
        {
            return smtpAddress;
        }

        if (string.Equals(addressType, "SMTP", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(typedAddress))
        {
            return typedAddress;
        }

        // An Exchange address is an X.500 distinguished name; it is noise and must never be emitted.
        return typedAddress is not null && typedAddress.Contains('@') && !typedAddress.StartsWith('/')
            ? typedAddress
            : null;
    }

    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }

        return string.IsNullOrWhiteSpace(second) ? null : second;
    }

    private readonly record struct PropertyRecord(ushort Type, ulong Value);
}
