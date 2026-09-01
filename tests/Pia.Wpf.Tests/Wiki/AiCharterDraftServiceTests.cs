using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Models.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Prompt-assembly tests for <see cref="AiCharterDraftService"/>. No live provider: the AI client is a
/// substitute, so what is asserted is which sources reach the prompt and how far they are truncated.
/// </summary>
public class AiCharterDraftServiceTests : IDisposable
{
    private readonly string _vaultRoot;
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IVaultSourcesService _sources = Substitute.For<IVaultSourcesService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    public AiCharterDraftServiceTests()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), $"pia-charterdraft-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(_vaultRoot, "sources"));
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "P", Endpoint = "http://p" });
        _ai.SendRequestAsync(Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new AiCompletionResult("  This vault is about widgets.  ", 0));
        var settings = new AppSettings();
        settings.Privacy.TokenizationEnabled = false;
        _settings.GetSettingsAsync().Returns(settings);
    }

    public void Dispose()
    {
        TempPath.Remove(_vaultRoot);
    }

    private AiCharterDraftService Build() => new(
        _ai, _providers, _sources, new VaultStore(_vaultRoot, new MarkdownVaultParser()),
        () => throw new InvalidOperationException("tokenization is off in these tests"),
        _settings, NullLogger<AiCharterDraftService>.Instance);

    private void Seed(string name, string content)
    {
        File.WriteAllText(Path.Combine(_vaultRoot, "sources", name), content);
        var current = _sources.ListSourcesAsync().Result?.ToList() ?? [];
        current.Add(new VaultSourceItem($"sources/{name}", name, content.Length, DateTime.MinValue, true, 0));
        _sources.ListSourcesAsync().Returns(current);
    }

    private string CapturedPrompt()
    {
        var call = _ai.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IAiClientService.SendRequestAsync));
        return (string)call.GetArguments()[1]!;
    }

    [Fact]
    public async Task Returns_empty_when_no_provider_is_configured()
    {
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns((AiProvider?)null);
        Seed("a.txt", "content");

        Assert.Equal(string.Empty, await Build().DraftAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Returns_empty_and_asks_nothing_when_there_are_no_sources()
    {
        _sources.ListSourcesAsync().Returns([]);

        Assert.Equal(string.Empty, await Build().DraftAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_ai.ReceivedCalls());
    }

    [Fact]
    public async Task Trims_the_returned_draft()
    {
        Seed("a.txt", "content");

        Assert.Equal("This vault is about widgets.", await Build().DraftAsync(TestContext.Current.CancellationToken));
    }

    // Breadth over depth: a charter describes the vault, so a long document must not crowd the others out.
    [Fact]
    public async Task Truncates_each_source_and_caps_how_many_are_read()
    {
        for (var i = 0; i < 15; i++)
        {
            Seed($"s{i:00}.txt", new string('x', 5000));
        }

        await Build().DraftAsync(TestContext.Current.CancellationToken);

        var prompt = CapturedPrompt();
        Assert.Equal(12, prompt.Split("--- DOCUMENT:").Length - 1);
        Assert.DoesNotContain(new string('x', 2001), prompt, StringComparison.Ordinal);
    }

    // The excerpts are meeting transcripts and the model rewrites them, so the decorator needs a map
    // published for this run — without one a mangled placeholder would be persisted to a synced page.
    [Fact]
    public async Task Publishes_an_ambient_token_map_around_the_call_when_tokenization_is_on()
    {
        var settings = new AppSettings();
        settings.Privacy.TokenizationEnabled = true;
        _settings.GetSettingsAsync().Returns(settings);
        Seed("a.txt", "content");

        var memory = Substitute.For<IMemoryService>();
        memory.GetObjectsByTypeAsync(Arg.Any<string>()).Returns(new List<MemoryObject>());
        var map = new TokenMapService(Substitute.For<IPiiDetector>(), memory, _settings);

        ITokenMapService? ambientDuringCall = null;
        _ai.SendRequestAsync(Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(_ =>
            {
                ambientDuringCall = TokenMapAmbient.Current;
                return new AiCompletionResult("drafted", 0);
            });

        var svc = new AiCharterDraftService(
            _ai, _providers, _sources, new VaultStore(_vaultRoot, new MarkdownVaultParser()),
            () => map, _settings, NullLogger<AiCharterDraftService>.Instance);
        await svc.DraftAsync(TestContext.Current.CancellationToken);

        Assert.Same(map, ambientDuringCall);
        Assert.Null(TokenMapAmbient.Current); // restored after the call
    }

    [Fact]
    public async Task Skips_a_non_text_source()
    {
        Seed("notes.txt", "readable");
        _sources.ListSourcesAsync().Returns(new List<VaultSourceItem>
        {
            new("sources/notes.txt", "notes.txt", 8, DateTime.MinValue, true, 0),
            new("sources/image.png", "image.png", 8, DateTime.MinValue, false, 0),
        });

        await Build().DraftAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("image.png", CapturedPrompt(), StringComparison.Ordinal);
    }
}
