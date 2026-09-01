using System.IO;
using System.Text;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Pins the FILEGROUPDESCRIPTORW reader against hand-built buffers. A synthetic fixture cannot prove the
/// struct layout is right — it is generated from the same offsets the parser reads — so it proves the
/// parser does not regress. The layout itself was confirmed against a real Outlook drag, which advertised
/// FileGroupDescriptorW plus FileContents as TYMED_ISTORAGE and yielded the mail's own subject as the name.
/// </summary>
public sealed class FileGroupDescriptorTests
{
    private const int EntrySize = 592;
    private const int SizeHighOffset = 64;
    private const int SizeLowOffset = 68;
    private const int NameOffset = 72;
    private const uint FdFileSize = 0x40;

    [Fact]
    public void Parse_ReadsASingleName()
    {
        var buffer = Build(("Re Angebot.msg", 1234L));

        var items = FileGroupDescriptor.Parse(buffer);

        Assert.Single(items);
        Assert.Equal("Re Angebot.msg", items[0].FileName);
        Assert.Equal(1234L, items[0].Length);
    }

    /// <summary>The one assertion that catches a wrong entry stride: with a bad stride the second name
    /// reads as garbage or as a repeat of the first.</summary>
    [Fact]
    public void Parse_ReadsEveryEntryInOrder()
    {
        var buffer = Build(("first.msg", 10L), ("second.msg", 20L), ("third.eml", 30L));

        var items = FileGroupDescriptor.Parse(buffer);

        Assert.Equal(["first.msg", "second.msg", "third.eml"], items.Select(i => i.FileName));
        Assert.Equal([10L, 20L, 30L], items.Select(i => i.Length));
    }

    [Fact]
    public void Parse_KeepsAUnicodeSubject()
    {
        var buffer = Build(("Wöchentlicher Aktivitätsbericht.msg", 1L));

        Assert.Equal("Wöchentlicher Aktivitätsbericht.msg", FileGroupDescriptor.Parse(buffer)[0].FileName);
    }

    /// <summary>cFileName is a fixed WCHAR[260]; everything after the first NUL is stale padding.</summary>
    [Fact]
    public void Parse_StopsTheNameAtTheFirstNul()
    {
        var buffer = Build(("mail.msg", 1L));
        // Stale bytes past the terminator, as a reused buffer would carry.
        Encoding.Unicode.GetBytes("XXXX").CopyTo(buffer, 4 + NameOffset + (Encoding.Unicode.GetByteCount("mail.msg") + 2));

        Assert.Equal("mail.msg", FileGroupDescriptor.Parse(buffer)[0].FileName);
    }

    [Fact]
    public void Parse_ReadsANameThatFillsTheWholeField()
    {
        var name = new string('a', 255) + ".msg";
        var buffer = Build((name, 1L));

        Assert.Equal(name, FileGroupDescriptor.Parse(buffer)[0].FileName);
    }

    [Fact]
    public void Parse_ReturnsNothingForTheSizeFlagBeingClear()
    {
        var buffer = Build(("mail.msg", null));

        Assert.Null(FileGroupDescriptor.Parse(buffer)[0].Length);
    }

    [Fact]
    public void Parse_ReadsASizeAboveFourGigabytes()
    {
        var buffer = Build(("huge.msg", 5_000_000_000L));

        Assert.Equal(5_000_000_000L, FileGroupDescriptor.Parse(buffer)[0].Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Parse_ReturnsEmptyForABufferTooShortToHoldTheCount(int bytes)
    {
        Assert.Empty(FileGroupDescriptor.Parse(new byte[bytes]));
    }

    [Fact]
    public void Parse_ReturnsEmptyForAZeroCount()
    {
        Assert.Empty(FileGroupDescriptor.Parse(new byte[4 + EntrySize]));
    }

    /// <summary>The count comes from another process, so it is not trusted to match the buffer.</summary>
    [Fact]
    public void Parse_StopsAtTheEndOfATruncatedBuffer()
    {
        var buffer = Build(("first.msg", 1L), ("second.msg", 2L));
        var truncated = buffer[..(4 + EntrySize + 100)];

        Assert.Equal(["first.msg"], FileGroupDescriptor.Parse(truncated).Select(i => i.FileName));
    }

    [Fact]
    public void Parse_RefusesAnAbsurdCount()
    {
        var buffer = new byte[4 + EntrySize];
        BitConverter.GetBytes(100_000u).CopyTo(buffer, 0);

        Assert.Empty(FileGroupDescriptor.Parse(buffer));
    }

    [Fact]
    public void Parse_SkipsAnEntryWithNoName()
    {
        var buffer = Build(("", 1L), ("real.msg", 2L));

        Assert.Equal(["real.msg"], FileGroupDescriptor.Parse(buffer).Select(i => i.FileName));
    }

    [Theory]
    [InlineData("Re Angebot.msg", "Re Angebot.msg")]
    [InlineData("Fwd: Rechnung.msg", "Fwd_ Rechnung.msg")]
    [InlineData("a/b\\c.msg", "a_b_c.msg")]
    [InlineData("  spaced.msg  ", "spaced.msg")]
    [InlineData("trailing dots...", "trailing dots")]
    public void ToSafeFileName_KeepsALeafName(string input, string expected)
    {
        Assert.Equal(expected, FileGroupDescriptor.ToSafeFileName(input));
    }

    /// <summary>The name arrives from another process and is used to build a path, so a traversal attempt
    /// must not survive as one. Separators are neutered, which is what leaves a leaf behind.</summary>
    [Theory]
    [InlineData("..\\..\\evil.msg", ".._.._evil.msg")]
    [InlineData("../../evil.msg", ".._.._evil.msg")]
    [InlineData("C:\\Windows\\System32\\evil.msg", "C__Windows_System32_evil.msg")]
    public void ToSafeFileName_NeutersATraversal(string input, string expected)
    {
        var safe = FileGroupDescriptor.ToSafeFileName(input);

        Assert.Equal(expected, safe);
        Assert.Equal(safe, Path.GetFileName(safe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void ToSafeFileName_RefusesANameThatIsNotOne(string input)
    {
        Assert.Null(FileGroupDescriptor.ToSafeFileName(input));
    }

    /// <summary>A mail subject can be far longer than a sane file name; the extension is what has to survive,
    /// because it is the only thing the accepted-extension check can read.</summary>
    [Fact]
    public void ToSafeFileName_CapsALongSubjectButKeepsTheExtension()
    {
        var safe = FileGroupDescriptor.ToSafeFileName(new string('x', 400) + ".msg");

        Assert.NotNull(safe);
        Assert.EndsWith(".msg", safe, StringComparison.Ordinal);
        Assert.True(safe.Length <= 84, $"name was {safe.Length} chars: {safe}");
    }

    private static byte[] Build(params (string Name, long? Length)[] entries)
    {
        var buffer = new byte[4 + (entries.Length * EntrySize)];
        BitConverter.GetBytes((uint)entries.Length).CopyTo(buffer, 0);

        for (var i = 0; i < entries.Length; i++)
        {
            var start = 4 + (i * EntrySize);
            var (name, length) = entries[i];

            if (length is { } value)
            {
                BitConverter.GetBytes(FdFileSize).CopyTo(buffer, start);
                BitConverter.GetBytes((uint)(value >> 32)).CopyTo(buffer, start + SizeHighOffset);
                BitConverter.GetBytes((uint)(value & 0xFFFFFFFF)).CopyTo(buffer, start + SizeLowOffset);
            }

            Encoding.Unicode.GetBytes(name).CopyTo(buffer, start + NameOffset);
        }

        return buffer;
    }
}
