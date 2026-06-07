using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Characterizes the <c>&lt;think&gt;</c>-tag splitter extracted from AssistantViewModel,
/// so the streaming visible/thinking separation stays behavior-identical.
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
    public void Parse_ConcatenatesVisibleAroundThinkBlock()
    {
        var (visible, thinking) = StreamThinkTagParser.Parse("before<think>mid</think>after");
        Assert.Equal("beforeafter", visible);
        Assert.Equal("mid", thinking);
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
