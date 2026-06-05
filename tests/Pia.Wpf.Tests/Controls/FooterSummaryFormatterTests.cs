using Pia.Controls.Chat;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Controls;

public class FooterSummaryFormatterTests
{
    [Fact]
    public void StatsAndPersona_ShowsTokensPersonaModel()
    {
        var text = FooterSummaryFormatter.Compose(new AnswerStats(1234, "gpt-4o"), "Marketing Writer");
        Assert.Equal("1,234 Tokens · Marketing Writer · gpt-4o", text);
    }

    [Fact]
    public void StatsOnly_Unchanged()
    {
        var text = FooterSummaryFormatter.Compose(new AnswerStats(1234, "gpt-4o"), null);
        Assert.Equal("1,234 Tokens · gpt-4o", text);
    }

    [Fact]
    public void PersonaOnly_NoStats_ShowsName()
    {
        var text = FooterSummaryFormatter.Compose(null, "Marketing Writer");
        Assert.Equal("Marketing Writer", text);
    }

    [Fact]
    public void Neither_IsEmpty()
    {
        Assert.Equal(string.Empty, FooterSummaryFormatter.Compose(null, null));
    }
}
