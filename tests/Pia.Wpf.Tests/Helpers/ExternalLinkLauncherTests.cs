using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

public sealed class ExternalLinkLauncherTests
{
    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("mailto:kontakt@pia-ai.de?subject=Pia%20Community%20licence")]
    public void Web_and_mail_links_open(string url)
        => Assert.True(ExternalLinkLauncher.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("ms-settings:privacy")]
    [InlineData("search-ms:query=x&crumb=location:\\\\host\\share")]
    [InlineData("ms-officecmd:{}")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox")]
    [InlineData("calculator:")]
    [InlineData("file:///C:/Users/me/Downloads/payload.appref-ms")]
    [InlineData(@"C:\Users\me\report.html")]
    [InlineData("file://attacker.example/share/payload.exe")]
    [InlineData(@"\\attacker.example\share\doc.docx")]
    [InlineData("mailto:a@example.com?attach=C:\\Users\\me\\.ssh\\id_rsa")]
    [InlineData("mailto:a@example.com?subject=x&ATTACHMENT=C:\\secret.txt")]
    [InlineData("pia-memory:topics/foo")]
    public void Every_other_target_is_refused(string url)
        => Assert.False(ExternalLinkLauncher.IsAllowed(new Uri(url)));

    [Fact]
    public void Relative_and_missing_uris_are_refused()
    {
        Assert.False(ExternalLinkLauncher.IsAllowed(null));
        Assert.False(ExternalLinkLauncher.IsAllowed(new Uri("docs/page.html", UriKind.Relative)));
    }
}
