using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

public class SpeakableTextTests
{
    [Fact]
    public void FromMarkdown_DropsEmphasisAndHeadingMarkers()
        => Assert.Equal("Ergebnis\nDas ist wichtig und klar.", SpeakableText.FromMarkdown("## Ergebnis\n\nDas ist **wichtig** und _klar_."));

    [Fact]
    public void FromMarkdown_ListItemsBecomeLines_WithoutMarkers()
        => Assert.Equal("Milch\nBrot", SpeakableText.FromMarkdown("- Milch\n- Brot"));

    [Fact]
    public void FromMarkdown_LinksKeepTheirLabel_BareUrlsGo()
        => Assert.Equal("Siehe die Doku.\nOder", SpeakableText.FromMarkdown("Siehe [die Doku](https://x.invalid).\n\nOder https://x.invalid"));

    [Fact]
    public void FromMarkdown_SkipsCodeBlocksAndTables_KeepsInlineCode()
        => Assert.Equal("Run npm install first.\nDone.",
            SpeakableText.FromMarkdown("Run `npm install` first.\n\n```bash\nnpm install\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\nDone."));

    [Fact]
    public void FromMarkdown_RemovesEmoji_KeepsDegreesAndCurrency()
        => Assert.Equal("Es sind 20 °C und 5 €", SpeakableText.FromMarkdown("Es sind 20 °C und 5 € 🙂"));

    [Fact]
    public void FromMarkdown_OnlyCode_IsEmpty()
        => Assert.Equal("", SpeakableText.FromMarkdown("```\nvar x = 1;\n```"));
}
