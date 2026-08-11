using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Characterizes the <c>&lt;think&gt;</c>-tag splitter extracted from AssistantViewModel.
/// </summary>
public class StreamThinkTagParserTests
{
    [Fact]
    public void Parse_PlainText_IsAllVisible()
    {
        var (visible, thinking) = StreamThinkTagParser.Parse("hello world");
        Assert.Equal("hello world", visible);
        Assert.Equal(string.Empty, thinking);
    }

    [Fact]
    public void Parse_SplitsThinkBlock()
    {
        var (visible, thinking) = StreamThinkTagParser.Parse("<think>reasoning</think>answer");
        Assert.Equal("answer", visible);
        Assert.Equal("reasoning", thinking);
    }

    [Fact]
    public void Parse_SeparatesVisibleAroundThinkBlock()
    {
        // Previously asserted "beforeafter" (glued, zero separator) — that was the bug: a model
        // reopening <think> mid-answer fused the word before the tag to the word after it.
        var (visible, thinking) = StreamThinkTagParser.Parse("before<think>mid</think>after");
        Assert.Equal("before after", visible);
        Assert.Equal("mid", thinking);
    }

    [Fact]
    public void Parse_SeparatesVisibleAcrossTwoThinkBlocks()
    {
        // Reasoning re-opening after real answer text has already streamed (e.g. around a tool
        // call): both visible runs must stay separated, not just the one around the first block.
        var (visible, thinking) = StreamThinkTagParser.Parse("before<think>r1</think>middle<think>r2</think>after");
        Assert.Equal("before middle after", visible);
        Assert.Equal("r1r2", thinking);
    }

    [Fact]
    public void Parse_UnclosedThink_TreatsRemainderAsThinking()
    {
        var (visible, thinking) = StreamThinkTagParser.Parse("partial<think>still going");
        Assert.Equal("partial", visible);
        Assert.Equal("still going", thinking);
    }

    [Fact]
    public void Parse_IsCaseInsensitiveAndTrimsVisibleLead()
    {
        var (visible, thinking) = StreamThinkTagParser.Parse("  <THINK>t</THINK>  visible");
        Assert.Equal("visible", visible);
        Assert.Equal("t", thinking);
    }
}
