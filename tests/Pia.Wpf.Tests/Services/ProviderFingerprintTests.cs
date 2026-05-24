using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class ProviderFingerprintTests
{
    private static AiProvider Make(
        AiProviderType type = AiProviderType.OpenAI,
        string endpoint = "https://api.openai.com/v1",
        string? model = "gpt-5.5",
        string? deployment = null,
        string name = "anything",
        Guid? id = null)
        => new AiProvider
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            ProviderType = type,
            Endpoint = endpoint,
            ModelName = model,
            AzureDeploymentName = deployment,
        };

    [Fact]
    public void PiaCloud_byType_yields_sentinel()
    {
        var p = Make(type: AiProviderType.PiaCloud, endpoint: "");
        Assert.Equal(ProviderFingerprint.PiaCloudSentinel, ProviderFingerprint.Compute(p));
    }

    [Fact]
    public void PiaCloud_byFixedId_yields_sentinel_even_with_other_values()
    {
        var p = Make(id: ProviderService.PiaCloudProviderId);
        Assert.Equal(ProviderFingerprint.PiaCloudSentinel, ProviderFingerprint.Compute(p));
    }

    [Fact]
    public void Name_does_not_affect_fingerprint()
    {
        var a = Make(name: "GPT 5.5");
        var b = Make(name: "Something else");
        Assert.Equal(ProviderFingerprint.Compute(a), ProviderFingerprint.Compute(b));
    }

    [Fact]
    public void Different_models_produce_different_fingerprints()
    {
        var a = Make(model: "gpt-5.4");
        var b = Make(model: "gpt-5.5");
        Assert.NotEqual(ProviderFingerprint.Compute(a), ProviderFingerprint.Compute(b));
    }

    [Fact]
    public void Different_endpoints_produce_different_fingerprints()
    {
        var a = Make(endpoint: "https://api.openai.com/v1");
        var b = Make(endpoint: "https://api.anthropic.com/v1");
        Assert.NotEqual(ProviderFingerprint.Compute(a), ProviderFingerprint.Compute(b));
    }

    [Fact]
    public void Different_types_with_same_endpoint_produce_different_fingerprints()
    {
        var a = Make(type: AiProviderType.OpenAI);
        var b = Make(type: AiProviderType.OpenAICompatible);
        Assert.NotEqual(ProviderFingerprint.Compute(a), ProviderFingerprint.Compute(b));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "HTTPS://API.OPENAI.COM/v1")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/")]
    [InlineData("https://api.openai.com/v1", "  https://api.openai.com/v1  ")]
    public void Endpoint_normalization_makes_near_equals_equal(string left, string right)
    {
        var a = Make(endpoint: left);
        var b = Make(endpoint: right);
        Assert.Equal(ProviderFingerprint.Compute(a), ProviderFingerprint.Compute(b));
    }

    [Fact]
    public void Model_normalization_is_case_and_whitespace_insensitive()
    {
        var a = Make(model: "GPT-5.5");
        var b = Make(model: " gpt-5.5 ");
        Assert.Equal(ProviderFingerprint.Compute(a), ProviderFingerprint.Compute(b));
    }

    [Fact]
    public void Azure_deployment_is_part_of_fingerprint()
    {
        var a = Make(type: AiProviderType.AzureOpenAI, deployment: "prod");
        var b = Make(type: AiProviderType.AzureOpenAI, deployment: "staging");
        Assert.NotEqual(ProviderFingerprint.Compute(a), ProviderFingerprint.Compute(b));
    }

    [Fact]
    public void Null_and_empty_model_treated_equivalently()
    {
        var a = Make(model: null);
        var b = Make(model: "");
        Assert.Equal(ProviderFingerprint.Compute(a), ProviderFingerprint.Compute(b));
    }
}
