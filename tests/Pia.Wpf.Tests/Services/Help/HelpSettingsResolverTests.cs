using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services;
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

    private static HelpSettingsResolver Build(AppSettings settings, out ILocalizationService localization)
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(settings);

        localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(call => "«" + call.Arg<string>() + "»");

        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns<IReadOnlyList<Persona>>([]);

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns<IReadOnlyList<AiProvider>>([]);

        return new HelpSettingsResolver(settingsService, localization, personas, providers);
    }

    [Fact]
    public async Task EveryAreaProducesRowsAndEveryRowNamesWhereToChangeIt()
    {
        var resolver = Build(new AppSettings(), out _);

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
        var resolver = Build(new AppSettings(), out _);

        var rows = await resolver.DescribeAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(HelpSettingsResolver.Areas.Count, rows.Select(r => r.Area).Distinct().Count());
    }

    [Fact]
    public async Task AnUnrecognisedAreaFallsBackToEverythingRatherThanNothing()
    {
        var resolver = Build(new AppSettings(), out _);

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
        var resolver = Build(new AppSettings { TtsVoiceModelKey = "de_DE-thorsten-medium", TtsEnabled = true }, out _);

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
        var resolver = Build(new AppSettings { UiLanguage = TargetLanguage.FR }, out _);

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
        };
        var resolver = Build(settings, out _);

        var text = string.Join("\n", (await resolver.DescribeAsync(null, TestContext.Current.CancellationToken)).Select(r => $"{r.Label}: {r.Value}"));

        foreach (var secret in new[] { "someone@example.com", "user-123", "device-abc", "ENCRYPTED-UMK-MATERIAL" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        // The host is useful ("am I on my company server?"); the path and port are not the model's business.
        Assert.Contains("pia.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("8443", text, StringComparison.Ordinal);
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

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(settings);
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(call => call.Arg<string>());
        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns<IReadOnlyList<Persona>>([]);
        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns<IReadOnlyList<AiProvider>>([provider]);

        var resolver = new HelpSettingsResolver(settingsService, localization, personas, providers);

        var rows = await resolver.DescribeAsync("providers", TestContext.Current.CancellationToken);

        Assert.Contains(rows, r => r.Value.Contains("cannot call tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheToolReportsTheAreasItAcceptsSoTheModelCanNarrow()
    {
        var handler = new HelpToolHandler(
            new HelpSearchService(),
            Build(new AppSettings(), out _),
            NullLogger<HelpToolHandler>.Instance);

        var result = await handler.HandleToolCallAsync(
            new Microsoft.Extensions.AI.FunctionCallContent("1", "pia_settings", new Dictionary<string, object?>()), TestContext.Current.CancellationToken);

        var text = Assert.IsType<string>(result);
        foreach (var area in HelpSettingsResolver.Areas)
        {
            Assert.Contains(area, text, StringComparison.Ordinal);
        }
    }
}
