using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Unit tests for <see cref="AiIngestSynthesisService"/> — the SUMMARY/body split parser, graceful
/// degradation when no provider is configured, and re-identification of PII placeholders before the
/// synthesized page is returned for persistence. No live LLM.
/// </summary>
public class AiIngestSynthesisServiceTests
{
    private static ISettingsService NewSettings(bool tokenizationEnabled = true)
    {
        var settings = Substitute.For<ISettingsService>();
        var app = new AppSettings();
        app.Privacy.TokenizationEnabled = tokenizationEnabled;
        settings.GetSettingsAsync().Returns(app);
        return settings;
    }

    private static ITokenMapService NewEmptyTokenMap()
    {
        var pii = Substitute.For<IPiiDetector>();
        var memory = Substitute.For<IMemoryService>();
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        memory.GetObjectsByTypeAsync(Arg.Any<string>()).Returns(new List<MemoryObject>());
        return new TokenMapService(pii, memory, settings);
    }

    [Fact]
    public async Task SynthesizeAsync_returns_empty_body_when_no_provider()
    {
        var svc = new AiIngestSynthesisService(
            new ThrowingAiClient(),
            new NullProviderService(),
            NewEmptyTokenMap,
            NewSettings(),
            NullLogger<AiIngestSynthesisService>.Instance);

        var page = await svc.SynthesizeAsync(
            "Pia", "product", "charter",
            [("sources/a.md", "some raw text")], []);

        Assert.Equal(string.Empty, page.Body);
        Assert.Equal(string.Empty, page.Summary);
    }

