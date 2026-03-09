using System.Windows;
using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class TemplateEditContentDialog : ContentDialog
{
    public TemplateEditModel EditModel { get; }

    public TemplateEditContentDialog(ContentDialogHost dialogHost, TemplateEditModel template)
        : base(dialogHost)
    {
        EditModel = template;
        DataContext = EditModel;
        InitializeComponent();

        Closing += OnClosing;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.Primary)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(EditModel.Name))
        {
            args.Cancel = true;
            ShowValidationError("Template name is required");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditModel.GeneratedPrompt))
        {
            args.Cancel = true;
            ShowValidationError("Generated prompt is required. Describe your style and click 'Generate Prompt'.");
            return;
        }
    }

    private void ShowValidationError(string message)
    {
        System.Windows.MessageBox.Show(message, "Validation Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }
}
