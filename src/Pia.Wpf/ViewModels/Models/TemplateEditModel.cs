using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Models;
using Pia.Services.Interfaces;
using System.ComponentModel.DataAnnotations;

namespace Pia.ViewModels.Models;

/// <summary>
/// Edit model behind the inline template editor. Mirrors <see cref="PersonaEditModel"/>, including the
/// AI-assist "draft from a description" command.
/// </summary>
public partial class TemplateEditModel : ObservableValidator
{
    private readonly ITextOptimizationService? _textOptimizationService;

    // Preserved across edit so sync conflict-resolution and creation order stay stable.
    private DateTime _createdAt = DateTime.UtcNow;

    [ObservableProperty]
    private Guid _id;

    [Required(ErrorMessage = "Template name is required")]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>One-line summary shown on the master row.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    [Required(ErrorMessage = "Style description is required")]
    [ObservableProperty]
    private string _styleDescription = string.Empty;

    [NotifyPropertyChangedFor(nameof(CanSave))]
    [ObservableProperty]
    private string _generatedPrompt = string.Empty;

    [ObservableProperty]
    private bool _isGeneratingPrompt;

    public TemplateEditModel()
    {
    }

    public TemplateEditModel(ITextOptimizationService? textOptimizationService)
    {
        _textOptimizationService = textOptimizationService;
    }

    public bool CanSave => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(GeneratedPrompt);

    [RelayCommand]
    private async Task GeneratePromptAsync()
    {
        if (string.IsNullOrWhiteSpace(StyleDescription) || _textOptimizationService is null)
            return;

        IsGeneratingPrompt = true;
        try
        {
            var draft = await _textOptimizationService.GenerateTemplateDraftAsync(StyleDescription);

            // Only fill what the user has not already set, so re-drafting never clobbers their input.
            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(draft.Name)) Name = draft.Name!;
            if (string.IsNullOrWhiteSpace(Description) && !string.IsNullOrWhiteSpace(draft.Description)) Description = draft.Description!;
            if (!string.IsNullOrWhiteSpace(draft.Prompt)) GeneratedPrompt = draft.Prompt!;
        }
        finally
        {
            IsGeneratingPrompt = false;
        }
    }

    public static TemplateEditModel FromTemplate(OptimizationTemplate template, ITextOptimizationService? textOptimizationService = null)
    {
        var model = new TemplateEditModel(textOptimizationService)
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description ?? string.Empty,
            StyleDescription = template.StyleDescription ?? string.Empty,
            GeneratedPrompt = template.Prompt,
        };
        model._createdAt = template.CreatedAt;
        return model;
    }

    public OptimizationTemplate ToTemplate()
    {
        return new OptimizationTemplate
        {
            Id = Id,
            Name = Name.Trim(),
            Prompt = GeneratedPrompt.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            StyleDescription = string.IsNullOrWhiteSpace(StyleDescription) ? null : StyleDescription.Trim(),
            IsBuiltIn = false,
            CreatedAt = _createdAt,
            ModifiedAt = DateTime.UtcNow,
        };
    }
}
