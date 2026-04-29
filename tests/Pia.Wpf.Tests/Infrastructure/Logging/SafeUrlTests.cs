using Pia.Logging;
using Xunit;

namespace Pia.Tests.Infrastructure.Logging;

public class SafeUrlTests
{
    [Fact]
    public void Format_NullUri_ReturnsPlaceholder()
    {
        Assert.Equal("<no-url>", SafeUrl.Format((Uri?)null));
    }

    [Fact]
    public void Format_NullOrWhitespaceString_ReturnsPlaceholder()
    {
        Assert.Equal("<no-url>", SafeUrl.Format((string?)null));
        Assert.Equal("<no-url>", SafeUrl.Format(""));
        Assert.Equal("<no-url>", SafeUrl.Format("   "));
    }

#if DEBUG
    [Fact]
    public void Format_Debug_ReturnsFullUri()
    {
        var url = "https://api.openai.com/v1/chat?key=secret";
        Assert.Equal(url, SafeUrl.Format(url));
        Assert.Equal(url, SafeUrl.Format(new Uri(url)));
    }

    [Fact]
    public void Format_Debug_TruncatesLongUrl()
    {
        var longUrl = "https://example.com/" + new string('x', 600);
        var formatted = SafeUrl.Format(longUrl);
        Assert.EndsWith("...", formatted);
        Assert.Equal(503, formatted.Length); // 500 + "..."
    }
#else
    [Fact]
    public void Format_Release_DropsPathAndQuery()
    {
        var formatted = SafeUrl.Format("https://api.openai.com/v1/chat?key=secret");
        Assert.DoesNotContain("api.openai.com", formatted);
        Assert.DoesNotContain("v1", formatted);
        Assert.DoesNotContain("secret", formatted);
        Assert.StartsWith("https://host-", formatted);
    }

    [Fact]
    public void Format_Release_SameHostYieldsSameCode()
    {
        var a = SafeUrl.Format("https://example.com/foo");
        var b = SafeUrl.Format("https://example.com/bar?x=1");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Format_Release_DifferentHostsYieldDifferentCodes()
    {
        // Statistical: SHA256 mod 1000, expect distinct codes for these two.
        var a = SafeUrl.Format("https://example.com/");
        var b = SafeUrl.Format("https://other-host.test/");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Format_Release_HostCaseInsensitive()
    {
        var a = SafeUrl.Format("https://Example.COM/");
        var b = SafeUrl.Format("https://example.com/");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Format_Release_ProducesThreeDigitCode()
    {
        var formatted = SafeUrl.Format("https://example.com/");
        // "https://host-NNN" — last segment after "host-" is exactly 3 digits.
        var idx = formatted.IndexOf("host-", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var code = formatted[(idx + "host-".Length)..];
        Assert.Equal(3, code.Length);
        Assert.True(code.All(char.IsDigit));
    }
#endif
}
