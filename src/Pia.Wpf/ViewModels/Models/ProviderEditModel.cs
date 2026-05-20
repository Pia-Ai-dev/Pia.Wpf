using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;

namespace Pia.ViewModels.Models;

public partial class ProviderEditModel : ObservableValidator
{
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

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

    [Range(1, 300, ErrorMessage = "Timeout must be between 1 and 300 seconds")]
    [ObservableProperty]
    private int _timeoutSeconds = 300;

    [ObservableProperty]
    private bool _supportsToolCalling = true;

    [ObservableProperty]
    private bool _supportsStreaming = true;

    [ObservableProperty]
    private ReasoningEffort _reasoningEffort = ReasoningEffort.None;

    public static AiProviderType[] EditableProviderTypes { get; } =
        Enum.GetValues<AiProviderType>().Where(t => t != AiProviderType.PiaCloud).ToArray();

    public ReasoningEffort[] ReasoningEffortOptions => ProviderType switch
    {
        AiProviderType.OpenAI or AiProviderType.AzureOpenAI =>
            [ReasoningEffort.None, ReasoningEffort.Medium, ReasoningEffort.High],
        AiProviderType.Mistral =>
            [ReasoningEffort.None, ReasoningEffort.High],
        _ => Enum.GetValues<ReasoningEffort>(),
    };

    public ObservableCollection<string> AvailableModels { get; } = [];

    [ObservableProperty]
    private bool _isFetchingModels;

    [ObservableProperty]
    private string? _fetchModelsError;

    private static readonly Dictionary<AiProviderType, string> DefaultEndpoints = new()
    {
        [AiProviderType.Ollama] = "http://localhost:11434/v1",
        [AiProviderType.OpenRouter] = "https://openrouter.ai/api/v1",
        [AiProviderType.OpenAI] = "https://api.openai.com/v1",
        [AiProviderType.Mistral] = "https://api.mistral.ai/v1",
        [AiProviderType.VLlm] = "http://localhost:8000/v1",
    };

    partial void OnProviderTypeChanged(AiProviderType oldValue, AiProviderType newValue)
    {
        if (!DefaultEndpoints.TryGetValue(newValue, out var newDefault))
            return;

        var isOldDefault = string.IsNullOrEmpty(Endpoint)
            || (DefaultEndpoints.TryGetValue(oldValue, out var oldDefault) && Endpoint == oldDefault);

        if (isOldDefault)
        {
            Endpoint = newDefault;
        }

        OnPropertyChanged(nameof(ReasoningEffortOptions));
        if (!ReasoningEffortOptions.Contains(ReasoningEffort))
            ReasoningEffort = ReasoningEffort.None;
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
            TimeoutSeconds = provider.TimeoutSeconds is > 0 and <= 300 ? provider.TimeoutSeconds : 300,
            SupportsToolCalling = provider.SupportsToolCalling,
            SupportsStreaming = provider.SupportsStreaming,
            ReasoningEffort = provider.ReasoningEffort ?? ReasoningEffort.None,
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
            SupportsStreaming = SupportsStreaming,
            TimeoutSeconds = TimeoutSeconds,
            ReasoningEffort = ReasoningEffort,
        };
    }
}
