using System.Collections.Generic;
using System.Linq;
using System.Windows.Documents;
using Pia.Controls.Markdown;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Controls;

[Collection("WpfApplicationStatic")]
public class CodeColorizerYamlTests
{
    private const string Compose =
        "# compose\nservices:\n  web:\n    image: \"nginx:1.27\"\n    replicas: 3\n    tty: true\n";

    [Theory]
    [InlineData("yaml")]
    [InlineData("YAML")]
    [InlineData("yml")]
    public void YamlHintResolvesToAGrammar(string hint)
    {
        Assert.NotNull(CodeColorizer.ResolveLanguage(hint));
    }

    [Fact]
    public void Highlighting_KeepsEveryCharacter()
    {
        Assert.Equal(Compose, string.Concat(Highlight(Compose)));
    }

    [Fact]
    public void Highlighting_SplitsKeysStringsCommentsNumbersAndConstants()
    {
        var texts = Highlight(Compose);

        Assert.Contains("# compose", texts);
        Assert.Contains("services", texts);
        Assert.Contains("web", texts);
        Assert.Contains("\"nginx:1.27\"", texts);
        Assert.Contains("3", texts);
        Assert.Contains("true", texts);
    }

    [Fact]
    public void AColonInsideAStringDoesNotBecomeAKey()
    {
        var texts = Highlight("cmd: \"host: 80\"\n");

        Assert.Contains("\"host: 80\"", texts);
        Assert.DoesNotContain("host", texts);
    }

    [Fact]
    public void AHashInsideAStringDoesNotBecomeAComment()
    {
        var texts = Highlight("colour: \"#ff0000\" # the brand red\n");

        Assert.Contains("\"#ff0000\"", texts);
        Assert.Contains("# the brand red", texts);
    }

    [Fact]
    public void CrlfContent_StillColoursTheKeys()
    {
        const string code = "services:\r\n  web:\r\n    image: nginx\r\n    replicas: 3\r\n";
        var texts = Highlight(code);

        Assert.Equal(code, string.Concat(texts));
        Assert.Contains("services", texts);
        Assert.Contains("web", texts);
        Assert.Contains("image", texts);
        Assert.Contains("3", texts);
    }

    [Fact]
    public void AnEscapedQuoteDoesNotEndTheString()
    {
        var texts = Highlight("msg: \"say \\\"hi\\\" now\" # done\n");

        Assert.Contains("\"say \\\"hi\\\" now\"", texts);
        Assert.Contains("# done", texts);
    }

    private static IReadOnlyList<string> Highlight(string code)
    {
        IReadOnlyList<string>? texts = null;
        WpfStaHost.Run(() =>
        {
            texts = new CodeColorizer()
                .Highlight(code, CodeColorizer.ResolveLanguage("yaml"))
                .Select(run => run.Text)
                .ToList();
            return 0;
        });
        return texts!;
    }
}
