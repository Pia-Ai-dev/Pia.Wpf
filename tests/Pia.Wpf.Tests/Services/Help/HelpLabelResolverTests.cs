using System.Globalization;
using NSubstitute;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services.Help;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services.Help;

public sealed class HelpLabelResolverTests
{
    private static readonly CultureInfo[] ShippedCultures =
        [CultureInfo.InvariantCulture, new("de"), new("fr")];

    internal static ILocalizationService Localization(TargetLanguage language)
    {
        var culture = language switch
        {
            TargetLanguage.DE => new CultureInfo("de"),
            TargetLanguage.FR => new CultureInfo("fr"),
            _ => CultureInfo.InvariantCulture,
        };
        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(language);
        localization[Arg.Any<string>()].Returns(call => ViewStrings.ResourceManager.GetString(call.Arg<string>(), culture));
        return localization;
    }

    private static IReadOnlyList<HelpLabel> Glossary(TargetLanguage language, string markdown) =>
        new HelpLabelResolver(Localization(language)).For(HelpLabelResolver.Candidates(markdown));

    [Fact]
    public void EveryGlossaryKeyResolvesInAllThreeLanguages()
    {
        foreach (var key in HelpLabelResolver.Keys)
        {
            foreach (var culture in ShippedCultures)
            {
                Assert.False(string.IsNullOrWhiteSpace(ViewStrings.ResourceManager.GetString(key, culture)),
                    $"'{key}' has no {culture.Name switch { "" => "English", var n => n }} value, so the glossary would map a label to nothing");
            }
        }
    }

    [Fact]
    public void EveryPathKeyIsAlsoAGlossaryLabel()
    {
        Assert.All(HelpSettingsResolver.PathLocalizationKeys, key => Assert.Contains(key, HelpLabelResolver.Keys));
    }

    [Fact]
    public void NoEnglishLabelHasTwoDifferentTranslations()
    {
        foreach (var culture in ShippedCultures.Skip(1))
        {
            var clashes = HelpLabelResolver.Keys
                .GroupBy(k => ViewStrings.ResourceManager.GetString(k, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(k => ViewStrings.ResourceManager.GetString(k, culture)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(g => $"{g.Key} ({string.Join(", ", g)})");

            Assert.Empty(clashes);
        }
    }

    [Fact]
    public void AGermanScreenGetsTheGermanLabelsOfASettingsPath()
    {
        var labels = Glossary(TargetLanguage.DE, "Open **Settings → Assistant → Tool access** and pick a tier.");

        Assert.Contains(new HelpLabel("Settings", "Einstellungen"), labels);
        Assert.Contains(new HelpLabel("Assistant", "Assistent"), labels);
        Assert.Contains(new HelpLabel("Tool access", "Tool-Zugriff"), labels);
    }

    [Fact]
    public void AFrenchScreenGetsTheFrenchLabels()
    {
        var labels = Glossary(TargetLanguage.FR, "**Settings → Plugins**");

        Assert.Contains(new HelpLabel("Plugins", "Extensions"), labels);
    }

    [Fact]
    public void AnEnglishScreenGetsNoGlossary()
    {
        Assert.Empty(Glossary(TargetLanguage.EN, "**Settings → Assistant → Tool access**"));
    }

    [Fact]
    public void HeadingsCountAsLabels()
    {
        var labels = Glossary(TargetLanguage.DE, "## Agent runs\n\nSome prose.\n\n### Private Keywords\n");

        Assert.Contains(new HelpLabel("Agent runs", "Agentenläufe"), labels);
        Assert.Contains(new HelpLabel("Private Keywords", "Private Schlüsselwörter"), labels);
    }

    [Fact]
    public void ProseThatOnlyMentionsALabelWordIsNotGlossed()
    {
        Assert.Empty(Glossary(TargetLanguage.DE, "Read about the general settings for your account."));
    }

    [Fact]
    public void ALabelThatReadsTheSameInBothLanguagesIsLeftOut()
    {
        Assert.DoesNotContain(Glossary(TargetLanguage.DE, "**Settings → Assistant → Personas**"), l => l.English == "Personas");
    }

    [Fact]
    public void EachLabelIsListedOnceHoweverOftenTheGuideRepeatsIt()
    {
        var labels = Glossary(TargetLanguage.DE, "**Settings → Account**, then **Settings → Account** again, then **account**.");

        Assert.Single(labels, l => l.English == "Account");
        Assert.Single(labels, l => l.English == "Settings");
    }

    [Fact]
    public void TrailingPunctuationInsideTheBoldDoesNotHideTheLabel()
    {
        Assert.Contains(new HelpLabel("Tool access", "Tool-Zugriff"), Glossary(TargetLanguage.DE, "**Tool access:** pick a tier."));
    }
}
