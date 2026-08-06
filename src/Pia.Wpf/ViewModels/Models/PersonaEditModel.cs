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

    /// <summary>Per-persona response-format guidance; blank ⇒ the substrate default is used.</summary>
    [ObservableProperty]
    private string _outputFormat = string.Empty;

    [ObservableProperty]
    private string _archetype = "custom";

    /// <summary>Free-form model-routing hint; blank ⇒ no persona-type routing.</summary>
    [ObservableProperty]
    private string _modelType = string.Empty;

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

    /// <summary>Suggestions for the editable model-type combo — a routing hint, not a closed vocabulary.</summary>
    public string[] ModelTypeOptions { get; } = ["general", "fast", "code"];

    public PersonaToolScope[] ToolScopeOptions { get; } = Enum.GetValues<PersonaToolScope>();

    /// <summary>Curated emoji suggestions for the in-dialog emoji picker.</summary>
    public string[] EmojiChoices { get; } =
    [
        "🟣", "🔵", "🟢", "🟡", "🟠", "🔴",
        "✨", "💡", "🧠", "🤖", "💻", "📈",
        "✍️", "🎨", "📚", "🔬", "🧭", "🛡️",
        "🌐", "🎯", "🚀", "🧒", "💬", "⚙️",
        "🦉", "🌟", "🔥", "💎", "🧩", "📊",
        "🗂️", "🎓", "⚖️", "🎙️", "🌱", "🔭",
    ];

    /// <summary>
    /// Preset accent-colour swatches for the picker — a hue-ordered palette (six per row) so the
    /// popup grid reads as a smooth spectrum.
    /// </summary>
    public string[] AccentSwatches { get; } =
    [
        "#F44336", "#E53935", "#FF5252", "#FF4081", "#EC407A", "#D81B60",
        "#AB47BC", "#9C27B0", "#7C4DFF", "#673AB7", "#5E35B1", "#3F51B5",
        "#2962FF", "#1E88E5", "#2196F3", "#03A9F4", "#00BCD4", "#00ACC1",
        "#009688", "#00BFA5", "#00C853", "#43A047", "#7CB342", "#C0CA33",
        "#FDD835", "#FFB300", "#FB8C00", "#FF6D00", "#8D6E63", "#607D8B",
    ];

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
    private void PickEmoji(string? emoji)
    {
        if (!string.IsNullOrWhiteSpace(emoji))
            Emoji = emoji;
    }

    [RelayCommand]
    private void PickAccentColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
            AccentColor = hex;
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

            // Only fill fields the user hasn't already set, so re-drafting (or drafting after some
            // manual edits) never clobbers their input — "prefill the unset values".
            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(draft.Name)) Name = draft.Name!;
            if (string.IsNullOrWhiteSpace(Tagline) && !string.IsNullOrWhiteSpace(draft.Tagline)) Tagline = draft.Tagline!;
            if (string.IsNullOrWhiteSpace(SystemPrompt) && !string.IsNullOrWhiteSpace(draft.SystemPrompt)) SystemPrompt = draft.SystemPrompt!;
            if (string.IsNullOrWhiteSpace(Guardrails) && !string.IsNullOrWhiteSpace(draft.Guardrails)) Guardrails = draft.Guardrails!;
            if (string.IsNullOrWhiteSpace(OutputFormat) && !string.IsNullOrWhiteSpace(draft.OutputFormat)) OutputFormat = draft.OutputFormat!;
            if (string.IsNullOrWhiteSpace(Emoji) && !string.IsNullOrWhiteSpace(draft.Emoji)) Emoji = draft.Emoji!;
            if (string.IsNullOrWhiteSpace(AccentColor) && !string.IsNullOrWhiteSpace(draft.AccentColor)) AccentColor = draft.AccentColor!;
            if (string.IsNullOrWhiteSpace(Expertise) && draft.Expertise is { Count: > 0 }) Expertise = string.Join(", ", draft.Expertise);
            if (IsUnsetArchetype(Archetype) && !string.IsNullOrWhiteSpace(draft.Archetype) && ArchetypeOptions.Contains(draft.Archetype))
                Archetype = draft.Archetype!;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    // The "custom" default (and blank) count as unset for draft-prefill purposes.
    private static bool IsUnsetArchetype(string archetype) =>
        string.IsNullOrWhiteSpace(archetype) || archetype == "custom";

    public static PersonaEditModel FromPersona(Persona persona, ITextOptimizationService? textOptimizationService = null)
    {
        var model = new PersonaEditModel(textOptimizationService)
        {
            Id = persona.Id,
            Name = persona.Name,
            Tagline = persona.Tagline ?? string.Empty,
            SystemPrompt = persona.SystemPrompt,
            Guardrails = persona.Guardrails ?? string.Empty,
            OutputFormat = persona.OutputFormat ?? string.Empty,
            Archetype = string.IsNullOrEmpty(persona.Archetype) ? "custom" : persona.Archetype,
            ModelType = persona.ModelType ?? string.Empty,
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
            OutputFormat = string.IsNullOrWhiteSpace(OutputFormat) ? null : OutputFormat.Trim(),
            Archetype = string.IsNullOrWhiteSpace(Archetype) ? "custom" : Archetype,
            ModelType = string.IsNullOrWhiteSpace(ModelType) ? null : ModelType.Trim(),
            Expertise = ParseExpertise(Expertise),
            Emoji = string.IsNullOrWhiteSpace(Emoji) ? null : Emoji.Trim(),
            AccentColor = string.IsNullOrWhiteSpace(AccentColor) ? null : AccentColor.Trim(),
            ToolScope = ToolScope,
            PreferredProviderId = SelectedProvider?.Id,
            ReasoningEffort = SelectedReasoningEffort?.Value,
            SchemaVersion = 1,
            IsBuiltIn = false,
            // Anything this editor produces is a user persona that syncs — including a duplicate seeded
            // from a managed original, which must not inherit the admin-owned flag.
            IsManaged = false,
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
