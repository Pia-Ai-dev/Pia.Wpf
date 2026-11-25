using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Help;
using Xunit;

namespace Pia.Tests.Services.Help;

public sealed class HelpToolHandlerTests
{
    private const string GlossaryLead = "quote the right-hand side";

    internal static HelpToolHandler Handler(TargetLanguage language)
    {
        var localization = HelpLabelResolverTests.Localization(language);
        return new HelpToolHandler(
            new HelpSearchService(),
            HelpSettingsResolverTests.Build(new AppSettings(), localization),
            new HelpLabelResolver(localization),
            NullLogger<HelpToolHandler>.Instance);
    }

    private static async Task<string> Help(TargetLanguage language, string argument, string value)
    {
        var result = await Handler(language).HandleToolCallAsync(
            new FunctionCallContent("1", "pia_help", new Dictionary<string, object?> { [argument] = value }),
            TestContext.Current.CancellationToken);
        return Assert.IsType<string>(result);
    }

    [Fact]
    public async Task AGermanScreenGetsTheGuidesLabelsInGermanWithTheSection()
    {
        var text = await Help(TargetLanguage.DE, "reference", "guides/tool-permissions#the-settings-page");

        Assert.Contains(GlossaryLead, text, StringComparison.Ordinal);
        Assert.Contains("German", text, StringComparison.Ordinal);
        Assert.Contains("- Tool access = Tool-Zugriff", text, StringComparison.Ordinal);
        Assert.Contains("- Settings = Einstellungen", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchHitsCarryTheGlossaryOfTheSectionsTheyPointAt()
    {
        var text = await Help(TargetLanguage.DE, "query", "tool access permission tier");

        Assert.Contains("- Tool access = Tool-Zugriff", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFrenchScreenIsNamedAsFrench()
    {
        var text = await Help(TargetLanguage.FR, "reference", "guides/plugins");

        Assert.Contains("French", text, StringComparison.Ordinal);
        Assert.Contains("- Plugins = Extensions", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEnglishScreenGetsNoGlossary()
    {
        var text = await Help(TargetLanguage.EN, "reference", "guides/tool-permissions#the-settings-page");

        Assert.DoesNotContain(GlossaryLead, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissGetsNoGlossary()
    {
        var text = await Help(TargetLanguage.DE, "reference", "no/such/page");

        Assert.DoesNotContain(GlossaryLead, text, StringComparison.Ordinal);
    }
}
