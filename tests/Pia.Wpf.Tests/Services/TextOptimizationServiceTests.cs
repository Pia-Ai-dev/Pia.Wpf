using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class TextOptimizationServiceTests
{
    private static readonly Guid BusinessEmailTemplateId = new("00000001-0000-0000-0000-000000000001");

    private static readonly OptimizationTemplate BusinessEmailTemplate = new()
    {
        Id = BusinessEmailTemplateId,
        Name = "Business Email",
        Prompt = "Transform this text into a professional business email.",
        IsBuiltIn = true
    };

    private static readonly AiProvider PiaCloudProvider = new()
    {
        Id = Guid.NewGuid(),
        Name = "Pia Cloud",
        ProviderType = AiProviderType.PiaCloud,
        Endpoint = "https://pia.example.com"
    };

    private static readonly AiProvider OpenAiProvider = new()
    {
        Id = Guid.NewGuid(),
        Name = "OpenAI",
        ProviderType = AiProviderType.OpenAI,
        Endpoint = "https://api.openai.com",
        EncryptedApiKey = "encrypted-key"
    };

    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IAiClientService _aiClientService = Substitute.For<IAiClientService>();

    private TextOptimizationService CreateService() =>
        new(_templateService, _providerService, _historyService, _aiClientService);

    [Fact]
    public async Task OptimizeTextAsync_PiaCloud_CallsOptimizeViaPiaCloud()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(PiaCloudProvider);
        _aiClientService.OptimizeViaPiaCloudAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("Optimized via PiaCloud", 0));

        var service = CreateService();
        var result = await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Optimized via PiaCloud", result.OptimizedText);
        await _aiClientService.Received(1).OptimizeViaPiaCloudAsync(
            "hello world",
            BusinessEmailTemplateId, "EN", false, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _aiClientService.DidNotReceive().SendRequestAsync(
            Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_OpenAI_CallsSendRequestAsync()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(OpenAiProvider);
        _aiClientService.SendRequestAsync(
                Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("Optimized via OpenAI", 0));

        var service = CreateService();
        var result = await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Optimized via OpenAI", result.OptimizedText);
        await _aiClientService.Received(1).SendRequestAsync(
            OpenAiProvider, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _aiClientService.DidNotReceive().OptimizeViaPiaCloudAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_PiaCloud_SendsRawInput()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(PiaCloudProvider);
        _aiClientService.OptimizeViaPiaCloudAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("result", 0));

        var service = CreateService();
        await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        // PiaCloud path forwards the raw input — the server is authoritative for the prompt.
        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            "hello world",
            BusinessEmailTemplateId,
            "EN",
            false,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_PiaCloud_VoiceInput_StripsTagsAndSetsFlag()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(PiaCloudProvider);
        _aiClientService.OptimizeViaPiaCloudAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("result", 0));

        var service = CreateService();
        await service.OptimizeTextAsync("<voice>um hello world</voice>", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        // Should strip voice tags from the raw input and set isVoiceInput = true so the server adds cleanup.
        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            "um hello world",
            BusinessEmailTemplateId,
            "EN",
            true,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_PiaCloud_ForwardsLanguage()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(PiaCloudProvider);
        _aiClientService.OptimizeViaPiaCloudAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("Ergebnis", 0));

        var service = CreateService();
        await service.OptimizeTextAsync("hello", BusinessEmailTemplateId, targetLanguage: "DE", cancellationToken: TestContext.Current.CancellationToken);

        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            "hello",
            BusinessEmailTemplateId,
            "DE",
            false,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_OpenAI_BuildsPromptClientSide()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(OpenAiProvider);
        _aiClientService.SendRequestAsync(
                Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("result", 0));

        var service = CreateService();
        await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        // OpenAI path should build the full prompt client-side
        await _aiClientService.Received().SendRequestAsync(
            OpenAiProvider,
            Arg.Is<string>(p =>
                p.Contains("Transform this text into a professional business email") &&
                p.Contains("Target language: EN") &&
                p.Contains("hello world")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_OpenAI_VoiceInput_AddsCleanupPrompt()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(OpenAiProvider);
        _aiClientService.SendRequestAsync(
                Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("result", 0));

        var service = CreateService();
        await service.OptimizeTextAsync("<voice>um hello</voice>", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        await _aiClientService.Received().SendRequestAsync(
            OpenAiProvider,
            Arg.Is<string>(p =>
                p.Contains("transcribed from spoken word") &&
                p.Contains("um hello") &&
                !p.Contains("<voice>")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_OpenAI_PropagatesTruncationAndDoesNotPersistSession()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(OpenAiProvider);
        _aiClientService.SendRequestAsync(
                Arg.Any<AiProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<AiCompletionResult>(_ => throw new LlmTruncatedException(OpenAiProvider.Name, 42));

        var service = CreateService();

        var ex = await Assert.ThrowsAsync<LlmTruncatedException>(
            () => service.OptimizeTextAsync("hello world", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(OpenAiProvider.Name, ex.ProviderName);
        Assert.Equal(42, ex.PartialLength);
        await _historyService.DidNotReceive().AddSessionAsync(Arg.Any<OptimizationSession>());
    }

    [Fact]
    public async Task GeneratePersonaDraftAsync_PiaCloud_DraftsAllFieldsViaChatEndpoint()
    {
        _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(PiaCloudProvider);

        const string json = """
            {
              "name": "Tax Advisor",
              "tagline": "Clear answers to German tax questions",
              "systemPrompt": "You are a meticulous German tax advisor.",
              "guardrails": "Do not give binding legal advice.",
              "outputFormat": "- Lead with the answer.\n- Cite the relevant paragraph.",
              "archetype": "analyst",
              "emoji": "📊",
              "accentColor": "#2962FF",
              "expertise": ["taxation", "finance"]
            }
            """;
        _aiClientService.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<AiProvider>(),
                Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ToStream(new TextDelta(json), new Finished(null, "pia-cloud")));

        var service = CreateService();
        var draft = await service.GeneratePersonaDraftAsync(
            "a German tax advisor");

        Assert.Equal("Tax Advisor", draft.Name);
        Assert.Equal("Clear answers to German tax questions", draft.Tagline);
        Assert.Equal("You are a meticulous German tax advisor.", draft.SystemPrompt);
        Assert.Equal("Do not give binding legal advice.", draft.Guardrails);
        Assert.Equal("- Lead with the answer.\n- Cite the relevant paragraph.", draft.OutputFormat);
        Assert.Equal("analyst", draft.Archetype);
        Assert.Equal("📊", draft.Emoji);
        Assert.Equal("#2962FF", draft.AccentColor);
        Assert.Equal(["taxation", "finance"], draft.Expertise);

        // The draft now streams through the general chat endpoint (tagged with the Assistant mode),
        // not the single-string PiaCloud prompt endpoint that only filled the system prompt.
        _aiClientService.Received(1).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(),
            PiaCloudProvider,
            Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(),
            nameof(WindowMode.Assistant),
            Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>());
        await _aiClientService.DidNotReceive().GeneratePromptViaPiaCloudAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<ChatStreamItem> ToStream(params ChatStreamItem[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }
}
