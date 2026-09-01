using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Pia.Helpers.Email;

internal sealed class CompoundFileException(string message) : Exception(message);

internal sealed class CompoundFile
{
    internal const int StorageObjectType = 1;
    internal const int StreamObjectType = 2;
    internal const int RootObjectType = 5;

    private const ulong Signature = 0xE11AB1A1E011CFD0;
    private const uint FirstSentinelSector = 0xFFFFFFFA;
    private const int DirectoryEntrySize = 128;
    private const int HeaderDifatSlots = 109;
    private const int HeaderSize = 512;
    private const int DefaultMiniStreamCutoff = 4096;
    private const long MaxStreamBytes = 256L * 1024 * 1024;

    private readonly Stream _stream;
    private readonly long _length;
    private readonly int _majorVersion;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly int _sectorCount;
    private readonly int _miniStreamCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<DirectoryEntry> _entries;
    private readonly Dictionary<int, IReadOnlyDictionary<string, DirectoryEntry>> _children = [];
    private byte[]? _miniStream;

    private CompoundFile(Stream stream)
    {
        _stream = stream;
        _length = stream.Length;
        if (_length < HeaderSize)
        {
            throw new CompoundFileException("The file is shorter than a compound-file header.");
        }

        var header = new byte[HeaderSize];
        ReadAt(0, header);
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != Signature)
        {
            throw new CompoundFileException("The file does not carry the compound-file signature.");
        }

        _majorVersion = ReadUInt16(header, 0x1A);
        var sectorShift = ReadUInt16(header, 0x1E);
        var miniSectorShift = ReadUInt16(header, 0x20);
        if (_majorVersion is not (3 or 4) || sectorShift != (_majorVersion == 3 ? 9 : 12))
        {
            throw new CompoundFileException($"Unsupported compound-file version {_majorVersion}, sector shift {sectorShift}.");
        }

        if (miniSectorShift < 2 || miniSectorShift >= sectorShift)
        {
            throw new CompoundFileException($"Unsupported mini-sector shift {miniSectorShift}.");
        }

        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniSectorShift;
        _sectorCount = (int)Math.Max(0, (_length / _sectorSize) - 1);

        // A zero or absurd cutoff would route every small stream through the normal FAT, which
        // returns plausible-looking garbage instead of failing.
        var declaredCutoff = ReadUInt32(header, 0x38);
        _miniStreamCutoff = declaredCutoff is > 0 and <= 1 << 20 ? (int)declaredCutoff : DefaultMiniStreamCutoff;

        _fat = ReadFat(header);
        _miniFat = ReadSectorTable(ReadUInt32(header, 0x3C));
        _entries = ReadDirectory(ReadUInt32(header, 0x30));

