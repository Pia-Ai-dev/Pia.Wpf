using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.ML.Tokenizers;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>Holds the estimate to o200k on real text: never more than 5 % under, never more than 1.5× over.</summary>
public sealed class TokenEstimatorCorpusTests
{
    private const double MaxUnder = 0.95;
    private const double MaxOver = 1.5;

    private static readonly Tokenizer O200k = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static TheoryData<string> Samples() =>
        ["en-prose.txt", "de-prose.txt", "fr-prose.txt", "de-pdf-table.txt", "csharp.txt", "xaml.txt", "cjk.txt"];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Estimate_StaysInTheBandAroundO200k(string sample) =>
        AssertInBand(sample, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "TokenCorpus", sample)));

    [Fact]
    public void Estimate_StaysInTheBandOnANumericSheet() => AssertInBand("numeric TSV", NumericTsv());

    public static TheoryData<string> WhitespaceShapes() =>
        ["alternating space and tab", "spaces", "newlines", "tabs", "crlf", "nbsp", "space and newline",
         "tab and newline", "four-way mix", "ogham space", "next line"];

    // Whitespace-only files once cost a single token, so a 60 KB one passed any budget whole.
    [Theory]
    [MemberData(nameof(WhitespaceShapes))]
    public void Estimate_NeverUndercountsALongWhitespaceRun(string shape)
    {
        var text = shape switch
        {
            "alternating space and tab" => Repeat(" \t", 30_000),
            "spaces" => new string(' ', 2_000),
            "newlines" => new string('\n', 2_000),
            "tabs" => new string('\t', 2_000),
            "crlf" => Repeat("\r\n", 1_000),
            "nbsp" => new string((char)0xA0, 2_000),
            "space and newline" => Repeat(" \n", 1_000),
            "tab and newline" => Repeat("\t\n", 1_000),
            "four-way mix" => Repeat(" \t\n\r", 500),
            "ogham space" => new string((char)0x1680, 200),
            "next line" => new string((char)0x85, 200),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var actual = O200k.CountTokens(text);
        var estimate = TokenEstimator.Estimate(text);

        Assert.True(estimate >= actual, $"{shape}: estimate {estimate} under o200k {actual}");
    }

    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static void AssertInBand(string name, string text)
    {
        var actual = O200k.CountTokens(text);
        var ratio = TokenEstimator.Estimate(text) / (double)actual;

        Assert.True(ratio is >= MaxUnder and <= MaxOver,
            $"{name}: estimate/o200k = {ratio.ToString("F2", CultureInfo.InvariantCulture)} at o200k {actual}, " +
            $"outside [{MaxUnder}, {MaxOver}]");
    }

    // An xlsx extract is what Length/4 undercounts 2.6×; generated, so the shape is fixed without a fixture.
    internal static string NumericTsv()
    {
        var random = new Random(42);
        var sb = new StringBuilder("Datum\tDateien\tHinzugefügt\tEntfernt\tNetto\tQuote\n");
        var day = new DateOnly(2026, 1, 1);
        for (var i = 0; i < 400; i++)
        {
            var files = random.Next(1, 120);
            var added = random.Next(0, 7000);
            var removed = random.Next(0, 3000);
            var quote = added + removed == 0 ? 0 : Math.Round(removed / (double)(added + removed), 3);
            sb.Append(day.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\t')
              .Append(files).Append('\t').Append(added).Append('\t').Append(removed).Append('\t')
              .Append(added - removed).Append('\t').Append(quote.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }
}
