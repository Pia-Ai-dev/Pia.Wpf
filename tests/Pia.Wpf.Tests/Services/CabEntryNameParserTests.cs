using System.Buffers.Binary;
using System.IO;
using System.Text;
using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services;

public sealed class CabEntryNameParserTests
{
    /// <summary>A header plus CFFILE records; folders and data blocks are irrelevant to the name table.</summary>
    private static byte[] Cabinet(ushort flags, int reserveBytes, params (string Name, bool Utf8)[] files)
    {
        using var stream = new MemoryStream();
        var header = new byte[36];
        "MSCF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)files.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), flags);
        stream.Write(header);

        if ((flags & 0x0004) != 0)
        {
            var reserve = new byte[4 + reserveBytes];
            BinaryPrimitives.WriteUInt16LittleEndian(reserve, (ushort)reserveBytes);
            stream.Write(reserve);
        }

        if ((flags & 0x0001) != 0)
            stream.Write("prev.cab\0disk1\0"u8);

        stream.Write(new byte[8]); // one CFFOLDER record
        var coffFiles = (uint)stream.Position;

        foreach (var (name, utf8) in files)
        {
            var record = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 3);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), (ushort)(utf8 ? 0xA0 : 0x20));
            stream.Write(record);
            stream.Write(utf8 ? Encoding.UTF8.GetBytes(name) : Encoding.Latin1.GetBytes(name));
            stream.WriteByte(0);
        }

        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), coffFiles);
        return bytes;
    }

    private static byte[] Cabinet(params string[] names) => Cabinet(0, 0, [.. names.Select(n => (n, false))]);

    [Fact]
    public void Reads_every_file_name_in_order()
        => Assert.Equal(["server.exe", @"lib\helper.dll", "README.txt"],
            CabEntryNameParser.ReadNames(Cabinet("server.exe", @"lib\helper.dll", "README.txt")));

    [Fact]
    public void Honours_the_reserve_and_previous_cabinet_header_fields()
        => Assert.Equal(["a.txt"],
            CabEntryNameParser.ReadNames(Cabinet(0x0005, reserveBytes: 6, ("a.txt", false))));

    [Fact]
    public void Decodes_utf8_flagged_names()
        => Assert.Equal(["über.txt"], CabEntryNameParser.ReadNames(Cabinet(0, 0, ("über.txt", true))));

    [Fact]
    public void A_cabinet_of_contained_names_is_safe()
        => Assert.Null(CabEntryNameParser.FindUnsafeEntry(Cabinet("server.exe", @"lib\sub\helper.dll", "a..b.txt")));

    [Theory]
    [InlineData(@"..\evil.exe")]
    [InlineData(@"lib\..\..\evil.exe")]
    [InlineData("../evil.exe")]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\Users\Public\evil.exe")]
    [InlineData("file.txt:hidden")]
    [InlineData("")]
    public void An_escaping_name_is_reported(string name)
        => Assert.Equal(name, CabEntryNameParser.FindUnsafeEntry(Cabinet("ok.txt", name)));

    [Fact]
    public void A_malformed_cabinet_counts_as_unsafe()
    {
        var truncated = Cabinet("server.exe")[..40];

        Assert.NotNull(CabEntryNameParser.FindUnsafeEntry(truncated));
        Assert.NotNull(CabEntryNameParser.FindUnsafeEntry("PK\x03\x04 not a cab at all, long enough"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => CabEntryNameParser.ReadNames(truncated));
    }
}
