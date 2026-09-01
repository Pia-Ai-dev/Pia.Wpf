using System.Buffers.Binary;
using System.IO;
using System.Text;
using Pia.Helpers;
using Pia.Helpers.Email;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>Covers the <c>.msg</c> reader and the mail text the file drop renders.</summary>
public sealed class MsgReaderTests
{
    private const string SampleMail = "sample-mail.msg";
    private const string HtmlOnlyMail = "sample-mail-html-only.msg";
    private const string FlatScanSentinel = "FLAT-SCAN-BUG-SENTINEL";
    private const string DateLine = "Date: 2026/08/31 11:46 +00:00";

    [Fact]
    public void Read_ReadsASmallStreamThroughTheMiniFat()
    {
        var mail = Parse(Fixture(SampleMail));

        Assert.Equal(
            "Hallo Marco,\n\ndies ist der Nachrichtentext mit Umlauten: äöü ß Größe.\n\nViele Grüße\nPia",
            mail.Body);
    }

    [Fact]
    public void Read_ReadsALargeStreamThroughTheNormalFat()
    {
        using var stream = new MemoryStream(Fixture(SampleMail));
        var file = CompoundFile.Open(stream);
        var headers = Encoding.Unicode.GetString(file.ReadStream(file.GetChildren(file.Root)["__substg1.0_007D001F"]));

        Assert.Equal(3049, headers.Length);
        Assert.StartsWith("X-Filler: yyyy", headers, StringComparison.Ordinal);
        Assert.EndsWith("Subject: Testbetreff: Grüße von Pia\r\n", headers, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ScopesPropertiesToTheRootStorage()
    {
        Assert.DoesNotContain(FlatScanSentinel, AllText(Parse(Fixture(SampleMail))), StringComparison.Ordinal);

        // With PR_BODY renamed away the HTML fallback runs, which is when a flat directory scan
        // would reach the __nameid_version1.0 copy of PR_HTML.
        var withoutPlainBody = Parse(PatchText(Fixture(SampleMail), "__substg1.0_1000001F", "__substg1.0_10090102"));

        Assert.DoesNotContain(FlatScanSentinel, AllText(withoutPlainBody), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WalksTheDirectoryChainFromTheFatNotNumDirectorySectors()
    {
        var bytes = Fixture(SampleMail);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x28)));

        var mail = Parse(bytes);

        // Both of these live in directory sectors the header's count does not describe.
        Assert.NotNull(mail.Date);
        Assert.Equal("Marco Altmann <marco@example.test>", Assert.Single(mail.To));
    }

    [Fact]
    public void Read_SkipsUnallocatedDirectoryEntries()
    {
        var bytes = Fixture(SampleMail);
        SetRightSibling(bytes, "__substg1.0_39FE001F", 17);

        using var stream = new MemoryStream(bytes);
        var file = CompoundFile.Open(stream);
        var recipient = file.GetChildren(file.Root)["__recip_version1.0_#00000000"];
        var children = file.GetChildren(recipient);

        Assert.DoesNotContain(children, child => child.Value.ObjectType == 0);
        Assert.False(children.ContainsKey(string.Empty));
    }

    [Fact]
    public void Read_TrimsATrailingNulFromAUnicodeProperty()
    {
        // Renaming the recipient storage is what forces the PR_DISPLAY_TO path, and that is the
        // property carrying the trailing NUL.
        var mail = Parse(PatchText(Fixture(SampleMail), "__recip_version1.0_#00000000", "xxrecip_version1.0_#00000000"));

        Assert.Equal("Marco Altmann", Assert.Single(mail.To));
        Assert.DoesNotContain('\0', AllText(mail));
    }

    [Fact]
    public void Read_UsesTheDirectoryStreamSizeNotThePropsLength()
    {
        var mail = Parse(Fixture(SampleMail));

        // 52 bytes inside a 64-byte mini sector: a sector-sized read appends six padding characters.
        Assert.Equal("Testbetreff: Grüße von Pia", mail.Subject);
    }

    [Fact]
    public void Read_ReadsTheSentDateFromClientSubmitTime()
    {
        var mail = Parse(Fixture(SampleMail));

        Assert.NotNull(mail.Date);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 11, 46, 20, TimeSpan.Zero), mail.Date.Value);
        Assert.Equal(TimeSpan.Zero, mail.Date.Value.Offset);
    }

    [Fact]
    public void Read_FallsBackToTheTransportHeaderDateWhenTheStreamIsAbsent()
    {
        var bytes = Patch(
            Fixture(SampleMail),
            Convert.FromHexString("4000390006000000004EEB563E39DD01"),
            Convert.FromHexString("40003900060000000000000000000000"));
        bytes = PatchText(
            bytes,
            "X-Filler: " + new string('y', 40),
            "Date: Mon, 31 Aug 2026 11:46:20 +0000 (UTC)\r\nyyyyy");

        var mail = Parse(bytes);

        Assert.NotNull(mail.Date);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 11, 46, 20, TimeSpan.Zero), mail.Date.Value);
        Assert.Equal(TimeSpan.Zero, mail.Date.Value.Offset);
    }

    [Fact]
    public void Read_ReadsTheCodepageFromThePropertyRecordNotAStream()
    {
        using var stream = new MemoryStream(Fixture(HtmlOnlyMail));
        var file = CompoundFile.Open(stream);
        Assert.False(file.GetChildren(file.Root).ContainsKey("__substg1.0_3FDE0003"));

        Assert.Contains("Änderungen", Parse(Fixture(HtmlOnlyMail)).Body, StringComparison.Ordinal);

        // 65001 -> 1252 in the property record: the UTF-8 bytes then decode as windows-1252, which
        // can only happen if the record was the source of the codepage.
        var asWindows1252 = Parse(Patch(
            Fixture(HtmlOnlyMail),
            Convert.FromHexString("0300DE3F06000000E9FD000000000000"),
            Convert.FromHexString("0300DE3F06000000E404000000000000")));

        Assert.Contains("\u00C3\u201Enderungen", asWindows1252.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_FallsBackToStrippedPrHtmlWhenPrBodyIsAbsent()
    {
        var mail = Parse(Fixture(HtmlOnlyMail));

        Assert.True(mail.BodyIsFromHtmlFallback);
        Assert.Equal("Hallo Marco,\nHTML&Text mit Änderungen <wichtig>.\n\nZweite Zeile", mail.Body);
        Assert.DoesNotContain("<p>", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("var x=1", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_PrefersRecipientStoragesOverDisplayTo()
    {
        var mail = Parse(Fixture(SampleMail));

        Assert.Equal("Marco Altmann <marco@example.test>", Assert.Single(mail.To));
    }

    [Fact]
    public void Read_ReturnsEmptyRecipientListsWhenThereAreNone()
    {
        var mail = Parse(Fixture(SampleMail));
        Assert.Empty(mail.Cc);
        Assert.Empty(mail.AttachmentNames);

        var htmlOnly = Parse(Fixture(HtmlOnlyMail));
        Assert.Empty(htmlOnly.Cc);
        Assert.Empty(htmlOnly.AttachmentNames);
    }

    [Fact]
    public void Read_DoesNotDecompressRtf()
    {
        var mail = Parse(PatchText(Fixture(SampleMail), "__substg1.0_1000001F", "__substg1.0_10090102"));

        Assert.Equal(string.Empty, mail.Body);
        Assert.False(mail.BodyIsFromHtmlFallback);
    }

    [Fact]
    public void Read_DoesNotHangOnACyclicFatChain()
    {
        var bytes = Fixture(SampleMail);
        var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x1E));
        var fatSector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x4C));
        // The directory chain is 2 -> 3 -> 4 -> 5 -> 6; point 3 back at 2.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)((fatSector + 1) * sectorSize) + (3 * 4)), 2);

        Assert.Throws<CompoundFileException>(() => Parse(bytes));
    }

    [Fact]
    public void Read_RejectsABadSignature()
    {
        var bytes = new byte[1024];
        Array.Fill(bytes, (byte)0x42);

        Assert.Throws<CompoundFileException>(() => Parse(bytes));
    }

    [Fact]
    public void Read_RejectsAnUnsupportedMajorVersion()
    {
        var bytes = Fixture(SampleMail);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x1A), 6);

        Assert.Throws<CompoundFileException>(() => Parse(bytes));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(300)]
    [InlineData(700)]
    public void Read_RaisesAHandledFailureForATruncatedFile(int length)
    {
        var bytes = Fixture(SampleMail)[..length];

        Assert.Throws<CompoundFileException>(() => Parse(bytes));
    }

    [Fact]
    public async Task ReadEmailAsync_ReportsAFailureWithoutThePathForGarbageBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pia-msg-{Guid.NewGuid():N}.msg");
        await File.WriteAllBytesAsync(path, [0x7A, 0x0B, 0x51, 0xC3, 0x19, 0x44, 0x02], TestContext.Current.CancellationToken);
        try
        {
            var result = await DroppedFileReader.ReadEmailAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(DroppedFileReader.ReadStatus.Failed, result.Status);
            Assert.NotNull(result.Error);
            Assert.DoesNotContain(path, result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadEmailAsync_RendersTheDateWithSlashesAndThePiiDetectorLeavesItAlone()
    {
        var result = await DroppedFileReader.ReadEmailAsync(FixturePath(SampleMail), TestContext.Current.CancellationToken);

        Assert.Equal(DroppedFileReader.ReadStatus.Ok, result.Status);
        Assert.Contains(DateLine, result.Text!, StringComparison.Ordinal);
        AssertNoPiiMatchTouches(result.Text!, DateLine);
    }

    [Theory]
    [InlineData(".eml")]
    [InlineData(".msg")]
    public async Task ReadEmailAsync_RefusesAFileOverTheSizeCeiling(string extension)
    {
        var result = await ReadTemporaryMail(extension, new byte[(DroppedFileReader.MaxTextBytes * 8) + 1]);

        Assert.Equal(DroppedFileReader.ReadStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task ReadEmailAsync_OmitsTheFromAndToLabelsWhenOnlyASubjectIsPresent()
    {
        var result = await ReadTemporaryMail(".eml", Encoding.UTF8.GetBytes("Subject: Betreff\r\n\r\n"));

        Assert.Equal(DroppedFileReader.ReadStatus.Ok, result.Status);
        Assert.Equal("Subject: Betreff\n", result.Text);
    }

    [Fact]
    public async Task ReadEmailAsync_RendersABodyOnlyMessageWithNoHeaderLabelsAndNoRule()
    {
        var result = await ReadTemporaryMail(".eml", Encoding.UTF8.GetBytes("\r\nHallo Welt"));

        Assert.Equal(DroppedFileReader.ReadStatus.Ok, result.Status);
        Assert.Equal("Hallo Welt", result.Text);
    }

    [Fact]
    public async Task ReadEmailAsync_RendersNothingForAnEmptyMessage()
    {
        var result = await ReadTemporaryMail(".eml", []);

        Assert.Equal(DroppedFileReader.ReadStatus.Ok, result.Status);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task ReadEmailAsync_KeepsThePiiDetectorOffTheDateWhenTheBodyOpensWithDigits()
    {
        var message = string.Join(
            "\r\n",
            "Date: Mon, 31 Aug 2026 11:46:00 +0000",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "1234567890 Ihre Bestellung");

        var result = await ReadTemporaryMail(".eml", Encoding.UTF8.GetBytes(message));

        Assert.Equal(DroppedFileReader.ReadStatus.Ok, result.Status);
        Assert.Contains(DateLine, result.Text!, StringComparison.Ordinal);
        Assert.Contains("1234567890 Ihre Bestellung", result.Text!, StringComparison.Ordinal);
        AssertNoPiiMatchTouches(result.Text!, DateLine);
    }

    private static async Task<DroppedFileReader.ReadResult> ReadTemporaryMail(string extension, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pia-mail-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        try
        {
            return await DroppedFileReader.ReadEmailAsync(path, TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertNoPiiMatchTouches(string text, string line)
    {
        var start = text.IndexOf(line, StringComparison.Ordinal);
        var end = start + line.Length;
        var matches = new StructuredPiiDetector().DetectPii(text);

        Assert.DoesNotContain(matches, match => match.Start < end && match.Start + match.Length > start);
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    private static byte[] Fixture(string name) => File.ReadAllBytes(FixturePath(name));

    private static EmailMessage Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return MsgReader.Read(stream);
    }

    private static string AllText(EmailMessage mail) =>
        string.Join('\n', new[] { mail.Subject, mail.From, mail.Body }
            .Concat(mail.To)
            .Concat(mail.Cc)
            .Concat(mail.AttachmentNames));

    private static byte[] PatchText(byte[] file, string from, string to) =>
        Patch(file, Encoding.Unicode.GetBytes(from), Encoding.Unicode.GetBytes(to));

    private static byte[] Patch(byte[] file, byte[] from, byte[] to)
    {
        Assert.Equal(from.Length, to.Length);
        var patched = (byte[])file.Clone();
        to.CopyTo(patched.AsSpan(IndexOfOnce(file, from)));
        return patched;
    }

    private static void SetRightSibling(byte[] file, string entryName, uint siblingId) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(IndexOfOnce(file, Encoding.Unicode.GetBytes(entryName)) + 72),
            siblingId);

    // Exactly once, or a later fixture edit would silently relocate the patch and leave the test green.
    private static int IndexOfOnce(byte[] file, byte[] pattern)
    {
        var index = file.AsSpan().IndexOf(pattern);
        Assert.True(index >= 0, "the fixture does not carry the pattern this test patches");
        Assert.Equal(-1, file.AsSpan(index + 1).IndexOf(pattern));
        return index;
    }
}
