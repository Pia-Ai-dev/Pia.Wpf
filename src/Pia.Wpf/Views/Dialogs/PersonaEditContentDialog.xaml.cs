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
    }

    // Picking a swatch inside a popup fires the bound command (which sets the value) and bubbles
    // here so the popup closes — StaysOpen=False only dismisses on clicks *outside* the popup.
    private void OnEmojiPicked(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = false;

    private void OnAccentPicked(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = false;
}
