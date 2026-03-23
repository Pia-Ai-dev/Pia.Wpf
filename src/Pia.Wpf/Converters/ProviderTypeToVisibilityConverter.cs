using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

public class ProviderTypeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AiProviderType providerType || parameter is not string field)
        {
            return Visibility.Collapsed;
        }

        return field switch
        {
            "ModelName" => providerType is AiProviderType.OpenAI or AiProviderType.Ollama or AiProviderType.OpenRouter or AiProviderType.OpenAICompatible or AiProviderType.Mistral or AiProviderType.Anthropic
                ? Visibility.Visible : Visibility.Collapsed,
            "AzureDeployment" => providerType == AiProviderType.AzureOpenAI
                ? Visibility.Visible : Visibility.Collapsed,
            "ApiKey" => providerType is not (AiProviderType.PiaCloud or AiProviderType.Ollama)
                ? Visibility.Visible : Visibility.Collapsed,
            "Endpoint" => providerType != AiProviderType.PiaCloud
                ? Visibility.Visible : Visibility.Collapsed,
            "Delete" => providerType != AiProviderType.PiaCloud
                ? Visibility.Visible : Visibility.Collapsed,
            "ReasoningEffort" => providerType is AiProviderType.OpenAI or AiProviderType.AzureOpenAI or AiProviderType.OpenRouter
                ? Visibility.Visible : Visibility.Collapsed,
            "WebSearch" => providerType is AiProviderType.OpenAI or AiProviderType.OpenRouter or AiProviderType.Anthropic or AiProviderType.Mistral
                ? Visibility.Visible : Visibility.Collapsed,
            "ExtendedThinking" => providerType == AiProviderType.Anthropic
                ? Visibility.Visible : Visibility.Collapsed,
            "PromptCaching" => providerType == AiProviderType.Anthropic
                ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Collapsed
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
