using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>A provider choice for the persona's optional <c>PreferredProviderId</c> picker.
/// <c>Id == null</c> is the "(Use mode default)" entry.</summary>
public record ProviderChoice(Guid? Id, string Name);

/// <summary>A reasoning-effort choice. <c>Value == null</c> is the "(Provider default)" entry.</summary>
public record ReasoningEffortChoice(ReasoningEffort? Value, string Display);

/// <summary>
/// Edit model for the single rich persona dialog. Mirrors <see cref="TemplateEditModel"/> — including
/// the AI-assist "draft from a description" command — and adds a provider picker (with a
/// "(Use mode default)" entry) and a nullable reasoning-effort picker.
/// </summary>
public partial class PersonaEditModel : ObservableValidator
{
    private readonly ITextOptimizationService? _textOptimizationService;

    // Preserved across edit so sync conflict-resolution (UpdatedAt) and creation order stay stable.
    private DateTime _createdAt = DateTime.UtcNow;

    [ObservableProperty]
    private Guid _id;

    [Required(ErrorMessage = "Persona name is required")]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _tagline = string.Empty;

    [Required(ErrorMessage = "System prompt is required")]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [ObservableProperty]
    private string _systemPrompt = string.Empty;

    [ObservableProperty]
    private string _guardrails = string.Empty;

    [ObservableProperty]
    private string _archetype = "custom";

    /// <summary>Comma-separated domain tags (round-tripped to <c>Persona.Expertise</c>).</summary>
    [ObservableProperty]
    private string _expertise = string.Empty;

    [ObservableProperty]
    private string _emoji = string.Empty;

    [ObservableProperty]
    private string _accentColor = string.Empty;

    [ObservableProperty]
    private PersonaToolScope _toolScope = PersonaToolScope.Full;

    [ObservableProperty]
    private ProviderChoice? _selectedProvider;

    [ObservableProperty]
    private ReasoningEffortChoice _selectedReasoningEffort;

    /// <summary>Free-text description used by the AI-assist "draft" command.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isGenerating;

    public ObservableCollection<ProviderChoice> ProviderChoices { get; } = [];

    public string[] ArchetypeOptions { get; } =
        ["assistant", "analyst", "creative", "visionary", "explainer", "custom"];

    public PersonaToolScope[] ToolScopeOptions { get; } = Enum.GetValues<PersonaToolScope>();

    public IReadOnlyList<ReasoningEffortChoice> ReasoningEffortOptions { get; } =
    [
        new(null, "(Provider default)"),
        new(ReasoningEffort.None, "None"),
        new(ReasoningEffort.Minimal, "Minimal"),
        new(ReasoningEffort.Low, "Low"),
        new(ReasoningEffort.Medium, "Medium"),
        new(ReasoningEffort.High, "High"),
        new(ReasoningEffort.XHigh, "X-High"),
    ];

    public bool CanSave => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(SystemPrompt);

    public PersonaEditModel()
    {
        _selectedReasoningEffort = ReasoningEffortOptions[0];
    }

    public PersonaEditModel(ITextOptimizationService? textOptimizationService) : this()
    {
        _textOptimizationService = textOptimizationService;
    }

    /// <summary>
    /// Builds the provider picker (a leading "(Use mode default)" entry plus the supplied providers)
    /// and selects the entry matching <paramref name="selectedId"/>.
    /// </summary>
    public void SetProviders(IEnumerable<AiProvider> providers, Guid? selectedId)
    {
        ProviderChoices.Clear();
        ProviderChoices.Add(new ProviderChoice(null, "(Use mode default)"));
        foreach (var p in providers)
            ProviderChoices.Add(new ProviderChoice(p.Id, p.Name));

        SelectedProvider = ProviderChoices.FirstOrDefault(c => c.Id == selectedId) ?? ProviderChoices[0];
    }

    [RelayCommand]
    private async Task GenerateDraftAsync()
    {
        if (string.IsNullOrWhiteSpace(Description) || _textOptimizationService is null)
            return;

        IsGenerating = true;
        try
        {
            var draft = await _textOptimizationService.GeneratePersonaDraftAsync(Description, SelectedProvider?.Id);

            if (!string.IsNullOrWhiteSpace(draft.Name)) Name = draft.Name!;
            if (!string.IsNullOrWhiteSpace(draft.Tagline)) Tagline = draft.Tagline!;
            if (!string.IsNullOrWhiteSpace(draft.SystemPrompt)) SystemPrompt = draft.SystemPrompt!;
            if (!string.IsNullOrWhiteSpace(draft.Emoji)) Emoji = draft.Emoji!;
            if (!string.IsNullOrWhiteSpace(draft.AccentColor)) AccentColor = draft.AccentColor!;
            if (draft.Expertise is { Count: > 0 }) Expertise = string.Join(", ", draft.Expertise);
        }
        finally
        {
            IsGenerating = false;
        }
    }

    public static PersonaEditModel FromPersona(Persona persona, ITextOptimizationService? textOptimizationService = null)
    {
        var model = new PersonaEditModel(textOptimizationService)
        {
            Id = persona.Id,
            Name = persona.Name,
            Tagline = persona.Tagline ?? string.Empty,
            SystemPrompt = persona.SystemPrompt,
            Guardrails = persona.Guardrails ?? string.Empty,
            Archetype = string.IsNullOrEmpty(persona.Archetype) ? "custom" : persona.Archetype,
            Expertise = persona.Expertise is { Count: > 0 } ? string.Join(", ", persona.Expertise) : string.Empty,
            Emoji = persona.Emoji ?? string.Empty,
            AccentColor = persona.AccentColor ?? string.Empty,
            ToolScope = persona.ToolScope,
        };
        model._createdAt = persona.CreatedAt;
        model.SelectedReasoningEffort = model.ReasoningEffortOptions.FirstOrDefault(o => o.Value == persona.ReasoningEffort)
            ?? model.ReasoningEffortOptions[0];
        // PreferredProviderId is applied once the provider list is populated via SetProviders.
        model.SelectedProvider = new ProviderChoice(persona.PreferredProviderId, string.Empty);
        return model;
    }

    public Persona ToPersona()
    {
        return new Persona
        {
            Id = Id,
            Name = Name.Trim(),
            Tagline = string.IsNullOrWhiteSpace(Tagline) ? null : Tagline.Trim(),
            SystemPrompt = SystemPrompt.Trim(),
            Guardrails = string.IsNullOrWhiteSpace(Guardrails) ? null : Guardrails.Trim(),
            Archetype = string.IsNullOrWhiteSpace(Archetype) ? "custom" : Archetype,
            Expertise = ParseExpertise(Expertise),
            Emoji = string.IsNullOrWhiteSpace(Emoji) ? null : Emoji.Trim(),
            AccentColor = string.IsNullOrWhiteSpace(AccentColor) ? null : AccentColor.Trim(),
            ToolScope = ToolScope,
            PreferredProviderId = SelectedProvider?.Id,
            ReasoningEffort = SelectedReasoningEffort?.Value,
            SchemaVersion = 1,
            IsBuiltIn = false,
            CreatedAt = _createdAt,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static List<string> ParseExpertise(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(16)
            .ToList();
    }
}
