using Pia.Models;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The provider dialog offers only what the organization's policy allows, and will not save the rest.</summary>
public class ProviderEditModelPolicyTests
{
    [Fact]
    public void WithoutAPolicy_EveryEditableTypeIsOffered()
    {
        var model = new ProviderEditModel();

        model.ApplyPolicy(new AppSettings());

        Assert.Equal(ProviderEditModel.EditableProviderTypes, model.AvailableProviderTypes);
        Assert.True(model.IsWebSearchAllowed);
    }

    [Fact]
    public void OnlyAllowedTypesAreOffered()
    {
        var model = new ProviderEditModel();

        model.ApplyPolicy(new AppSettings { AllowedProviderTypes = ["AzureOpenAI", "Ollama"] });

        Assert.Equal([AiProviderType.AzureOpenAI, AiProviderType.Ollama], model.AvailableProviderTypes);
    }

    [Fact]
    public void AnExistingProviderOfARefusedType_StaysListed_ButCannotBeSaved()
    {
        var model = ProviderEditModel.FromProvider(new AiProvider
        {
            Name = "mine", ProviderType = AiProviderType.OpenAI, Endpoint = "https://api.openai.com/v1",
        });

        model.ApplyPolicy(new AppSettings { AllowedProviderTypes = ["AzureOpenAI"] });

        Assert.Contains(AiProviderType.OpenAI, model.AvailableProviderTypes);
        Assert.True(model.IsTypeBlocked);
        Assert.False(model.CanSave);
    }

    [Fact]
    public void AnEndpointOutsideTheAllowedHosts_CannotBeSaved()
    {
        var model = new ProviderEditModel { Name = "mine", ProviderType = AiProviderType.OpenAICompatible };
        model.ApplyPolicy(new AppSettings { AllowedProviderEndpoints = ["llm.corp.example"] });

        model.Endpoint = "https://api.example.com/v1";
        Assert.True(model.IsEndpointBlocked);
        Assert.False(model.CanSave);

        model.Endpoint = "https://llm.corp.example/v1";
        Assert.False(model.IsEndpointBlocked);
        Assert.True(model.CanSave);
    }

    [Fact]
    public void WebSearchSwitchedOffByPolicy_IsReportedWithoutClearingTheUsersChoice()
    {
        var model = ProviderEditModel.FromProvider(new AiProvider
        {
            Name = "mine", ProviderType = AiProviderType.OpenAI, Endpoint = "https://api.openai.com/v1",
            EnableWebSearch = true,
        });

        model.ApplyPolicy(new AppSettings { AllowProviderWebSearch = false });

        Assert.False(model.IsWebSearchAllowed);
        Assert.True(model.ToProvider().EnableWebSearch);
    }
}
