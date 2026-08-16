using System.ComponentModel;
using Pia.Models;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The edit dialogs bind IsPrimaryButtonEnabled to CanSave, and WPF binding failures are
/// trace-only — so the contract is pinned here rather than left to the XAML.
/// </summary>
public class TemplateEditModelCanSaveTests
{
    [Theory]
    [InlineData("", "prompt", false)]
    [InlineData("Name", "", false)]
    [InlineData("Name", "   ", false)]
    [InlineData("Name", "prompt", true)]
    public void CanSave_RequiresNameAndGeneratedPrompt(string name, string prompt, bool expected)
    {
        var model = new TemplateEditModel { Name = name, GeneratedPrompt = prompt };
        Assert.Equal(expected, model.CanSave);
    }

    [Fact]
    public void ChangingNameOrPrompt_RaisesCanSave()
    {
        var model = new TemplateEditModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Name = "Name";
        model.GeneratedPrompt = "prompt";

        Assert.Equal(2, raised.Count(n => n == nameof(TemplateEditModel.CanSave)));
        Assert.True(model.CanSave);
    }
}

public class ProviderEditModelCanSaveTests
{
    [Theory]
    [InlineData("", "https://example.test/v1", false)]
    [InlineData("Name", "", false)]
    [InlineData("Name", "   ", false)]
    [InlineData("Name", "https://example.test/v1", true)]
    public void CanSave_RequiresNameAndEndpoint(string name, string endpoint, bool expected)
    {
        var model = new ProviderEditModel
        {
            Name = name,
            Endpoint = endpoint,
            ProviderType = AiProviderType.OpenAICompatible,
        };
        Assert.Equal(expected, model.CanSave);
    }

    /// <summary>The cloud provider's endpoint field is hidden, so gating Save on it stranded the dialog.</summary>
    [Fact]
    public void CanSave_CloudProvider_DoesNotRequireEndpoint()
    {
        var model = ProviderEditModel.FromProvider(new AiProvider
        {
            Name = "Pia Cloud",
            ProviderType = AiProviderType.PiaCloud,
            Endpoint = string.Empty,
        });

        Assert.False(model.RequiresEndpoint);
        Assert.True(model.CanSave);
    }

    /// <summary>PiaCloud is enum 0, so a fresh Add lands there without the user choosing it.</summary>
    [Fact]
    public void CanSave_NewProviderDefaultingToCloudType_StillRequiresEndpoint()
    {
        var model = new ProviderEditModel { Name = "New" };

        Assert.Equal(AiProviderType.PiaCloud, model.ProviderType);
        Assert.True(model.RequiresEndpoint);
        Assert.False(model.CanSave);
    }

    [Fact]
    public void CloudProviderSwitchedToAnotherType_RequiresEndpointAgain()
    {
        var model = ProviderEditModel.FromProvider(new AiProvider
        {
            Name = "Pia Cloud",
            ProviderType = AiProviderType.PiaCloud,
            Endpoint = string.Empty,
        });

        model.ProviderType = AiProviderType.OpenAICompatible;

        Assert.True(model.RequiresEndpoint);
        Assert.False(model.CanSave);
    }

    [Fact]
    public void ChangingNameOrEndpoint_RaisesCanSave()
    {
        var model = new ProviderEditModel { ProviderType = AiProviderType.OpenAICompatible };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Name = "Name";
        model.Endpoint = "https://example.test/v1";

        Assert.Equal(2, raised.Count(n => n == nameof(ProviderEditModel.CanSave)));
        Assert.True(model.CanSave);
    }

    [Fact]
    public void ChangingProviderType_RaisesCanSave()
    {
        var model = new ProviderEditModel { Name = "Name" };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.ProviderType = AiProviderType.Ollama;

        Assert.Contains(nameof(ProviderEditModel.CanSave), raised);
    }
}
