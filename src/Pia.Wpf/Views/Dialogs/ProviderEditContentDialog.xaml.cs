using System.Windows;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class ProviderEditContentDialog : ContentDialog
{
    public ProviderEditModel Provider { get; }
    private readonly IProviderService _providerService;

    public ProviderEditContentDialog(ContentDialogHost dialogHost, ProviderEditModel provider, IProviderService providerService)
        : base(dialogHost)
    {
        Provider = provider;
        _providerService = providerService;
        DataContext = Provider;
        InitializeComponent();

        // Initialize PasswordBox with existing API key if available
        if (!string.IsNullOrEmpty(Provider.ApiKey))
        {
            ApiKeyPasswordBox.Password = Provider.ApiKey;
        }

        // Sync PasswordBox changes to the model
        ApiKeyPasswordBox.PasswordChanged += (s, e) =>
        {
            Provider.ApiKey = ApiKeyPasswordBox.Password;
        };

        FetchModelsButton.Click += OnFetchModelsClick;
        Provider.PropertyChanged += OnProviderPropertyChanged;
    }

    private void OnProviderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProviderEditModel.FetchModelsError))
        {
            FetchModelsErrorText.Visibility = string.IsNullOrEmpty(Provider.FetchModelsError)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private async void OnFetchModelsClick(object sender, RoutedEventArgs e)
    {
        if (Provider.IsFetchingModels)
            return;

        Provider.FetchModelsError = null;
        Provider.IsFetchingModels = true;

        try
        {
            var models = await _providerService.FetchModelsAsync(
                Provider.Endpoint, Provider.ApiKey, Provider.ProviderType);

            Provider.AvailableModels.Clear();
            foreach (var model in models)
                Provider.AvailableModels.Add(model);

            if (models.Count == 0)
                Provider.FetchModelsError = "No models found at this endpoint.";
        }
        catch (Exception ex)
        {
            Provider.FetchModelsError = ex.Message;
        }
        finally
        {
            Provider.IsFetchingModels = false;
        }
    }
}
