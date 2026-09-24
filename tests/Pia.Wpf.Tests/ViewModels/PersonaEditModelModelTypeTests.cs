using System.ComponentModel;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The editor's "needs a private model on the provider side" hint is bound to IsPrivateModelType,
/// and WPF binding failures are trace-only — so the contract is pinned here, not left to the XAML.
/// </summary>
public class PersonaEditModelModelTypeTests
{
    [Fact]
    public void ModelTypeOptions_OfferPrivate()
    {
        Assert.Contains(PersonaEditModel.PrivateModelType, new PersonaEditModel().ModelTypeOptions);
    }

    [Theory]
    [InlineData("private", true)]
    [InlineData("Private", true)]
    [InlineData("  private  ", true)]
    [InlineData("general", false)]
    [InlineData("fast", false)]
    [InlineData("privateer", false)]
    [InlineData("", false)]
    public void IsPrivateModelType_MatchesTrimmedCaseInsensitively(string modelType, bool expected)
    {
        var model = new PersonaEditModel { ModelType = modelType };
        Assert.Equal(expected, model.IsPrivateModelType);
    }

    [Fact]
    public void ChangingModelType_RaisesIsPrivateModelType()
    {
        var model = new PersonaEditModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.ModelType = PersonaEditModel.PrivateModelType;

        Assert.Contains(nameof(PersonaEditModel.IsPrivateModelType), raised);
        Assert.True(model.IsPrivateModelType);
    }
}
