using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Models;

namespace Pia.ViewModels.Models;

/// <summary>One option of a multi-choice question; the single-choice kinds bind the string directly.</summary>
public partial class AdvancedCreationOption(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A question plus the answer being typed into it. One row type for every kind, so the template selector
/// is the only thing that differs between them.
/// </summary>
public partial class AdvancedCreationQuestionRow : ObservableObject
{
    public AdvancedCreationQuestion Question { get; }

    public ObservableCollection<AdvancedCreationOption> Options { get; } = [];

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _selectedOption;

    /// <summary>Set by the view model, which owns the services a file import needs; the row only has to know
    /// which question the drop landed on.</summary>
    public Func<IReadOnlyList<string>, Task>? FilesDropped { get; set; }

    public IAsyncRelayCommand<IReadOnlyList<string>> FilesDroppedCommand { get; }

    public AdvancedCreationQuestionRow(AdvancedCreationQuestion question)
    {
        Question = question;
        FilesDroppedCommand = new AsyncRelayCommand<IReadOnlyList<string>>(
            paths => paths is null ? Task.CompletedTask : FilesDropped?.Invoke(paths) ?? Task.CompletedTask);

        foreach (var option in question.Options)
        {
            var row = new AdvancedCreationOption(option);
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HasAnswer));
            Options.Add(row);
        }
    }

    /// <summary>Appends rather than replaces: a box the user has already typed into is theirs.</summary>
    public void AppendDroppedText(string dropped) =>
        Text = string.IsNullOrWhiteSpace(Text) ? dropped : $"{Text}{Environment.NewLine}{Environment.NewLine}{dropped}";

    public string Label => Question.Label;

    public string? Help => Question.Help;

    public bool HasHelp => !string.IsNullOrWhiteSpace(Question.Help);

    /// <summary>An id, not the label: a per-item AutomationId keyed on the visible text would change
    /// with the UI language.</summary>
    public string AutomationId => Question.Id;

    /// <summary>Whatever the user put in, flattened to the one string the model gets back.</summary>
    public string Answer => Question.Kind switch
    {
        AdvancedCreationAnswerKind.Choice => SelectedOption ?? string.Empty,
        AdvancedCreationAnswerKind.MultiChoice =>
            string.Join(", ", Options.Where(o => o.IsSelected).Select(o => o.Label)),
        _ => Text,
    };

    public bool HasAnswer => !string.IsNullOrWhiteSpace(Answer);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasAnswer));

    partial void OnSelectedOptionChanged(string? value) => OnPropertyChanged(nameof(HasAnswer));
}
