using System.Windows;
using System.Windows.Controls;
using Pia.Models;
using Pia.ViewModels.Models;

namespace Pia.Views.Dialogs.Overlay;

/// <summary>One template per answer kind. This is what makes the three subjects look alike: only the
/// questions differ between them, never the controls they are answered with.</summary>
public class AdvancedCreationQuestionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? LongTextTemplate { get; set; }
    public DataTemplate? SampleTemplate { get; set; }
    public DataTemplate? ChoiceTemplate { get; set; }
    public DataTemplate? MultiChoiceTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is not AdvancedCreationQuestionRow row
            ? base.SelectTemplate(item, container)
            : row.Question.Kind switch
            {
                AdvancedCreationAnswerKind.LongText => LongTextTemplate,
                AdvancedCreationAnswerKind.Sample => SampleTemplate,
                AdvancedCreationAnswerKind.Choice => ChoiceTemplate,
                AdvancedCreationAnswerKind.MultiChoice => MultiChoiceTemplate,
                _ => TextTemplate,
            };
}
