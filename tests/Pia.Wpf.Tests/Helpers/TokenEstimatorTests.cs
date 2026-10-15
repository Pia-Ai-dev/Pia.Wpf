using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

public sealed class TokenEstimatorTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("1", 1)]
    [InlineData("123", 1)]
    [InlineData("1234", 2)]
    [InlineData("1234567", 3)]
    public void Estimate_CountsDigitRunsInGroupsOfThree(string? text, int expected) =>
        Assert.Equal(expected, TokenEstimator.Estimate(text));

    [Fact]
    public void Estimate_CountsEveryCjkCharacter() =>
        Assert.Equal(6, TokenEstimator.Estimate("请帮我总结这"));

    [Fact]
    public void Estimate_FoldsASingleSpaceIntoTheNextWord() =>
        Assert.Equal(2, TokenEstimator.Estimate("word word"));

    [Fact]
    public void Estimate_ChargesAWhitespaceRunOnce() =>
        Assert.Equal(3, TokenEstimator.Estimate("word\t\t\tword"));

    [Fact]
    public void Estimate_ChargesPunctuationRunsInPairs() =>
        Assert.Equal(2, TokenEstimator.Estimate("();"));

    [Fact]
    public void Estimate_ChargesNumbersFarAboveLengthOverFour()
    {
        var text = string.Concat(Enumerable.Repeat("2026-10-14\t13\t238\t111\t127\t0.318\n", 100));

        Assert.True(TokenEstimator.Estimate(text) > 2 * (text.Length / 4));
    }

    [Fact]
    public void CutToBudget_KeepsTextThatFits()
    {
        var cut = TokenEstimator.CutToBudget("alpha\nbeta\n", 100);

        Assert.False(cut.Truncated);
        Assert.Equal("alpha\nbeta\n", cut.Text);
        Assert.Equal(2, cut.ShownLines);
        Assert.Equal(2, cut.TotalLines);
    }

    [Fact]
    public void CutToBudget_CountsCrLfLinesOnce() =>
        Assert.Equal(2, TokenEstimator.CutToBudget("a\r\nb\r\n", 100).TotalLines);

    [Fact]
    public void CutToBudget_ACrLfTextGrantedItsOwnEstimate_IsNotCut()
    {
        // docx and xlsx extracts end their lines with CRLF.
        var text = string.Concat(Enumerable.Repeat("alpha beta gamma\r\n", 50));

        Assert.False(TokenEstimator.CutToBudget(text, TokenEstimator.Estimate(text)).Truncated);
    }

    [Fact]
    public void CutToBudget_CutsAtALineBoundary()
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i} of the report"));

        var cut = TokenEstimator.CutToBudget(text, 50);

        Assert.True(cut.Truncated);
        Assert.False(cut.EndsMidLine);
        Assert.Equal(100, cut.TotalLines);
        Assert.InRange(cut.ShownLines, 1, 99);
        Assert.Equal(string.Join('\n', text.Split('\n').Take(cut.ShownLines)), cut.Text);
        Assert.True(TokenEstimator.Estimate(cut.Text) <= 50);
    }

    [Fact]
    public void CutToBudget_CutsInsideALineThatAloneIsTooLong()
    {
        var oneLine = string.Join(',', Enumerable.Range(0, 5000));

        var cut = TokenEstimator.CutToBudget(oneLine, 100);

        Assert.True(cut.Truncated);
        Assert.True(cut.EndsMidLine);
        Assert.Equal(1, cut.ShownLines);
        Assert.Equal(1, cut.TotalLines);
        Assert.NotEmpty(cut.Text);
        Assert.StartsWith(cut.Text, oneLine, StringComparison.Ordinal);
        Assert.True(TokenEstimator.Estimate(cut.Text) <= 100);
    }

    [Fact]
    public void CutToBudget_ABlankLineBeforeAHugeLine_CutsInsideTheHugeLine()
    {
        var text = "\n" + string.Join(',', Enumerable.Range(0, 5000));

        var cut = TokenEstimator.CutToBudget(text, 100);

        Assert.True(cut.EndsMidLine);
        Assert.Equal(2, cut.ShownLines);
        Assert.Equal(2, cut.TotalLines);
        Assert.StartsWith(cut.Text, text, StringComparison.Ordinal);
        Assert.InRange(TokenEstimator.Estimate(cut.Text), 51, 100);
    }

    [Fact]
    public void CutToBudget_ABannerBeforeAHugeLine_FillsTheBudgetInsideTheHugeLine()
    {
        var text = "/*! chart.js v4.4.1 | MIT */\n" + string.Join(',', Enumerable.Range(0, 5000));

        var cut = TokenEstimator.CutToBudget(text, 100);

        Assert.True(cut.EndsMidLine);
        Assert.Equal(2, cut.ShownLines);
        Assert.StartsWith(cut.Text, text, StringComparison.Ordinal);
        Assert.InRange(TokenEstimator.Estimate(cut.Text), 51, 100);
    }

    [Fact]
    public void CutToBudget_NeverSplitsASurrogatePair()
    {
        // The '#' joins the emoji's punctuation run, so the largest fitting prefix ends on a high surrogate.
        var text = "#" + string.Concat(Enumerable.Repeat("😀", 400));

        var cut = TokenEstimator.CutToBudget(text, 51);

        Assert.False(char.IsHighSurrogate(cut.Text[^1]));
    }

    [Fact]
    public void CutToBudget_RejectsAnEmptyBudget() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenEstimator.CutToBudget("text", 0));
}
