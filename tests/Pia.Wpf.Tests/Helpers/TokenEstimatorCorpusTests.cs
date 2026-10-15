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