        if (_entries.Count == 0 || _entries[0].ObjectType != RootObjectType)
        {
            throw new CompoundFileException("The compound file has no root directory entry.");
        }
    }

    internal DirectoryEntry Root => _entries[0];

    internal static CompoundFile Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new CompoundFileException("A compound file can only be read from a seekable stream.");
        }

        return new CompoundFile(stream);
    }

    internal IReadOnlyDictionary<string, DirectoryEntry> GetChildren(DirectoryEntry storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (_children.TryGetValue(storage.Id, out var cached))
        {
            return cached;
        }

        var map = new Dictionary<string, DirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<uint>();
        var pending = new Stack<uint>();
        pending.Push(storage.ChildId);

        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id >= (uint)_entries.Count || !visited.Add(id))
            {
                continue;
            }

            var entry = _entries[(int)id];
            if (entry.ObjectType == 0)
            {
                continue;
            }

            // The indexer, not Add: a malformed file with two same-named children must not throw.
            map[entry.Name] = entry;
            pending.Push(entry.LeftSiblingId);
            pending.Push(entry.RightSiblingId);
        }

        _children[storage.Id] = map;
        return map;
    }

    internal byte[] ReadStream(DirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Size <= 0)
        {
            return [];
        }

        if (entry.Size > MaxStreamBytes || entry.Size > _length)
        {
            throw new CompoundFileException("A directory entry declares a stream larger than the file.");
        }

        // The root entry's own stream IS the mini stream, so it is always allocated in the normal
        // FAT however small it is; dispatching it on the cutoff would read it out of itself.
        return entry.Size < _miniStreamCutoff && entry.ObjectType != RootObjectType
            ? ReadMiniChain(entry.StartSector, (int)entry.Size)
            : ReadFatChain(entry.StartSector, (int)entry.Size);
    }

    private uint[] ReadFat(byte[] header)
    {
        var fatSectors = new List<uint>();
        for (var i = 0; i < HeaderDifatSlots; i++)
        {
            var sector = ReadUInt32(header, 0x4C + (i * 4));
            if (sector < FirstSentinelSector)
            {
                fatSectors.Add(sector);
            }
        }

        var slotsPerSector = (_sectorSize / 4) - 1;
        var buffer = new byte[_sectorSize];
        var visited = new HashSet<uint>();
        var next = ReadUInt32(header, 0x44);
        while (next < FirstSentinelSector && visited.Add(next))
        {
            ReadSector(next, buffer);
            for (var i = 0; i < slotsPerSector; i++)
            {
                var sector = ReadUInt32(buffer, i * 4);
                if (sector < FirstSentinelSector)
                {
                    fatSectors.Add(sector);
                }
            }

            next = ReadUInt32(buffer, slotsPerSector * 4);
        }

        if (fatSectors.Count > _sectorCount + 1)
        {
            throw new CompoundFileException("The DIFAT lists more allocation-table sectors than the file holds.");
        }

        var entriesPerSector = _sectorSize / 4;
        var fat = new uint[fatSectors.Count * entriesPerSector];
        for (var i = 0; i < fatSectors.Count; i++)
        {
            ReadSector(fatSectors[i], buffer);
            for (var j = 0; j < entriesPerSector; j++)
            {
                fat[(i * entriesPerSector) + j] = ReadUInt32(buffer, j * 4);
            }
        }

        return fat;
    }

    private uint[] ReadSectorTable(uint start)
    {
        var chain = CollectChain(start);
        var entriesPerSector = _sectorSize / 4;
        var table = new uint[chain.Count * entriesPerSector];
        var buffer = new byte[_sectorSize];
        for (var i = 0; i < chain.Count; i++)
        {
            ReadSector(chain[i], buffer);
            for (var j = 0; j < entriesPerSector; j++)
            {
                table[(i * entriesPerSector) + j] = ReadUInt32(buffer, j * 4);
            }
        }

        return table;
    }

    private List<DirectoryEntry> ReadDirectory(uint start)
    {
        // The header's directory-sector count is always 0 in version 3, so the FAT chain is the
        // only way to find every entry.
        var chain = CollectChain(start);
        var entries = new List<DirectoryEntry>(chain.Count * (_sectorSize / DirectoryEntrySize));
        var buffer = new byte[_sectorSize];
        foreach (var sector in chain)
        {
            ReadSector(sector, buffer);
            for (var offset = 0; offset + DirectoryEntrySize <= _sectorSize; offset += DirectoryEntrySize)
            {
                entries.Add(ParseDirectoryEntry(entries.Count, buffer, offset));
            }
        }

        return entries;
    }

    private DirectoryEntry ParseDirectoryEntry(int id, byte[] buffer, int offset)
    {
        var nameLength = ReadUInt16(buffer, offset + 64);
        var characters = nameLength is >= 2 and <= 64 ? (nameLength - 2) / 2 : 0;
        var name = characters == 0
            ? string.Empty
            : Encoding.Unicode.GetString(buffer, offset, characters * 2).TrimEnd('\0');

        var sizeLow = ReadUInt32(buffer, offset + 120);
        var sizeHigh = ReadUInt32(buffer, offset + 124);
        // Version 3 writers leave the high dword undefined rather than zero, so mask it away.
        var size = _majorVersion == 3 ? sizeLow : sizeLow | ((long)sizeHigh << 32);

        return new DirectoryEntry(
            id,
            name,
            buffer[offset + 66],
            ReadUInt32(buffer, offset + 68),
            ReadUInt32(buffer, offset + 72),
            ReadUInt32(buffer, offset + 76),
            ReadUInt32(buffer, offset + 116),
            size);
    }

    private List<uint> CollectChain(uint start)
    {
        var chain = new List<uint>();
        var visited = new HashSet<uint>();
        var sector = start;
        while (sector < FirstSentinelSector)
        {
            if (sector >= (uint)_fat.Length)
            {
                throw new CompoundFileException("A sector chain leaves the allocation table.");
            }

            if (!visited.Add(sector))
            {
                throw new CompoundFileException("A sector chain is cyclic.");
            }

            if (chain.Count > _sectorCount)
            {
                throw new CompoundFileException("A sector chain is longer than the file.");
            }

            chain.Add(sector);
            sector = _fat[sector];
        }

        return chain;
    }

    private byte[] ReadFatChain(uint start, int size)
    {
        var result = new byte[size];
        var buffer = new byte[_sectorSize];
        var sector = start;
        var written = 0;
        var steps = 0;
        while (written < result.Length)
        {
            if (sector >= (uint)_fat.Length)
            {
                throw new CompoundFileException("A stream chain leaves the allocation table.");
            }

            if (++steps > _fat.Length)
            {
                throw new CompoundFileException("A stream chain is cyclic.");
            }

            ReadSector(sector, buffer);
            var take = Math.Min(_sectorSize, result.Length - written);
            buffer.AsSpan(0, take).CopyTo(result.AsSpan(written));
            written += take;
            sector = _fat[sector];
        }

        return result;
    }

    private byte[] ReadMiniChain(uint start, int size)
    {
        var mini = MiniStream();
        var result = new byte[size];
        var sector = start;
        var written = 0;
        var steps = 0;
        while (written < result.Length)
        {
            if (sector >= (uint)_miniFat.Length)
            {
                throw new CompoundFileException("A mini-stream chain leaves the mini allocation table.");
            }

            if (++steps > _miniFat.Length)
            {
                throw new CompoundFileException("A mini-stream chain is cyclic.");
            }

            var offset = (long)sector * _miniSectorSize;
            if (offset + _miniSectorSize > mini.Length)
            {
                throw new CompoundFileException("A mini-stream sector lies past the end of the mini stream.");
            }

            var take = Math.Min(_miniSectorSize, result.Length - written);
            mini.AsSpan((int)offset, take).CopyTo(result.AsSpan(written));
            written += take;
            sector = _miniFat[sector];
        }

        return result;
    }

    private byte[] MiniStream()
    {
        if (_miniStream is not null)
        {
            return _miniStream;
        }

        var root = Root;
        if (root.Size <= 0 || root.Size > MaxStreamBytes || root.Size > _length)
        {
            throw new CompoundFileException("The root entry declares an unusable mini-stream size.");
        }

        // The declared size is shorter than the chain: its last sector is padding.
        return _miniStream = ReadFatChain(root.StartSector, (int)root.Size);
    }

    private void ReadSector(uint sector, byte[] buffer)
    {
        var offset = ((long)sector + 1) * _sectorSize;
        if (sector >= (uint)_sectorCount || offset + _sectorSize > _length)
        {
            throw new CompoundFileException("A sector lies past the end of the file.");
        }

        ReadAt(offset, buffer.AsSpan(0, _sectorSize));
    }

    private void ReadAt(long offset, Span<byte> buffer)
    {
        _stream.Position = offset;
        _stream.ReadExactly(buffer);
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));

    internal sealed record DirectoryEntry(
        int Id,
        string Name,
        int ObjectType,
        uint LeftSiblingId,
        uint RightSiblingId,
        uint ChildId,
        uint StartSector,
        long Size)
    {
        internal bool IsStream => ObjectType == StreamObjectType;

        internal bool IsStorage => ObjectType == StorageObjectType;
    }
}
