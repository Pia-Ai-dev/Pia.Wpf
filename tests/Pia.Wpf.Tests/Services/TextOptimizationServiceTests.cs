using FluentAssertions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
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
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("Optimized via PiaCloud");

        var service = CreateService();
        var result = await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId);

        result.OptimizedText.Should().Be("Optimized via PiaCloud");
        await _aiClientService.Received(1).OptimizeViaPiaCloudAsync(
            "hello world", BusinessEmailTemplateId, "EN", false, Arg.Any<CancellationToken>());
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
            .Returns("Optimized via OpenAI");

        var service = CreateService();
        var result = await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId);

        result.OptimizedText.Should().Be("Optimized via OpenAI");
        await _aiClientService.Received(1).SendRequestAsync(
            OpenAiProvider, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _aiClientService.DidNotReceive().OptimizeViaPiaCloudAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OptimizeTextAsync_PiaCloud_SendsRawTextNotPrompt()
    {
        _templateService.GetTemplateAsync(BusinessEmailTemplateId)
            .Returns(BusinessEmailTemplate);
        _providerService.GetDefaultProviderAsync()
            .Returns(PiaCloudProvider);
        _aiClientService.OptimizeViaPiaCloudAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("result");

        var service = CreateService();
        await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId);

        // PiaCloud path should send raw text, not the constructed prompt
        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            "hello world",
            BusinessEmailTemplateId,
            "EN",
            false,
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
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("result");

        var service = CreateService();
        await service.OptimizeTextAsync("<voice>um hello world</voice>", BusinessEmailTemplateId);

        // Should strip voice tags and set isVoiceInput = true
        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            "um hello world",
            BusinessEmailTemplateId,
            "EN",
            true,
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
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("Ergebnis");

        var service = CreateService();
        await service.OptimizeTextAsync("hello", BusinessEmailTemplateId, targetLanguage: "DE");

        await _aiClientService.Received().OptimizeViaPiaCloudAsync(
            "hello",
            BusinessEmailTemplateId,
            "DE",
            false,
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
            .Returns("result");

        var service = CreateService();
        await service.OptimizeTextAsync("hello world", BusinessEmailTemplateId);

        // OpenAI path should build the full prompt client-side
        await _aiClientService.Received().SendRequestAsync(
            OpenAiProvider,
            Arg.Is<string>(p =>
                p.Contains("Transform this text into a professional business email") &&
                p.Contains("Please answer in EN") &&
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
            .Returns("result");

        var service = CreateService();
        await service.OptimizeTextAsync("<voice>um hello</voice>", BusinessEmailTemplateId);

        await _aiClientService.Received().SendRequestAsync(
            OpenAiProvider,
            Arg.Is<string>(p =>
                p.Contains("transcribed from spoken word") &&
                p.Contains("um hello") &&
                !p.Contains("<voice>")),
            Arg.Any<CancellationToken>());
    }
}
