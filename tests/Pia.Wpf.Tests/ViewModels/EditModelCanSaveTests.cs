using System.ComponentModel;
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
        var model = new ProviderEditModel { Name = name, Endpoint = endpoint };
        Assert.Equal(expected, model.CanSave);
    }

    [Fact]
    public void ChangingNameOrEndpoint_RaisesCanSave()
    {
        var model = new ProviderEditModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Name = "Name";
        model.Endpoint = "https://example.test/v1";

        Assert.Equal(2, raised.Count(n => n == nameof(ProviderEditModel.CanSave)));
        Assert.True(model.CanSave);
    }
}
