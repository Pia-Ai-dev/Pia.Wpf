using System.Globalization;
using System.IO;
using System.Text;
using Pia.Helpers.Email;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// RFC 5322 is plain text, so every fixture here is built in the test and carries the traps the
/// real sample mails were measured to contain.
/// </summary>
public sealed class EmlReaderTests
{
    private static readonly string[] FoldedSubjectMessage =
    [
        "Subject: =?UTF-8?Q?Maik_Behring_hat_Folgendes_gepostet:_Es_ist_23:17_Uhr_und_?=",
        " =?UTF-8?Q?ich_h=C3=A4nge_gerade_noch_=C3=BCber_Be?=",
        " =?UTF-8?Q?nchmarks.=0A=0A=0ADer_Grund:_F=C3=BCr=E2=80=A6_=F0=9F=92=A1?=",
        "Content-Type: text/plain; charset=UTF-8",
        "",
        "Rumpf",
    ];

    private const string DecodedFoldedSubject =
        "Maik Behring hat Folgendes gepostet: Es ist 23:17 Uhr und ich hänge gerade noch "
        + "über Benchmarks. Der Grund: Für… \U0001F4A1";

    [Fact]
    public void Read_UnfoldsAHeaderFoldedWithSpaceAndWithTab()
    {
        var mail = Parse(
            "Subject: alpha",
            " beta",
            "To: first@example.test,",
            "\tsecond@example.test",
            "",
            "Rumpf");

        Assert.Equal("alpha beta", mail.Subject);
        Assert.Equal(new[] { "first@example.test", "second@example.test" }, mail.To);
    }

