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
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("Optimized via PiaCloud", 0));

        var service = CreateService();
        var result = await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Optimized via PiaCloud", result.OptimizedText);
        await _aiClientService.Received(1).OptimizeViaPiaCloudAsync(
            Arg.Is<string>(p => p.Contains("hello world") && p.Contains(BusinessEmailTemplate.Prompt)),
            BusinessEmailTemplateId, "EN", false, Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_PiaCloud_SendsFullPromptWithInput()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(PiaCloudProvider);
        _aiClientService.OptimizeViaPiaCloudAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("result", 0));

        var service = CreateService();
        await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        // PiaCloud path sends the full constructed prompt (template + language + input)
        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            Arg.Is<string>(p =>
                p.Contains(BusinessEmailTemplate.Prompt) &&
                p.Contains("Target language: EN") &&
                p.Contains("hello world")),
            BusinessEmailTemplateId,
            "EN",
            false,
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
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("result", 0));

        var service = CreateService();
        await service.OptimizeTextAsync("<voice>um hello world</voice>", BusinessEmailTemplateId, cancellationToken: TestContext.Current.CancellationToken);

        // Should strip voice tags from the input in the constructed prompt and set isVoiceInput = true
        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            Arg.Is<string>(p =>
                p.Contains("um hello world") &&
                !p.Contains("<voice>") &&
                p.Contains("transcribed from spoken word")),
            BusinessEmailTemplateId,
            "EN",
            true,
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
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiCompletionResult("Ergebnis", 0));

        var service = CreateService();
        await service.OptimizeTextAsync("hello", BusinessEmailTemplateId, targetLanguage: "DE", cancellationToken: TestContext.Current.CancellationToken);

        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            Arg.Is<string>(p => p.Contains("Target language: DE") && p.Contains("hello")),
            BusinessEmailTemplateId,
            "DE",
            false,
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
}
