using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public class AnswerProvenanceTests
{
    private static AiProvider Provider(AiProviderType type, string name = "My provider", string? model = "cfg-model") =>
        new() { Name = name, ProviderType = type, Endpoint = "https://example.invalid", ModelName = model };

    [Fact]
    public void PiaCloud_IsNamedAsTheServiceOnly_WhateverTheUpstreamReported()
    {
        var (model, provider) = AnswerProvenance.Describe(Provider(AiProviderType.PiaCloud, "Pia Cloud", null), "gpt-4o-2024-08-06");

        Assert.Equal("Pia Cloud", model);
        Assert.Null(provider);
    }

    [Fact]
    public void Byok_PrefersTheModelTheResponseReported()
    {
        var (model, provider) = AnswerProvenance.Describe(Provider(AiProviderType.OpenAI, model: "gpt-4o"), "gpt-4o-2024-08-06");

        Assert.Equal("gpt-4o-2024-08-06", model);
        Assert.Equal("OpenAI", provider);
    }

    [Fact]
    public void Byok_FallsBackToTheConfiguredModel_ThenTheProviderName()
    {
        Assert.Equal(("llama3", "Ollama"), AnswerProvenance.Describe(Provider(AiProviderType.Ollama, model: "llama3"), null));
        Assert.Equal(("Work LLM", "vLLM"), AnswerProvenance.Describe(Provider(AiProviderType.VLlm, "Work LLM", model: " "), ""));
    }

    [Theory]
    [InlineData(AiProviderType.AzureOpenAI, "Azure OpenAI")]
    [InlineData(AiProviderType.OpenRouter, "OpenRouter")]
    [InlineData(AiProviderType.Mistral, "Mistral")]
    public void ProviderLabel_IsTheHumanNameOfTheType(AiProviderType type, string expected)
    {
        Assert.Equal(expected, AnswerProvenance.ProviderLabel(Provider(type)));
    }

    [Fact]
    public void OnlyPiaCloudAnswers_AreRateable()
    {
        Assert.True(new AnswerStats(null, AnswerProvenance.PiaCloudLabel).IsPiaCloud);
        Assert.False(new AnswerStats(10, "gpt-4o", "OpenAI").IsPiaCloud);
        Assert.False(new AnswerStats(10, AnswerProvenance.PiaCloudLabel, "OpenAI-compatible").IsPiaCloud);

        var message = new AssistantMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "answer");
        Assert.False(message.IsRateable);
        message.Stats = new AnswerStats(12, AnswerProvenance.PiaCloudLabel);
        Assert.True(message.IsRateable);
    }

    [Fact]
    public void OpenAiCompatible_IsNamedByTheUsersOwnProviderName()
    {
        Assert.Equal("Firmen-Gateway", AnswerProvenance.ProviderLabel(Provider(AiProviderType.OpenAICompatible, "Firmen-Gateway")));
        Assert.Equal("OpenAI-compatible", AnswerProvenance.ProviderLabel(Provider(AiProviderType.OpenAICompatible, "  ")));
    }
}
