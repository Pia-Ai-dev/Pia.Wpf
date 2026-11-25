using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

public class SentenceChunkerTests
{
    [Theory]
    [InlineData("Wir treffen uns am 3. Oktober in Berlin.", new[] { "Wir treffen uns am 3. Oktober in Berlin." })]
    [InlineData("Das gilt z. B. für Dr. Müller und Nr. 5.", new[] { "Das gilt z. B. für Dr. Müller und Nr. 5." })]
    [InlineData("Use a tool, e.g. grep. Then stop.", new[] { "Use a tool, e.g. grep.", "Then stop." })]
    [InlineData("Voir M. Dupont, p. ex. demain. Merci.", new[] { "Voir M. Dupont, p. ex. demain.", "Merci." })]
    [InlineData("Das war 2023. Dann kam 2024.", new[] { "Das war 2023.", "Dann kam 2024." })]
    [InlineData("No. I won't.", new[] { "No.", "I won't." })]
    [InlineData("Wait… What?", new[] { "Wait…", "What?" })]
    [InlineData("Nimm ein Werkzeug (z. B. grep). Fertig.", new[] { "Nimm ein Werkzeug (z. B. grep).", "Fertig." })]
    public void Split_KeepsAbbreviationsAndOrdinalsInsideTheSentence(string text, string[] expected)
        => Assert.Equal(expected, SentenceChunker.Split(text));

    [Fact]
    public void Split_ALineBreakEndsAChunk()
        => Assert.Equal(["Einkaufsliste", "Milch", "Brot"], SentenceChunker.Split("Einkaufsliste\nMilch\n\nBrot"));

    [Fact]
    public void AddToken_Streamed_ADotAtTheEndOfTheBufferWaitsForTheNextToken()
    {
        var chunker = new SentenceChunker();
        Assert.Empty(chunker.AddToken("Pi is 3."));
        Assert.Equal(["Pi is 3.14."], chunker.AddToken("14. Next"));
        Assert.Equal("Next", chunker.Flush());
    }

    [Fact]
    public void Split_ALongRunWithoutPunctuation_IsCutAtAClauseMarkWithinTheCap()
    {
        var text = string.Join(", ", Enumerable.Repeat("Apfel Birne Kirsche", 40));
        var chunks = SentenceChunker.Split(text);
        Assert.All(chunks, c => Assert.True(c.Length <= SentenceChunker.MaxChunkLength));
        Assert.All(chunks.SkipLast(1), c => Assert.EndsWith(",", c));
        Assert.Equal(text.Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
    }

    [Fact]
    public void Split_ALongRunWithoutAnySpace_IsCutHardAtTheCap()
        => Assert.Equal(SentenceChunker.MaxChunkLength, SentenceChunker.Split(new string('x', 700))[0].Length);
}