    [Fact]
    public void Read_DecodesQEncodedSubjectAcrossThreeFoldedWords()
    {
        var mail = Parse(FoldedSubjectMessage);

        Assert.Equal(DecodedFoldedSubject, mail.Subject);
        Assert.Contains("und ich", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("Benchmarks", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DropsWhitespaceBetweenAdjacentEncodedWords()
    {
        var mail = Parse(FoldedSubjectMessage);

        Assert.DoesNotContain("und  ich", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("Be nchmarks", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DecodesBEncodedSubject()
    {
        var mail = Parse("Subject: =?UTF-8?B?SGFsbG8gV2VsdA==?=", "", "Rumpf");

        Assert.Equal("Hallo Welt", mail.Subject);
    }

    [Fact]
    public void Read_EmitsMalformedEncodedWordVerbatim()
    {
        var mail = Parse("Subject: hello =?UTF-8?B?%%%?= world", "", "Rumpf");

        Assert.Equal("hello =?UTF-8?B?%%%?= world", mail.Subject);
    }

    [Fact]
    public void Read_CollapsesWhitespaceAndNewlinesInSubject()
    {
        var mail = Parse("Subject: =?UTF-8?Q?line1=0Aline2?=", "", "Rumpf");

        Assert.Equal("line1 line2", mail.Subject);
        Assert.DoesNotContain('\n', mail.Subject!);
    }

    [Fact]
    public void Read_JoinsQuotedPrintableSoftLineBreaks()
    {
        var mail = Parse(
            "Content-Type: text/plain; charset=UTF-8",
            "Content-Transfer-Encoding: quoted-printable",
            "",
            "ich h=C3=A4nge =",
            "gerade noch");

        Assert.Equal("ich hänge gerade noch", mail.Body);
    }

    [Fact]
    public void Read_DecodesMultiByteUtf8SplitAcrossEscapes()
    {
        var mail = Parse(
            "Content-Type: text/plain; charset=UTF-8",
            "Content-Transfer-Encoding: quoted-printable",
            "",
            "Gr=C3=BC=C3=9Fe");

        Assert.Equal("Grüße", mail.Body);
    }

    [Fact]
    public void Read_ParsesContentTypeParameterWithNoSpaceAfterSemicolon()
    {
        var mail = Parse(
            "Content-Type: text/plain;charset=iso-8859-1",
            "Content-Transfer-Encoding: quoted-printable",
            "",
            "Gr=E4=DFe");

        // Single-byte Latin1, so a charset the parser failed to see would leave replacement characters.
        Assert.Equal("Gräße", mail.Body);
    }

    [Fact]
    public void Read_ParsesBoundaryParameterPrecededByTab()
    {
        var mail = Parse(
            "MIME-Version: 1.0",
            "Content-Type: multipart/alternative;",
            "\tboundary=\"B1\"",
            "",
            "--B1",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Der Text",
            "--B1--");

        Assert.Equal("Der Text", mail.Body);
    }

    [Fact]
    public void Read_SplitsMultipartWhenTheBoundaryCarriesRaw8BitBytes()
    {
        var mail = Parse(
            "Content-Type: multipart/alternative; boundary=\"B-ü-1\"",
            "",
            "--B-ü-1",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Der Text",
            "--B-ü-1--");

        Assert.Equal("Der Text", mail.Body);
    }

    [Fact]
    public void Read_KeepsABase64AttachmentOutOfTheBodyBehindARaw8BitBoundary()
    {
        var mail = Parse(
            "Content-Type: multipart/mixed; boundary=\"B-ü-1\"",
            "",
            "--B-ü-1",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Haupttext",
            "--B-ü-1",
            "Content-Type: application/pdf; name=\"bericht.pdf\"",
            "Content-Transfer-Encoding: base64",
            "",
            "QUJD",
            "--B-ü-1--");

        Assert.Equal("Haupttext", mail.Body);
        Assert.Equal(new[] { "bericht.pdf" }, mail.AttachmentNames);
        Assert.DoesNotContain("QUJD", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RecoversARawUtf8AttachmentFilename()
    {
        var mail = Parse(
            "Content-Type: multipart/mixed; boundary=\"MIX\"",
            "",
            "--MIX",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Haupttext",
            "--MIX",
            "Content-Type: application/pdf",
            "Content-Disposition: attachment; filename=\"Größe.pdf\"",
            "",
            "QUJD",
            "--MIX--");

        Assert.Equal(new[] { "Größe.pdf" }, mail.AttachmentNames);
    }

    [Fact]
    public void Read_RecoversARawUtf8AttachmentNameFromContentType()
    {
        var mail = Parse(
            "Content-Type: multipart/mixed; boundary=\"MIX\"",
            "",
            "--MIX",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Haupttext",
            "--MIX",
            "Content-Type: application/pdf; name=\"Jahresübersicht.pdf\"",
            "Content-Transfer-Encoding: base64",
            "",
            "QUJD",
            "--MIX--");

        Assert.Equal(new[] { "Jahresübersicht.pdf" }, mail.AttachmentNames);
    }

    [Fact]
    public void Read_PrefersTextPlainOverTextHtml()
    {
        var mail = Parse(
            "Content-Type: multipart/alternative; boundary=\"ALT\"",
            "",
            "--ALT",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Nur Text",
            "--ALT",
            "Content-Type: text/html; charset=UTF-8",
            "",
            "<html><body><p>Nur HTML</p></body></html>",
            "--ALT--");

        Assert.False(mail.BodyIsFromHtmlFallback);
        Assert.Equal("Nur Text", mail.Body);
    }

    [Fact]
    public void Read_FallsBackToStrippedHtmlWhenNoPlainPart()
    {
        var mail = Parse(
            "Content-Type: text/html; charset=UTF-8",
            "",
            "<html><head><style>p{color:red}</style></head><body><p>Hallo\u034FMarco</p>"
            + "<script>var x=1;</script><div>Zeile zwei</div></body></html>");

        Assert.True(mail.BodyIsFromHtmlFallback);
        Assert.Equal("HalloMarco\nZeile zwei", mail.Body);
        Assert.DoesNotContain("color:red", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("var x=1", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain('\u034F', mail.Body);
    }

    [Fact]
    public void Read_RecursesIntoNestedMultipart()
    {
        var mail = Parse(
            "Content-Type: multipart/mixed; boundary=\"OUT\"",
            "",
            "--OUT",
            "Content-Type: multipart/alternative; boundary=\"IN\"",
            "",
            "--IN",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Innerer Text",
            "--IN",
            "Content-Type: text/html; charset=UTF-8",
            "",
            "<p>Innerer HTML</p>",
            "--IN--",
            "--OUT",
            "Content-Type: text/plain; charset=UTF-8",
            "Content-Disposition: attachment; filename=\"notiz.txt\"",
            "",
            "egal",
            "--OUT--");

        Assert.False(mail.BodyIsFromHtmlFallback);
        Assert.Equal("Innerer Text", mail.Body);
        Assert.Equal("notiz.txt", Assert.Single(mail.AttachmentNames));
    }

    [Theory]
    [InlineData("SGFsbG8gV2VsdA==")]
    [InlineData("SGFsbG8gV2VsdA")]
    public void Read_DecodesBase64TransferEncoding(string payload)
    {
        var mail = Parse(
            "Content-Type: text/plain; charset=UTF-8",
            "Content-Transfer-Encoding: base64",
            "",
            payload);

        Assert.Equal("Hallo Welt", mail.Body);
    }

    [Fact]
    public void Read_HandlesBareLfLineEndings()
    {
        var mail = EmlReader.Parse(string.Join(
            '\n',
            "Subject: Nur LF",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Zeile eins",
            "Zeile zwei"));

        Assert.Equal("Nur LF", mail.Subject);
        Assert.Equal("Zeile eins\nZeile zwei", mail.Body);
    }

    [Fact]
    public void Read_ReturnsEmptyListsNotNullWhenThereAreNoRecipients()
    {
        var mail = Parse("Subject: Ohne Empfänger", "", "Rumpf");

        // Raw UTF-8 in the header: no encoded-word marks it, so nothing else would re-decode it.
        Assert.Equal("Ohne Empfänger", mail.Subject);
        Assert.Equal(-1, mail.Subject!.AsSpan().IndexOfAnyInRange('\u0080', '\u009F'));
        Assert.Empty(mail.To);
        Assert.Empty(mail.Cc);
        Assert.Empty(mail.AttachmentNames);
    }

    [Fact]
    public void Read_PreservesTheDateOffset()
    {
        var mail = Parse("Date: Mon, 31 Aug 2026 20:12:28 +0200", "", "Rumpf");

        Assert.NotNull(mail.Date);
        Assert.Equal(TimeSpan.FromHours(2), mail.Date.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 20, 12, 28, TimeSpan.FromHours(2)), mail.Date.Value);
    }

    [Fact]
    public void Read_ExtractsAttachmentNamesWithoutBytes()
    {
        var mail = Parse(
            "Content-Type: multipart/mixed; boundary=\"MIX\"",
            "",
            "--MIX",
            "Content-Type: text/plain; charset=UTF-8",
            "",
            "Haupttext",
            "--MIX",
            "Content-Type: application/pdf; name=\"bericht.pdf\"",
            "Content-Transfer-Encoding: base64",
            "",
            "QUJD",
            "--MIX",
            "Content-Type: application/octet-stream",
            "Content-Disposition: attachment; filename=\"daten.bin\"",
            "Content-Transfer-Encoding: base64",
            "",
            "REVG",
            "--MIX--");

        Assert.Equal(new[] { "bericht.pdf", "daten.bin" }, mail.AttachmentNames);
        Assert.Equal("Haupttext", mail.Body);
    }

    [Fact]
    public void Read_TreatsWholeBodyAsTextWhenTheBoundaryNeverOccurs()
    {
        var mail = Parse(
            "Content-Type: multipart/alternative; boundary=\"NIEMALS\"",
            "",
            "nur text, keine grenze");

        Assert.Equal("nur text, keine grenze", mail.Body);
    }

    [Fact]
    public void Read_DoesNotThrowOnAMessageWithNoBlankLine()
    {
        var mail = Parse("Subject: Nur Header", "From: absender@example.test");

        Assert.Equal("Nur Header", mail.Subject);
        Assert.Equal("absender@example.test", mail.From);
        Assert.Equal(string.Empty, mail.Body);
    }

    [Fact]
    public void Read_FallsBackToUtf8OnAnUnknownCharset()
    {
        var mail = Parse(
            "Content-Type: text/plain; charset=x-nicht-vorhanden",
            "Content-Transfer-Encoding: quoted-printable",
            "",
            "Gr=C3=BC=C3=9Fe");

        Assert.Equal("Grüße", mail.Body);
    }

    [Fact]
    public void Read_ParsesADateCarryingAnRfc5322Comment()
    {
        var mail = Parse("Date: Mon, 31 Aug 2026 20:12:28 +0000 (UTC)", "", "Rumpf");

        Assert.NotNull(mail.Date);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 20, 12, 28, TimeSpan.Zero), mail.Date.Value);
        Assert.Equal(TimeSpan.Zero, mail.Date.Value.Offset);
    }

    [Fact]
    public void Read_ParsesADateUnderANonEnglishCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            // Green today because the reader parses invariantly; the guard is against a future ParseExact.
            var mail = Parse("Date: Mon, 31 Aug 2026 20:12:28 +0000", "", "Rumpf");

            Assert.NotNull(mail.Date);
            Assert.Equal(new DateTimeOffset(2026, 8, 31, 20, 12, 28, TimeSpan.Zero), mail.Date.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Read_DecodesWindows1252PunctuationThatLatin1WouldTurnIntoControls()
    {
        var mail = Parse(
            "Content-Type: text/plain; charset=windows-1252",
            "Content-Transfer-Encoding: quoted-printable",
            "",
            "Preis: 20=80 =96 =93Angebot=94=85");

        Assert.Equal("Preis: 20\u20AC \u2013 \u201CAngebot\u201D\u2026", mail.Body);
        Assert.Equal(-1, mail.Body.AsSpan().IndexOfAnyInRange('\u0080', '\u009F'));
    }

    [Fact]
    public void Read_LeavesIso88591ControlBytesAlone()
    {
        var mail = Parse(
            "Content-Type: text/plain; charset=iso-8859-1",
            "Content-Transfer-Encoding: quoted-printable",
            "",
            "A=93B");

        Assert.Equal("A\u0093B", mail.Body);
    }

    [Fact]
    public void GetEncoding_ResolvesWindows1252ByNameAndByCodePage()
    {
        byte[] bytes = [0x80, 0x93, 0x94, 0x85];
        const string mapped = "\u20AC\u201C\u201D\u2026";

        Assert.Equal(mapped, MimeDecoding.GetEncoding("windows-1252").GetString(bytes));
        Assert.Equal(mapped, MimeDecoding.GetEncoding("cp1252").GetString(bytes));
        Assert.Equal(mapped, MimeDecoding.GetEncoding(1252).GetString(bytes));
        Assert.Equal("\u0080\u0093\u0094\u0085", MimeDecoding.GetEncoding("iso-8859-1").GetString(bytes));
    }

    [Fact]
    public void GetEncoding_ReportsTheWindows1252CodePageAndWebName()
    {
        var encoding = MimeDecoding.GetEncoding("windows-1252");

        Assert.Equal(1252, encoding.CodePage);
        Assert.Equal("windows-1252", encoding.WebName);
    }

    [Fact]
    public void Read_KeepsALatin1SubjectThatIsNotValidUtf8()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pia-eml-{Guid.NewGuid():N}.eml");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes("Subject: Größe\r\n\r\nRumpf\r\n"));
        try
        {
            var mail = EmlReader.Read(path);

            Assert.Equal("Größe", mail.Subject);
            Assert.DoesNotContain('\uFFFD', mail.Subject!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_BreaksHtmlListItemsHeadingsAndTableCellsApart()
    {
        var mail = Parse(
            "Content-Type: text/html; charset=UTF-8",
            "",
            "<html><body><h1>Titel</h1><ul><li>Punkt eins</li><li>Punkt zwei</li></ul>"
            + "<table><tr><th>Name:</th><td>x.zip</td></tr><tr><td>Gr&ouml;&szlig;e:</td><td>2 MB</td></tr></table></body></html>");

        Assert.Equal("Titel\nPunkt eins\nPunkt zwei\nName: x.zip\nGr\u00F6\u00DFe: 2 MB", mail.Body);
    }

    private static EmailMessage Parse(params string[] lines) => EmlReader.Parse(string.Join("\r\n", lines));
}
