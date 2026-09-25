using System.Globalization;
using NSubstitute;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services.Help;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services.Help;

/// <summary>
/// The settings half exists because the guide describes a build and this describes the machine. Two
/// things have to hold: the navigation path must be one the user can actually read on screen in their
/// own language, and nothing secret may ride out to the AI provider.
/// </summary>
public sealed class HelpSettingsResolverTests
{
    private static readonly CultureInfo[] ShippedCultures =
        [CultureInfo.InvariantCulture, new("de"), new("fr")];

    internal static HelpSettingsResolver Build(
        AppSettings settings,
        ILocalizationService? localization = null,
        IReadOnlyList<AiProvider>? providerList = null,
        IReadOnlyList<OptimizationTemplate>? templateList = null)
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(settings);

        if (localization is null)
        {
            localization = Substitute.For<ILocalizationService>();
            localization[Arg.Any<string>()].Returns(call => "«" + call.Arg<string>() + "»");
        }

        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns<IReadOnlyList<Persona>>([]);

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(providerList ?? []);

        var templates = Substitute.For<ITemplateService>();
        templates.GetTemplatesAsync().Returns(templateList ?? []);

        return new HelpSettingsResolver(settingsService, localization, personas, providers, templates);
    }

    [Fact]
    public async Task EveryAreaProducesRowsAndEveryRowNamesWhereToChangeIt()
    {
        var resolver = Build(new AppSettings());

        foreach (var area in HelpSettingsResolver.Areas)
        {
            var rows = await resolver.DescribeAsync(area, TestContext.Current.CancellationToken);

            Assert.NotEmpty(rows);
            Assert.All(rows, row =>
            {
                Assert.Equal(area, row.Area);
                Assert.False(string.IsNullOrWhiteSpace(row.Label));
                Assert.False(string.IsNullOrWhiteSpace(row.Value));
                Assert.Contains("«Nav_Settings»", row.Path, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public async Task OmittingTheAreaReturnsEveryArea()
    {
        var resolver = Build(new AppSettings());

        var rows = await resolver.DescribeAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(HelpSettingsResolver.Areas.Count, rows.Select(r => r.Area).Distinct().Count());
    }

    [Fact]
    public async Task AnUnrecognisedAreaFallsBackToEverythingRatherThanNothing()
    {
        var resolver = Build(new AppSettings());

        Assert.NotEmpty(await resolver.DescribeAsync("wobble", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EveryLocalizationKeyThePathsAreBuiltFromResolvesInAllThreeLanguages()
    {
        foreach (var key in HelpSettingsResolver.PathLocalizationKeys)
        {
            foreach (var culture in ShippedCultures)
            {
                var value = ViewStrings.ResourceManager.GetString(key, culture);
                Assert.False(string.IsNullOrWhiteSpace(value),
                    $"'{key}' has no {culture.Name switch { "" => "English", var n => n }} translation, so the path Pia quotes would be blank");
            }
        }
    }

    [Fact]
    public async Task TheSpokenLanguageAnswerNamesTheVoiceRatherThanAnImaginarySetting()
    {
        var resolver = Build(new AppSettings { TtsVoiceModelKey = "de_DE-thorsten-medium", TtsEnabled = true });

        var rows = await resolver.DescribeAsync("speech", TestContext.Current.CancellationToken);
        var voice = rows.Single(r => r.Label == "Active voice");
        var language = rows.Single(r => r.Label == "Spoken output language");

        Assert.Contains("Thorsten", voice.Value, StringComparison.Ordinal);
        Assert.Contains("German", voice.Value, StringComparison.Ordinal);
        Assert.Contains("property of the voice", language.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAnswerLanguageRowSaysItFollowsTheInterfaceLanguage()
    {
        var resolver = Build(new AppSettings { UiLanguage = TargetLanguage.FR });

        var rows = await resolver.DescribeAsync("language", TestContext.Current.CancellationToken);

        Assert.Contains(rows, r => r.Value.Contains("French", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Label == "Answer language" && r.Value.Contains("no separate setting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSecretEverReachesTheModel()
    {
        var settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "https://pia.example.com:8443/api",
            SyncUserId = "user-123",
            SyncUserEmail = "someone@example.com",
            SyncDeviceId = "device-abc",
            E2EEEncryptedUmk = "ENCRYPTED-UMK-MATERIAL",
            Privacy = new PrivacySettings { PiiKeywords = [new PiiKeywordEntry { Keyword = "Project Nightingale" }] },
        };
        var resolver = Build(settings);

        var text = string.Join("\n", (await resolver.DescribeAsync(null, TestContext.Current.CancellationToken)).Select(r => $"{r.Label}: {r.Value}"));

        foreach (var secret in new[] { "someone@example.com", "user-123", "device-abc", "ENCRYPTED-UMK-MATERIAL", "Nightingale" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        // The host is useful ("am I on my company server?"); the path and port are not the model's business.
        Assert.Contains("pia.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("8443", text, StringComparison.Ordinal);

        // The count answers "did my keywords save?" without handing over the words themselves.
        Assert.Contains("1 configured", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledProviderCapabilityIsReportedBecauseItSilentlyDisablesEveryTool()
    {
        var provider = new AiProvider
        {
            Name = "Local llama",
            Endpoint = "http://localhost:11434",
            ModelName = "qwen3",
            SupportsToolCalling = false,
        };
        // UseSameProviderForAllModes defaults to true, and that path reads the Optimize slot then
        // DefaultProviderId — setting only the Assistant slot would silently resolve to nothing.
        var settings = new AppSettings { DefaultProviderId = provider.Id };

        var resolver = Build(settings, providerList: [provider]);

        var rows = await resolver.DescribeAsync("providers", TestContext.Current.CancellationToken);

        Assert.Contains(rows, r => r.Value.Contains("cannot call tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheToolReportsTheAreasItAcceptsSoTheModelCanNarrow()
    {
        var result = await HelpToolHandlerTests.Handler(TargetLanguage.EN).HandleToolCallAsync(
            new Microsoft.Extensions.AI.FunctionCallContent("1", "pia_settings", new Dictionary<string, object?>()), TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        foreach (var area in HelpSettingsResolver.Areas)
        {
            Assert.Contains(area, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheToolSchemaOffersEveryArea()
    {
        var tool = HelpToolHandlerTests.Handler(TargetLanguage.EN).GetTools()
            .OfType<Microsoft.Extensions.AI.AIFunction>()
            .Single(t => t.Name == "pia_settings");
        var description = tool.JsonSchema.GetProperty("properties").GetProperty("area").GetProperty("description").GetString();

        Assert.All(HelpSettingsResolver.Areas, area => Assert.Contains(area, description, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HotkeysReportTheShortcutOrThatNoneIsSet()
    {
        var resolver = Build(new AppSettings { FastPathHotkey = null });

        var rows = await resolver.DescribeAsync("hotkeys", TestContext.Current.CancellationToken);

        Assert.Contains(rows, r => r.Value == "Ctrl+Alt+P" && r.Path.EndsWith("«Settings_Hotkey_Assistant»", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Value == "not set" && r.Path.EndsWith("«Settings_Hotkey_FastPath»", StringComparison.Ordinal));
        Assert.All(rows, r => Assert.Contains("«Settings_InnerTab_Hotkeys»", r.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplicationRowsEndAtTheLabelOfTheSettingItself()
    {
        var resolver = Build(new AppSettings { LaunchAtStartup = false });

        var rows = await resolver.DescribeAsync("application", TestContext.Current.CancellationToken);

        Assert.Contains(rows, r => r.Value == "off" && r.Path.EndsWith("«Settings_InnerTab_Application» > «Settings_LaunchAtStartup»", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OptimizeReportsWhereTheResultGoesAndTheDefaultTemplate()
    {
        var template = new OptimizationTemplate { Name = "Formal e-mail", Prompt = "p" };
        var resolver = Build(
            new AppSettings { DefaultOutputAction = OutputAction.AutoType, DefaultTemplateId = template.Id },
            templateList: [template, new OptimizationTemplate { Name = "Casual", Prompt = "p" }]);

        var rows = await resolver.DescribeAsync("optimize", TestContext.Current.CancellationToken);

        Assert.Contains(rows, r => r.Value.Contains("typed", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Label.StartsWith("Default template", StringComparison.Ordinal) && r.Value == "Formal e-mail");
        Assert.Contains(rows, r => r.Value.Contains("Casual", StringComparison.Ordinal) && r.Path.EndsWith("«Settings_Tab_Templates»", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AboutReportsTheInstalledVersion()
    {
        var rows = await Build(new AppSettings()).DescribeAsync("about", TestContext.Current.CancellationToken);

        Assert.Contains(rows, r => r.Value == AppVersionInfo.Version && r.Path.EndsWith("«Settings_Tab_About»", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PluginsAreNamedUnderToolsWithTheirOwnTab()
    {
        var rows = await Build(new AppSettings()).DescribeAsync("tools", TestContext.Current.CancellationToken);

        Assert.Contains(rows, r => r.Path == "«Nav_Settings» > «Settings_Tab_Plugins»");
    }
}