    [Fact]
    public async Task SynthesizeAsync_reidentifies_mangled_pii_placeholder_before_returning()
    {
        // A real token map that maps the canonical token [Person_1] -> "Alice Anderson".
        var pii = Substitute.For<IPiiDetector>();
        var memory = Substitute.For<IMemoryService>();
        var settings = NewSettings();
        memory.GetObjectsByTypeAsync(Arg.Any<string>()).Returns(new List<MemoryObject>());
        var tokenMap = new TokenMapService(pii, memory, settings);
        tokenMap.Tokenize("Alice Anderson", "Person"); // -> [Person_1]

        // The bug this guards: the model wove the token into prose in a MANGLED (lowercased) form the
        // decorator's strict, case-sensitive Detokenize cannot recover — so without the loose pass the
        // placeholder would be persisted to the topic page. Prove the strict path misses it first.
        Assert.Equal("[person_1]", tokenMap.Detokenize("[person_1]"));

        var aiClient = new StubAiClient(
            "SUMMARY: About [person_1].\n\n[person_1] leads the project.");

        var svc = new AiIngestSynthesisService(
            aiClient,
            new SingleProviderService(),
            () => tokenMap,
            settings,
            NullLogger<AiIngestSynthesisService>.Instance);

        var page = await svc.SynthesizeAsync(
            "Alice", "person", "charter",
            [("sources/a.md", "Alice Anderson leads the project.")], []);

        Assert.Equal("About Alice Anderson.", page.Summary);
        Assert.Equal("Alice Anderson leads the project.", page.Body);
        Assert.DoesNotContain("person_1", page.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SynthesizeAsync_leaves_output_untouched_when_tokenization_disabled()
    {
        var aiClient = new StubAiClient("SUMMARY: Plain summary.\n\nPlain body about Alice Anderson.");
        var svc = new AiIngestSynthesisService(
            aiClient,
            new SingleProviderService(),
            NewEmptyTokenMap,
            NewSettings(tokenizationEnabled: false),
            NullLogger<AiIngestSynthesisService>.Instance);

        var page = await svc.SynthesizeAsync(
            "Alice", "person", "charter",
            [("sources/a.md", "Alice Anderson leads the project.")], []);

        Assert.Equal("Plain body about Alice Anderson.", page.Body);
    }

    [Fact]
    public async Task SynthesizeAsync_grounds_the_prompt_in_the_known_slugs()
    {
        var aiClient = new StubAiClient("SUMMARY: s.\n\nbody");
        var svc = new AiIngestSynthesisService(
            aiClient, new SingleProviderService(), NewEmptyTokenMap,
            NewSettings(tokenizationEnabled: false),
            NullLogger<AiIngestSynthesisService>.Instance);

        await svc.SynthesizeAsync("Acme", "organization", "charter",
            [("sources/a.md", "raw")], ["acme-corp", "globex-inc"]);

        // The exact slugs are embedded and the model is told to link ONLY to them.
        Assert.Contains("acme-corp", aiClient.LastPrompt!);
        Assert.Contains("globex-inc", aiClient.LastPrompt!);
        Assert.Contains("ONLY when the topic's slug appears in the list", aiClient.LastPrompt!);
        Assert.DoesNotContain("lowercase-hyphen form", aiClient.LastPrompt!); // old freeform instruction gone
    }

    [Fact]
    public async Task SynthesizeAsync_forbids_links_when_no_known_slugs()
    {
        var aiClient = new StubAiClient("SUMMARY: s.\n\nbody");
        var svc = new AiIngestSynthesisService(
            aiClient, new SingleProviderService(), NewEmptyTokenMap,
            NewSettings(tokenizationEnabled: false),
            NullLogger<AiIngestSynthesisService>.Instance);

        await svc.SynthesizeAsync("Acme", "organization", "charter",
            [("sources/a.md", "raw")], []);

        Assert.Contains("Do NOT output any [[...]]", aiClient.LastPrompt!);
    }

    [Fact]
    public async Task SynthesizeAsync_withholds_the_slug_list_when_tokenization_enabled()
    {
        // Privacy: slugs are name-derived and the PII tokenizer can't mask the hyphenated form, so the
        // explicit roster must NOT be embedded when tokenization is on. The reconciler still guarantees
        // link integrity, so a generic (list-free) instruction is used instead.
        var aiClient = new StubAiClient("SUMMARY: s.\n\nbody");
        var svc = new AiIngestSynthesisService(
            aiClient, new SingleProviderService(), NewEmptyTokenMap,
            NewSettings(tokenizationEnabled: true),
            NullLogger<AiIngestSynthesisService>.Instance);

        await svc.SynthesizeAsync("Acme", "organization", "charter",
            [("sources/a.md", "raw")], ["aylin-demir", "marco-altmann"]);

        Assert.DoesNotContain("aylin-demir", aiClient.LastPrompt!);
        Assert.DoesNotContain("marco-altmann", aiClient.LastPrompt!);
        Assert.DoesNotContain("Known topic slugs", aiClient.LastPrompt!);
        Assert.Contains("lowercase-hyphen form", aiClient.LastPrompt!); // generic instruction instead
    }

    [Fact]
    public void ParseSynthesis_splits_summary_and_body()
    {
        var page = AiIngestSynthesisService.ParseSynthesis(
            "SUMMARY: Pia is an assistant.\n\nPia is a privacy-first AI assistant.\nIt runs on Windows.");

        Assert.Equal("Pia is an assistant.", page.Summary);
        Assert.Equal("Pia is a privacy-first AI assistant.\nIt runs on Windows.", page.Body);
    }

    [Fact]
    public void ParseSynthesis_uses_first_line_as_summary_when_no_marker()
    {
        var page = AiIngestSynthesisService.ParseSynthesis(
            "Pia is a privacy-first AI assistant.\nIt runs on Windows.");

        Assert.Equal("Pia is a privacy-first AI assistant.", page.Summary);
        Assert.Equal("Pia is a privacy-first AI assistant.\nIt runs on Windows.", page.Body);
    }

    [Fact]
    public void ParseSynthesis_returns_empty_page_for_blank_output()
    {
        var page = AiIngestSynthesisService.ParseSynthesis("   \n  ");
        Assert.Equal(string.Empty, page.Body);
        Assert.Equal(string.Empty, page.Summary);
    }

    private sealed class NullProviderService : IProviderService
    {
#pragma warning disable CS0067 // Event is never used in tests.
        public event EventHandler? ProvidersChanged;
#pragma warning restore CS0067

        public Task<AiProvider?> GetDefaultProviderAsync() => Task.FromResult<AiProvider?>(null);
        public Task<IReadOnlyList<AiProvider>> GetProvidersAsync() => Task.FromResult<IReadOnlyList<AiProvider>>([]);
        public Task<AiProvider?> GetProviderAsync(Guid id) => Task.FromResult<AiProvider?>(null);
        public Task<AiProvider?> GetDefaultProviderForModeAsync(WindowMode mode) => Task.FromResult<AiProvider?>(null);
        public Task<AiProvider> AddProviderAsync(AiProvider provider, string? apiKey) => throw new NotImplementedException();
        public Task UpdateProviderAsync(AiProvider provider, string? newApiKey = null) => throw new NotImplementedException();
        public Task DeleteProviderAsync(Guid id) => throw new NotImplementedException();
        public string? GetDecryptedApiKey(AiProvider provider) => null;
        public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider) => throw new NotImplementedException();
        public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider, string? plainApiKey) => throw new NotImplementedException();
        public Task EnsureBuiltInProviderAsync() => Task.CompletedTask;
        public Task<List<string>> FetchModelsAsync(string endpoint, string? apiKey, AiProviderType providerType) => throw new NotImplementedException();
        public Task<bool> IsProviderActiveAsync(AiProvider provider) => Task.FromResult(false);
        public Task ReassignProviderIdAsync(Guid oldId, Guid newId, AiProvider merged) => Task.CompletedTask;
        public Task RepairModeDefaultsAsync() => Task.CompletedTask;
        public Task ConsolidateLocalDuplicatesAsync() => Task.CompletedTask;
    }

