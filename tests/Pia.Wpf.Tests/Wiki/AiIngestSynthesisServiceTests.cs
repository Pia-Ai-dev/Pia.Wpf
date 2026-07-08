using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Unit tests for <see cref="AiIngestSynthesisService"/> — the SUMMARY/body split parser and graceful
/// degradation when no provider is configured. No live LLM.
/// </summary>
public class AiIngestSynthesisServiceTests
{
    [Fact]
    public async Task SynthesizeAsync_returns_empty_body_when_no_provider()
    {
        var svc = new AiIngestSynthesisService(
            new ThrowingAiClient(),
            new NullProviderService(),
            NullLogger<AiIngestSynthesisService>.Instance);

        var page = await svc.SynthesizeAsync(
            "Pia", "product", "charter",
            [("sources/a.md", "some raw text")]);

        Assert.Equal(string.Empty, page.Body);
        Assert.Equal(string.Empty, page.Summary);
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
