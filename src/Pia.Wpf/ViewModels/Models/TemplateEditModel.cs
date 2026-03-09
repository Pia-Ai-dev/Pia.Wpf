using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Models;
using Pia.Services.Interfaces;
using System.ComponentModel.DataAnnotations;

namespace Pia.ViewModels.Models;

public partial class TemplateEditModel : ObservableValidator
{
    private readonly ITextOptimizationService? _textOptimizationService;

    [ObservableProperty]
    private Guid _id;

    [Required(ErrorMessage = "Template name is required")]
    [ObservableProperty]
    private string _name = string.Empty;

    [Required(ErrorMessage = "Style description is required")]
    [ObservableProperty]
    private string _styleDescription = string.Empty;

    [ObservableProperty]
    private string _generatedPrompt = string.Empty;

    [ObservableProperty]
    private bool _isGeneratingPrompt;

    public TemplateEditModel()
    {
    }

    public TemplateEditModel(ITextOptimizationService textOptimizationService)
    {
        _textOptimizationService = textOptimizationService;
    }

    [RelayCommand]
    private async Task GeneratePromptAsync()
    {
        if (string.IsNullOrWhiteSpace(StyleDescription))
            return;

        IsGeneratingPrompt = true;
        try
        {
            GeneratedPrompt = await _textOptimizationService!.GeneratePromptAsync(StyleDescription);
        }
        finally
        {
            IsGeneratingPrompt = false;
        }
    }

    public static TemplateEditModel FromTemplate(OptimizationTemplate template, ITextOptimizationService? textOptimizationService = null)
    {
        return new TemplateEditModel(textOptimizationService!)
        {
            Id = template.Id,
            Name = template.Name,
            StyleDescription = template.StyleDescription ?? string.Empty,
            GeneratedPrompt = template.Prompt
        };
    }

    public OptimizationTemplate ToTemplate()
    {
        return new OptimizationTemplate
        {
            Id = Id,
            Name = Name,
            Prompt = GeneratedPrompt,
            StyleDescription = StyleDescription,
            IsBuiltIn = false
        };
    }
}