    private sealed class SingleProviderService : IProviderService
    {
#pragma warning disable CS0067 // Event is never used in tests.
        public event EventHandler? ProvidersChanged;
#pragma warning restore CS0067
        private readonly AiProvider _provider = new() { Name = "test", Endpoint = "http://localhost" };

        public Task<AiProvider?> GetDefaultProviderAsync() => Task.FromResult<AiProvider?>(_provider);
        public Task<IReadOnlyList<AiProvider>> GetProvidersAsync() => Task.FromResult<IReadOnlyList<AiProvider>>([_provider]);
        public Task<AiProvider?> GetProviderAsync(Guid id) => Task.FromResult<AiProvider?>(_provider);
        public Task<AiProvider?> GetDefaultProviderForModeAsync(WindowMode mode) => Task.FromResult<AiProvider?>(_provider);
        public Task<AiProvider> AddProviderAsync(AiProvider provider, string? apiKey) => throw new NotImplementedException();
        public Task UpdateProviderAsync(AiProvider provider, string? newApiKey = null) => throw new NotImplementedException();
        public Task DeleteProviderAsync(Guid id) => throw new NotImplementedException();
        public string? GetDecryptedApiKey(AiProvider provider) => null;
        public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider) => throw new NotImplementedException();
        public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider, string? plainApiKey) => throw new NotImplementedException();
        public Task EnsureBuiltInProviderAsync() => Task.CompletedTask;
        public Task<List<string>> FetchModelsAsync(string endpoint, string? apiKey, AiProviderType providerType) => throw new NotImplementedException();
        public Task<bool> IsProviderActiveAsync(AiProvider provider) => Task.FromResult(true);
        public Task ReassignProviderIdAsync(Guid oldId, Guid newId, AiProvider merged) => Task.CompletedTask;
        public Task RepairModeDefaultsAsync() => Task.CompletedTask;
        public Task ConsolidateLocalDuplicatesAsync() => Task.CompletedTask;
    }

    private sealed class StubAiClient : IAiClientService
    {
        private readonly string _response;
        public StubAiClient(string response) => _response = response;

        public string? LastPrompt { get; private set; }

        public Task<AiCompletionResult> SendRequestAsync(AiProvider provider, string prompt, CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(new AiCompletionResult(_response, 0));
        }

        public IAsyncEnumerable<string> StreamChatCompletionAsync(IList<ChatMessage> messages, AiProvider provider, string? mode = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ChatResponse> GetChatResponseAsync(IList<ChatMessage> messages, AiProvider provider, IList<AITool>? tools = null, string? mode = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatStreamItem> GetChatCompletionWithToolsAsync(IList<ChatMessage> messages, AiProvider provider, IList<AITool>? tools = null, Func<FunctionCallContent, Task<object?>>? toolHandler = null, string? mode = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AiCompletionResult> OptimizeViaPiaCloudAsync(string text, Guid templateId, string language, bool isVoiceInput, string? mode = null, string? customPrompt = null, string? customTemplateName = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GeneratePromptViaPiaCloudAsync(string styleDescription, string? mode = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class ThrowingAiClient : IAiClientService
    {
        public Task<AiCompletionResult> SendRequestAsync(AiProvider provider, string prompt, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not be called when no provider is configured.");

        public IAsyncEnumerable<string> StreamChatCompletionAsync(IList<ChatMessage> messages, AiProvider provider, string? mode = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ChatResponse> GetChatResponseAsync(IList<ChatMessage> messages, AiProvider provider, IList<AITool>? tools = null, string? mode = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatStreamItem> GetChatCompletionWithToolsAsync(IList<ChatMessage> messages, AiProvider provider, IList<AITool>? tools = null, Func<FunctionCallContent, Task<object?>>? toolHandler = null, string? mode = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AiCompletionResult> OptimizeViaPiaCloudAsync(string text, Guid templateId, string language, bool isVoiceInput, string? mode = null, string? customPrompt = null, string? customTemplateName = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GeneratePromptViaPiaCloudAsync(string styleDescription, string? mode = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
