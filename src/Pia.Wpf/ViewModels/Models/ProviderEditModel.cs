using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;

namespace Pia.ViewModels.Models;

public partial class ProviderEditModel : ObservableValidator
{
    [ObservableProperty]
    private Guid _id;

    [Required(ErrorMessage = "Provider name is required")]
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private AiProviderType _providerType;

    [Required(ErrorMessage = "Endpoint is required")]
    [ObservableProperty]
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private string? _apiKey;

    [ObservableProperty]
    private string? _modelName;

    [ObservableProperty]
    private string? _azureDeploymentName;

    [ObservableProperty]
    private int _maxCharacters = 2000;

    [Range(1, 300, ErrorMessage = "Timeout must be between 1 and 300 seconds")]
    [ObservableProperty]
    private int _timeoutSeconds = 30;

    [ObservableProperty]
    private bool _supportsToolCalling = true;

    [ObservableProperty]
    private bool _supportsStreaming = true;

    public static AiProviderType[] EditableProviderTypes { get; } =
        Enum.GetValues<AiProviderType>().Where(t => t != AiProviderType.PiaCloud).ToArray();

    public ObservableCollection<string> AvailableModels { get; } = [];

    [ObservableProperty]
    private bool _isFetchingModels;

    [ObservableProperty]
    private string? _fetchModelsError;

    partial void OnProviderTypeChanged(AiProviderType value)
    {
        if (value == AiProviderType.Ollama && string.IsNullOrEmpty(Endpoint))
        {
            Endpoint = "http://localhost:11434/v1";
        }
        else if (value == AiProviderType.OpenRouter && string.IsNullOrEmpty(Endpoint))
        {
            Endpoint = "https://openrouter.ai/api/v1";
        }
    }

    public static ProviderEditModel FromProvider(AiProvider provider)
    {
        return new ProviderEditModel
        {
            Id = provider.Id,
            Name = provider.Name,
            ProviderType = provider.ProviderType,
            Endpoint = provider.Endpoint,
            ApiKey = null,
            ModelName = provider.ModelName,
            AzureDeploymentName = provider.AzureDeploymentName,
            MaxCharacters = 2000,
            TimeoutSeconds = 30,
            SupportsToolCalling = provider.SupportsToolCalling,
            SupportsStreaming = provider.SupportsStreaming
        };
    }

    public AiProvider ToProvider()
    {
        return new AiProvider
        {
            Id = Id,
            Name = Name,
            ProviderType = ProviderType,
            Endpoint = Endpoint,
            ModelName = ModelName,
            AzureDeploymentName = AzureDeploymentName,
            SupportsToolCalling = SupportsToolCalling,
            SupportsStreaming = SupportsStreaming
        };
    }
}
