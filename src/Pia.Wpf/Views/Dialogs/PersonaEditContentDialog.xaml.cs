using System.Windows;
using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class PersonaEditContentDialog : ContentDialog
{
    public PersonaEditModel EditModel { get; }

    public PersonaEditContentDialog(ContentDialogHost dialogHost, PersonaEditModel persona)
        : base(dialogHost)
    {
        EditModel = persona;
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
            ShowValidationError("Persona name is required");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditModel.SystemPrompt))
        {
            args.Cancel = true;
            ShowValidationError("A system prompt is required. Describe the persona and click 'Draft with AI', or write one yourself.");
            return;
        }
    }

    // Picking a swatch inside a popup fires the bound command (which sets the value) and bubbles
    // here so the popup closes — StaysOpen=False only dismisses on clicks *outside* the popup.
    private void OnEmojiPicked(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = false;

    private void OnAccentPicked(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = false;

    private static void ShowValidationError(string message)
    {
        System.Windows.MessageBox.Show(message, "Validation Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }
}
